using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;
using TwinCatAdsTool.Interfaces.Values;

namespace TwinCatAdsTool.Logic.Values
{
    /// <summary>
    /// One step of the way from a persistent variable down to one of its leaves: either a member
    /// of a structure or an element of an array.
    /// </summary>
    public class PlcPathStep
    {
        private PlcPathStep(string memberName, int elementPosition)
        {
            MemberName = memberName;
            ElementPosition = elementPosition;
        }

        public static PlcPathStep Member(string name) => new PlcPathStep(name, -1);

        /// <param name="position">Position among the elements, counted from zero, not the index
        /// the plc declares. Array symbols are enumerated in the same order, so this addresses
        /// the element without having to reconstruct its declared index.</param>
        public static PlcPathStep Element(int position) => new PlcPathStep(null, position);

        public string MemberName { get; }
        public int ElementPosition { get; }
        public bool IsElement => MemberName == null;
    }

    /// <summary>A single value from the backup, and the way to reach the leaf it belongs to.</summary>
    public class PlcLeafWrite
    {
        public PlcLeafWrite(IReadOnlyList<PlcPathStep> steps, string path, object value)
        {
            Steps = steps;
            Path = path;
            Value = value;
        }

        /// <summary>Steps from the persistent variable down to the leaf. Empty for a scalar variable.</summary>
        public IReadOnlyList<PlcPathStep> Steps { get; }

        /// <summary>Full readable path of the leaf, for the report.</summary>
        public string Path { get; }

        /// <summary>The backup value, already converted to the type the plc declares for the leaf.</summary>
        public object Value { get; }
    }

    /// <summary>How much of the plc variable the json is expected to account for.</summary>
    public enum PlanScope
    {
        /// <summary>
        /// The json is a whole backup of this variable. Anything the plc declares and the json does
        /// not hold is a mismatch worth reporting: a restore that silently left members untouched is
        /// the kind of thing that is only discovered on the machine.
        /// </summary>
        WholeVariable,

        /// <summary>
        /// The json holds only some of the values on purpose, the rest being json null. Used when a
        /// comparison writes a few chosen differences back onto the plc: everything absent was
        /// deliberately not asked for, so it is skipped rather than reported.
        /// </summary>
        OnlyValuesPresent
    }

    public class PlcLeafPlan
    {
        public PlcLeafPlan(IReadOnlyList<PlcLeafWrite> writes, IReadOnlyList<string> mismatches)
        {
            Writes = writes;
            Mismatches = mismatches;
        }

        public IReadOnlyList<PlcLeafWrite> Writes { get; }

        /// <summary>Everything that did not line up between the backup file and the plc.</summary>
        public IReadOnlyList<string> Mismatches { get; }

        public bool IsClean => Mismatches.Count == 0;
    }

    /// <summary>
    /// Turns a backup entry into the list of individual leaves that have to be written on the plc.
    ///
    /// The restore used to take the opposite route: read the whole variable, change the value tree
    /// in memory and write the variable back in one go. That cannot work below the first level.
    /// The ads library hands out a *copy* of the buffer whenever a member of a structure or an
    /// element of an array is entered (DynamicValueFactory.CreateValue calls sourceData.ToArray()),
    /// so anything written into a nested branch went into a copy nobody ever sent to the plc -
    /// while the restore still reported success. Measured on a live plc on 2026-08-26: of
    /// GVL.PersVarGlobalUser1_1 the five first level members were all written and both members of
    /// the nested InInVar kept their old value.
    ///
    /// Addressing the leaves instead removes the copy from the path entirely: each value is
    /// written to the symbol that owns it. Nothing here touches ads - the current value tree is
    /// only read, to learn the declared type of every leaf - which keeps the whole conversion
    /// testable without a plc.
    /// </summary>
    public static class PlcLeafPlanner
    {
        public static PlcLeafPlan Plan(IPlcValueNode current, JToken json, string path,
            PlanScope scope = PlanScope.WholeVariable)
        {
            var writes = new List<PlcLeafWrite>();
            var mismatches = new List<string>();

            Walk(current, json, path ?? string.Empty, new List<PlcPathStep>(), writes, mismatches, scope);

            return new PlcLeafPlan(writes, mismatches);
        }

