using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Logic.Values;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// The planner decides which individual plc symbols a restore has to write. What is checked
    /// here above all is that a value nested inside a structure or an array produces a write of
    /// its own, addressed all the way down - that is exactly what the previous implementation
    /// silently dropped on a live plc.
    /// </summary>
    public class PlcLeafPlannerTests
    {
        private static string Describe(PlcLeafWrite write)
            => string.Join("/", write.Steps.Select(s => s.IsElement ? $"[{s.ElementPosition}]" : s.MemberName));

        [Fact]
        public void A_scalar_variable_is_written_on_the_variable_itself()
        {
            var plan = PlcLeafPlanner.Plan(FakeValueNode.Leaf((short) 157), new JValue(1001), "GVL.Counter");

            var write = Assert.Single(plan.Writes);
            Assert.Empty(write.Steps);
            Assert.Equal("GVL.Counter", write.Path);
            Assert.Equal((short) 1001, write.Value);
            Assert.True(plan.IsClean);
        }

        [Fact]
        public void A_member_of_a_structure_is_addressed_by_its_name()
        {
            var target = FakeValueNode.Struct(("Int1", FakeValueNode.Leaf((short) 157)));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""Int1"": 1001 }"), "GVL.User");

            var write = Assert.Single(plan.Writes);
            Assert.Equal("Int1", Describe(write));
            Assert.Equal("GVL.User.Int1", write.Path);
            Assert.Equal((short) 1001, write.Value);
        }

        /// <summary>
        /// The regression test for the defect measured on the plc on 2026-08-26: Int1 was written
        /// and InInVar.IntInIn1 was not, while the restore reported success. A nested member has
        /// to come out of the planner as a write of its own, two steps deep.
        /// </summary>
        [Fact]
        public void A_member_nested_inside_another_structure_is_written_too()
        {
            var target = FakeValueNode.Struct(
                ("Int1", FakeValueNode.Leaf((short) 157)),
                ("InInVar", FakeValueNode.Struct(
                    ("IntInIn1", FakeValueNode.Leaf((short) 12)),
                    ("RealInIn1", FakeValueNode.Leaf(123.2345f)))));

            var json = JObject.Parse(@"{
                ""Int1"": 1001,
                ""InInVar"": { ""IntInIn1"": 9999, ""RealInIn1"": 999.5 }
            }");

            var plan = PlcLeafPlanner.Plan(target, json, "GVL.PersVarGlobalUser1_1");

            Assert.True(plan.IsClean);
            Assert.Equal(3, plan.Writes.Count);

            var nested = plan.Writes.Single(w => w.Path == "GVL.PersVarGlobalUser1_1.InInVar.IntInIn1");
            Assert.Equal("InInVar/IntInIn1", Describe(nested));
            Assert.Equal((short) 9999, nested.Value);

            var real = plan.Writes.Single(w => w.Path == "GVL.PersVarGlobalUser1_1.InInVar.RealInIn1");
            Assert.Equal(999.5f, real.Value);
        }

        [Fact]
        public void Nesting_keeps_going_however_deep_the_structure_is()
        {
            var target = FakeValueNode.Struct(
                ("Device", FakeValueNode.Struct(
                    ("Sub", FakeValueNode.Struct(
                        ("PersInt1", FakeValueNode.Leaf((short) 2334)))))));

            var plan = PlcLeafPlanner.Plan(target,
                JObject.Parse(@"{ ""Device"": { ""Sub"": { ""PersInt1"": 13372 } } }"),
                "MAIN.Fb");

            var write = Assert.Single(plan.Writes);
            Assert.Equal("Device/Sub/PersInt1", Describe(write));
            Assert.Equal("MAIN.Fb.Device.Sub.PersInt1", write.Path);
        }

        [Fact]
        public void Every_element_of_an_array_gets_its_own_write()
        {
            var target = FakeValueNode.Array(0,
                FakeValueNode.Leaf((short) 6),
                FakeValueNode.Leaf((short) 7),
                FakeValueNode.Leaf((short) 8));

            var plan = PlcLeafPlanner.Plan(target, JArray.Parse("[11, 22, 33]"), "GVL.Values");

            Assert.True(plan.IsClean);
            Assert.Equal(new[] {"[0]", "[1]", "[2]"}, plan.Writes.Select(Describe));
            Assert.Equal(new object[] {(short) 11, (short) 22, (short) 33}, plan.Writes.Select(w => w.Value));
        }

        /// <summary>
        /// Elements are addressed by position, not by the index the plc declares: the symbol
        /// collection enumerates them in the same order whatever the lower bound is. The declared
        /// index still shows up in the readable path, which is what ends up in the report.
        /// </summary>
        [Fact]
        public void An_array_that_does_not_start_at_zero_is_addressed_by_position()
        {
            var target = FakeValueNode.Array(1, FakeValueNode.Leaf(0), FakeValueNode.Leaf(0));

            var plan = PlcLeafPlanner.Plan(target, JArray.Parse("[10, 20]"), "GVL.Values");

            Assert.Equal(new[] {"[0]", "[1]"}, plan.Writes.Select(Describe));
            Assert.Equal(new[] {"GVL.Values[1]", "GVL.Values[2]"}, plan.Writes.Select(w => w.Path));
        }

        [Fact]
        public void An_array_of_structures_reaches_the_members_of_every_element()
        {
            var target = FakeValueNode.Array(0,
                FakeValueNode.Struct(("A", FakeValueNode.Leaf(0)), ("B", FakeValueNode.Leaf(0))),
                FakeValueNode.Struct(("A", FakeValueNode.Leaf(0)), ("B", FakeValueNode.Leaf(0))));

            var plan = PlcLeafPlanner.Plan(target,
                JArray.Parse(@"[{ ""A"": 1, ""B"": 2 }, { ""A"": 3, ""B"": 4 }]"),
                "GVL.Items");

            Assert.True(plan.IsClean);
            Assert.Equal(new[] {"[0]/A", "[0]/B", "[1]/A", "[1]/B"}, plan.Writes.Select(Describe));
            Assert.Equal(new object[] {1, 2, 3, 4}, plan.Writes.Select(w => w.Value));
        }

        [Fact]
        public void An_array_of_arrays_reaches_the_innermost_element()
        {
            var target = FakeValueNode.Array(0,
                FakeValueNode.Array(0, FakeValueNode.Leaf(0), FakeValueNode.Leaf(0)),
                FakeValueNode.Array(0, FakeValueNode.Leaf(0), FakeValueNode.Leaf(0)));

            var plan = PlcLeafPlanner.Plan(target, JArray.Parse("[[1, 2], [3, 4]]"), "GVL.Jagged");

            Assert.True(plan.IsClean);
            Assert.Equal(new[] {"[0]/[0]", "[0]/[1]", "[1]/[0]", "[1]/[1]"}, plan.Writes.Select(Describe));
            Assert.Equal("GVL.Jagged[1][1]", plan.Writes.Last().Path);
        }

        [Fact]
        public void A_member_missing_from_the_backup_is_reported_and_the_others_are_still_written()
        {
            var target = FakeValueNode.Struct(
                ("Kept", FakeValueNode.Leaf(0)),
                ("Gone", FakeValueNode.Leaf(0)));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""Kept"": 5 }"), "GVL.Data");

            Assert.Equal("Kept", Describe(Assert.Single(plan.Writes)));
            Assert.Contains(plan.Mismatches, m => m.Contains("GVL.Data.Gone") && m.Contains("missing in the backup"));
        }

        [Fact]
        public void A_member_that_no_longer_exists_on_the_plc_is_reported()
        {
            var target = FakeValueNode.Struct(("Kept", FakeValueNode.Leaf(0)));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""Kept"": 5, ""Removed"": 9 }"), "GVL.Data");

            Assert.Single(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("GVL.Data.Removed") && m.Contains("no longer exists"));
        }

        [Fact]
        public void A_value_that_does_not_fit_the_plc_type_produces_no_write()
        {
            var target = FakeValueNode.Struct(("Number", FakeValueNode.Leaf(0)));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""Number"": ""not a number"" }"), "GVL.Data");

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("does not fit the plc type"));
        }

        [Fact]
        public void A_structure_the_backup_holds_as_a_single_value_is_reported()
        {
            var target = FakeValueNode.Struct(("Number", FakeValueNode.Leaf(0)));

            var plan = PlcLeafPlanner.Plan(target, new JValue(5), "GVL.Data");

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("plc expects a structure"));
        }

        [Fact]
        public void An_array_shorter_in_the_backup_is_reported_and_the_common_part_is_written()
        {
            var target = FakeValueNode.Array(0,
                FakeValueNode.Leaf(0), FakeValueNode.Leaf(0), FakeValueNode.Leaf(0));

            var plan = PlcLeafPlanner.Plan(target, JArray.Parse("[10, 20]"), "GVL.Values");

            Assert.Equal(2, plan.Writes.Count);
            Assert.Contains(plan.Mismatches, m => m.Contains("array length differs"));
        }

        [Fact]
        public void An_array_longer_in_the_backup_is_reported_and_the_common_part_is_written()
        {
            var target = FakeValueNode.Array(0, FakeValueNode.Leaf(0), FakeValueNode.Leaf(0));

            var plan = PlcLeafPlanner.Plan(target, JArray.Parse("[1, 2, 3, 4]"), "GVL.Values");

            Assert.Equal(2, plan.Writes.Count);
            Assert.Contains(plan.Mismatches, m => m.Contains("array length differs"));
        }

        [Fact]
        public void An_array_the_backup_holds_as_a_structure_is_reported()
        {
            var target = FakeValueNode.Array(0, FakeValueNode.Leaf(0));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""a"": 1 }"), "GVL.Values");

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("plc expects an array"));
        }

        /// <summary>
        /// A backup written by an older version, or edited by hand, may not keep the casing the
        /// plc declares.
        /// </summary>
        [Fact]
        public void Member_names_are_matched_case_insensitively()
        {
            var target = FakeValueNode.Struct(("Enabled", FakeValueNode.Leaf(false)));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""enabled"": true }"), "GVL.Data");

            Assert.True(plan.IsClean);
            var write = Assert.Single(plan.Writes);
            Assert.Equal("Enabled", Describe(write));
            Assert.Equal(true, write.Value);
        }

        /// <summary>
        /// What a backup holds has to come back out unchanged: every value of the file is planned,
        /// with the same value it was written with, and nothing is left over.
        /// </summary>
        [Fact]
        public void Everything_a_backup_holds_comes_back_out_of_the_plan()
        {
            var source = FakeValueNode.Struct(
                ("Enabled", FakeValueNode.Leaf(true)),
                ("Items", FakeValueNode.Array(1,
                    FakeValueNode.Struct(("Id", FakeValueNode.Leaf(3)), ("Name", FakeValueNode.Leaf("a"))),
                    FakeValueNode.Struct(("Id", FakeValueNode.Leaf(4)), ("Name", FakeValueNode.Leaf("b"))))));

            var json = PlcJsonConverter.ToJson(source);

            var target = FakeValueNode.Struct(
                ("Enabled", FakeValueNode.Leaf(false)),
                ("Items", FakeValueNode.Array(1,
                    FakeValueNode.Struct(("Id", FakeValueNode.Leaf(0)), ("Name", FakeValueNode.Leaf(""))),
                    FakeValueNode.Struct(("Id", FakeValueNode.Leaf(0)), ("Name", FakeValueNode.Leaf(""))))));

            var plan = PlcLeafPlanner.Plan(target, json, "GVL.Data");

            Assert.True(plan.IsClean);
            Assert.Equal(new[] {"Enabled", "Items/[0]/Id", "Items/[0]/Name", "Items/[1]/Id", "Items/[1]/Name"},
                plan.Writes.Select(Describe));
            Assert.Equal(new object[] {true, 3, "a", 4, "b"}, plan.Writes.Select(w => w.Value));
            Assert.Equal(new[] {"GVL.Data.Items[1].Id", "GVL.Data.Items[2].Id"},
                plan.Writes.Where(w => w.Path.EndsWith(".Id")).Select(w => w.Path));
        }

        [Fact]
        public void A_branch_absent_from_the_backup_is_reported_rather_than_written_as_null()
        {
            var target = FakeValueNode.Struct(
                ("Inner", FakeValueNode.Struct(("Value", FakeValueNode.Leaf(0)))));

            var plan = PlcLeafPlanner.Plan(target, JObject.Parse(@"{ ""Inner"": null }"), "GVL.Data");

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("no value in the backup file"));
        }

        /// <summary>
        /// The comparison writes a handful of chosen values back onto the plc through this planner.
        /// A subset is shaped like a backup with a null wherever a value was not asked for, so in
        /// this scope a null is silence rather than a hole - the opposite of what it means when a
        /// whole variable is being restored, which the test above pins down.
        /// </summary>
        [Fact]
        public void Only_the_members_the_subset_holds_are_written_and_the_rest_are_not_reported()
        {
            var target = FakeValueNode.Struct(
                ("Int1", FakeValueNode.Leaf((short) 157)),
                ("Real1", FakeValueNode.Leaf(1.5f)),
                ("Inner", FakeValueNode.Struct(("Value", FakeValueNode.Leaf(0)))));

            var plan = PlcLeafPlanner.Plan(target,
                JObject.Parse(@"{ ""Real1"": 9.5 }"),
                "GVL.Data",
                PlanScope.OnlyValuesPresent);

            var write = Assert.Single(plan.Writes);
            Assert.Equal("Real1", Describe(write));
            Assert.Equal(9.5f, write.Value);
            Assert.True(plan.IsClean);
        }

        [Fact]
        public void A_null_in_a_subset_is_a_value_that_was_not_asked_for()
        {
            var target = FakeValueNode.Struct(
                ("Inner", FakeValueNode.Struct(("Value", FakeValueNode.Leaf(0)))));

            var plan = PlcLeafPlanner.Plan(target,
                JObject.Parse(@"{ ""Inner"": null }"),
                "GVL.Data",
                PlanScope.OnlyValuesPresent);

            Assert.Empty(plan.Writes);
            Assert.True(plan.IsClean);
        }

        /// <summary>
        /// The element has to land on the position the subset put it in. Writing it one place along
        /// would put a value on the wrong axis, the wrong recipe or the wrong station, and nothing
        /// downstream would notice.
        /// </summary>
        [Fact]
        public void One_element_of_an_array_is_written_where_the_subset_left_it()
        {
            var target = FakeValueNode.Array(1,
                FakeValueNode.Leaf((short) 0),
                FakeValueNode.Leaf((short) 0),
                FakeValueNode.Leaf((short) 0));

            var plan = PlcLeafPlanner.Plan(target,
                JArray.Parse("[null, 42, null]"),
                "GVL.Values",
                PlanScope.OnlyValuesPresent);

            var write = Assert.Single(plan.Writes);
            Assert.Equal("[1]", Describe(write));
            Assert.Equal("GVL.Values[2]", write.Path);
            Assert.Equal((short) 42, write.Value);
            Assert.True(plan.IsClean);
        }

        /// <summary>
        /// A subset names exactly what was asked for, so a name in it that the plc does not have is
        /// a real problem and stays reported whichever scope is in force.
        /// </summary>
        [Fact]
        public void A_value_the_plc_no_longer_has_is_still_reported_in_a_subset()
        {
            var target = FakeValueNode.Struct(("Int1", FakeValueNode.Leaf((short) 157)));

            var plan = PlcLeafPlanner.Plan(target,
                JObject.Parse(@"{ ""Gone"": 1 }"),
                "GVL.Data",
                PlanScope.OnlyValuesPresent);

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("no longer exists on the plc"));
        }
    }
}
