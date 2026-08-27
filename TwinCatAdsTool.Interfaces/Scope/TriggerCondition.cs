namespace TwinCatAdsTool.Interfaces.Scope
{
    public enum TriggerEdge
    {
        /// <summary>A bit going TRUE.</summary>
        GoesTrue,

        /// <summary>A bit going FALSE.</summary>
        GoesFalse,

        /// <summary>A value crossing a level on the way up.</summary>
        RisesAbove,

        /// <summary>A value crossing a level on the way down.</summary>
        FallsBelow
    }

    /// <summary>
    /// What the scope is waiting for. Every condition is a crossing rather than a state: a signal
    /// that is already TRUE when the trigger is armed has not just gone TRUE, and firing on it would
    /// make arming useless for the case it exists for - catching the moment something changed.
    /// </summary>
    public class TriggerCondition
    {
        /// <summary>Halfway between the only two values a bit takes.</summary>
        private const double DigitalThreshold = 0.5;

        public TriggerCondition(TriggerEdge edge, double level)
        {
            Edge = edge;
            Level = level;
        }

        public TriggerEdge Edge { get; }

        public double Level { get; }

        /// <summary>
        /// Whether this reading is the crossing being waited for. The first reading of a signal never
        /// fires: with nothing before it there is no crossing, only a value.
        /// </summary>
        public bool Fires(double? previous, double current)
        {
            if (!previous.HasValue)
            {
                return false;
            }

            switch (Edge)
            {
                case TriggerEdge.GoesTrue:
                    return previous.Value < DigitalThreshold && current >= DigitalThreshold;

                case TriggerEdge.GoesFalse:
                    return previous.Value >= DigitalThreshold && current < DigitalThreshold;

                case TriggerEdge.RisesAbove:
                    return previous.Value < Level && current >= Level;

                case TriggerEdge.FallsBelow:
                    return previous.Value > Level && current <= Level;

                default:
                    return false;
            }
        }
    }
}
