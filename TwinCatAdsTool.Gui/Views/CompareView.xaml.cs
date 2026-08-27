using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TwinCatAdsTool.Gui.Views
{
    /// <summary>
    /// Interaction logic for CompareView.xaml
    /// </summary>
    public partial class CompareView : UserControl
    {
        private ScrollViewer leftScroller;
        private ScrollViewer rightScroller;

        /// <summary>
        /// Set while one pane is being moved to follow the other. Without it the two chase each
        /// other: the pane being followed reports the offset it actually reached, which is clamped
        /// to its own extent, and scrolling the first one straight back to that clamped value is
        /// what made the wheel look dead.
        /// </summary>
        private bool synchronising;

        public CompareView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Scrolls the pane the pointer is over. The wheel is taken here, on the tunnelling event
        /// of the list itself, rather than left to the scroll viewer inside the template: the
        /// pointer sits over a row, and whether the event ever reaches the scroll viewer depends
        /// on which scroll viewer the theme decided to build. Some of the ones wpf ui provides
        /// decline the wheel on purpose so that the page behind them scrolls instead, which is
        /// exactly the behaviour the panes must not have. Handling it here settles the question:
        /// the list sees the event before anything inside it can, so nothing downstream matters.
        /// </summary>
        private void PaneWheel(object sender, MouseWheelEventArgs e)
        {
            var scroller = ReferenceEquals(sender, LeftPane)
                ? ScrollerOf(LeftPane, ref leftScroller)
                : ScrollerOf(RightPane, ref rightScroller);

            if (scroller == null || e.Delta == 0)
            {
                return;
            }

            // A notch is 120, and windows says how many lines one notch is worth. Set to page
            // scrolling it reports -1, in which case a screenful is the honest reading of it.
            var linesPerNotch = SystemParameters.WheelScrollLines;
            var step = linesPerNotch < 0 ? scroller.ViewportHeight : linesPerNotch;

            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - (e.Delta / 120.0 * step));
            e.Handled = true;
        }

        private void PaneScrolled(object sender, ScrollChangedEventArgs e)
        {
            if (synchronising)
            {
                return;
            }

            var follower = ReferenceEquals(sender, LeftPane)
                ? ScrollerOf(RightPane, ref rightScroller)
                : ScrollerOf(LeftPane, ref leftScroller);

            if (follower == null)
            {
                return;
            }

            synchronising = true;
            try
            {
                follower.ScrollToVerticalOffset(e.VerticalOffset);
                follower.ScrollToHorizontalOffset(e.HorizontalOffset);
            }
            finally
            {
                synchronising = false;
            }
        }

        /// <summary>
        /// The scroll viewer belongs to the list's template, so it only exists once the template
        /// has been applied - hence looking it up on first use rather than in the constructor.
        /// </summary>
        private static ScrollViewer ScrollerOf(DependencyObject pane, ref ScrollViewer cached)
        {
            return cached ?? (cached = FindScroller(pane));
        }

        private static ScrollViewer FindScroller(DependencyObject root)
        {
            if (root is ScrollViewer scroller)
            {
                return scroller;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var found = FindScroller(VisualTreeHelper.GetChild(root, i));
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
