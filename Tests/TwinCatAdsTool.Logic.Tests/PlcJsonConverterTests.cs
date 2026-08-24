using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Logic.Values;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    public class PlcJsonConverterToJsonTests
    {
        [Fact]
        public void Writes_a_leaf()
        {
            var json = PlcJsonConverter.ToJson(FakeValueNode.Leaf(42));

            Assert.Equal(JTokenType.Integer, json.Type);
            Assert.Equal(42, json.Value<int>());
        }

        [Fact]
        public void Writes_a_structure_keeping_the_declaration_order()
        {
            var node = FakeValueNode.Struct(
                ("Enabled", FakeValueNode.Leaf(true)),
                ("Position", FakeValueNode.Leaf(12.5)),
                ("Name", FakeValueNode.Leaf("axis 1")));

            var json = (JObject) PlcJsonConverter.ToJson(node);

            Assert.Equal(new[] {"Enabled", "Position", "Name"}, json.Properties().Select(p => p.Name));
            Assert.True(json["Enabled"].Value<bool>());
            Assert.Equal(12.5, json["Position"].Value<double>());
            Assert.Equal("axis 1", json["Name"].Value<string>());
        }

        [Fact]
        public void Writes_an_array_of_structures()
        {
            var node = FakeValueNode.Array(1,
                FakeValueNode.Struct(("Id", FakeValueNode.Leaf(1)), ("Value", FakeValueNode.Leaf(10))),
                FakeValueNode.Struct(("Id", FakeValueNode.Leaf(2)), ("Value", FakeValueNode.Leaf(20))));

            var json = (JArray) PlcJsonConverter.ToJson(node);

            Assert.Equal(2, json.Count);
            Assert.Equal(1, json[0]["Id"].Value<int>());
            Assert.Equal(20, json[1]["Value"].Value<int>());
        }

        [Fact]
        public void Writes_nested_arrays_inside_structures()
        {
            var node = FakeValueNode.Struct(
                ("Recipe", FakeValueNode.Struct(
                    ("Steps", FakeValueNode.Array(0,
                        FakeValueNode.Leaf(5),
                        FakeValueNode.Leaf(6))))));

            var json = PlcJsonConverter.ToJson(node);

            Assert.Equal(6, json["Recipe"]["Steps"][1].Value<int>());
        }

        [Fact]
        public void Writes_null_for_a_missing_member()
        {
            var json = PlcJsonConverter.ToJson(FakeValueNode.Struct(("Ghost", null)));

            Assert.Equal(JTokenType.Null, json["Ghost"].Type);
        }
    }

    public class PlcJsonConverterToManagedTests
    {
        [Fact]
        public void Reads_an_integer_as_it_was_written()
        {
            Assert.Equal(42L, PlcJsonConverter.ToManaged(new JValue(42)));
        }

        [Fact]
        public void Reads_null_as_null()
        {
            Assert.Null(PlcJsonConverter.ToManaged(JValue.CreateNull()));
            Assert.Null(PlcJsonConverter.ToManaged(null));
        }

        /// <summary>
        /// A timestamp stored with an explicit offset has to keep that offset, otherwise
        /// restoring a backup on a machine in another time zone shifts every date.
        /// </summary>
        [Fact]
        public void Keeps_the_offset_of_a_timestamp()
        {
            var json = JObject.Parse(@"{ ""When"": ""2026-08-24T10:30:00+00:00"" }",
                new JsonLoadSettings());

            var value = PlcJsonConverter.ToManaged(json["When"]);

            var instant = value is DateTimeOffset offset ? offset.UtcDateTime : ((DateTime) value).ToUniversalTime();
            Assert.Equal(new DateTime(2026, 8, 24, 10, 30, 0, DateTimeKind.Utc), instant);
        }
    }

    public class PlcJsonConverterApplyTests
    {
        [Fact]
        public void Applies_every_member_of_a_structure()
        {
            var target = FakeValueNode.Struct(
                ("Enabled", FakeValueNode.Leaf(false)),
                ("Count", FakeValueNode.Leaf(0)));

            var json = JObject.Parse(@"{ ""Enabled"": true, ""Count"": 7 }");

            var result = PlcJsonConverter.ApplyJson(target, json, "GVL.Data");

            Assert.True(result.IsClean);
            Assert.Equal(2, result.AppliedCount);
            Assert.Equal(true, target.MemberValue("Enabled"));
            Assert.Equal(7, target.MemberValue("Count"));
        }

        [Fact]
        public void Reports_a_member_missing_from_the_backup_instead_of_ignoring_it()
        {
            var target = FakeValueNode.Struct(
                ("Kept", FakeValueNode.Leaf(1)),
                ("Added", FakeValueNode.Leaf(0)));

            var result = PlcJsonConverter.ApplyJson(target, JObject.Parse(@"{ ""Kept"": 5 }"), "GVL.Data");

            Assert.False(result.IsClean);
            Assert.Equal(1, result.AppliedCount);
            Assert.Contains(result.Mismatches, m => m.Contains("GVL.Data.Added") && m.Contains("missing in the backup"));
        }

        [Fact]
        public void Reports_a_backup_entry_that_no_longer_exists_on_the_plc()
        {
            var target = FakeValueNode.Struct(("Kept", FakeValueNode.Leaf(1)));

            var result = PlcJsonConverter.ApplyJson(target,
                JObject.Parse(@"{ ""Kept"": 5, ""Removed"": 9 }"), "GVL.Data");

            Assert.False(result.IsClean);
            Assert.Contains(result.Mismatches, m => m.Contains("GVL.Data.Removed") && m.Contains("no longer exists"));
        }

        [Fact]
        public void Applies_an_array_honouring_the_plc_lower_bound()
        {
            var target = FakeValueNode.Array(1,
                FakeValueNode.Leaf(0),
                FakeValueNode.Leaf(0),
                FakeValueNode.Leaf(0));

            var result = PlcJsonConverter.ApplyJson(target, JArray.Parse("[10, 20, 30]"), "GVL.Values");

            Assert.True(result.IsClean);
            Assert.Equal(10, target.ElementValue(1));
            Assert.Equal(30, target.ElementValue(3));
        }

        [Fact]
        public void Reports_an_array_that_grew_on_the_plc_and_writes_what_it_can()
        {
            var target = FakeValueNode.Array(1,
                FakeValueNode.Leaf(0),
                FakeValueNode.Leaf(0),
                FakeValueNode.Leaf(0));

            var result = PlcJsonConverter.ApplyJson(target, JArray.Parse("[10, 20]"), "GVL.Values");

            Assert.False(result.IsClean);
            Assert.Contains(result.Mismatches, m => m.Contains("array length differs") && m.Contains("plc has 3"));
            Assert.Equal(10, target.ElementValue(1));
            Assert.Equal(0, target.ElementValue(3));
        }

        [Fact]
        public void Reports_an_array_that_shrank_on_the_plc()
        {
            var target = FakeValueNode.Array(1, FakeValueNode.Leaf(0), FakeValueNode.Leaf(0));

            var result = PlcJsonConverter.ApplyJson(target, JArray.Parse("[1, 2, 3, 4]"), "GVL.Values");

            Assert.False(result.IsClean);
            Assert.Contains(result.Mismatches, m => m.Contains("backup has 4"));
            Assert.Equal(2, target.ElementValue(2));
        }

        [Fact]
        public void Applies_an_array_of_structures()
        {
            var target = FakeValueNode.Array(1,
                FakeValueNode.Struct(("Id", FakeValueNode.Leaf(0)), ("Value", FakeValueNode.Leaf(0))),
                FakeValueNode.Struct(("Id", FakeValueNode.Leaf(0)), ("Value", FakeValueNode.Leaf(0))));

            var json = JArray.Parse(@"[ { ""Id"": 1, ""Value"": 11 }, { ""Id"": 2, ""Value"": 22 } ]");

            var result = PlcJsonConverter.ApplyJson(target, json, "GVL.Items");

            Assert.True(result.IsClean);
            Assert.Equal(4, result.AppliedCount);
        }

        [Fact]
        public void Reports_a_structure_where_the_backup_holds_a_scalar()
        {
            var target = FakeValueNode.Struct(("Value", FakeValueNode.Leaf(0)));

            var result = PlcJsonConverter.ApplyJson(target, new JValue(5), "GVL.Data");

            Assert.False(result.IsClean);
            Assert.Contains(result.Mismatches, m => m.Contains("expects a structure"));
        }

        [Fact]
        public void Reports_an_array_where_the_backup_holds_a_structure()
        {
            var target = FakeValueNode.Array(0, FakeValueNode.Leaf(0));

            var result = PlcJsonConverter.ApplyJson(target, JObject.Parse(@"{ ""a"": 1 }"), "GVL.Values");

            Assert.False(result.IsClean);
            Assert.Contains(result.Mismatches, m => m.Contains("expects an array"));
        }

        [Fact]
        public void Matches_member_names_case_insensitively()
        {
            var target = FakeValueNode.Struct(("Enabled", FakeValueNode.Leaf(false)));

            var result = PlcJsonConverter.ApplyJson(target, JObject.Parse(@"{ ""enabled"": true }"), "GVL.Data");

            Assert.True(result.IsClean);
            Assert.Equal(true, target.MemberValue("Enabled"));
        }

        [Fact]
        public void A_round_trip_leaves_the_values_unchanged()
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

            var result = PlcJsonConverter.ApplyJson(target, json, "GVL.Data");

            Assert.True(result.IsClean);
            Assert.Equal(json.ToString(), PlcJsonConverter.ToJson(target).ToString());
        }
    }
}
