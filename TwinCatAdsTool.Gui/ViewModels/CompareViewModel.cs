using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.Windows;
using DiffPlex;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCatAdsTool.Gui.Models;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Services;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class CompareViewModel : ViewModelBase
    {
        private readonly Subject<string> leftTextSubject = new Subject<string>();
        private readonly Subject<string> rightTextSubject = new Subject<string>();
        private readonly IClientService clientService;
        private readonly SideBySideDiffBuilder comparisonBuilder = new SideBySideDiffBuilder(new Differ());
        private SideBySideDiffModel comparisonModel = new SideBySideDiffModel();
        private IReadOnlyList<DiffLine> leftLines = new List<DiffLine>();
        private readonly IPersistentVariableService persistentVariableService;
        private IReadOnlyList<DiffLine> rightLines = new List<DiffLine>();
        private string sourceLeft;
        private string sourceRight;
        private int differenceCount;
        private bool hasComparison;

        public CompareViewModel(IClientService clientService, IPersistentVariableService persistentVariableService)
        {
            this.clientService = clientService;
            this.persistentVariableService = persistentVariableService;
        }

        public IReadOnlyList<DiffLine> LeftLines
        {
            get => leftLines;
            set
            {
                if (Equals(value, leftLines))
                {
                    return;
                }

                leftLines = value;
                raisePropertyChanged();
            }
        }

        public string SourceLeft
        {
            get
            {
                if (sourceLeft == null)
                {
                    sourceLeft = "";
                }

                return sourceLeft;
            } set
            {
                if (value == sourceLeft)
                {
                    return;
                }

                sourceLeft = value;
                raisePropertyChanged();
            }
        }

        public string SourceRight
        {
            get
            {
                if (sourceRight == null)
                {
                    sourceRight = "";
                }

                return sourceRight;
            } set
            {
                if (value == sourceRight)
                {
                    return;
                }

                sourceRight = value;
                raisePropertyChanged();
            }
        }

        /// <summary>
        /// Lines that differ between the two sides. Scrolling through a few thousand lines to find
        /// out whether there is any difference at all is not a reasonable way to answer that.
        /// </summary>
        public int DifferenceCount
        {
            get => differenceCount;
            set
            {
                if (value == differenceCount) return;
                differenceCount = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(AreIdentical));
            }
        }

        /// <summary>True once both sides hold something to compare.</summary>
        public bool HasComparison
        {
            get => hasComparison;
            set
            {
                if (value == hasComparison) return;
                hasComparison = value;
                raisePropertyChanged();
            }
        }

        public bool AreIdentical => DifferenceCount == 0;

        public ReactiveCommand<RxVoid, RxVoid> LoadLeft { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> LoadRight { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ReadLeft { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ReadRight { get; set; }
        public IReadOnlyList<DiffLine> RightLines
        {
            get => rightLines;
            set
            {
                if (Equals(value, rightLines)) return;
                rightLines = value;
                raisePropertyChanged();
            }
        }


        public override void Init()
        {
            var x = leftTextSubject.StartWith("")
                .CombineLatest(rightTextSubject.StartWith(""),
                               (l, r) => comparisonModel = GenerateDiffModel(l, r));

            x.ObserveOnDispatcher()
                .Retry()
                .Subscribe()
                .AddDisposableTo(Disposables);

            AssignCommands();
        }

        private void AssignCommands()
        {
            ReadLeft = ReactiveCommand.CreateFromTask(ReadVariablesLeft,
                    canExecute: clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables);

            LoadLeft = ReactiveCommand.CreateFromTask(LoadJsonLeft)
                .AddDisposableTo(Disposables);


            ReadRight = ReactiveCommand.CreateFromTask(ReadVariablesRight,
                    canExecute: clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables);

            LoadRight = ReactiveCommand.CreateFromTask(LoadJsonRight)
                .AddDisposableTo(Disposables);
        }

        private SideBySideDiffModel GenerateDiffModel(string left, string right)
        {
            var diffModel = comparisonBuilder.BuildDiffModel(left, right);


            var leftBox = diffModel.OldText.Lines;
            var rightBox = diffModel.NewText.Lines;

            // Every row is the same fixed height, which is what lets the two panes be kept in
            // step by line number rather than by pixel.
            LeftLines = leftBox.Select(ToLine).ToList();
            RightLines = rightBox.Select(ToLine).ToList();

            DifferenceCount = rightBox.Count(line => line.Type != ChangeType.Unchanged);
            HasComparison = !string.IsNullOrEmpty(left) || !string.IsNullOrEmpty(right);

            Logger.Debug($"Generated Comparison Model - {DifferenceCount} differing lines");
            return diffModel;
        }

        private static DiffLine ToLine(DiffPiece piece)
        {
            return new DiffLine(piece.Text, KindOf(piece.Type));
        }

        private static DiffKind KindOf(ChangeType type)
        {
            switch (type)
            {
                case ChangeType.Deleted:
                    return DiffKind.Deleted;
                case ChangeType.Inserted:
                    return DiffKind.Inserted;
                case ChangeType.Modified:
                    return DiffKind.Modified;
                case ChangeType.Imaginary:
                    return DiffKind.Filler;
                default:
                    return DiffKind.Unchanged;
            }
        }

        private Task<(JObject, string)> LoadJson()
        {
            try
            {
                OpenFileDialog openFileDialog = new OpenFileDialog();
                openFileDialog.Filter = "Json files (*.json)|*.json";
                openFileDialog.RestoreDirectory = true;
                if (openFileDialog.ShowDialog() == true)
                {
                    JObject json = JObject.Parse(File.ReadAllText(openFileDialog.FileName));
                    Logger.Debug(string.Format(Resources.LoadOfFile0Wasuccesful, openFileDialog.FileName));
                    return Task.FromResult((json, System.IO.Path.GetFileName(openFileDialog.FileName)));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Resources.ErrorDuringLoadOfFile, ex);
            }

            return Task.FromResult<(JObject, string)>((null, ""));
        }


        private Task LoadJsonLeft()
        {
            var (json, fileName) = LoadJson().Result;
            if (json != null)
            {
                leftTextSubject.OnNext(json.ToString());
                SourceLeft = fileName;
                Logger.Debug(Resources.UpdatedLeftTextBox);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private Task LoadJsonRight()
        {
            var (json, fileName) = LoadJson().Result;
            if (json != null)
            {
                rightTextSubject.OnNext(json.ToString());
                SourceRight = fileName;
                Logger.Debug(Resources.UpdatedRightTextBox);
            }


            return Task.FromResult(RxVoid.Default);
        }

        private async Task<JObject> ReadVariables()
        {
            var backup = await persistentVariableService.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols);

            Logger.Debug($"{Resources.ReadPersistentVariables} - {backup.Report.Summary}");

            // Comparing against a partial read would show differences that are not real.
            if (!backup.Report.IsComplete)
            {
                MessageBox.Show(
                    $"{backup.Report.Summary}.{Environment.NewLine}{Environment.NewLine}" +
                    "Variables that could not be read are missing from this side of the " +
                    "comparison and will show up as differences.",
                    "Incomplete read",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }

            return backup.Data;
        }

        private async Task ReadVariablesLeft()
        {
            var json = await ReadVariables().ConfigureAwait(false);
            leftTextSubject.OnNext(json.ToString());
            SourceLeft = "PLC";

            Logger.Debug(Resources.UpdatedLeftTextBox);
        }

        private async Task ReadVariablesRight()
        {
            var json = await ReadVariables().ConfigureAwait(false);
            rightTextSubject.OnNext(json.ToString());
            SourceRight = "PLC";

            Logger.Debug(Resources.UpdatedRightTextBox);
        }
    }
}
