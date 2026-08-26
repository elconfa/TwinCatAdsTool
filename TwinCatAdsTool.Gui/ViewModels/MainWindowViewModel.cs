using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCatAdsTool.Gui.Themes;
using TwinCatAdsTool.Interfaces;
using TwinCatAdsTool.Interfaces.Commons;
using TwinCatAdsTool.Interfaces.Extensions;
using Wpf.Ui.Controls;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class MainWindowViewModel : ViewModelBase
    {
        private readonly IViewModelFactory viewModelFactory;
        private string version;
        private bool isDarkTheme = true;

        public MainWindowViewModel(IViewModelFactory viewModelFactory)
        {
            this.viewModelFactory = viewModelFactory;
        }

        public ConnectionCabViewModel ConnectionCabViewModel { get; set; }
        public TabsViewModel TabsViewModel { get; set; }

        public string Version
        {
            get => version;
            set
            {
                if (value == version) return;
                version = value;
                raisePropertyChanged();
            }
        }

        /// <summary>Drives the title bar toggle and the icon shown on it.</summary>
        public bool IsDarkTheme
        {
            get => isDarkTheme;
            set
            {
                if (value == isDarkTheme) return;
                isDarkTheme = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(ThemeIcon));
                raisePropertyChanged(nameof(ThemeToolTip));
            }
        }

        /// <summary>The icon shows what clicking will switch to, not the theme in use.</summary>
        public SymbolRegular ThemeIcon => IsDarkTheme ? SymbolRegular.WeatherSunny24 : SymbolRegular.WeatherMoon24;

        public string ThemeToolTip => IsDarkTheme ? "Switch to the light theme" : "Switch to the dark theme";

        public ReactiveCommand<RxVoid, RxVoid> ToggleTheme { get; set; }

        public override void Init()
        {
            Logger.Debug("Initialize main window view model");

            Version = $"v{Constants.Version}";

            ToggleTheme = ReactiveCommand.CreateFromTask(SwitchTheme)
                .AddDisposableTo(Disposables);

            ConnectionCabViewModel = viewModelFactory.CreateViewModel<ConnectionCabViewModel>();
            ConnectionCabViewModel.AddDisposableTo(Disposables);

            TabsViewModel = viewModelFactory.CreateViewModel<TabsViewModel>();
            TabsViewModel.AddDisposableTo(Disposables);
        }

        /// <summary>Keeps the view model in step with a theme applied from elsewhere, such as startup.</summary>
        public void SyncThemeState()
        {
            IsDarkTheme = AppTheme.IsDark;
        }

        private Task<RxVoid> SwitchTheme()
        {
            IsDarkTheme = AppTheme.Toggle();
            return Task.FromResult(RxVoid.Default);
        }
    }
}
