using System.Collections.Generic;
using Newtonsoft.Json;

namespace TwinCatAdsTool.Gui.Models
{
    /// <summary>
    /// A group of symbols to watch, as it is written to file. Deliberately shallow and named after
    /// what a programmer sees rather than after the objects behind it: the file is meant to be edited
    /// by hand, pasted into a mail and kept next to the plc project.
    /// </summary>
    public class WatchSet
    {
        [JsonProperty("variables")]
        public List<WatchSetEntry> Variables { get; set; } = new List<WatchSetEntry>();
    }

    public class WatchSetEntry
    {
        /// <summary>The instance path, exactly as the plc spells it.</summary>
        [JsonProperty("path")]
        public string Path { get; set; }

        /// <summary>Whether the symbol goes on the scope as well as in the list.</summary>
        [JsonProperty("graph")]
        public bool Graph { get; set; }
    }
}
