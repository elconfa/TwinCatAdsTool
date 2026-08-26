using System;
using System.Windows;
using TwinCatAdsTool.Gui.ViewModels;
using Wpf.Ui.Abstractions;

namespace TwinCatAdsTool.Gui.Views.Pages
{
    /// <summary>
    /// Hands the navigation view its pages with the matching view model already attached.
    ///
    /// The view models are the ones the kernel built and initialised at startup; letting the
    /// navigation view construct them itself would produce a second, uninitialised set that never
    /// talks to the plc.
    /// </summary>
    public class NavigationPageProvider : INavigationViewPageProvider
    {
        private readonly TabsViewModel tabs;

        public NavigationPageProvider(TabsViewModel tabs)
        {
            this.tabs = tabs ?? throw new ArgumentNullException(nameof(tabs));
        }

        public object GetPage(Type pageType)
        {
            if (pageType == typeof(BackupPage))
            {
                return WithDataContext(new BackupPage(), tabs.BackupViewModel);
            }

            if (pageType == typeof(RestorePage))
            {
                return WithDataContext(new RestorePage(), tabs.RestoreViewModel);
            }

            if (pageType == typeof(ComparePage))
            {
                return WithDataContext(new ComparePage(), tabs.CompareViewModel);
            }

            if (pageType == typeof(ExplorePage))
            {
                return WithDataContext(new ExplorePage(), tabs.ExploreViewModel);
            }

            return null;
        }

        private static FrameworkElement WithDataContext(FrameworkElement page, object dataContext)
        {
            page.DataContext = dataContext;
            return page;
        }
    }
}
