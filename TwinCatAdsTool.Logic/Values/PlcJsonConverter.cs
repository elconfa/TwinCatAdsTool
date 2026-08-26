using System;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Values
{
    /// <summary>
    /// Turns an in-memory plc value tree into the json of a backup file, and json values back
    /// into plain .net ones. Deliberately free of any ads dependency: everything here runs on
    /// values that have already been transferred.
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

            return ToJsonValue(node.Value);
        }

        /// <summary>
        /// JValue only knows the primitive types. Anything else the ads library hands back -
        /// a collection, a wrapper type - goes through the general converter rather than failing
        /// and taking the whole variable out of the backup.
        /// </summary>
        private static JToken ToJsonValue(object value)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            try
            {
                return new JValue(value);
            }
            catch (ArgumentException)
            {
                return JToken.FromObject(value);
            }
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
    }
}
