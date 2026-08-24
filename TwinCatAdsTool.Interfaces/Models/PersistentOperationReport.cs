using System;
using System.Collections.Generic;
using System.Linq;

namespace TwinCatAdsTool.Interfaces.Models
{
    /// <summary>
    /// Complete outcome of a backup or a restore run. A run is only trustworthy when
    /// <see cref="IsComplete"/> is true - anything else means variables are missing and the
    /// user has to be told about it.
    /// </summary>
    public class PersistentOperationReport
    {
        public PersistentOperationReport(IEnumerable<VariableOperationResult> results, TimeSpan duration)
        {
            Results = (results ?? Enumerable.Empty<VariableOperationResult>()).ToList();
            Duration = duration;
        }

        public IReadOnlyList<VariableOperationResult> Results { get; }
        public TimeSpan Duration { get; }

        public IReadOnlyList<VariableOperationResult> Failed
            => Results.Where(r => r.State == VariableOperationState.Failed).ToList();

        public IReadOnlyList<VariableOperationResult> SkippedVariables
            => Results.Where(r => r.State == VariableOperationState.Skipped).ToList();

        public int SucceededCount => Results.Count(r => r.State == VariableOperationState.Succeeded);
        public int FailedCount => Failed.Count;
        public int SkippedCount => SkippedVariables.Count;

        /// <summary>True when every variable was processed successfully.</summary>
        public bool IsComplete => FailedCount == 0 && SkippedCount == 0;

        public string Summary => $"{SucceededCount} ok, {FailedCount} failed, {SkippedCount} skipped " +
                                 $"in {Duration.TotalSeconds:F1} s";

        /// <summary>Multi line report listing every variable that did not succeed.</summary>
        public string Details()
        {
            var problems = Results
                .Where(r => r.State != VariableOperationState.Succeeded)
                .Select(r => r.ToString())
                .ToList();

            return problems.Any()
                ? string.Join(Environment.NewLine, problems)
                : "All variables processed successfully.";
        }
    }
}
