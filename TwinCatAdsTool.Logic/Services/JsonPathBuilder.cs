using System;
using Newtonsoft.Json.Linq;

namespace TwinCatAdsTool.Logic.Services
{
    /// <summary>
    /// Places a value into a json tree following a dotted instance path, creating the
    /// intermediate objects on the way.
    ///
    /// The previous implementation derived the parent path with
    /// <c>InstancePath.Replace("." + localName, "")</c>, which replaces *every* occurrence: a
    /// path such as GVL.Axis.Axis collapsed to GVL and the value ended up in the wrong node.
    /// Splitting the path on its separators keeps repeated names intact.
    /// </summary>
    public static class JsonPathBuilder
    {
        public static void Insert(JObject root, string instancePath, JToken value)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }

            if (string.IsNullOrWhiteSpace(instancePath))
            {
                throw new ArgumentException("instance path must not be empty", nameof(instancePath));
            }

            var segments = instancePath.Split('.');
            var current = root;

            for (var i = 0; i < segments.Length - 1; i++)
            {
                var segment = segments[i];
                var existing = current[segment];

                if (existing is JObject child)
                {
                    current = child;
                    continue;
                }

                if (existing != null)
                {
                    throw new InvalidOperationException(
                        $"cannot place '{instancePath}': '{string.Join(".", segments, 0, i + 1)}' already holds a value");
                }

                child = new JObject();
                current[segment] = child;
                current = child;
            }

            var leaf = segments[segments.Length - 1];
            if (current[leaf] != null)
            {
                throw new InvalidOperationException($"cannot place '{instancePath}': it is already present in the backup");
            }

            current[leaf] = value;
        }

        /// <summary>Reads back a value placed by <see cref="Insert"/>, or null when absent.</summary>
        public static JToken Find(JObject root, string instancePath)
        {
            if (root == null || string.IsNullOrWhiteSpace(instancePath))
            {
                return null;
            }

            JToken current = root;
            foreach (var segment in instancePath.Split('.'))
            {
                if (!(current is JObject obj))
                {
                    return null;
                }

                current = obj.Property(segment, StringComparison.OrdinalIgnoreCase)?.Value;
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }
    }
}
