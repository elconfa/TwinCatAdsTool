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
using TwinCAT;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;
using MessageBox = System.Windows.MessageBox;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class BackupViewModel : ViewModelBase
    {
        private readonly IClientService clientService;
        private readonly IPersistentVariableService persistentVariableService;
        private readonly Subject<JObject> variableSubject = new Subject<JObject>();
        private string backupText;
        private ObservableAsPropertyHelper<string> currentTaskHelper;
        private string lastReportSummary;
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
        /// Outcome of the last read, shown next to the buttons so an incomplete backup cannot go
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

        public ReactiveCommand<Unit, Unit> Read { get; set; }
        public ReactiveCommand<Unit, Unit> Save { get; set; }
        public ReactiveCommand<Unit, Unit> ShowReport { get; set; }

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

            currentTaskHelper = persistentVariableService.CurrentTask.ToProperty(this, vm => vm.CurrentTask);
        }

        public string CurrentTask => currentTaskHelper.Value;

        private async Task<Unit> ReadVariables()
        {
            var backup = await persistentVariableService.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols);

            lastReport = backup.Report;
            LastReportSummary = backup.Report.Summary;

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

            return Unit.Default;
        }

        private Task<Unit> ShowLastReport()
        {
            MessageBox.Show(
                lastReport == null
                    ? "No backup has been read yet."
                    : $"{lastReport.Summary}{Environment.NewLine}{Environment.NewLine}{lastReport.Details()}",
                "Backup report",
                MessageBoxButton.OK,
                lastReport == null || lastReport.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);

            return Task.FromResult(Unit.Default);
        }

        private static string Preview(PersistentOperationReport report)
        {
            const int maxLines = 10;
            var lines = report.Results
                .Where(r => r.State != VariableOperationState.Succeeded)
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

        private Task<Unit> SaveVariables()
        {
            if (string.IsNullOrEmpty(BackupText))
            {
                MessageBox.Show("There is nothing to save yet - read the variables first.", "Nothing to save",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return Task.FromResult(Unit.Default);
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
                    return Task.FromResult(Unit.Default);
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

            return Task.FromResult(Unit.Default);
        }
    }
}
