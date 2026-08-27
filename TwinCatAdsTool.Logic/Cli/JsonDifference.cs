using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace TwinCatAdsTool.Logic.Cli
{
    /// <summary>One place where two backups disagree, named by the path a plc programmer would use.</summary>
    public class JsonDifferenceEntry
    {
        public JsonDifferenceEntry(string path, string left, string right)
        {
            Path = path;
            Left = left;
            Right = right;
        }

        public string Path { get; }

        /// <summary>The value on the left, or null when the left does not have this leaf at all.</summary>
        public string Left { get; }

        public string Right { get; }

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
    /// </summary>
    public static class JsonDifference
    {
        public static IReadOnlyList<JsonDifferenceEntry> Find(JToken left, JToken right)
        {
            var found = new List<JsonDifferenceEntry>();
            Walk(string.Empty, left, right, found);
            return found;
        }

        private static void Walk(string path, JToken left, JToken right, List<JsonDifferenceEntry> found)
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
                    Walk(Join(path, name), leftObject[name], rightObject[name], found);
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
                        found);
                }

                return;
            }

            var leftText = Describe(left);
            var rightText = Describe(right);

            if (!string.Equals(leftText, rightText, StringComparison.Ordinal))
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
