using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TwinCatAdsTool.Gui.Themes;
using TwinCatAdsTool.Gui.ViewModels;
using TwinCatAdsTool.Gui.Views.Pages;
using Wpf.Ui.Controls;

namespace TwinCatAdsTool.Gui.Views
{
	/// <summary>
	/// Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : FluentWindow
	{
		public MainWindow()
		{
			InitializeComponent();

			// The data context is assigned from outside once the kernel has built and initialised
			// the view model, so the navigation cannot be wired up in the constructor.
			Loaded += OnLoaded;
		}

		private void OnLoaded(object sender, RoutedEventArgs e)
		{
			Loaded -= OnLoaded;

			// The backdrop needs a window handle, which only exists from here on.
			AppTheme.ApplyStored();

			LetThePageFillTheFrame();

			if (DataContext is MainWindowViewModel viewModel)
			{
				viewModel.SyncThemeState();
				ShowFirstPage(viewModel);
			}
		}

		/// <summary>
		/// Stops the frame the navigation view puts the pages in from scrolling.
		/// A scroll viewer offers its child unlimited height, so a page laid out to fill the frame -
		/// which all four of these are - is never told how much height it has: a star sized row grows
		/// to whatever it contains, a list sizes itself to every item it holds instead of scrolling,
		/// and the only scrollbar in the window is the frame's. On the compare page that showed up as
		/// panes the wheel would not scroll and a scrollbar too thin to aim at - neither of which
		/// belonged to the panes at all.
		///
		/// Disabling the scrollbars rather than asking the navigation view not to build the scroll
		/// viewer: a disabled scroll viewer hands its child the height it actually has, which is the
		/// whole point, and it does not matter which template the theme decided to use or what the
		/// scroll viewer inside it turns out to be.
		/// </summary>
		private void LetThePageFillTheFrame()
		{
			if (StopFrameScrolling())
			{
				return;
			}

			// The frame builds itself lazily, so on a cold start there can be nothing to find yet.
			// Layout says when there is.
			LayoutUpdated += OnLayoutUpdated;
		}

		private void OnLayoutUpdated(object sender, EventArgs e)
		{
			if (StopFrameScrolling())
			{
				LayoutUpdated -= OnLayoutUpdated;
			}
		}

		private bool StopFrameScrolling()
		{
			// Anchored on the presenter rather than on the navigation view: the menu on the left has a
			// scroll viewer of its own, and it is the one a search from the top would meet first.
			var presenter = FindDescendant<NavigationViewContentPresenter>(RootNavigation);
			var frame = presenter == null ? null : FindFrameScroller(presenter);

			if (frame == null)
			{
				return false;
			}

			frame.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
			frame.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
			return true;
		}

		/// <summary>
		/// The first scroll viewer under the navigation view that is not inside a page. The pages own
		/// the scrolling of their own contents - the compare panes are scroll viewers themselves - so
		/// the search must stop at the page boundary rather than return the first one it meets.
		/// </summary>
		private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);

				if (child is T wanted)
				{
					return wanted;
				}

				var found = FindDescendant<T>(child);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private static ScrollViewer FindFrameScroller(DependencyObject root)
		{
			for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
			{
				var child = VisualTreeHelper.GetChild(root, i);

				if (child is Page)
				{
					continue;
				}

				if (child is ScrollViewer scroller)
				{
					return scroller;
				}

				var found = FindFrameScroller(child);
				if (found != null)
				{
					return found;
				}
			}

			return null;
		}

		private void ShowFirstPage(MainWindowViewModel viewModel)
		{
			if (viewModel.TabsViewModel == null)
			{
				return;
			}

			RootNavigation.SetPageProviderService(new NavigationPageProvider(viewModel.TabsViewModel));
			_ = RootNavigation.Navigate(typeof(BackupPage));
		}
	}
}
