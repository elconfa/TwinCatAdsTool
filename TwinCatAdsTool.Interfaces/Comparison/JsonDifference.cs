using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TwinCatAdsTool.Interfaces.Comparison
{
    /// <summary>How one leaf of a backup relates to the same leaf of the other one.</summary>
    public enum JsonDifferenceKind
    {
        /// <summary>Both sides hold this leaf and hold the same reading.</summary>
        Equal,

        /// <summary>Both sides hold this leaf and the readings differ.</summary>
        Changed,

        /// <summary>Only the left side has this leaf at all.</summary>
        OnlyOnLeft,

        /// <summary>Only the right side has this leaf at all.</summary>
        OnlyOnRight
    }

    /// <summary>One leaf of two backups, named by the path a plc programmer would use.</summary>
    public class JsonDifferenceEntry
    {
        public JsonDifferenceEntry(string path, string left, string right)
        {
            Path = path;
            Left = left;
            Right = right;
            Kind = Classify(left, right);
        }

        private static JsonDifferenceKind Classify(string left, string right)
        {
            if (left == null)
            {
                return right == null ? JsonDifferenceKind.Equal : JsonDifferenceKind.OnlyOnRight;
            }

            if (right == null)
            {
                return JsonDifferenceKind.OnlyOnLeft;
            }

            return string.Equals(left, right, StringComparison.Ordinal)
                ? JsonDifferenceKind.Equal
                : JsonDifferenceKind.Changed;
        }

        public string Path { get; }

        /// <summary>The value on the left, or null when the left does not have this leaf at all.</summary>
        public string Left { get; }

        public string Right { get; }

        public JsonDifferenceKind Kind { get; }

        public bool IsDifferent => Kind != JsonDifferenceKind.Equal;

        /// <summary>
        /// Whether this leaf can be carried from one side to the other. Only a leaf both sides
        /// hold can: a variable that exists on one side only would have to be created on the
        /// plc, and ads writes values, it does not declare symbols.
        /// </summary>
        public bool IsMergeable => Kind == JsonDifferenceKind.Changed;

        public override string ToString()
        {
            if (Left == null)
            {
                return $"{Path}: only on the right, {Right}";
            }

            return Right == null
                ? $"{Path}: only on the left, {Left}"
                : $"{Path}: {Left} -> {Right}";
        }
    }

    /// <summary>
    /// Compares two backups leaf by leaf rather than as text. A textual diff of two json files
    /// answers a different question - whether the files were written the same way - and reports
    /// formatting, key order and whitespace as though they were changes to the plant.
    ///
    /// Leaf by leaf is also what makes a comparison act rather than only report: every entry names
    /// a single value on the plc, which is exactly what a write needs to be addressed to.
    /// </summary>
    public static class JsonDifference
    {
        /// <summary>Only the leaves that disagree.</summary>
        public static IReadOnlyList<JsonDifferenceEntry> Find(JToken left, JToken right)
        {
            var found = new List<JsonDifferenceEntry>();
            Walk(string.Empty, left, right, found, false);
            return found;
        }

        /// <summary>
        /// Every leaf of either side, agreeing or not. Costs one entry per value rather than one
        /// per difference, so it is for the comparison window - which offers to show the whole
        /// backup - and not for the command line, which only ever reports what differs.
        /// </summary>
        public static IReadOnlyList<JsonDifferenceEntry> Compare(JToken left, JToken right)
        {
            var found = new List<JsonDifferenceEntry>();
            Walk(string.Empty, left, right, found, true);
            return found;
        }

        private static void Walk(string path, JToken left, JToken right,
            List<JsonDifferenceEntry> found, bool includeEqual)
        {
            if (left is JObject leftObject && right is JObject rightObject)
            {
                // The union of both sides, in the order the left declares them, so a variable that
                // exists on one side only is reported rather than quietly ignored.
                var names = leftObject.Properties().Select(p => p.Name)
                    .Concat(rightObject.Properties().Select(p => p.Name))
                    .Distinct(StringComparer.Ordinal);

                foreach (var name in names)
                {
                    Walk(Join(path, name), leftObject[name], rightObject[name], found, includeEqual);
                }

                return;
            }

            if (left is JArray leftArray && right is JArray rightArray)
            {
                for (var i = 0; i < Math.Max(leftArray.Count, rightArray.Count); i++)
                {
                    Walk($"{path}[{i}]",
                        i < leftArray.Count ? leftArray[i] : null,
                        i < rightArray.Count ? rightArray[i] : null,
                        found, includeEqual);
                }

                return;
            }

            var leftText = Describe(left);
            var rightText = Describe(right);

            if (includeEqual || !string.Equals(leftText, rightText, StringComparison.Ordinal))
            {
                found.Add(new JsonDifferenceEntry(path, leftText, rightText));
            }
        }

        private static string Join(string path, string name) => path.Length == 0 ? name : $"{path}.{name}";

        /// <summary>
        /// A leaf as text. Numbers are compared this way on purpose: a value that came back from the
        /// plc as 1.0 and one written as 1 are the same reading, and json alone cannot say which
        /// width the plc used.
        /// </summary>
        private static string Describe(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (token is JValue value && value.Value is IFormattable formattable &&
                (token.Type == JTokenType.Float || token.Type == JTokenType.Integer))
            {
                return formattable.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }

            return token.Type == JTokenType.Object || token.Type == JTokenType.Array
                ? token.ToString(Newtonsoft.Json.Formatting.None)
                : token.ToString();
        }
    }
}
