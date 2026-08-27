using System;
using System.Linq;
using TwinCatAdsTool.Interfaces.Scope;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// The trace is what makes the graph a scope rather than a ticker: the recording and the slice
    /// being looked at are separate, so the past survives leaving the screen. What is checked here
    /// above all is the sample just outside the window, which is the one that decides whether a
    /// signal that has not changed recently is drawn at all.
    /// </summary>
    public class SignalTraceTests
    {
        private static readonly DateTime Start = new DateTime(2026, 1, 1, 12, 0, 0);

        private static DateTime At(double seconds) => Start.AddSeconds(seconds);

        private static SignalTrace Recorded(params (double seconds, double value)[] samples)
        {
            var trace = new SignalTrace("signal", false);

            foreach (var sample in samples)
            {
                trace.Record(At(sample.seconds), sample.value);
            }

            return trace;
        }

        [Fact]
        public void A_new_trace_holds_nothing()
        {
            var trace = new SignalTrace("signal", false);

            Assert.Equal(0, trace.Count);
            Assert.Null(trace.FirstAt);
            Assert.Null(trace.LastAt);
            Assert.Null(trace.LastValue);
        }

        [Fact]
        public void Samples_are_kept_in_the_order_they_arrived()
        {
            var trace = Recorded((0, 10), (1, 20), (2, 30));

            Assert.Equal(3, trace.Count);
            Assert.Equal(At(0), trace.FirstAt);
            Assert.Equal(At(2), trace.LastAt);
            Assert.Equal(30, trace.LastValue);
        }

        [Fact]
        public void A_sample_older_than_the_last_one_is_dropped()
        {
            var trace = Recorded((0, 10), (5, 20), (3, 99));

            Assert.Equal(2, trace.Count);
            Assert.Equal(20, trace.LastValue);
        }

        [Fact]
        public void A_window_returns_the_samples_inside_it()
        {
            var trace = Recorded((0, 10), (1, 20), (2, 30), (3, 40));

            var window = trace.Window(At(1), At(2));

            Assert.Equal(new double[] { 20, 30 }, window.Select(s => s.Value));
        }

        /// <summary>
        /// A sample sitting exactly on the left edge is the one the trace enters at, so the one
        /// before it is not needed as well.
        /// </summary>
        [Fact]
        public void A_window_reaches_back_only_when_nothing_sits_on_its_left_edge()
        {
            var trace = Recorded((0, 10), (1, 20), (2, 30), (3, 40));

            var window = trace.Window(At(1.5), At(2));

            Assert.Equal(new double[] { 20, 30 }, window.Select(s => s.Value));
        }

        /// <summary>
        /// The point of the whole class: a signal whose last change was before the window still has
        /// to be drawn across it, at the value it was holding.
        /// </summary>
        [Fact]
        public void A_window_carries_the_last_sample_before_it()
        {
            var trace = Recorded((0, 7));

            var window = trace.Window(At(30), At(40));

            Assert.Single(window);
            Assert.Equal(7, window[0].Value);
            Assert.Equal(At(0), window[0].At);
        }

        [Fact]
        public void A_window_that_starts_before_the_first_sample_holds_no_earlier_one()
        {
            var trace = Recorded((10, 7));

            var window = trace.Window(At(0), At(20));

            Assert.Single(window);
            Assert.Equal(At(10), window[0].At);
        }

        [Fact]
        public void A_window_after_the_recording_still_carries_the_last_value()
        {
            var trace = Recorded((0, 1), (1, 2));

            var window = trace.Window(At(100), At(200));

            Assert.Single(window);
            Assert.Equal(2, window[0].Value);
        }

        [Fact]
        public void An_empty_trace_has_an_empty_window()
        {
            Assert.Empty(new SignalTrace("signal", false).Window(At(0), At(10)));
        }

        [Fact]
        public void Forgetting_drops_what_is_older_than_the_cutoff()
        {
            var trace = Recorded((0, 1), (10, 2), (20, 3), (30, 4));

            trace.Forget(At(20));

            Assert.Equal(2, trace.Count);
            Assert.Equal(At(20), trace.FirstAt);
        }

        /// <summary>
        /// Pruning keeps the sample the signal was holding at the cutoff, otherwise the oldest part
        /// of the buffer would be blank for every signal that changes slowly.
        /// </summary>
        [Fact]
        public void Forgetting_keeps_the_value_being_held_at_the_cutoff()
        {
            var trace = Recorded((0, 1), (30, 2));

            trace.Forget(At(10));

            Assert.Equal(2, trace.Count);
            Assert.Equal(At(0), trace.FirstAt);
        }

        [Fact]
        public void Forgetting_everything_but_the_last_leaves_one_sample()
        {
            var trace = Recorded((0, 1), (1, 2), (2, 3));

            trace.Forget(At(100));

            Assert.Equal(1, trace.Count);
            Assert.Equal(3, trace.LastValue);
        }

        [Fact]
        public void Clearing_empties_the_trace()
        {
            var trace = Recorded((0, 1), (1, 2));

            trace.Clear();

            Assert.Equal(0, trace.Count);
            Assert.Null(trace.LastValue);
        }

        [Fact]
        public void A_digital_trace_says_so()
        {
            Assert.True(new SignalTrace("bit", true).IsDigital);
            Assert.False(new SignalTrace("value", false).IsDigital);
        }
    }
}
