using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Logic.Values;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// An array of a primitive plc type is handed back by the ads library as a plain managed
    /// array rather than as a DynamicValue. These cases need no plc to exercise.
    ///
    /// Regression: such arrays used to be treated as leaves, which made the backup fail with
    /// "Could not determine JSON object type for type System.Boolean[]" and dropped the
    /// variable from the file.
    /// </summary>
    public class DynamicValueNodeNativeArrayTests
    {
        [Fact]
        public void An_array_of_bool_is_an_array_not_a_leaf()
        {
            var node = new DynamicValueNode(new[] {true, false, true});

            Assert.True(node.IsArray);
            Assert.False(node.IsStruct);
            Assert.Equal(3, node.Elements.Count());
        }

        [Fact]
        public void An_array_of_bool_converts_to_a_json_array()
        {
            var json = PlcJsonConverter.ToJson(new DynamicValueNode(new[] {true, false, true}));

            Assert.IsType<JArray>(json);
            Assert.Equal(3, ((JArray) json).Count);
            Assert.True(json[0].Value<bool>());
            Assert.False(json[1].Value<bool>());
        }

        [Fact]
        public void An_array_of_byte_converts_to_a_json_array()
        {
            var json = PlcJsonConverter.ToJson(new DynamicValueNode(new byte[] {1, 2, 250}));

            Assert.IsType<JArray>(json);
            Assert.Equal(250, json[2].Value<int>());
        }

        public static TheoryData<object> PrimitiveArrays => new TheoryData<object>
        {
            new short[] {1, 2},
            new[] {1.5f, 2.5f},
            new[] {1.5d, 2.5d},
            new[] {"a", "b"},
            new[] {1, 2},
            new sbyte[] {1, 2}
        };

        [Theory]
        [MemberData(nameof(PrimitiveArrays))]
        public void Arrays_of_the_other_primitive_types_convert_too(object array)
        {
            var json = PlcJsonConverter.ToJson(new DynamicValueNode(array));

            Assert.IsType<JArray>(json);
            Assert.Equal(2, ((JArray) json).Count);
        }

        [Fact]
        public void A_single_bool_is_still_a_leaf()
        {
            var node = new DynamicValueNode(true);

            Assert.False(node.IsArray);
            Assert.False(node.IsStruct);
            Assert.Equal(JTokenType.Boolean, PlcJsonConverter.ToJson(node).Type);
        }

        [Fact]
        public void A_string_is_a_leaf_and_not_an_array_of_characters()
        {
            var node = new DynamicValueNode("hello");

            Assert.False(node.IsArray);
            Assert.Equal("hello", PlcJsonConverter.ToJson(node).Value<string>());
        }

        [Fact]
        public void Planning_a_restore_of_an_array_of_bool_reaches_every_element()
        {
            var plan = PlcLeafPlanner.Plan(new DynamicValueNode(new[] {false, false, false}),
                JArray.Parse("[true, false, true]"), "PersistentVars.Alarms");

            Assert.True(plan.IsClean);
            Assert.Equal(new object[] {true, false, true}, plan.Writes.Select(w => w.Value));
            Assert.Equal("PersistentVars.Alarms[2]", plan.Writes.Last().Path);
        }

        [Fact]
        public void Planning_a_restore_of_an_array_of_byte_narrows_the_json_numbers()
        {
            var plan = PlcLeafPlanner.Plan(new DynamicValueNode(new byte[] {0, 0}),
                JArray.Parse("[7, 200]"), "PersistentVars.Positions");

            Assert.True(plan.IsClean);
            Assert.Equal(new object[] {(byte) 7, (byte) 200}, plan.Writes.Select(w => w.Value));
        }

        [Fact]
        public void A_value_that_does_not_fit_the_element_type_is_reported()
        {
            var plan = PlcLeafPlanner.Plan(new DynamicValueNode(new byte[] {0, 0}),
                JArray.Parse("[7, 5000]"), "PersistentVars.Positions");

            Assert.False(plan.IsClean);
            Assert.Contains(plan.Mismatches, m => m.Contains("PersistentVars.Positions[1]"));
            Assert.Equal((byte) 7, Assert.Single(plan.Writes).Value);
        }

        [Fact]
        public void An_array_that_changed_length_is_reported()
        {
            var plan = PlcLeafPlanner.Plan(new DynamicValueNode(new[] {false, false, false}),
                JArray.Parse("[true, true]"), "PersistentVars.Alarms");

            Assert.False(plan.IsClean);
            Assert.Contains(plan.Mismatches, m => m.Contains("array length differs"));
            Assert.Equal(2, plan.Writes.Count);
        }

        /// <summary>
        /// Safety net: a value JValue cannot type goes through the general converter instead of
        /// failing and taking the whole variable out of the backup.
        /// </summary>
        [Fact]
        public void A_value_JValue_cannot_type_still_converts()
        {
            var json = PlcJsonConverter.ToJson(new FakeLeaf(new System.Collections.Generic.List<int> {1, 2, 3}));

            Assert.IsType<JArray>(json);
            Assert.Equal(3, ((JArray) json).Count);
        }

        private class FakeLeaf : TwinCatAdsTool.Interfaces.Values.IPlcValueNode
        {
            public FakeLeaf(object value) => Value = value;
            public bool IsArray => false;
            public bool IsStruct => false;
            public System.Collections.Generic.IEnumerable<TwinCatAdsTool.Interfaces.Values.IPlcValueNode> Elements
                => System.Array.Empty<TwinCatAdsTool.Interfaces.Values.IPlcValueNode>();
            public System.Collections.Generic.IEnumerable<string> MemberNames => System.Array.Empty<string>();
            public bool TryGetMember(string name, out TwinCatAdsTool.Interfaces.Values.IPlcValueNode member)
            {
                member = null;
                return false;
            }
            public object Value { get; }
            public object NativeValue => Value;
            public int ArrayLowerBound => 0;
        }

        [Fact]
        public void A_round_trip_through_json_gives_the_values_back_unchanged()
        {
            var source = new byte[] {1, 42, 255};
            var json = PlcJsonConverter.ToJson(new DynamicValueNode(source));

            var plan = PlcLeafPlanner.Plan(new DynamicValueNode(new byte[] {0, 0, 0}), json, "PersistentVars.Data");

            Assert.True(plan.IsClean);
            Assert.Equal(source.Cast<object>(), plan.Writes.Select(w => w.Value));
        }
    }
}
