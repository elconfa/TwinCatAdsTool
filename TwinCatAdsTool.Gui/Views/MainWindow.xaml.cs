using System;
using System.Linq;
using System.Windows;
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

			if (DataContext is MainWindowViewModel viewModel)
			{
				viewModel.SyncThemeState();
				ShowFirstPage(viewModel);
			}
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
