using System;

namespace TwinCatAdsTool.Interfaces.Models
{
    /// <summary>
    /// How far a backup or a restore has got. A plain message cannot drive a progress bar, so the
    /// counts travel next to it instead of being formatted into the text and parsed back out.
    /// </summary>
    public class OperationProgress
    {
        /// <summary>Progress of a run that is not active: the bar goes back to empty.</summary>
        public static readonly OperationProgress Idle = new OperationProgress(string.Empty, 0, 0);

        public OperationProgress(string message, int done, int total)
        {
            Message = message ?? string.Empty;
            Done = done;
            Total = total;
        }

        public string Message { get; }

        /// <summary>Variables processed so far.</summary>
        public int Done { get; }

        /// <summary>Variables the run has to process in total, 0 while it is not running.</summary>
        public int Total { get; }

        public bool IsRunning => Total > 0;

        public double Percentage => Total > 0 ? Math.Min(100.0, 100.0 * Done / Total) : 0.0;

        public override string ToString() => Message;
    }
}
