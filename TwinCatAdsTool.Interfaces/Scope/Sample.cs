using System;

namespace TwinCatAdsTool.Interfaces.Scope
{
    /// <summary>
    /// One reading of a signal, at the moment it arrived.
    /// </summary>
    public readonly struct Sample
    {
        public Sample(DateTime at, double value)
        {
            At = at;
            Value = value;
        }

        public DateTime At { get; }

        public double Value { get; }
    }
}
