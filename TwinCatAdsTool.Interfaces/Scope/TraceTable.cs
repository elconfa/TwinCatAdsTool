using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace TwinCatAdsTool.Interfaces.Scope
{
    /// <summary>
    /// The recorded signals laid out as a table, one column per signal and one row per instant at
    /// which any of them was read.
    ///
    /// Signals do not change together - each one is read when it happens to change - so a naive
    /// table would be mostly blank. Every cell instead carries the value its signal was holding at
    /// that instant, which is what the signal actually was, and is what makes the file plottable in
    /// a spreadsheet without any further work.
    /// </summary>
    public class TraceTable
    {
        private TraceTable(IReadOnlyList<string> columns, IReadOnlyList<TraceRow> rows)
        {
            Columns = columns;
            Rows = rows;
        }

        public IReadOnlyList<string> Columns { get; }

        public IReadOnlyList<TraceRow> Rows { get; }

        public static TraceTable Build(IEnumerable<SignalTrace> traces, DateTime from, DateTime to)
        {
            var recorded = traces.ToList();
            var windows = recorded.Select(trace => trace.Window(from, to)).ToList();

            var instants = windows
                .SelectMany(window => window.Select(sample => sample.At))
                .Where(at => at >= from)
                .Distinct()
                .OrderBy(at => at)
                .ToList();

            var rows = new List<TraceRow>(instants.Count);

            // One cursor per signal, walked forward with the instants: both are in order, so the
            // whole table costs one pass rather than a search for every cell.
            var cursors = new int[windows.Count];

            foreach (var instant in instants)
            {
                var values = new double?[windows.Count];

                for (var column = 0; column < windows.Count; column++)
                {
                    var window = windows[column];

                    while (cursors[column] + 1 < window.Count && window[cursors[column] + 1].At <= instant)
                    {
                        cursors[column]++;
                    }

                    values[column] = window.Count > 0 && window[cursors[column]].At <= instant
                        ? window[cursors[column]].Value
                        : (double?)null;
                }

                rows.Add(new TraceRow(instant, values));
            }

            return new TraceTable(recorded.Select(trace => trace.Name).ToList(), rows);
        }

        /// <summary>
        /// The table as delimited text. The separator follows the culture: where the decimal mark is
        /// a comma, a comma cannot also separate the fields, and a spreadsheet opened in that culture
        /// expects a semicolon. Timestamps stay in a sortable form that does not depend on culture.
        /// </summary>
        public string ToDelimitedText(CultureInfo culture)
        {
            var separator = culture.NumberFormat.NumberDecimalSeparator == "," ? ';' : ',';
            var text = new StringBuilder();

            text.Append("timestamp");
            foreach (var column in Columns)
            {
                text.Append(separator).Append(column);
            }

            text.AppendLine();

            foreach (var row in Rows)
            {
                text.Append(row.At.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));

                foreach (var value in row.Values)
                {
                    text.Append(separator);

                    if (value.HasValue)
                    {
                        text.Append(value.Value.ToString(culture));
                    }
                }

                text.AppendLine();
            }

            return text.ToString();
        }
    }

    public class TraceRow
    {
        public TraceRow(DateTime at, IReadOnlyList<double?> values)
        {
            At = at;
            Values = values;
        }

        public DateTime At { get; }

        /// <summary>
        /// The value each signal was holding, or nothing at all for a signal that had not been read
        /// yet at this instant.
        /// </summary>
        public IReadOnlyList<double?> Values { get; }
    }
}
