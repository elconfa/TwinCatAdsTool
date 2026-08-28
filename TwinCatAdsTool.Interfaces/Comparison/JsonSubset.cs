using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace TwinCatAdsTool.Interfaces.Comparison
{
    /// <summary>
    /// Cuts a backup down to a handful of its leaves, keeping the shape around them.
    ///
    /// This is what lets a few differences be written onto the plc through the same path a whole
    /// restore takes. The result is shaped exactly like a backup - same nesting, arrays of the same
    /// length - but everything that was not asked for is left as json null, which the restore reads
    /// as "not requested" rather than as a value.
    ///
    /// Array positions are kept as they are, which matters more than it looks: a backup writes an
    /// array as a json array and does not record the index the plc declares it from, so position
    /// zero is the first element whether the plc calls it [0] or [1]. Everything downstream - the
    /// planner, and the walk from a variable down to a leaf - addresses elements by position for
    /// the same reason, so the two agree end to end.
    /// </summary>
    public static class JsonSubset
    {
        public static JObject Prune(JObject source, IEnumerable<string> paths)
        {
            var pruned = new JObject();

            if (source == null || paths == null)
            {
                return pruned;
            }

            foreach (var path in paths)
            {
                Copy(source, pruned, path);
            }

            return pruned;
        }

        private static void Copy(JObject source, JObject target, string path)
        {
            var steps = Parse(path);
            if (steps.Count == 0)
            {
                return;
            }

            // The whole path is followed through the backup before anything is built, so a path
            // that turns out to lead nowhere leaves no half made objects behind. A root created
            // for a value that is not there would be a variable the restore then reads, plans and
            // writes nothing of.
            var chain = new JToken[steps.Count];
            JToken from = source;

            for (var i = 0; i < steps.Count; i++)
            {
                from = Descend(from, steps[i]);

                // The path names something this backup does not hold. Nothing to carry over, and
                // nothing to complain about either - the caller is asking for a subset.
                if (from == null)
                {
                    return;
                }

                chain[i] = from;
            }

            JToken to = target;

            for (var i = 0; i < steps.Count - 1; i++)
            {
                to = Mirror(to, steps[i], chain[i]);
                if (to == null)
                {
                    return;
                }
            }

            Place(to, steps[steps.Count - 1], chain[steps.Count - 1].DeepClone());
        }

        private static JToken Descend(JToken from, Step step)
        {
            if (step.IsElement)
            {
                return from is JArray array && step.Index >= 0 && step.Index < array.Count
                    ? array[step.Index]
                    : null;
            }

            return (from as JObject)?.Property(step.Name, StringComparison.OrdinalIgnoreCase)?.Value;
        }

        private static void Place(JToken container, Step step, JToken value)
        {
            if (step.IsElement)
            {
                if (container is JArray array && step.Index >= 0 && step.Index < array.Count)
                {
                    array[step.Index] = value;
                }

                return;
            }

            if (container is JObject obj)
            {
                obj[step.Name] = value;
            }
        }

        /// <summary>
        /// The child of the pruned tree that stands for the same node of the source, created to
        /// match its shape if it is not there yet. An array is created at full length and filled
        /// with nulls so the positions of the elements that were asked for still line up with the
        /// ones the plc has.
        /// </summary>
        private static JToken Mirror(JToken container, Step step, JToken source)
        {
            var existing = step.IsElement
                ? (container is JArray array && step.Index >= 0 && step.Index < array.Count ? array[step.Index] : null)
                : (container as JObject)?.Property(step.Name, StringComparison.OrdinalIgnoreCase)?.Value;

            if (existing != null && existing.Type != JTokenType.Null)
            {
                return existing;
            }

            JToken created;

            if (source is JObject)
            {
                created = new JObject();
            }
            else if (source is JArray sourceArray)
            {
                var blanks = new JArray();
                for (var i = 0; i < sourceArray.Count; i++)
                {
                    blanks.Add(JValue.CreateNull());
                }

                created = blanks;
            }
            else
            {
                // The path claims there is more below, but the source ends here.
                return null;
            }

            Place(container, step, created);
            return created;
        }

        /// <summary>
        /// Splits a path such as <c>GVL.Machine.Axes[2].Offset</c> into its steps. Plc identifiers
        /// cannot contain a dot or a bracket, so the split has nothing to guess at.
        /// </summary>
        private static List<Step> Parse(string path)
        {
            var steps = new List<Step>();

            if (string.IsNullOrEmpty(path))
            {
                return steps;
            }

            var position = 0;

            // True at the start and straight after a dot: the only thing that may come next is a
            // name. Anything else - a second dot, a bracket, the end of the string - is malformed,
            // and a malformed path is refused outright rather than half understood.
            var expectName = true;

            while (position < path.Length)
            {
                var c = path[position];

                if (c == '.')
                {
                    if (expectName)
                    {
                        return new List<Step>();
                    }

                    expectName = true;
                    position++;
                    continue;
                }

                if (c == '[')
                {
                    var close = path.IndexOf(']', position);

                    if (expectName || close < 0 ||
                        !int.TryParse(path.Substring(position + 1, close - position - 1), out var index))
                    {
                        return new List<Step>();
                    }

                    steps.Add(Step.Element(index));
                    position = close + 1;
                    continue;
                }

                var start = position;
                while (position < path.Length && path[position] != '.' && path[position] != '[')
                {
                    position++;
                }

                steps.Add(Step.Member(path.Substring(start, position - start)));
                expectName = false;
            }

            return expectName ? new List<Step>() : steps;
        }

        private struct Step
        {
            private Step(string name, int index)
            {
                Name = name;
                Index = index;
            }

            public static Step Member(string name) => new Step(name, -1);

            public static Step Element(int index) => new Step(null, index);

            public string Name { get; }
            public int Index { get; }
            public bool IsElement => Name == null;
        }
    }
}
