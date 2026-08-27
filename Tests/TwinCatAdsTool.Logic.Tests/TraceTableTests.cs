using System;
using System.Globalization;
using System.Linq;
using TwinCatAdsTool.Interfaces.Scope;
using Xunit;

namespace TwinCatAdsTool.Logic.Tests
{
    /// <summary>
    /// Signals are read one at a time, as each happens to change, so laying them out side by side is
    /// where a capture is most easily misread. What is checked here is that every cell says what its
    /// signal was holding at that instant rather than leaving a hole, and that nothing is invented
    /// for a signal that had not been read yet.
    /// </summary>
    public class TraceTableTests
    {
        private static readonly DateTime Start = new DateTime(2026, 1, 1, 12, 0, 0);

        private static DateTime At(double seconds) => Start.AddSeconds(seconds);

        private static SignalTrace Trace(string name, params (double seconds, double value)[] samples)
        {
            var trace = new SignalTrace(name, false);

            foreach (var sample in samples)
            {
                trace.Record(At(sample.seconds), sample.value);
            }

            return trace;
        }

        [Fact]
        public void The_columns_are_the_signals_in_the_order_given()
        {
            var table = TraceTable.Build(new[] { Trace("a", (0, 1)), Trace("b", (0, 2)) }, At(0), At(10));

            Assert.Equal(new[] { "a", "b" }, table.Columns);
        }

        [Fact]
        public void Every_instant_any_signal_was_read_becomes_a_row()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 1), (3, 2)), Trace("b", (2, 9)) }, At(0), At(10));

            Assert.Equal(new[] { At(1), At(2), At(3) }, table.Rows.Select(row => row.At));
        }

        /// <summary>
        /// The point of the class: a signal that did not change at this instant still had a value.
        /// </summary>
        [Fact]
        public void A_signal_that_did_not_change_carries_the_value_it_was_holding()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 7)), Trace("b", (2, 9)) }, At(0), At(10));

            Assert.Equal(7, table.Rows[1].Values[0]);
            Assert.Equal(9, table.Rows[1].Values[1]);
        }

        [Fact]
        public void A_signal_not_yet_read_carries_nothing()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 7)), Trace("b", (5, 9)) }, At(0), At(10));

            Assert.Equal(At(1), table.Rows[0].At);
            Assert.Equal(7, table.Rows[0].Values[0]);
            Assert.Null(table.Rows[0].Values[1]);
        }

        /// <summary>
        /// A sample from before the window is what the signal entered it holding, so it belongs in
        /// the cells but must not open a row of its own outside the range that was asked for.
        /// </summary>
        [Fact]
        public void A_sample_from_before_the_window_fills_cells_without_adding_a_row()
        {
            var table = TraceTable.Build(new[] { Trace("held", (0, 4)), Trace("moving", (6, 1)) }, At(5), At(10));

            Assert.Single(table.Rows);
            Assert.Equal(At(6), table.Rows[0].At);
            Assert.Equal(4, table.Rows[0].Values[0]);
            Assert.Equal(1, table.Rows[0].Values[1]);
        }

        [Fact]
        public void Two_signals_read_at_the_same_instant_share_one_row()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 1)), Trace("b", (1, 2)) }, At(0), At(10));

            Assert.Single(table.Rows);
            Assert.Equal(new double?[] { 1, 2 }, table.Rows[0].Values);
        }

        [Fact]
        public void Nothing_recorded_gives_no_rows()
        {
            var table = TraceTable.Build(new[] { Trace("a") }, At(0), At(10));

            Assert.Empty(table.Rows);
            Assert.Equal(new[] { "a" }, table.Columns);
        }

        [Fact]
        public void A_culture_that_writes_decimals_with_a_comma_separates_fields_with_a_semicolon()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 1.5)) }, At(0), At(10));

            var text = table.ToDelimitedText(CultureInfo.GetCultureInfo("it-IT"));

            Assert.Contains("timestamp;a", text);
            Assert.Contains("1,5", text);
        }

        [Fact]
        public void A_culture_that_writes_decimals_with_a_point_separates_fields_with_a_comma()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 1.5)) }, At(0), At(10));

            var text = table.ToDelimitedText(CultureInfo.InvariantCulture);

            Assert.Contains("timestamp,a", text);
            Assert.Contains("1.5", text);
        }

        [Fact]
        public void A_timestamp_is_written_the_same_way_in_every_culture()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 1)) }, At(0), At(10));

            Assert.Contains("2026-01-01 12:00:01.000", table.ToDelimitedText(CultureInfo.GetCultureInfo("it-IT")));
            Assert.Contains("2026-01-01 12:00:01.000", table.ToDelimitedText(CultureInfo.InvariantCulture));
        }

        [Fact]
        public void A_cell_with_nothing_in_it_is_left_empty()
        {
            var table = TraceTable.Build(new[] { Trace("a", (1, 7)), Trace("b", (5, 9)) }, At(0), At(10));

            var firstRow = table.ToDelimitedText(CultureInfo.InvariantCulture).Split('\n')[1];

            Assert.EndsWith("7,", firstRow.TrimEnd('\r'));
        }
    }
}
