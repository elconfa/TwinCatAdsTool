using ReactiveUI;
using TwinCatAdsTool.Interfaces.Comparison;

namespace TwinCatAdsTool.Gui.Models
{
    /// <summary>Which way a value has been picked to travel, once the merge is applied.</summary>
    public enum MergeMark
    {
        None,

        /// <summary>The left side is to take the value the right side holds.</summary>
        ToLeft,

        /// <summary>The right side is to take the value the left side holds.</summary>
        ToRight
    }

    /// <summary>
    /// One value of the comparison, as a row of the window.
    ///
    /// A row is a single leaf on the plc rather than a line of text, which is what lets it be acted
    /// on: the path names exactly one symbol, so a value picked here can be written straight to it.
    ///
    /// Marking a row does not write anything. The marks are collected, shown, and can be taken back
    /// wholesale, and only then does one deliberate action send them to the plc - which is the only
    /// safe order when the other end of the cable is a running machine.
    /// </summary>
    public class ValueDifference : ReactiveObject
    {
        private MergeMark mark;

        public ValueDifference(JsonDifferenceEntry entry)
        {
            Path = entry.Path;
            Left = entry.Left;
            Right = entry.Right;
            Kind = entry.Kind;
            IsDifferent = entry.IsDifferent;
            IsMergeable = entry.IsMergeable;
        }

        public string Path { get; }

        /// <summary>The reading on the left, or null when that side does not have this value.</summary>
        public string Left { get; }

        public string Right { get; }

        public JsonDifferenceKind Kind { get; }

        public bool IsDifferent { get; }

        /// <summary>
        /// Whether this value can be carried from one side to the other at all. A value only one
        /// side has cannot: bringing it across would mean declaring a symbol on the plc, and ads
        /// writes values, it does not create variables.
        /// </summary>
        public bool IsMergeable { get; }

        public MergeMark Mark
        {
            get => mark;
            set
            {
                if (value == mark)
                {
                    return;
                }

                this.RaiseAndSetIfChanged(ref mark, value);
                this.RaisePropertyChanged(nameof(IsMarked));
            }
        }

        public bool IsMarked => Mark != MergeMark.None;

        /// <summary>An em dash reads as a missing value; an empty cell reads as an empty string.</summary>
        public string LeftText => Left ?? "—";

        public string RightText => Right ?? "—";
    }
}
