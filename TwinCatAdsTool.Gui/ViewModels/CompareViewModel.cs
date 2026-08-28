using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCatAdsTool.Gui.Models;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Comparison;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Services;

namespace TwinCatAdsTool.Gui.ViewModels
{
    /// <summary>
    /// Compares two backups value by value, and carries chosen values from one side onto the plc.
    ///
    /// It used to compare the two files as text. That answers a different question - whether the
    /// files were written the same way - so key order, spacing and the width a number was printed
    /// at all showed up as changes to the plant, and a line of a diff names nothing the tool could
    /// act on. Comparing leaf by leaf gives every row a plc path, which is both the honest answer
    /// and the thing a write has to be addressed to.
    /// </summary>
    public class CompareViewModel : ViewModelBase
    {
        private readonly IClientService clientService;
        private readonly IPersistentVariableService persistentVariableService;

        private JObject leftJson;
        private JObject rightJson;
        private IReadOnlyList<ValueDifference> allValues = new List<ValueDifference>();
        private IReadOnlyList<ValueDifference> rows = new List<ValueDifference>();
        private ValueDifference selectedRow;
        private string sourceLeft;
        private string sourceRight;
        private bool leftIsPlc;
        private bool rightIsPlc;
        private bool isConnected;
        private bool onlyDifferences = true;
        private int differenceCount;
        private int markedCount;
        private bool hasComparison;

        public CompareViewModel(IClientService clientService, IPersistentVariableService persistentVariableService)
        {
            this.clientService = clientService;
            this.persistentVariableService = persistentVariableService;
        }

        /// <summary>The rows on screen: either every value of both backups, or only what differs.</summary>
        public IReadOnlyList<ValueDifference> Rows
        {
            get => rows;
            private set
            {
                rows = value;
                raisePropertyChanged();
            }
        }

        public ValueDifference SelectedRow
        {
            get => selectedRow;
            set
            {
                if (ReferenceEquals(value, selectedRow)) return;
                selectedRow = value;
                raisePropertyChanged();
            }
        }

        public string SourceLeft
        {
            get => sourceLeft ?? "";
            set
            {
                if (value == sourceLeft) return;
                sourceLeft = value;
                raisePropertyChanged();
            }
        }

        public string SourceRight
        {
            get => sourceRight ?? "";
            set
            {
                if (value == sourceRight) return;
                sourceRight = value;
                raisePropertyChanged();
            }
        }

        /// <summary>
        /// Values that differ between the two sides. Scrolling through a few thousand of them to
        /// find out whether there is any difference at all is not a reasonable way to answer that.
        /// </summary>
        public int DifferenceCount
        {
            get => differenceCount;
            private set
            {
                if (value == differenceCount) return;
                differenceCount = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(AreIdentical));
            }
        }

