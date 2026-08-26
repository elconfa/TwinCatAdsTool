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
}
