using System;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;
using InfoBarSeverity = Wpf.Ui.Controls.InfoBarSeverity;
using MessageBox = System.Windows.MessageBox;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class BackupViewModel : ViewModelBase
    {
        private readonly IClientService clientService;
        private readonly IPersistentVariableService persistentVariableService;
        private readonly Subject<JObject> variableSubject = new Subject<JObject>();
        private string backupText;
        private string currentTask = string.Empty;
        private double progressPercentage;
        private bool isBusy;
        private string lastReportSummary;
        private bool hasReport;
        private InfoBarSeverity reportSeverity = InfoBarSeverity.Informational;
        private string reportTitle;
        private PersistentOperationReport lastReport;

        public BackupViewModel(IClientService clientService, IPersistentVariableService persistentVariableService)
        {
            this.clientService = clientService;
            this.persistentVariableService = persistentVariableService;
        }

        public string BackupText
        {
            get => backupText;
            set
            {
                if (value == backupText) return;
                backupText = value;
                raisePropertyChanged();
            }
        }

        /// <summary>
        /// Outcome of the last read, shown in the info bar so an incomplete backup cannot go
        /// unnoticed.
        /// </summary>
        public string LastReportSummary
        {
            get => lastReportSummary;
            set
            {
                if (value == lastReportSummary) return;
                lastReportSummary = value;
                raisePropertyChanged();
            }
        }

        /// <summary>The info bar stays out of the way until there is something to report.</summary>
        public bool HasReport
        {
            get => hasReport;
            set
            {
                if (value == hasReport) return;
                hasReport = value;
                raisePropertyChanged();
            }
        }

        public InfoBarSeverity ReportSeverity
        {
            get => reportSeverity;
            set
            {
                if (value == reportSeverity) return;
                reportSeverity = value;
                raisePropertyChanged();
            }
        }

        public string ReportTitle
        {
            get => reportTitle;
            set
            {
                if (value == reportTitle) return;
                reportTitle = value;
                raisePropertyChanged();
            }
        }

        /// <summary>What the read is doing right now, empty while nothing runs.</summary>
        public string CurrentTask
        {
            get => currentTask;
            private set
            {
                if (value == currentTask) return;
                currentTask = value;
                raisePropertyChanged();
            }
        }

        /// <summary>Share of the persistent variables already read, 0 to 100.</summary>
        public double ProgressPercentage
        {
            get => progressPercentage;
            private set
            {
                if (value.Equals(progressPercentage)) return;
                progressPercentage = value;
                raisePropertyChanged();
            }
        }

        /// <summary>True while a read is in flight: shows the progress bar and hides the info bar.</summary>
        public bool IsBusy
        {
            get => isBusy;
            private set
            {
                if (value == isBusy) return;
                isBusy = value;
                raisePropertyChanged();
            }
        }

        public ReactiveCommand<RxVoid, RxVoid> Read { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> Save { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ShowReport { get; set; }

        public override void Init()
        {
            variableSubject
                .ObserveOnDispatcher()
                .Do(o => BackupText = o.ToString(Formatting.Indented))
                .Retry()
                .Subscribe()
                .AddDisposableTo(Disposables)
                ;

            Read = ReactiveCommand.CreateFromTask(ReadVariables, canExecute: clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables);

            Save = ReactiveCommand.CreateFromTask(SaveVariables, clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables);

            ShowReport = ReactiveCommand.CreateFromTask(ShowLastReport)
                .AddDisposableTo(Disposables);

            persistentVariableService.CurrentTask
                .ObserveOnDispatcher()
                .Do(ShowProgress)
                .Retry()
                .Subscribe()
                .AddDisposableTo(Disposables);
        }

        private void ShowProgress(OperationProgress progress)
        {
            CurrentTask = progress?.Message ?? string.Empty;
            ProgressPercentage = progress?.Percentage ?? 0.0;
            IsBusy = progress?.IsRunning == true;
        }

        private async Task<RxVoid> ReadVariables()
        {
            var backup = await persistentVariableService.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols);

            lastReport = backup.Report;
            ShowReportOf(backup.Report);

            variableSubject.OnNext(backup.Data);
            Logger.Debug($"{Resources.ReadPersistentVariables} - {backup.Report.Summary}");

            // A backup that silently lost variables is worse than no backup at all, so say so.
            if (!backup.Report.IsComplete)
            {
                MessageBox.Show(
                    $"{backup.Report.Summary}.{Environment.NewLine}{Environment.NewLine}" +
                    $"{Preview(backup.Report)}{Environment.NewLine}{Environment.NewLine}" +
                    "This backup is incomplete. Use 'Report' for the full list.",
                    "Incomplete backup",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return RxVoid.Default;
        }

        private void ShowReportOf(PersistentOperationReport report)
        {
            LastReportSummary = report.Summary;
            ReportTitle = report.IsComplete ? "Backup complete" : "Backup incomplete";
            ReportSeverity = report.IsComplete ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
            HasReport = true;
        }

        private Task<RxVoid> ShowLastReport()
        {
            MessageBox.Show(
                lastReport == null
                    ? "No backup has been read yet."
                    : $"{lastReport.Summary}{Environment.NewLine}{Environment.NewLine}{lastReport.Details()}",
                "Backup report",
                MessageBoxButton.OK,
                lastReport == null || lastReport.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);

            return Task.FromResult(RxVoid.Default);
        }

        private static string Preview(PersistentOperationReport report)
        {
            const int maxLines = 10;
            var lines = report.Problems()
                .Take(maxLines)
                .Select(r => r.ToString())
                .ToList();

            var remaining = report.FailedCount + report.SkippedCount - lines.Count;
            if (remaining > 0)
            {
                lines.Add($"... and {remaining} more");
            }

            return string.Join(Environment.NewLine, lines);
        }

        private Task<RxVoid> SaveVariables()
        {
            if (string.IsNullOrEmpty(BackupText))
            {
                MessageBox.Show("There is nothing to save yet - read the variables first.", "Nothing to save",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return Task.FromResult(RxVoid.Default);
            }

            if (lastReport != null && !lastReport.IsComplete)
            {
                var proceed = MessageBox.Show(
                    $"The last read was incomplete ({lastReport.Summary}).{Environment.NewLine}" +
                    "Saving it will store a backup that does not cover every persistent variable." +
                    $"{Environment.NewLine}{Environment.NewLine}Save anyway?",
                    "Incomplete backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (proceed != MessageBoxResult.Yes)
                {
                    return Task.FromResult(RxVoid.Default);
                }
            }

            SaveFileDialog saveFileDialog1 = new SaveFileDialog();
            saveFileDialog1.Filter = "Json|*.json";
            saveFileDialog1.Title = "Save in a json file";
            saveFileDialog1.FileName = $"Backup_{DateTime.Now:yyy-MM-dd-HHmmss}.json";
            saveFileDialog1.RestoreDirectory = true;
            var result = saveFileDialog1.ShowDialog();
            if (result == DialogResult.OK || result == DialogResult.Yes)
            {
                File.WriteAllText(saveFileDialog1.FileName, BackupText);
                Logger.Debug(string.Format(Resources.SavedBackupTo0Logging, saveFileDialog1.FileName));
            }

            return Task.FromResult(RxVoid.Default);
        }
    }
}
