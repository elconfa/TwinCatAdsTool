using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class RestoreViewModel : ViewModelBase
    {
        private readonly BehaviorSubject<bool> canWrite = new BehaviorSubject<bool>(false);
        private readonly IClientService clientService;
        private readonly BehaviorSubject<JObject> fileVariableSubject = new BehaviorSubject<JObject>(new JObject());
        private readonly BehaviorSubject<JObject> liveVariableSubject = new BehaviorSubject<JObject>(new JObject());
        private readonly IPersistentVariableService persistentVariableService;
        private ObservableCollection<VariableViewModel> displayVariables;
        private ObservableCollection<VariableViewModel> fileVariables;
        private ObservableCollection<VariableViewModel> liveVariables;
        private PersistentOperationReport lastReport;
        private string lastReportSummary;

        public RestoreViewModel(IClientService clientService, IPersistentVariableService persistentVariableService)
        {
            this.clientService = clientService;
            this.persistentVariableService = persistentVariableService;
        }

        public ObservableCollection<VariableViewModel> DisplayVariables
        {
            get => displayVariables ?? (displayVariables = new ObservableCollection<VariableViewModel>());
            set
            {
                if (value == displayVariables)
                {
                    return;
                }

                liveVariables = value;
                raisePropertyChanged();
            }
        }

        public ObservableCollection<VariableViewModel> FileVariables
        {
            get => fileVariables ?? (fileVariables = new ObservableCollection<VariableViewModel>());
            set
            {
                if (value == fileVariables)
                {
                    return;
                }

                fileVariables = value;
                raisePropertyChanged();
            }
        }

        public ObservableCollection<VariableViewModel> LiveVariables
        {
            get => liveVariables ?? (liveVariables = new ObservableCollection<VariableViewModel>());
            set
            {
                if (value == liveVariables) return;
                liveVariables = value;
                raisePropertyChanged();
            }
        }

        public ReactiveCommand<RxVoid, RxVoid> Load { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> Write { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ShowReport { get; set; }

        /// <summary>
        /// Outcome of the last restore. Every persistent variable of the plc is accounted for
        /// here, including the ones the backup file did not cover.
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

        public override void Init()
        {
            fileVariableSubject
                .ObserveOnDispatcher()
                .Do(x => UpdateVariables(x, FileVariables))
                .Do(x => UpdateDisplayIfMatching())
                .Retry()
                .Subscribe()
                .AddDisposableTo(Disposables)
                ;

            canWrite.Subscribe().AddDisposableTo(Disposables);


            Load = ReactiveCommand.CreateFromTask(LoadVariables, canExecute: clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables);

            Write = ReactiveCommand.CreateFromTask(WriteVariables, canWrite.Select(x => x))
                .AddDisposableTo(Disposables);

            ShowReport = ReactiveCommand.CreateFromTask(ShowLastReport)
                .AddDisposableTo(Disposables);
        }

        private void AddVariable(IEnumerable<JProperty> token, ObservableCollection<VariableViewModel> variables)
        {
            try
            {
                foreach (var prop in token)
                {
                    if (prop.Value is JObject)
                    {
                        var variable = new VariableViewModel();
                        variable.Name = prop.Name;
                        variable.Json = prop.Value.ToString();
                        variables.Add(variable);
                    }
                }
            }
            finally
            {
                raisePropertyChanged("LiveVariables");
            }
        }


        private async Task<RxVoid> LoadVariables()
        {
            await LoadVariablesFromFile();

            return RxVoid.Default;
        }

        private Task<RxVoid> LoadVariablesFromFile()
        {
            OpenFileDialog openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "Json files (*.json)|*.json";
            openFileDialog.RestoreDirectory = true;
            if (openFileDialog.ShowDialog() == true)
            {
                JObject json = JObject.Parse(File.ReadAllText(openFileDialog.FileName));
                fileVariableSubject.OnNext(json);
                canWrite.OnNext(true);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private void UpdateDisplayIfMatching()
        {
            DisplayVariables.Clear();
            var array = new VariableViewModel[FileVariables.Count];
            FileVariables.CopyTo(array, 0);
            DisplayVariables.AddRange(array);

            raisePropertyChanged("DisplayVariables");
        }

        private void UpdateVariables(JObject json, ObservableCollection<VariableViewModel> viewModels)
        {
            viewModels.Clear();
            AddVariable(json.Properties(), viewModels);
            Logger.Debug(Resources.UpdatedRestoreView);
        }

        private async Task<RxVoid> WriteVariables()
        {
            var backup = fileVariableSubject.Value;

            if (backup == null || !backup.Properties().Any())
            {
                MessageBox.Show("Load a backup file first.", "Nothing to restore",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return RxVoid.Default;
            }

            var messageBoxResult = MessageBox.Show(Resources.AreYouSureYouWantToOverwriteTheLiveVariablesOnThePLC,
                Resources.OverwriteConfirmation, MessageBoxButton.YesNo);

            if (messageBoxResult != MessageBoxResult.Yes)
            {
                return RxVoid.Default;
            }

            var report = await persistentVariableService.WritePersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols,
                backup);

            lastReport = report;
            LastReportSummary = report.Summary;
            Logger.Debug($"Restore finished - {report.Summary}");

            // Anything that was not written has to reach the user, not just the log file.
            MessageBox.Show(
                report.IsComplete
                    ? $"All persistent variables were restored ({report.Summary})."
                    : $"{report.Summary}.{Environment.NewLine}{Environment.NewLine}" +
                      $"{Preview(report)}{Environment.NewLine}{Environment.NewLine}" +
                      "Use 'Report' for the full list.",
                report.IsComplete ? "Restore complete" : "Restore incomplete",
                MessageBoxButton.OK,
                report.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);

            return RxVoid.Default;
        }

        private Task<RxVoid> ShowLastReport()
        {
            MessageBox.Show(
                lastReport == null
                    ? "Nothing has been restored yet."
                    : $"{lastReport.Summary}{Environment.NewLine}{Environment.NewLine}{lastReport.Details()}",
                "Restore report",
                MessageBoxButton.OK,
                lastReport == null || lastReport.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);

            return Task.FromResult(RxVoid.Default);
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
    }
}