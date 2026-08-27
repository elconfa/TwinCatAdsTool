using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.ComponentModel;
using System.Linq;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Reactive.Linq;
using System.Windows;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using ReactiveUI;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Scope;
using TwinCatAdsTool.Gui.Models;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Wpf.Ui.Appearance;
using Color = System.Windows.Media.Color;

namespace TwinCatAdsTool.Gui.ViewModels
{
    /// <summary>
    /// A scope over the observed symbols. What is recorded and what is on screen are two different
    /// spans: the recording is kept for <see cref="History"/>, the plot shows the last
    /// <see cref="Span"/> of it, and the two are only tied together while the view is following the
    /// live edge. That separation is what makes it possible to stop, scroll back and zoom into what
    /// just happened - with a single sliding window, which is what this used to be, the past was
    /// discarded as it left the screen and there was nothing to go back to.
    /// </summary>
    public class GraphViewModel : ViewModelBase
    {
        /// <summary>A lane narrow enough that several digitals fit under the analogue signals.</summary>
        private const double DigitalLaneHeight = 0.12;

        private const double LaneGap = 0.02;

        private static readonly TimeSpan NarrowestSpan = TimeSpan.FromMilliseconds(200);

        /// <summary>
        /// What ads uses when nobody says otherwise, and therefore what every reading in this tool
        /// has always been sampled at.
        /// </summary>
        private const double DefaultSampleMilliseconds = 200;

        private readonly Dictionary<string, SignalTrace> traces = new Dictionary<string, SignalTrace>();
        private readonly Dictionary<string, DataPointSeries> series = new Dictionary<string, DataPointSeries>();
        private readonly ObservableCollection<SymbolObservationViewModel> symbols = new ObservableCollection<SymbolObservationViewModel>();

        private PlotModel plotModel;

        /// <summary>
        /// The axis along the bottom, held rather than looked up. Looking it up by type is a trap:
        /// DateTimeAxis derives from LinearAxis, so a search for the value axes finds it too.
        /// </summary>
        private DateTimeAxis timeAxis;
        private TimeSpan history = TimeSpan.FromMinutes(10);
        private TimeSpan span = TimeSpan.FromMinutes(1);
        private DateTime end = DateTime.Now;
        private bool isRecording = true;
        private bool isFollowing = true;
        private double sampleMilliseconds = DefaultSampleMilliseconds;
        private SymbolObservationViewModel triggerSymbol;
        private TriggerEdge triggerEdge = TriggerEdge.GoesTrue;
        private double triggerLevel;
        private bool isArmed;
        private DateTime? triggeredAt;

        /// <summary>
        /// When the capture that a trigger started is to be closed. A trigger that stopped the
        /// recording at the instant it fired would show only what led up to the event and nothing of
        /// what followed, which is usually the half worth having.
        /// </summary>
        private DateTime? holdUntil;

        /// <summary>How far back the recording is kept. Everything older is dropped.</summary>
        public TimeSpan History
        {
            get => history;
            set
            {
                history = value < NarrowestSpan ? NarrowestSpan : value;

                if (span > history)
                {
                    Span = history;
                }

                raisePropertyChanged();
            }
        }

        /// <summary>How much of the recording the plot shows.</summary>
        public TimeSpan Span
        {
            get => span;
            set
            {
                var wanted = value < NarrowestSpan ? NarrowestSpan : value;
                span = wanted > history ? history : wanted;
                raisePropertyChanged();
                Redraw();
            }
        }

        /// <summary>
        /// Whether readings are being added to the recording. Stopping freezes what is on screen
        /// without discarding it, which is the only way to read a transition that has already passed.
        /// </summary>
        public bool IsRecording
        {
            get => isRecording;
            set
            {
                isRecording = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(RecordingLabel));
            }
        }

        /// <summary>Whether the right hand edge of the plot is pinned to now.</summary>
        public bool IsFollowing
        {
            get => isFollowing;
            set
            {
                isFollowing = value;
                raisePropertyChanged();
            }
        }

        public string RecordingLabel => IsRecording ? "Stop" : "Start";

        /// <summary>
        /// How often the plc is asked to look at the plotted symbols. It is the shortest event that
        /// can appear on the plot at all: zooming below it magnifies the steps between readings and
        /// shows nothing that was not already there.
        /// </summary>
        public double SampleMilliseconds
        {
            get => sampleMilliseconds;
            set
            {
                sampleMilliseconds = Math.Min(Math.Max(Math.Round(value), 1), 60000);
                raisePropertyChanged();

                foreach (var symbol in symbols)
                {
                    symbol.Resample((int)sampleMilliseconds);
                }
            }
        }

