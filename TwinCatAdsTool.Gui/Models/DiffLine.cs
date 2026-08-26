namespace TwinCatAdsTool.Gui.Models
{
    /// <summary>What happened to one line of the comparison.</summary>
    public enum DiffKind
    {
        Unchanged,
        Inserted,
        Deleted,
        Modified,

        /// <summary>
        /// No line at all on this side: it is there only to keep the two panes lined up against
        /// an insertion or a deletion on the other side.
        /// </summary>
        Filler
    }

    /// <summary>
    /// One line of one side of the comparison.
    ///
    /// The view model used to hand the view ready made <c>ListBoxItem</c> controls with their
    /// colours already set. That put the rendering at the mercy of whatever container style the
    /// list happened to wrap them in, which is how the differences ended up not showing at all.
    /// The colours now belong to the data template, where the row can be stretched across the
    /// whole width and nothing can paint over it.
    /// </summary>
    public class DiffLine
    {
        public DiffLine(string text, DiffKind kind)
        {
            Text = text ?? string.Empty;
            Kind = kind;
        }

        public string Text { get; }
        public DiffKind Kind { get; }
    }
}
