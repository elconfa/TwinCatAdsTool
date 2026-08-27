using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using TwinCatAdsTool.Gui.ViewModels;

namespace TwinCatAdsTool.Gui.Views
{
    /// <summary>
    /// Interaction logic for GraphView.xaml
    /// </summary>
    public partial class GraphView : UserControl
    {
        /// <summary>The border on the explore page the scope belongs to when it is not in a window.</summary>
        private Border home;

        private Window host;

        public GraphView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Moves the scope between the page and a window of its own.
        ///
        /// The control itself is moved rather than a second one being built next to it: a plot model
        /// belongs to one plot view at a time, and two views onto the same recording would leave one
        /// of them dark. Moving it also means there is no state to keep in step - the recording, the
        /// window being looked at and the trigger are all in the view model, which never moves.
        /// </summary>
        private void TogglePopOut(object sender, RoutedEventArgs e)
        {
            if (host != null)
            {
                // Closing is what docks it back, so there is one path rather than two.
                host.Close();
                return;
            }

            home = Parent as Border;

            if (home == null)
            {
                return;
            }

            // The data context arrives here through a binding that reads the explore page's own
            // context. Once the scope is in a window of its own that chain is gone and the binding
            // would resolve against nothing, leaving the plot blank; so what it currently resolves
            // to is pinned in place before the move. It is the same view model either way.
            var scope = DataContext;
            BindingOperations.ClearBinding(this, DataContextProperty);
            DataContext = scope;

            home.Child = Placeholder();

            host = new Window
            {
                Title = "Scope",
                Width = 1100,
                Height = 640,
                MinWidth = 640,
                MinHeight = 320,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,

                // Owned by the main window, so it goes away with the application instead of keeping
                // it alive after the main window has been closed.
                Owner = Application.Current?.MainWindow,
                Background = TryFindResource("ApplicationBackgroundBrush") as System.Windows.Media.Brush,
                Content = this
            };

            host.Closed += (_, __) => Dock();
            host.Show();

            PopOutButton.ToolTip = "Put the scope back on the page";
        }

        private void Dock()
        {
            var window = host;
            host = null;

            if (window != null)
            {
                window.Content = null;
            }

            if (home != null)
            {
                home.Child = this;
            }

            PopOutButton.ToolTip = "Show the scope in a window of its own";
        }

        /// <summary>
        /// What the page shows while the scope is elsewhere. Without it the panel would simply be
        /// empty, which reads as something having gone wrong.
        /// </summary>
        private TextBlock Placeholder()
        {
            return new TextBlock
            {
                Text = "The scope is showing in a window of its own." + System.Environment.NewLine +
                       "Close that window to bring it back here.",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Style = TryFindResource("SecondaryText") as Style
            };
        }

        /// <summary>
        /// Wheel zooms, wheel with shift scrolls - the gestures a scope is expected to answer to.
        /// Oxyplot's own zoom is left off: the visible slice is state the view model owns, and two
        /// things moving the same axis would fight over it on every redraw.
        /// </summary>
        private void PlotWheel(object sender, MouseWheelEventArgs e)
        {
            if (!(DataContext is GraphViewModel scope) || e.Delta == 0)
            {
                return;
            }

            var steps = e.Delta > 0 ? 1 : -1;

            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                scope.Pan(steps);
            }
            else
            {
                scope.Zoom(steps);
            }

            e.Handled = true;
        }
    }
}
