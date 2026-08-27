using System;
using System.Collections.Generic;

namespace TwinCatAdsTool.Interfaces.Scope
{
    /// <summary>
    /// What has been recorded of one signal. Two spans decide what happens to a sample: how far back
    /// the recording is kept, which is what limits memory, and which slice of it is being looked at,
    /// which is what the plot draws. Keeping them apart is what makes it possible to stop the
    /// recording and scroll back through it - with one span the past is thrown away as it leaves the
    /// screen, which is what the graph used to do.
    /// </summary>
    public class SignalTrace
    {
        private readonly List<Sample> samples = new List<Sample>();

        public SignalTrace(string name, bool isDigital)
        {
            Name = name;
            IsDigital = isDigital;
        }

        public string Name { get; }

        /// <summary>
        /// A signal that only ever holds two states. It is drawn as steps rather than as a line: a
        /// BOOL that changes between two readings did not travel through the values in between, and
        /// a straight line between them says it did.
        /// </summary>
        public bool IsDigital { get; }

        public int Count => samples.Count;

        public DateTime? FirstAt => samples.Count == 0 ? (DateTime?)null : samples[0].At;

        public DateTime? LastAt => samples.Count == 0 ? (DateTime?)null : samples[samples.Count - 1].At;

        public double? LastValue => samples.Count == 0 ? (double?)null : samples[samples.Count - 1].Value;

        /// <summary>
        /// Appends a reading. A reading older than the one before it is dropped rather than inserted:
        /// the samples arrive from a notification stream that is already in order, and an out of order
        /// one means the clock moved, not that the signal did.
        /// </summary>
        public void Record(DateTime at, double value)
        {
            if (samples.Count > 0 && at < samples[samples.Count - 1].At)
            {
                return;
            }

            samples.Add(new Sample(at, value));
        }

        /// <summary>
        /// Drops everything older than the history being kept, except the last sample before the
        /// cutoff: that one is what the signal was holding when the window opens, and without it a
        /// signal that has not changed recently draws nothing at all.
        /// </summary>
        public void Forget(DateTime before)
        {
            var keepFrom = LastIndexAtOrBefore(before);

            if (keepFrom > 0)
            {
                samples.RemoveRange(0, keepFrom);
            }
        }

        public void Clear()
        {
            samples.Clear();
        }

        /// <summary>
        /// The samples needed to draw the slice between two instants: everything inside it, preceded
        /// by the last sample before it so that the trace enters the window at the height it was
        /// already holding.
        /// </summary>
        public IReadOnlyList<Sample> Window(DateTime from, DateTime to)
        {
            var window = new List<Sample>();

            if (samples.Count == 0)
            {
                return window;
            }

            for (var i = Math.Max(LastIndexAtOrBefore(from), 0); i < samples.Count; i++)
            {
                if (samples[i].At > to)
                {
                    break;
                }

                window.Add(samples[i]);
            }

            return window;
        }

        /// <summary>
        /// The position of the last sample at or before an instant, or -1 when every sample is later.
        /// </summary>
        private int LastIndexAtOrBefore(DateTime instant)
        {
            var low = 0;
            var high = samples.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);

                if (samples[middle].At <= instant)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return found;
        }
    }
}
