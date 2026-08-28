using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Interfaces.Comparison;
using TwinCatAdsTool.Logic.Values;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// The whole way a difference travels from the comparison window onto the plc: it is spotted by
    /// the comparison, cut out of the backup it came from, and turned into writes addressed to the
    /// symbols that own those values. Each of the three steps is tested on its own elsewhere; what
    /// is checked here is that they agree with each other, because they are joined by a path written
    /// as text and by array positions, and either could drift without any one of them being wrong.
    ///
    /// Nothing here touches ads. What the plc would do with the writes is the one part that a test
    /// cannot answer.
    /// </summary>
    public class MergeSelectedValuesTests
    {
        private static JObject Json(string text) => JObject.Parse(text.Replace('\'', '"'));

        private static string Describe(PlcLeafWrite write)
            => string.Join("/", write.Steps.Select(s => s.IsElement ? $"[{s.ElementPosition}]" : s.MemberName));

        /// <summary>
        /// One value picked out of a structure. Everything the plc holds beside it has to come out
        /// of the planner with no write against it at all - not a write of the value it already
        /// has, which would be the same thing to the plant but not to the report.
        /// </summary>
        [Fact]
        public void One_picked_value_becomes_one_write_and_its_neighbours_become_none()
        {
            var onThePlc = Json("{'GVL':{'Speed':10,'Offset':3,'Name':'a'}}");
            var inTheFile = Json("{'GVL':{'Speed':10,'Offset':7,'Name':'b'}}");

            var differences = JsonDifference.Find(onThePlc, inTheFile);
            Assert.Equal(new[] {"GVL.Offset", "GVL.Name"}, differences.Select(d => d.Path));

            // Only the offset is picked; the name is left as the plc has it.
            var subset = JsonSubset.Prune(inTheFile, new[] {"GVL.Offset"});

            var plcValues = FakeValueNode.Struct(
                ("Speed", FakeValueNode.Leaf((short) 10)),
                ("Offset", FakeValueNode.Leaf((short) 3)),
                ("Name", FakeValueNode.Leaf("a")));

            var plan = PlcLeafPlanner.Plan(plcValues, subset["GVL"], "GVL", PlanScope.OnlyValuesPresent);

            var write = Assert.Single(plan.Writes);
            Assert.Equal("Offset", Describe(write));
            Assert.Equal("GVL.Offset", write.Path);
            Assert.Equal((short) 7, write.Value);
            Assert.True(plan.IsClean);
        }

        /// <summary>
        /// The trap this test exists for: a backup writes an array as a json array and does not
        /// record the index the plc declares it from, so the comparison names the third element
        /// [2] while the plc calls it [3]. Everything downstream addresses elements by position, so
        /// the value has to land on the third element and the path shown for it has to say [3].
        /// </summary>
        [Fact]
        public void An_element_of_an_array_that_the_plc_counts_from_one_still_lands_on_the_right_one()
        {
            var onThePlc = Json("{'GVL':{'Recipe':[100,200,300,400]}}");
            var inTheFile = Json("{'GVL':{'Recipe':[100,200,999,400]}}");

            var difference = Assert.Single(JsonDifference.Find(onThePlc, inTheFile));
            Assert.Equal("GVL.Recipe[2]", difference.Path);

            var subset = JsonSubset.Prune(inTheFile, new[] {difference.Path});

            var plcValues = FakeValueNode.Array(1,
                FakeValueNode.Leaf((short) 100),
                FakeValueNode.Leaf((short) 200),
                FakeValueNode.Leaf((short) 300),
                FakeValueNode.Leaf((short) 400));

            var plan = PlcLeafPlanner.Plan(plcValues, subset["GVL"]["Recipe"], "GVL.Recipe",
                PlanScope.OnlyValuesPresent);

            var write = Assert.Single(plan.Writes);
            Assert.Equal("[2]", Describe(write));
            Assert.Equal((short) 999, write.Value);

            // The plc counts this array from one, so the third element is the one it calls [3].
            Assert.Equal("GVL.Recipe[3]", write.Path);
            Assert.True(plan.IsClean);
        }

        [Fact]
        public void A_value_deep_inside_an_array_of_structures_reaches_it()
        {
            var onThePlc = Json("{'GVL':{'Axes':[{'Home':0.0,'Max':10.0},{'Home':0.0,'Max':10.0}]}}");
            var inTheFile = Json("{'GVL':{'Axes':[{'Home':0.0,'Max':10.0},{'Home':2.5,'Max':10.0}]}}");

            var difference = Assert.Single(JsonDifference.Find(onThePlc, inTheFile));
            Assert.Equal("GVL.Axes[1].Home", difference.Path);

            var subset = JsonSubset.Prune(inTheFile, new[] {difference.Path});

            var axis = new[] {0, 1}.Select(_ => FakeValueNode.Struct(
                ("Home", FakeValueNode.Leaf(0.0f)),
                ("Max", FakeValueNode.Leaf(10.0f)))).ToArray();

            var plan = PlcLeafPlanner.Plan(FakeValueNode.Struct(("Axes", FakeValueNode.Array(0, axis))),
                subset["GVL"], "GVL", PlanScope.OnlyValuesPresent);

            var write = Assert.Single(plan.Writes);
            Assert.Equal("Axes/[1]/Home", Describe(write));
            Assert.Equal("GVL.Axes[1].Home", write.Path);
            Assert.Equal(2.5f, write.Value);
            Assert.True(plan.IsClean);
        }

        /// <summary>
        /// Several picks at once, from different places, still produce exactly those writes. This is
        /// what "copy all changes" turns into, and the failure worth catching is a subset in which
        /// two picks under the same parent overwrite one another.
        /// </summary>
        [Fact]
        public void Several_picks_produce_exactly_those_writes()
        {
            var onThePlc = Json("{'G':{'a':1,'b':2,'c':{'d':3,'e':4}}}");
            var inTheFile = Json("{'G':{'a':9,'b':2,'c':{'d':8,'e':7}}}");

            var differences = JsonDifference.Find(onThePlc, inTheFile);
            var subset = JsonSubset.Prune(inTheFile, differences.Select(d => d.Path));

            var plcValues = FakeValueNode.Struct(
                ("a", FakeValueNode.Leaf((short) 1)),
                ("b", FakeValueNode.Leaf((short) 2)),
                ("c", FakeValueNode.Struct(
                    ("d", FakeValueNode.Leaf((short) 3)),
                    ("e", FakeValueNode.Leaf((short) 4)))));

            var plan = PlcLeafPlanner.Plan(plcValues, subset["G"], "G", PlanScope.OnlyValuesPresent);

            Assert.Equal(new[] {"a", "c/d", "c/e"}, plan.Writes.Select(Describe));
            Assert.Equal(new object[] {(short) 9, (short) 8, (short) 7}, plan.Writes.Select(w => w.Value));
            Assert.True(plan.IsClean);
        }

        /// <summary>
        /// A value only one side has cannot be carried across, and the window does not offer it. If
        /// one were forced through anyway the planner would say so rather than write something else,
        /// which is what this pins down.
        /// </summary>
        [Fact]
        public void A_value_the_plc_does_not_have_is_reported_rather_than_written_somewhere_else()
        {
            var inTheFile = Json("{'G':{'Gone':5}}");
            var subset = JsonSubset.Prune(inTheFile, new[] {"G.Gone"});

            var plan = PlcLeafPlanner.Plan(FakeValueNode.Struct(("Here", FakeValueNode.Leaf((short) 1))),
                subset["G"], "G", PlanScope.OnlyValuesPresent);

            Assert.Empty(plan.Writes);
            Assert.Contains(plan.Mismatches, m => m.Contains("no longer exists on the plc"));
        }
    }
}