        /// <summary>How many values have been picked to be written, but not written yet.</summary>
        public int MarkedCount
        {
            get => markedCount;
            private set
            {
                if (value == markedCount) return;
                markedCount = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(HasMarks));
                raisePropertyChanged(nameof(CanApply));
            }
        }

        public bool HasMarks => MarkedCount > 0;

        /// <summary>True once both sides hold a backup to compare.</summary>
        public bool HasComparison
        {
            get => hasComparison;
            private set
            {
                if (value == hasComparison) return;
                hasComparison = value;
                raisePropertyChanged();
            }
        }

        public bool AreIdentical => DifferenceCount == 0;

        /// <summary>
        /// Hides everything the two sides agree on. Off, the window lists every value of the
        /// backup, which is how a value that has *not* moved is confirmed to be where it should be.
        /// </summary>
        public bool OnlyDifferences
        {
            get => onlyDifferences;
            set
            {
                if (value == onlyDifferences) return;
                onlyDifferences = value;
                raisePropertyChanged();
                ShowRows();
            }
        }

        public bool LeftIsPlc
        {
            get => leftIsPlc;
            private set
            {
                if (value == leftIsPlc) return;
                leftIsPlc = value;
                RaiseMergeAvailability();
            }
        }

        public bool RightIsPlc
        {
            get => rightIsPlc;
            private set
            {
                if (value == rightIsPlc) return;
                rightIsPlc = value;
                RaiseMergeAvailability();
            }
        }

        public bool IsConnected
        {
            get => isConnected;
            private set
            {
                if (value == isConnected) return;
                isConnected = value;
                RaiseMergeAvailability();
            }
        }

        /// <summary>
        /// Which way a value may travel. Only the plc can be written to - a backup file is left
        /// exactly as it was found - so at most one of the two directions is ever open, and which
        /// one it is depends on which side was read from the plc.
        /// </summary>
        private MergeMark PlcDirection
            => LeftIsPlc ? MergeMark.ToLeft : RightIsPlc ? MergeMark.ToRight : MergeMark.None;

        public bool CanMergeToLeft => PlcDirection == MergeMark.ToLeft && IsConnected;

        public bool CanMergeToRight => PlcDirection == MergeMark.ToRight && IsConnected;

        public bool CanApply => HasMarks && (CanMergeToLeft || CanMergeToRight);

        /// <summary>
        /// What is standing in the way, in one line. Null when nothing is: an empty tab that says
        /// nothing at all reads as a tab that has failed.
        /// </summary>
        public string MergeHint
        {
            get
            {
                if (!HasComparison)
                {
                    return leftJson == null && rightJson == null
                        ? null
                        : "One side is loaded. Read the plc or open a file on the other side to compare them.";
                }

                if (PlcDirection == MergeMark.None)
                {
                    return "Neither side was read from the plc, so there is nothing to write to. " +
                           "Read one side from the plc to be able to correct it from the other.";
                }

                return IsConnected ? null : "Not connected to the plc.";
            }
        }

        public bool HasMergeHint => !string.IsNullOrEmpty(MergeHint);

        public ReactiveCommand<RxVoid, RxVoid> LoadLeft { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> LoadRight { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ReadLeft { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> ReadRight { get; set; }

        public ReactiveCommand<ValueDifference, RxVoid> CmdCopyToLeft { get; set; }
        public ReactiveCommand<ValueDifference, RxVoid> CmdCopyToRight { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdCopyAllToLeft { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdCopyAllToRight { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdUndoAll { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdApply { get; set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdFirstChange { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdPreviousChange { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdNextChange { get; set; }
        public ReactiveCommand<RxVoid, RxVoid> CmdLastChange { get; set; }

        public override void Init()
        {
            clientService.ConnectionState
                .Select(state => state == ConnectionState.Connected)
                .ObserveOnDispatcher()
                .Retry()
                .Subscribe(connected => IsConnected = connected)
                .AddDisposableTo(Disposables);

            AssignCommands();
        }

        private void AssignCommands()
        {
            var connected = clientService.ConnectionState.Select(state => state == ConnectionState.Connected);

            ReadLeft = ReactiveCommand.CreateFromTask(ReadVariablesLeft, canExecute: connected)
                .AddDisposableTo(Disposables);

            LoadLeft = ReactiveCommand.CreateFromTask(LoadJsonLeft)
                .AddDisposableTo(Disposables);

            ReadRight = ReactiveCommand.CreateFromTask(ReadVariablesRight, canExecute: connected)
                .AddDisposableTo(Disposables);

            LoadRight = ReactiveCommand.CreateFromTask(LoadJsonRight)
                .AddDisposableTo(Disposables);

            CmdCopyToLeft = ReactiveCommand.Create<ValueDifference, RxVoid>(row => Mark(row, MergeMark.ToLeft))
                .AddDisposableTo(Disposables);

            CmdCopyToRight = ReactiveCommand.Create<ValueDifference, RxVoid>(row => Mark(row, MergeMark.ToRight))
                .AddDisposableTo(Disposables);

            CmdCopyAllToLeft = ReactiveCommand.Create(() => MarkAll(MergeMark.ToLeft))
                .AddDisposableTo(Disposables);

            CmdCopyAllToRight = ReactiveCommand.Create(() => MarkAll(MergeMark.ToRight))
                .AddDisposableTo(Disposables);

            CmdUndoAll = ReactiveCommand.Create(UndoAll)
                .AddDisposableTo(Disposables);

            CmdApply = ReactiveCommand.CreateFromTask(ApplyToPlc)
                .AddDisposableTo(Disposables);

            CmdFirstChange = ReactiveCommand.Create(() => GoToChange(-1, 1))
                .AddDisposableTo(Disposables);

            CmdPreviousChange = ReactiveCommand.Create(() => GoToChange(IndexOf(SelectedRow), -1))
                .AddDisposableTo(Disposables);

            CmdNextChange = ReactiveCommand.Create(() => GoToChange(IndexOf(SelectedRow), 1))
                .AddDisposableTo(Disposables);

            CmdLastChange = ReactiveCommand.Create(() => GoToChange(Rows.Count, -1))
                .AddDisposableTo(Disposables);
        }

        private void RaiseMergeAvailability()
        {
            raisePropertyChanged(nameof(LeftIsPlc));
            raisePropertyChanged(nameof(RightIsPlc));
            raisePropertyChanged(nameof(IsConnected));
            raisePropertyChanged(nameof(CanMergeToLeft));
            raisePropertyChanged(nameof(CanMergeToRight));
            raisePropertyChanged(nameof(CanApply));
            raisePropertyChanged(nameof(MergeHint));
            raisePropertyChanged(nameof(HasMergeHint));
        }

        // ---- comparing -----------------------------------------------------------------------

        private void SideLoaded(bool left, JObject json, string source, bool fromPlc)
        {
            if (left)
            {
                leftJson = json;
                SourceLeft = source;
                LeftIsPlc = fromPlc;
            }
            else
            {
                rightJson = json;
                SourceRight = source;
                RightIsPlc = fromPlc;
            }

            Compare();
        }

        private void Compare()
        {
            // Comparing against a side that is not there yet would report the whole of the other
            // one as missing, which says nothing and buries the moment the second side arrives.
            if (leftJson == null || rightJson == null)
            {
                allValues = new List<ValueDifference>();
                HasComparison = false;
            }
            else
            {
                allValues = JsonDifference.Compare(leftJson, rightJson)
                    .Select(entry => new ValueDifference(entry))
                    .ToList();
                HasComparison = true;
            }

            DifferenceCount = allValues.Count(value => value.IsDifferent);
            MarkedCount = 0;
            ShowRows();
            RaiseMergeAvailability();

            Logger.Debug($"Compared two backups - {allValues.Count} values, {DifferenceCount} of them differing");
        }

        /// <summary>
        /// Hands the view a whole new list rather than adding to and removing from one it is
        /// already showing. A backup of a real plant holds six figures of values, and a change
        /// notification apiece is the difference between a redraw and a freeze. The rows themselves
        /// are the same objects either way, so the marks survive the filter being switched.
        /// </summary>
        private void ShowRows()
        {
            var wanted = SelectedRow;

            Rows = OnlyDifferences
                ? allValues.Where(value => value.IsDifferent).ToList()
                : allValues;

            SelectedRow = IndexOf(wanted) >= 0 ? wanted : null;
        }

        // ---- marking -------------------------------------------------------------------------

        private RxVoid Mark(ValueDifference row, MergeMark direction)
        {
            if (row == null || !row.IsMergeable || direction != PlcDirection || !IsConnected)
            {
                return RxVoid.Default;
            }

            // Clicking the same arrow again takes the mark back, which is what makes a mark worth
            // placing: nothing has been written yet, so changing one's mind must cost nothing.
            row.Mark = row.Mark == direction ? MergeMark.None : direction;
            CountMarks();
            return RxVoid.Default;
        }

        private void MarkAll(MergeMark direction)
        {
            if (direction != PlcDirection || !IsConnected)
            {
                return;
            }

            foreach (var row in allValues.Where(value => value.IsDifferent && value.IsMergeable))
            {
                row.Mark = direction;
            }

            CountMarks();
        }

        private void UndoAll()
        {
            foreach (var row in allValues)
            {
                row.Mark = MergeMark.None;
            }

            CountMarks();
        }

        private void CountMarks() => MarkedCount = allValues.Count(value => value.IsMarked);

        // ---- moving between changes ----------------------------------------------------------

        /// <summary>Where a row sits among the ones on screen, or -1 when it is not among them.</summary>
        private int IndexOf(ValueDifference row)
        {
            if (row == null)
            {
                return -1;
            }

            for (var i = 0; i < Rows.Count; i++)
            {
                if (ReferenceEquals(Rows[i], row))
                {
                    return i;
                }
            }

            return -1;
        }

        private void GoToChange(int from, int step)
        {
            if (Rows.Count == 0)
            {
                return;
            }

            if (from < 0 && step < 0)
            {
                from = Rows.Count;
            }

            for (var i = from + step; i >= 0 && i < Rows.Count; i += step)
            {
                if (Rows[i].IsDifferent)
                {
                    SelectedRow = Rows[i];
                    return;
                }
            }
        }

        // ---- writing -------------------------------------------------------------------------

        private async Task ApplyToPlc()
        {
            var direction = PlcDirection;
            var marked = allValues.Where(value => value.Mark == direction && value.IsMarked).ToList();

            if (marked.Count == 0 || direction == MergeMark.None || !IsConnected)
            {
                return;
            }

            // The values come from the side that is not the plc: copying onto the left means the
            // left is to end up holding what the right one has.
            var source = direction == MergeMark.ToLeft ? rightJson : leftJson;
            var subset = JsonSubset.Prune(source, marked.Select(value => value.Path));
            var variables = subset.Properties().Count();

            if (variables == 0)
            {
                MessageBox.Show("None of the marked values could be found in the backup they come from.",
                    "Nothing to write", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"{Count(marked.Count, "value")} in {Count(variables, "persistent variable")} " +
                $"will be written to the plc.{Environment.NewLine}{Environment.NewLine}" +
                $"Everything else on the plc is left as it is. A value that has been overwritten " +
                $"cannot be brought back from here.",
                "Write to the plc",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                var report = await persistentVariableService
                    .WriteSelectedValues(clientService.Client, clientService.TreeViewSymbols, subset)
                    .ConfigureAwait(true);

                Logger.Info($"Merge finished - {report.Summary}");

                MessageBox.Show(
                    report.IsComplete
                        ? $"{Count(marked.Count, "value")} written ({report.Summary})."
                        : $"{report.Summary}.{Environment.NewLine}{Environment.NewLine}{report.Details()}",
                    report.IsComplete ? "Written" : "Not everything was written",
                    MessageBoxButton.OK,
                    report.IsComplete ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception e)
            {
                Logger.Error("Could not write the marked values to the plc", e);
                MessageBox.Show(e.Message, "Could not write to the plc",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Read the plc back rather than assume. What is on screen after a write has to be what
            // is on the machine, including anything the write could not place.
            if (direction == MergeMark.ToLeft)
            {
                await ReadVariablesLeft().ConfigureAwait(true);
            }
            else
            {
                await ReadVariablesRight().ConfigureAwait(true);
            }
        }

        private static string Count(int howMany, string noun)
            => howMany == 1 ? $"1 {noun}" : $"{howMany} {noun}s";

        // ---- loading the two sides -----------------------------------------------------------

        private (JObject Json, string Name) LoadJson()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Json files (*.json)|*.json",
                    RestoreDirectory = true
                };

                if (openFileDialog.ShowDialog() == true)
                {
                    var json = JObject.Parse(File.ReadAllText(openFileDialog.FileName));
                    Logger.Debug(string.Format(Resources.LoadOfFile0Wasuccesful, openFileDialog.FileName));
                    return (json, Path.GetFileName(openFileDialog.FileName));
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Resources.ErrorDuringLoadOfFile, ex);
                MessageBox.Show(ex.Message, "Could not read that file",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return (null, "");
        }

        private Task<RxVoid> LoadJsonLeft()
        {
            var (json, name) = LoadJson();
            if (json != null)
            {
                SideLoaded(true, json, name, false);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private Task<RxVoid> LoadJsonRight()
        {
            var (json, name) = LoadJson();
            if (json != null)
            {
                SideLoaded(false, json, name, false);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private async Task<JObject> ReadVariables()
        {
            var backup = await persistentVariableService.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols).ConfigureAwait(true);

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
            var json = await ReadVariables().ConfigureAwait(true);
            SideLoaded(true, json, "PLC", true);
        }

        private async Task ReadVariablesRight()
        {
            var json = await ReadVariables().ConfigureAwait(true);
            SideLoaded(false, json, "PLC", true);
        }
    }
}
