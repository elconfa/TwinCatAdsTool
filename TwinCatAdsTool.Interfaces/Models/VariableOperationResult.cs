using System;

namespace TwinCatAdsTool.Interfaces.Models
{
    public enum VariableOperationState
    {
        /// <summary>The variable was read from or written to the plc successfully.</summary>
        Succeeded,

        /// <summary>The operation failed. <see cref="VariableOperationResult.Error"/> tells why.</summary>
        Failed,

        /// <summary>
        /// The variable was deliberately not processed (e.g. it is present in the backup file
        /// but no longer exists on the plc, or it exists on the plc but not in the backup file).
        /// </summary>
        Skipped
    }

    /// <summary>
    /// Outcome of reading or writing a single persistent variable. Every variable taking part in a
    /// backup or restore produces exactly one of these, so that nothing can fail unnoticed.
    /// </summary>
    public class VariableOperationResult
    {
        private VariableOperationResult(string instancePath, VariableOperationState state, string error)
        {
            InstancePath = instancePath;
            State = state;
            Error = error;
        }

        public string InstancePath { get; }
        public VariableOperationState State { get; }

        /// <summary>Human readable reason, set for <see cref="VariableOperationState.Failed"/> and <see cref="VariableOperationState.Skipped"/>.</summary>
        public string Error { get; }

        public static VariableOperationResult Success(string instancePath)
            => new VariableOperationResult(instancePath, VariableOperationState.Succeeded, null);

        public static VariableOperationResult Failure(string instancePath, string error)
            => new VariableOperationResult(instancePath, VariableOperationState.Failed, error);

        public static VariableOperationResult Failure(string instancePath, Exception exception)
            => new VariableOperationResult(instancePath, VariableOperationState.Failed, Describe(exception));

        public static VariableOperationResult Skipped(string instancePath, string reason)
            => new VariableOperationResult(instancePath, VariableOperationState.Skipped, reason);

        private static string Describe(Exception exception)
        {
            if (exception == null)
            {
                return "unknown error";
            }

            // Ads errors carry the useful detail in the inner exception more often than not.
            var inner = exception.InnerException;
            return inner == null
                ? exception.Message
                : $"{exception.Message} ({inner.Message})";
        }

        public override string ToString() => State == VariableOperationState.Succeeded
            ? $"{InstancePath}: ok"
            : $"{InstancePath}: {State.ToString().ToLowerInvariant()} - {Error}";
    }
}
