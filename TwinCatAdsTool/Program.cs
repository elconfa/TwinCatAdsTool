using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using log4net;
using Ninject;
using TwinCatAdsTool.Gui;
using TwinCatAdsTool.Gui.ViewModels;
using TwinCatAdsTool.Gui.Views;
using TwinCatAdsTool.Interfaces;
using TwinCatAdsTool.Interfaces.Commons;
using TwinCatAdsTool.Interfaces.Logging;
using TwinCatAdsTool.Logic;

namespace TwinCatAdsTool
{
	public class Program
	{
		[DllImport("kernel32.dll", SetLastError = true)]
		private static extern bool FreeConsole();

		[STAThread]
		public static void Main(string[] args)
		{
			FreeConsole();

			// The console is gone and the logger may not be up yet, so anything that escapes
			// here would leave no trace at all and the window would simply never appear.
			AppDomain.CurrentDomain.UnhandledException +=
				(_, e) => ReportStartupFailure(e.ExceptionObject as Exception, "unhandled exception");

			using (IKernel kernel = new StandardKernel())
			{
				try
				{
					CreateLogger();
					Log("Application starts!", string.Join("", Enumerable.Repeat("#", 80)));
					Log("Loading kernel modules... ");
					LoadModules(kernel);
					Log("Kernel modules loaded");

					var viewModelFactory = kernel.Get<ViewModelLocator>();
					var application = CreateApplication(viewModelFactory);

					var mainWindowViewModel = viewModelFactory.CreateViewModel<MainWindowViewModel>();

					var mainWindow = kernel.Get<MainWindow>();
					mainWindow.DataContext = mainWindowViewModel;

					Log(string.Join("", Enumerable.Repeat("#", 80)));

					application.Run(mainWindow);
					application.Shutdown();
					Log("Application ended...");
				}
				catch (Exception e)
				{
					ReportStartupFailure(e, "startup failed");
					throw;
				}
			}
		}

		/// <summary>
		/// Writes the failure next to the executable and shows it, so a window that never opens
		/// can still be diagnosed on a machine without a debugger.
		/// </summary>
		private static void ReportStartupFailure(Exception exception, string headline)
		{
			var text = $"TwinCatAdsTool {Constants.Version} - {headline}{Environment.NewLine}" +
			           $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
			           $"{RuntimeInformation.OSDescription} / {RuntimeInformation.FrameworkDescription}{Environment.NewLine}{Environment.NewLine}" +
			           $"{exception}";

			TryLog(text);
			TryWriteReport(text);
			TryShowReport(text);
		}

		private static void TryLog(string text)
		{
			try
			{
				LoggerFactory.GetLogger().Error(text);
			}
			catch (Exception)
			{
				// The logger itself may be the thing that failed.
			}
		}

		private static void TryWriteReport(string text)
		{
			try
			{
				File.AppendAllText(Path.Combine(ExecutableDirectory(), "startup-error.txt"),
					text + Environment.NewLine + new string('-', 80) + Environment.NewLine,
					Encoding.UTF8);
			}
			catch (Exception)
			{
				// Read only directory: the message box below is then the only channel left.
			}
		}

		private static void TryShowReport(string text)
		{
			try
			{
				MessageBox.Show(text, "TwinCatAdsTool could not start", MessageBoxButton.OK, MessageBoxImage.Error);
			}
			catch (Exception)
			{
				// No message pump available.
			}
		}

		/// <summary>
		/// The folder holding the executable. Not the working directory, which for a single file
		/// build is wherever the user happened to start it from.
		/// </summary>
		private static string ExecutableDirectory()
		{
			var path = Environment.ProcessPath;
			return string.IsNullOrEmpty(path)
				? AppContext.BaseDirectory
				: Path.GetDirectoryName(path) ?? AppContext.BaseDirectory;
		}

		private static void Log(params string[] messages)
		{
			try
			{
				foreach (var message in messages)
				{
					LoggerFactory.GetLogger().Info(message);
				}
			}
			catch (Exception)
			{
				// Never let logging keep the application from starting.
			}
		}

		private static void CreateLogger()
		{
			// The config sits next to the executable; a single file build unpacks its content
			// somewhere else entirely, so the working directory is not good enough.
			var config = new FileInfo(Path.Combine(ExecutableDirectory(), "log.config"));

			if (config.Exists)
			{
				log4net.Config.XmlConfigurator.Configure(config);
			}
			else
			{
				log4net.Config.BasicConfigurator.Configure();
			}

			CreateRepository(Constants.LoggingRepositoryName);
			CreateRepository(Constants.LoggingObservationRepositoryName);
		}

		private static void CreateRepository(string name)
		{
			try
			{
				LogManager.CreateRepository(name);
			}
			catch (Exception)
			{
				// Already created - happens when the configuration file declares it as well.
			}
		}

		private static void LoadModules(IKernel kernel)
		{
			kernel.Load<GuiModuleCatalog>();
			kernel.Load<LogicModuleCatalog>();
		}

		private static Application CreateApplication(IViewModelFactory viewModelLocator)
		{
			var application = new App() { ShutdownMode = ShutdownMode.OnLastWindowClose };

			application.InitializeComponent();
			application.ReplaceViewModelLocator(viewModelLocator);

			application.DispatcherUnhandledException += (_, e) =>
			{
				ReportStartupFailure(e.Exception, "unhandled dispatcher exception");
				e.Handled = false;
			};

			return application;
		}
	}

	public static class ApplicationExtensions
	{
		public static void ReplaceViewModelLocator(this Application application, IViewModelFactory viewModelLocator, string locatorKey = "Locator")
		{
            if (application.Resources.Contains(locatorKey))
            {
                application.Resources.Remove(locatorKey);
            }

            application.Resources.Add(locatorKey, viewModelLocator);
		}
	}
}
