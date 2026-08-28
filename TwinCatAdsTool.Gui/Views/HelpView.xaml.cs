using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using log4net;
using TwinCatAdsTool.Interfaces.Logging;

namespace TwinCatAdsTool.Gui.Views
{
    /// <summary>
    /// Interaction logic for HelpView.xaml
    /// </summary>
    public partial class HelpView : UserControl
    {
        private const string Manual = "TwinCatAdsTool-Manual.pdf";

        private const string Project = "https://github.com/elconfa/TwinCatAdsTool";

        private const string OnlineManual = Project + "/blob/upgrade/net8-modern-ui/docs/MANUAL.md";

        private const string Issues = Project + "/issues";

        private readonly ILog logger = LoggerFactory.GetLogger();

        public HelpView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Opens the manual that ships beside the executable, and falls back to the copy on github
        /// when it is not there - which is the normal state of affairs when someone has copied the
        /// executable on its own onto a cabinet pc.
        /// </summary>
        private void OpenTheManual(object sender, RoutedEventArgs e)
        {
            var beside = Path.Combine(AppContext.BaseDirectory, Manual);

            Open(File.Exists(beside) ? beside : OnlineManual);
        }

        private void OpenTheProject(object sender, RoutedEventArgs e) => Open(Project);

        private void ReportAProblem(object sender, RoutedEventArgs e) => Open(Issues);

        private void Open(string target)
        {
            try
            {
                // Without UseShellExecute a path is treated as an executable to run, and a url is
                // not treated as anything at all.
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception exception)
            {
                logger.Error($"Could not open {target}", exception);
                MessageBox.Show(exception.Message, "Help", MessageBoxButton.OK);
            }
        }
    }
}
