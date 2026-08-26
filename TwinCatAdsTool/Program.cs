using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using log4net;
using Ninject;
using ReactiveUI.Builder;
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
					InitializeReactiveUI();
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
		/// ReactiveUI 24 no longer registers itself on first use: without this the first
		/// WhenAnyValue throws a TypeInitializationException and the window never opens.
		/// Has to run before anything touches ReactiveUI.
		/// </summary>
		private static void InitializeReactiveUI()
		{
			// The wpf registrations - the dispatcher scheduler among them - live in
			// ReactiveUI.Wpf. Nothing in this application references a type from that assembly,
			// so the runtime would never load it and the builder would find no platform to
			// register. Pull it in first.
			try
			{
				Assembly.Load("ReactiveUI.Wpf");
			}
			catch (Exception e)
			{
				Log($"Could not preload ReactiveUI.Wpf: {e.Message}");
			}

			RxAppBuilder
				.CreateReactiveUIBuilder()
				.WithPlatformServices()
				.Build();
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
				File.AppendAllText(Path.Combine(DiagnosticsDirectory(), "startup-error.txt"),
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

		/// <summary>
		/// The folder the log files and the diagnostic notes are written into: next to the
		/// executable, so that a copied folder carries its own logs.
		///
		/// Windows can refuse to write there - a protected folder, a read only share, controlled
		/// folder access - and then nothing appears at all, not even the note explaining why,
		/// which is exactly what a missing log folder looks like. The per user application data
		/// folder is always writable, so it is used as the fallback.
		/// </summary>
		private static string DiagnosticsDirectory()
		{
			if (diagnosticsDirectory != null)
			{
				return diagnosticsDirectory;
			}

			var next = ExecutableDirectory();

			diagnosticsDirectory = IsWritable(next)
				? next
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
					Constants.LoggingRepositoryName);

			return diagnosticsDirectory;
		}

		private static string diagnosticsDirectory;

		private static bool IsWritable(string directory)
		{
			try
			{
				Directory.CreateDirectory(directory);

				var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
				using (File.Create(probe))
				{
				}

				File.Delete(probe);
				return true;
			}
			catch (Exception)
			{
				return false;
			}
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

			// The appenders write to a path relative to the process working directory, which is
			// not necessarily the folder holding the executable. Passing the folder in as a
			// property keeps the log files next to it, where they can actually be found - or in
			// the fallback folder when that one cannot be written to.
			log4net.GlobalContext.Properties["LogDirectory"] = DiagnosticsDirectory();

			// Configure the repository the loggers really end up in. LoggerFactory lives in
			// TwinCatAdsTool.Interfaces and calls LogManager.GetLogger(name), an overload that
			// resolves the repository from the *calling assembly*. Configuring without saying
			// which repository would configure this executable's one instead, leaving every
			// logger the application creates without a single appender - which is why no log
			// file was ever written.
			var repository = log4net.LogManager.GetRepository(typeof(LoggerFactory).Assembly);

			if (config.Exists)
			{
				log4net.Config.XmlConfigurator.Configure(repository, config);
			}
			else
			{
				ConfigureDefaultLogging(repository);
			}

			CreateRepository(Constants.LoggingRepositoryName);
			CreateRepository(Constants.LoggingObservationRepositoryName);

			// A logger that quietly writes nowhere is worse than none: it makes every later
			// problem undiagnosable. Write one line and check it actually landed on disk.
			LoggerFactory.GetLogger().Info("Logging initialised");
			ReportLoggingProblems(repository, config);
		}

		/// <summary>
		/// Sets up logging without a configuration file, so that the executable on its own is
		/// enough. This is a single file build: it gets copied somewhere by itself far more often
		/// than with the folder it was published in, and log.config left behind used to mean no
		/// log at all - the previous fallback wrote to a console FreeConsole has already closed.
		///
		/// log.config stays an override for whoever wants one; it is the only way to send the
		/// observation logger to a file of its own, which this default does not do.
		/// </summary>
		private static void ConfigureDefaultLogging(log4net.Repository.ILoggerRepository repository)
		{
			var layout = new log4net.Layout.PatternLayout(
				"[%date{dd.MM. HH:mm:ss.fff}] %-5level - %C{1}.%M - %message%newline");
			layout.ActivateOptions();

			var appender = new log4net.Appender.RollingFileAppender
			{
				Name = "RollingFile",
				File = Path.Combine(DiagnosticsDirectory(), "logs", "TwinCatAdsTool.log"),
				AppendToFile = true,
				RollingStyle = log4net.Appender.RollingFileAppender.RollingMode.Size,
				MaxSizeRollBackups = 10,
				MaximumFileSize = "10MB",
				StaticLogFileName = true,
				Encoding = Encoding.UTF8,
				Layout = layout
			};

			appender.ActivateOptions();

			log4net.Config.BasicConfigurator.Configure(repository, appender);

			if (repository is log4net.Repository.Hierarchy.Hierarchy hierarchy)
			{
				hierarchy.Root.Level = log4net.Core.Level.All;
			}
		}

		/// <summary>
		/// Leaves a note next to the executable when the configured file appenders did not
		/// produce a file, so a missing log folder can be explained instead of guessed at.
		/// </summary>
		private static void ReportLoggingProblems(log4net.Repository.ILoggerRepository repository, FileInfo config)
		{
			try
			{
				var appenders = repository.GetAppenders()
					.OfType<log4net.Appender.FileAppender>()
					.ToList();

				var missing = appenders.Where(a => !File.Exists(a.File)).ToList();

				if (appenders.Count > 0 && missing.Count == 0)
				{
					return;
				}

				var lines = new List<string>
				{
					$"TwinCatAdsTool {Constants.Version} - logging is not writing any file",
					$"{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
					$"config    : {config.FullName} (exists: {config.Exists})",
					$"repository: {repository.Name}",
					$"log folder: {DiagnosticsDirectory()}",
					$"appenders : {appenders.Count}"
				};

				foreach (var appender in missing)
				{
					lines.Add($"  no file at: {appender.File}");

					// An appender that cannot open its file keeps the reason to itself: it is
					// handed to the error handler and reported nowhere else. Without this the
					// note would say a file is missing without ever saying why.
					if (appender.ErrorHandler is log4net.Util.OnlyOnceErrorHandler handler &&
					    !string.IsNullOrEmpty(handler.ErrorMessage))
					{
						lines.Add($"    reason  : {handler.ErrorMessage}");
					}
				}

				if (appenders.Count == 0)
				{
					lines.Add("  no file appender was configured at all");
				}

				File.WriteAllText(Path.Combine(DiagnosticsDirectory(), "logging-error.txt"),
					string.Join(Environment.NewLine, lines) + Environment.NewLine,
					Encoding.UTF8);
			}
			catch (Exception)
			{
				// Read only folder: nothing further can be done from here.
			}
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
