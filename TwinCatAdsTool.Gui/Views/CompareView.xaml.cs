using System.Windows;
using System.Windows.Controls;
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
