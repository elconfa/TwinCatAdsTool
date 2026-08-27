using TwinCatAdsTool.Interfaces.Scope;

namespace TwinCatAdsTool.Gui.Models
{
    /// <summary>
    /// A trigger condition together with the words for it. The enum names are written for code; what
    /// a list offers should read the way the condition would be said out loud.
    /// </summary>
    public class TriggerChoice
    {
        public TriggerChoice(TriggerEdge edge, string label)
        {
            Edge = edge;
            Label = label;
        }

        public TriggerEdge Edge { get; }

        public string Label { get; }
    }
}