        private static void Walk(IPlcValueNode current, JToken json, string path,
            List<PlcPathStep> steps, List<PlcLeafWrite> writes, List<string> mismatches, PlanScope scope)
        {
            if (current == null)
            {
                mismatches.Add($"{path}: no matching variable on the plc");
                return;
            }

            if (json == null || json.Type == JTokenType.Null)
            {
                // A null is the absence of a request when only part of the variable is being
                // written, and a hole in the file when the whole of it should have been there.
                if (scope == PlanScope.WholeVariable)
                {
                    mismatches.Add($"{path}: no value in the backup file");
                }

                return;
            }

            if (current.IsArray)
            {
                WalkArray(current, json, path, steps, writes, mismatches, scope);
                return;
            }

            if (current.IsStruct)
            {
                WalkStruct(current, json, path, steps, writes, mismatches, scope);
                return;
            }

            WalkLeaf(current, json, path, steps, writes, mismatches);
        }

        private static void WalkArray(IPlcValueNode current, JToken json, string path,
            List<PlcPathStep> steps, List<PlcLeafWrite> writes, List<string> mismatches, PlanScope scope)
        {
            if (!(json is JArray array))
            {
                mismatches.Add($"{path}: plc expects an array but the backup holds {Describe(json)}");
                return;
            }

            var elements = current.Elements.ToList();

            if (array.Count != elements.Count)
            {
                mismatches.Add($"{path}: array length differs - plc has {elements.Count}, " +
                               $"backup has {array.Count}; writing the first {Math.Min(array.Count, elements.Count)} elements");
            }

            var count = Math.Min(array.Count, elements.Count);
            for (var i = 0; i < count; i++)
            {
                var declaredIndex = current.ArrayLowerBound + i;

                steps.Add(PlcPathStep.Element(i));
                Walk(elements[i], array[i], $"{path}[{declaredIndex}]", steps, writes, mismatches, scope);
                steps.RemoveAt(steps.Count - 1);
            }
        }

        private static void WalkStruct(IPlcValueNode current, JToken json, string path,
            List<PlcPathStep> steps, List<PlcLeafWrite> writes, List<string> mismatches, PlanScope scope)
        {
            if (!(json is JObject obj))
            {
                mismatches.Add($"{path}: plc expects a structure but the backup holds {Describe(json)}");
                return;
            }

            var plcMembers = current.MemberNames.ToList();

            foreach (var name in plcMembers)
            {
                var memberPath = string.IsNullOrEmpty(path) ? name : $"{path}.{name}";
                var property = obj.Properties()
                    .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

                if (property == null)
                {
                    if (scope == PlanScope.WholeVariable)
                    {
                        mismatches.Add($"{memberPath}: missing in the backup file, left unchanged on the plc");
                    }

                    continue;
                }

                if (!current.TryGetMember(name, out var member))
                {
                    mismatches.Add($"{memberPath}: could not be read from the plc");
                    continue;
                }

                steps.Add(PlcPathStep.Member(name));
                Walk(member, property.Value, memberPath, steps, writes, mismatches, scope);
                steps.RemoveAt(steps.Count - 1);
            }

            foreach (var property in obj.Properties())
            {
                if (!plcMembers.Any(m => string.Equals(m, property.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    var memberPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";
                    mismatches.Add($"{memberPath}: present in the backup but no longer exists on the plc");
                }
            }
        }

        private static void WalkLeaf(IPlcValueNode current, JToken json, string path,
            List<PlcPathStep> steps, List<PlcLeafWrite> writes, List<string> mismatches)
        {
            if (json is JObject || json is JArray)
            {
                mismatches.Add($"{path}: plc expects a single value but the backup holds {Describe(json)}");
                return;
            }

            var managed = PlcJsonConverter.ToManaged(json);

            // Two conversions: the first fits the value to the managed type the plc member holds,
            // the second wraps it again when that member is a PlcOpen type - the first template
            // is unwrapped, so a DT is compared against a plain DateTime.
            if (!ValueCoercion.TryCoerce(managed, current.Value, out var coerced) ||
                !ValueCoercion.TryCoerce(coerced, current.NativeValue, out var wrapped))
            {
                mismatches.Add($"{path}: backup value {Describe(json)} does not fit the plc type");
                return;
            }

            writes.Add(new PlcLeafWrite(steps.ToList(), path, wrapped));
        }

        private static string Describe(JToken token)
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