        public PlotModel PlotModel
        {
            get => plotModel;
            set
            {
                plotModel = value;
                raisePropertyChanged();
            }
        }

        public ReactiveCommand<RxVoid, RxVoid> CmdToggleRecording { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdZoomIn { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdZoomOut { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdPanBack { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdPanForward { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdGoLive { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdClear { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdExportCsv { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdExportImage { get; private set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdToggleArmed { get; private set; }

        /// <summary>The signal the trigger watches. Only what is on the plot can be waited on.</summary>
        public SymbolObservationViewModel TriggerSymbol
        {
            get => triggerSymbol;
            set
            {
                triggerSymbol = value;
                raisePropertyChanged();
            }
        }

        public TriggerEdge TriggerEdge
        {
            get => triggerEdge;
            set
            {
                triggerEdge = value;
                raisePropertyChanged();
            }
        }

        /// <summary>The level the two crossing conditions are measured against.</summary>
        public double TriggerLevel
        {
            get => triggerLevel;
            set
            {
                triggerLevel = value;
                raisePropertyChanged();
            }
        }

        public bool IsArmed
        {
            get => isArmed;
            set
            {
                isArmed = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(TriggerState));
            }
        }

        public bool HasTriggered => triggeredAt.HasValue;

        public string TriggerState
        {
            get
            {
                if (triggeredAt.HasValue)
                {
                    return $"Triggered at {triggeredAt.Value:HH:mm:ss.fff}";
                }

                return IsArmed ? "Waiting" : string.Empty;
            }
        }

        /// <summary>The conditions offered, worded the way a plc programmer would say them.</summary>
        public IReadOnlyList<TriggerChoice> TriggerEdges { get; } = new[]
        {
            new TriggerChoice(TriggerEdge.GoesTrue, "goes TRUE"),
            new TriggerChoice(TriggerEdge.GoesFalse, "goes FALSE"),
            new TriggerChoice(TriggerEdge.RisesAbove, "rises above"),
            new TriggerChoice(TriggerEdge.FallsBelow, "falls below")
        };

        public override void Init()
        {
            PlotModel = CreateDefaultPlotModel();
            timeAxis = PlotModel.Axes.OfType<DateTimeAxis>().First();

            // Oxyplot draws with its own colours, so it has to be told about the theme switch.
            ApplicationThemeManager.Changed += OnApplicationThemeChanged;
            ApplyPlotTheme();

            AssignCommands();

            // The plot is rebuilt from the recording rather than appended to, so the cost of a redraw
            // is set by how much is on screen and not by how long the tool has been running. Only the
            // live edge needs a clock: once the view is frozen it changes only when asked to.
            Observable.Interval(TimeSpan.FromMilliseconds(100))
                .Where(_ => IsFollowing)
                .ObserveOnDispatcher()
                .Subscribe(_ => Redraw())
                .AddDisposableTo(Disposables);
        }

        public void AddSymbol(SymbolObservationViewModel symbol)
        {
            if (traces.ContainsKey(symbol.FullName))
            {
                return;
            }

            var trace = new SignalTrace(symbol.FullName, IsDigital(symbol));
            traces[symbol.FullName] = trace;
            symbols.Add(symbol);

            symbol.Resample((int)sampleMilliseconds);

            if (TryReadAsNumber(symbol.Value, out var initial))
            {
                trace.Record(DateTime.Now, initial);
            }

            Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                    handler => handler.Invoke,
                    h => symbol.PropertyChanged += h,
                    h => symbol.PropertyChanged -= h)
                .Where(args => args.EventArgs.PropertyName == nameof(SymbolObservationViewModel.Value))
                .Subscribe(_ => Record(symbol))
                .AddDisposableTo(Disposables);

            RebuildAxesAndSeries();
        }

        public void RemoveSymbol(SymbolObservationViewModel symbol)
        {
            if (!traces.Remove(symbol.FullName))
            {
                return;
            }

            var plotted = symbols.FirstOrDefault(candidate => candidate.FullName == symbol.FullName);
            if (plotted != null)
            {
                symbols.Remove(plotted);
            }

            // A trigger waiting on a signal that is no longer on the plot would never fire and would
            // give no sign of why.
            if (TriggerSymbol != null && TriggerSymbol.FullName == symbol.FullName)
            {
                TriggerSymbol = null;
                IsArmed = false;
            }

            RebuildAxesAndSeries();
        }

        /// <summary>The symbols on the plot, in the order they were added.</summary>
        public ObservableCollection<SymbolObservationViewModel> Symbols => symbols;

        private void AssignCommands()
        {
            CmdToggleRecording = ReactiveCommand.Create(() =>
            {
                IsRecording = !IsRecording;

                // Stopping holds the view where it is - a scope that carries on scrolling into empty
                // time after stop has thrown away the very thing that was being looked at. Starting
                // again returns to the live edge rather than to where the frozen view was left.
                IsFollowing = IsRecording;
                Redraw();
            }).AddDisposableTo(Disposables);

            CmdZoomIn = ReactiveCommand.Create(() => { Span = TimeSpan.FromTicks(span.Ticks / 2); }).AddDisposableTo(Disposables);
            CmdZoomOut = ReactiveCommand.Create(() => { Span = TimeSpan.FromTicks(span.Ticks * 2); }).AddDisposableTo(Disposables);
            CmdPanBack = ReactiveCommand.Create(() => Pan(-1)).AddDisposableTo(Disposables);
            CmdPanForward = ReactiveCommand.Create(() => Pan(1)).AddDisposableTo(Disposables);

            CmdGoLive = ReactiveCommand.Create(() =>
            {
                IsFollowing = true;
                Redraw();
            }).AddDisposableTo(Disposables);

            CmdToggleArmed = ReactiveCommand.Create(() =>
            {
                if (IsArmed)
                {
                    IsArmed = false;
                    holdUntil = null;
                    return;
                }

                // Arming means waiting for something to happen from now on, so the recording has to
                // be running and looking at the live edge for there to be anything to wait for.
                triggeredAt = null;
                holdUntil = null;
                IsRecording = true;
                IsFollowing = true;
                IsArmed = true;
                raisePropertyChanged(nameof(HasTriggered));
                Redraw();
            }).AddDisposableTo(Disposables);

            CmdExportCsv = ReactiveCommand.Create(ExportCsv).AddDisposableTo(Disposables);
            CmdExportImage = ReactiveCommand.Create(ExportImage).AddDisposableTo(Disposables);

            CmdClear = ReactiveCommand.Create(() =>
            {
                foreach (var trace in traces.Values)
                {
                    trace.Clear();
                }

                IsFollowing = true;
                Redraw();
            }).AddDisposableTo(Disposables);
        }

        /// <summary>
        /// Writes what is on screen, not the whole recording: the slice being looked at is the one
        /// that was chosen deliberately, and exporting more than that would silently throw away the
        /// framing. The bounds go in the file name so a capture can be told apart later.
        /// </summary>
        private void ExportCsv()
        {
            try
            {
                if (symbols.Count == 0)
                {
                    MessageBox.Show("There is nothing on the scope to export.", "Export", MessageBoxButton.OK);
                    return;
                }

                var start = end - span;
                var table = TraceTable.Build(symbols.Select(symbol => traces[symbol.FullName]), start, end);

                if (table.Rows.Count == 0)
                {
                    MessageBox.Show("Nothing was recorded in the window being shown.", "Export", MessageBoxButton.OK);
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Csv|*.csv",
                    Title = "Export the window being shown",
                    FileName = $"Scope_{start:yyyy-MM-dd-HHmmss}_{end:HHmmss}.csv",
                    RestoreDirectory = true
                };

                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, table.ToDelimitedText(CultureInfo.CurrentCulture));
                    Logger.Debug($"Exported {table.Rows.Count} rows to {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not export the capture", ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }
        }

        /// <summary>
        /// The plot as a picture, on white rather than on the window's own background: it is going
        /// into a mail or a report, where a transparent background comes out as whatever is behind it.
        /// </summary>
        private void ExportImage()
        {
            try
            {
                var dialog = new SaveFileDialog
                {
                    Filter = "Png|*.png",
                    Title = "Save the plot as a picture",
                    FileName = $"Scope_{DateTime.Now:yyyy-MM-dd-HHmmss}.png",
                    RestoreDirectory = true
                };

                if (dialog.ShowDialog() != true)
                {
                    return;
                }

                var exporter = new OxyPlot.Wpf.PngExporter
                {
                    Width = 1600,
                    Height = 900
                };

                // The plot is drawn transparent, on whatever the window is showing behind it, and its
                // chrome is coloured for the current theme. Neither survives being put in a file, so
                // the picture is taken on white with dark text and the theme is restored afterwards.
                PlotModel.Background = OxyColors.White;
                PlotModel.TextColor = OxyColors.Black;
                PlotModel.PlotAreaBorderColor = OxyColors.Gray;

                using (var file = File.Create(dialog.FileName))
                {
                    exporter.Export(PlotModel, file);
                }

                ApplyPlotTheme();
                Logger.Debug($"Saved the plot to {dialog.FileName}");
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save the plot", ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }
        }

        /// <summary>
        /// Moves the window by a quarter of its width. Stepping by a fraction of the width rather than
        /// by a fixed time keeps the gesture meaning the same thing at every zoom level.
        /// </summary>
        public void Pan(int steps)
        {
            var moved = end.AddTicks(span.Ticks / 4 * steps);
            var now = DateTime.Now;

            if (moved >= now)
            {
                IsFollowing = true;
                Redraw();
                return;
            }

            IsFollowing = false;
            end = moved < Oldest ? Oldest : moved;
            Redraw();
        }

        /// <summary>Zooms about the right hand edge, so what is being looked at stays in view.</summary>
        public void Zoom(int steps)
        {
            Span = steps > 0
                ? TimeSpan.FromTicks(span.Ticks / 2)
                : TimeSpan.FromTicks(span.Ticks * 2);
        }

        private DateTime Oldest
        {
            get
            {
                var recorded = traces.Values.Select(trace => trace.FirstAt).Where(at => at.HasValue).ToList();
                return recorded.Count == 0 ? DateTime.Now : recorded.Min().Value;
            }
        }

        private void Record(SymbolObservationViewModel symbol)
        {
            if (!IsRecording || !traces.TryGetValue(symbol.FullName, out var trace))
            {
                return;
            }

            if (!TryReadAsNumber(symbol.Value, out var value))
            {
                return;
            }

            var now = DateTime.Now;
            var previous = trace.LastValue;

            trace.Record(now, value);
            trace.Forget(now - history);

            if (IsArmed && TriggerSymbol != null && TriggerSymbol.FullName == symbol.FullName &&
                new TriggerCondition(TriggerEdge, TriggerLevel).Fires(previous, value))
            {
                Fire(now);
            }
        }

        /// <summary>
        /// The trigger has seen what it was waiting for. The recording carries on for half a window
        /// so that what followed the event is captured too, and only then is everything held still,
        /// with the event itself in the middle of the plot.
        /// </summary>
        private void Fire(DateTime at)
        {
            triggeredAt = at;
            IsArmed = false;
            holdUntil = at.AddTicks(span.Ticks / 2);
            raisePropertyChanged(nameof(HasTriggered));
            raisePropertyChanged(nameof(TriggerState));
        }

        private void CloseTriggeredCapture()
        {
            holdUntil = null;
            IsRecording = false;
            IsFollowing = false;
            end = triggeredAt.Value.AddTicks(span.Ticks / 2);
        }

        /// <summary>
        /// A line where the trigger fired, so the event can be told from everything else on the plot
        /// once the window has been scrolled away from it.
        /// </summary>
        private void DrawTriggerMarker(DateTime start)
        {
            PlotModel.Annotations.Clear();

            if (!triggeredAt.HasValue || triggeredAt.Value < start || triggeredAt.Value > end)
            {
                return;
            }

            PlotModel.Annotations.Add(new LineAnnotation
            {
                Type = LineAnnotationType.Vertical,
                X = DateTimeAxis.ToDouble(triggeredAt.Value),
                Color = OxyColor.FromRgb(0xE8, 0x11, 0x23),
                LineStyle = LineStyle.Dash,
                StrokeThickness = 1.5,
                Text = "trigger",
                TextColor = OxyColor.FromRgb(0xE8, 0x11, 0x23),
                TextOrientation = AnnotationTextOrientation.Horizontal,
                TextVerticalAlignment = OxyPlot.VerticalAlignment.Top
            });
        }

        /// <summary>
        /// Rebuilds the whole plot from the recording. Everything the plot shows is derived - the
        /// series hold no state of their own - so freezing, scrolling and zooming are all the same
        /// operation on two numbers.
        /// </summary>
        private void Redraw()
        {
            if (PlotModel == null || timeAxis == null)
            {
                return;
            }

            if (holdUntil.HasValue && DateTime.Now >= holdUntil.Value)
            {
                CloseTriggeredCapture();
            }

            if (IsFollowing)
            {
                end = DateTime.Now;
            }

            var start = end - span;

            DrawTriggerMarker(start);

            timeAxis.Minimum = DateTimeAxis.ToDouble(start);
            timeAxis.Maximum = DateTimeAxis.ToDouble(end);

            // Zoomed into a couple of seconds, labels a second apart say nothing about where the
            // edges fell; across ten minutes, milliseconds are noise.
            timeAxis.StringFormat = span < TimeSpan.FromSeconds(10) ? "HH:mm:ss.fff" : "HH:mm:ss";

            foreach (var pair in series)
            {
                if (!traces.TryGetValue(pair.Key, out var trace))
                {
                    continue;
                }

                var points = pair.Value.Points;
                points.Clear();

                foreach (var sample in trace.Window(start, end))
                {
                    points.Add(DateTimeAxis.CreateDataPoint(sample.At, sample.Value));
                }

                // A signal holds its value until it changes, so the trace has to run on to the right
                // hand edge rather than stop at the last reading.
                if (points.Count > 0 && trace.LastAt < end)
                {
                    points.Add(DateTimeAxis.CreateDataPoint(end, trace.LastValue.Value));
                }
            }

            PlotModel.InvalidatePlot(true);
        }

        private static bool IsDigital(SymbolObservationViewModel symbol)
        {
            return string.Equals(symbol.Model?.TypeName, "BOOL", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryReadAsNumber(object value, out double number)
        {
            number = 0;

            if (value == null)
            {
                return false;
            }

            try
            {
                number = Convert.ToDouble(value);
                return true;
            }
            catch (Exception)
            {
                // A symbol whose value does not reduce to a number has no place on the plot; it is
                // kept in the watch list, which is where it is readable.
                return false;
            }
        }

        private static PlotModel CreateDefaultPlotModel()
        {
            // OxyPlot 2 moved the legend settings off the plot model into its Legends collection.
            var model = new PlotModel();

            model.Legends.Add(new Legend
            {
                LegendBorderThickness = 1,
                LegendPosition = LegendPosition.RightTop,
                LegendPlacement = LegendPlacement.Outside
            });

            model.Axes.Add(new DateTimeAxis
            {
                Position = AxisPosition.Bottom,
                StringFormat = "HH:mm:ss",
                IsZoomEnabled = false,
                IsPanEnabled = false
            });

            return model;
        }

        /// <summary>
        /// Gives every signal an axis and a series, and splits the plot area into lanes: one narrow
        /// lane per digital along the bottom, the rest shared by the analogue signals. A BOOL on the
        /// same scale as a position in millimetres is a flat line at the bottom of the plot, which is
        /// how the interlocks and the step bits - the things most worth watching on a machine - used
        /// to be unreadable.
        /// </summary>
        private void RebuildAxesAndSeries()
        {
            series.Clear();
            PlotModel.Series.Clear();

            // Everything except the axis along the bottom, which outlives any change to the signals.
            // Clearing by type used to take it with them - see the field - and the plot was left
            // without an x axis at all; oxyplot then built a plain one, which is why the times along
            // the bottom turned into a single unchanging number: the raw day count a date is stored as.
            foreach (var stale in PlotModel.Axes.OfType<LinearAxis>().Where(axis => !ReferenceEquals(axis, timeAxis)).ToList())
            {
                PlotModel.Axes.Remove(stale);
            }

            var digitals = symbols.Where(symbol => traces[symbol.FullName].IsDigital).ToList();
            var analogues = symbols.Where(symbol => !traces[symbol.FullName].IsDigital).ToList();

            var digitalBand = digitals.Count * (DigitalLaneHeight + LaneGap);
            var analogueFloor = analogues.Count == 0 ? 0 : Math.Min(digitalBand, 0.7);

            for (var i = 0; i < digitals.Count; i++)
            {
                var colour = PlotModel.DefaultColors[PlotModel.Series.Count % PlotModel.DefaultColors.Count];
                var bottom = i * (DigitalLaneHeight + LaneGap);

                PlotModel.Axes.Add(new LinearAxis
                {
                    Key = digitals[i].FullName,
                    Position = AxisPosition.Left,
                    StartPosition = bottom,
                    EndPosition = bottom + DigitalLaneHeight,
                    Minimum = -0.15,
                    Maximum = 1.15,
                    MajorStep = 1,
                    MinorStep = 1,
                    MajorTickSize = 0,
                    MinorTickSize = 0,
                    LabelFormatter = level => level >= 0.5 ? "1" : "0",
                    AxislineThickness = 1,
                    AxislineColor = colour,
                    TicklineColor = colour,
                    TextColor = colour,
                    IsZoomEnabled = false,
                    IsPanEnabled = false
                });

                AddSeries(new StairStepSeries
                {
                    Title = DisplayName(digitals[i]),
                    YAxisKey = digitals[i].FullName,
                    Color = colour,
                    StrokeThickness = 1.5,
                    VerticalStrokeThickness = 1.5
                }, digitals[i].FullName);
            }

            for (var i = 0; i < analogues.Count; i++)
            {
                var colour = PlotModel.DefaultColors[PlotModel.Series.Count % PlotModel.DefaultColors.Count];

                PlotModel.Axes.Add(new LinearAxis
                {
                    Key = analogues[i].FullName,
                    Position = AxisPosition.Left,
                    StartPosition = analogueFloor,
                    EndPosition = 1,
                    AxisDistance = i * 45,
                    AxislineThickness = 2,
                    AxislineColor = colour,
                    MinorTickSize = 4,
                    MajorTickSize = 7,
                    TicklineColor = colour,
                    TextColor = colour,
                    MinimumPadding = 0.1,
                    MaximumPadding = 0.1,
                    IsZoomEnabled = false,
                    IsPanEnabled = false
                });

                AddSeries(new LineSeries
                {
                    Title = DisplayName(analogues[i]),
                    YAxisKey = analogues[i].FullName,
                    Color = colour,
                    StrokeThickness = 1.5
                }, analogues[i].FullName);
            }

            ApplyPlotTheme();
            Redraw();
        }

        /// <summary>
        /// The instance name, which is what fits in a legend, unless another watched symbol carries
        /// the same one - two members called Position in different structures would otherwise appear
        /// as one signal. Traces themselves are keyed by the instance path, which is unique.
        /// </summary>
        private string DisplayName(SymbolObservationViewModel symbol)
        {
            return symbols.Count(candidate => candidate.Name == symbol.Name) > 1
                ? symbol.FullName
                : symbol.Name;
        }

        private void AddSeries(DataPointSeries added, string name)
        {
            series[name] = added;
            PlotModel.Series.Add(added);
        }

        private void OnApplicationThemeChanged(ApplicationTheme currentApplicationTheme, Color systemAccent)
        {
            ApplyPlotTheme();
        }

        /// <summary>
        /// Repaints the plot chrome in the colours of the active theme. A white legend on a dark
        /// window, which is what the fixed colours produced, is unreadable.
        /// </summary>
        private void ApplyPlotTheme()
        {
            if (PlotModel == null)
            {
                return;
            }

            var dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;

            var foreground = dark ? OxyColor.FromRgb(0xE6, 0xE6, 0xE6) : OxyColor.FromRgb(0x1B, 0x1B, 0x1B);
            var chrome = dark ? OxyColor.FromRgb(0x50, 0x50, 0x50) : OxyColor.FromRgb(0xC8, 0xC8, 0xC8);
            var surface = dark ? OxyColor.FromArgb(0xB0, 0x2B, 0x2B, 0x2B) : OxyColor.FromArgb(0xB0, 0xFF, 0xFF, 0xFF);

            PlotModel.Background = OxyColors.Transparent;
            PlotModel.TextColor = foreground;
            PlotModel.TitleColor = foreground;
            PlotModel.PlotAreaBorderColor = chrome;

            foreach (var legend in PlotModel.Legends)
            {
                legend.LegendBackground = surface;
                legend.LegendBorder = chrome;
                legend.TextColor = foreground;
            }

            if (timeAxis != null)
            {
                timeAxis.TicklineColor = chrome;
                timeAxis.TextColor = foreground;
                timeAxis.MajorGridlineColor = chrome;
                timeAxis.MinorGridlineColor = chrome;
            }

            PlotModel.InvalidatePlot(false);
            raisePropertyChanged(nameof(PlotModel));
        }

        protected override void Dispose(bool disposing)
        {
            ApplicationThemeManager.Changed -= OnApplicationThemeChanged;
            base.Dispose(disposing);
        }
    }
}
