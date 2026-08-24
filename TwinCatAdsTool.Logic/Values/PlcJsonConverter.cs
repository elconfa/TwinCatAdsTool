using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Values
{
    /// <summary>
    /// Collects everything that did not line up while applying a backup onto a live value tree.
    /// A restore that reports no mismatches has written every value present in the file.
    /// </summary>
    public class ValueApplyResult
    {
        private readonly List<string> mismatches = new List<string>();

        public IReadOnlyList<string> Mismatches => mismatches;
        public bool IsClean => mismatches.Count == 0;
        public int AppliedCount { get; private set; }

        internal void Applied() => AppliedCount++;
        internal void Mismatch(string message) => mismatches.Add(message);
    }

    /// <summary>
    /// Converts an in-memory plc value tree to json and back. Deliberately free of any ads
    /// dependency: everything here runs on values that have already been transferred.
    /// </summary>
    public static class PlcJsonConverter
    {
        public static JToken ToJson(IPlcValueNode node)
        {
            if (node == null)
            {
                return JValue.CreateNull();
            }

            if (node.IsArray)
            {
                var array = new JArray();
                foreach (var element in node.Elements)
                {
                    array.Add(ToJson(element));
                }

                return array;
            }

            if (node.IsStruct)
            {
                var obj = new JObject();
                foreach (var name in node.MemberNames)
                {
                    obj.Add(name, node.TryGetMember(name, out var member)
                        ? ToJson(member)
                        : JValue.CreateNull());
                }

                return obj;
            }

            return node.Value == null ? JValue.CreateNull() : new JValue(node.Value);
        }

        /// <summary>
        /// Writes <paramref name="json"/> into <paramref name="target"/>. Values present in the
        /// json but absent on the plc - and the other way round - are recorded as mismatches
        /// instead of being dropped silently.
        /// </summary>
        public static ValueApplyResult ApplyJson(IMutablePlcValueNode target, JToken json, string path)
        {
            var result = new ValueApplyResult();
            Apply(target, json, path ?? string.Empty, result);
            return result;
        }

        private static void Apply(IMutablePlcValueNode target, JToken json, string path, ValueApplyResult result)
        {
            if (target == null)
            {
                result.Mismatch($"{path}: no matching variable on the plc");
                return;
            }

            if (json == null || json.Type == JTokenType.Null)
            {
                result.Mismatch($"{path}: no value in the backup file");
                return;
            }

            if (target.IsArray)
            {
                ApplyArray(target, json, path, result);
                return;
            }

            if (target.IsStruct)
            {
                ApplyStruct(target, json, path, result);
                return;
            }

            ApplyLeaf(target, json, path, result);
        }

        private static void ApplyArray(IMutablePlcValueNode target, JToken json, string path, ValueApplyResult result)
        {
            if (!(json is JArray array))
            {
                result.Mismatch($"{path}: plc expects an array but the backup holds {DescribeToken(json)}");
                return;
            }

            if (array.Count != target.ArrayLength)
            {
                result.Mismatch($"{path}: array length differs - plc has {target.ArrayLength}, " +
                                $"backup has {array.Count}; writing the first {Math.Min(array.Count, target.ArrayLength)} elements");
            }

            var count = Math.Min(array.Count, target.ArrayLength);
            for (var i = 0; i < count; i++)
            {
                var index = target.ArrayLowerBound + i;
                var elementPath = $"{path}[{index}]";
                var element = array[i];

                if (target.TryGetMutableElement(index, out var mutableElement) &&
                    (mutableElement.IsArray || mutableElement.IsStruct))
                {
                    Apply(mutableElement, element, elementPath, result);
                    continue;
                }

                if (target.TrySetElement(index, ToManaged(element)))
                {
                    result.Applied();
                }
                else
                {
                    result.Mismatch($"{elementPath}: could not write {DescribeToken(element)}");
                }
            }
        }

        private static void ApplyStruct(IMutablePlcValueNode target, JToken json, string path, ValueApplyResult result)
        {
            if (!(json is JObject obj))
            {
                result.Mismatch($"{path}: plc expects a structure but the backup holds {DescribeToken(json)}");
                return;
            }

            var plcMembers = target.MemberNames.ToList();

            foreach (var name in plcMembers)
            {
                var memberPath = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
                var property = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    result.Mismatch($"{memberPath}: missing in the backup file, left unchanged on the plc");
                    continue;
                }

                if (target.TryGetMutableMember(name, out var member) && (member.IsArray || member.IsStruct))
                {
                    Apply(member, property.Value, memberPath, result);
                    continue;
                }

                if (target.TrySetMember(name, ToManaged(property.Value)))
                {
                    result.Applied();
                }
                else
                {
                    result.Mismatch($"{memberPath}: could not write {DescribeToken(property.Value)}");
                }
            }

            foreach (var property in obj.Properties())
            {
                if (!plcMembers.Any(m => string.Equals(m, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var memberPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    result.Mismatch($"{memberPath}: present in the backup but no longer exists on the plc");
                }
            }
        }

        private static void ApplyLeaf(IMutablePlcValueNode target, JToken json, string path, ValueApplyResult result)
        {
            if (json is JObject || json is JArray)
            {
                result.Mismatch($"{path}: plc expects a single value but the backup holds {DescribeToken(json)}");
                return;
            }

            result.Mismatch($"{path}: cannot be written on its own");
        }

        /// <summary>
        /// Turns a json token into the closest plain .net value. The final coercion to the plc
        /// type is left to the value tree implementation, which knows the declared member type.
        /// </summary>
        public static object ToManaged(JToken token)
        {
            if (token == null)
            {
                return null;
            }

            switch (token.Type)
            {
                case JTokenType.Null:
                case JTokenType.Undefined:
                    return null;
                case JTokenType.Date:
                    // Keep the offset that was written into the backup: reading this back as a
                    // plain DateTime would drop it and shift the timestamp on a machine set to
                    // another time zone.
                    var date = (token as JValue)?.Value;
                    return date is DateTime plain && plain.Kind == DateTimeKind.Unspecified
                        ? plain
                        : date ?? token.Value<DateTimeOffset>();
                case JTokenType.TimeSpan:
                    return token.Value<TimeSpan>();
                default:
                    return (token as JValue)?.Value;
            }
        }

        private static string DescribeToken(JToken token)
        {
            if (token == null)
            {
                return "nothing";
            }

            switch (token.Type)
            {
                case JTokenType.Object:
                    return "a structure";
                case JTokenType.Array:
                    return $"an array of {((JArray) token).Count} elements";
                default:
                    return $"'{Convert.ToString(((JValue) token).Value, CultureInfo.InvariantCulture)}'";
            }
        }
    }
}
