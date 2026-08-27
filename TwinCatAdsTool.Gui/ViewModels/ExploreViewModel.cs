using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.ComponentModel;
using System.Reactive.Linq;
using DynamicData.Binding;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using System.IO;
using System.Windows;
using Newtonsoft.Json;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCAT.Ads.TypeSystem;
using TwinCAT.TypeSystem;
using TwinCatAdsTool.Gui.Commands;
using TwinCatAdsTool.Gui.Models;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Commons;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Services;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class ExploreViewModel : ViewModelBase
    {
        private readonly IClientService clientService;
        private readonly ISelectionService<ISymbol> symbolSelection;

        private readonly Subject<ISymbolCollection<ISymbol>> variableSubject = new Subject<ISymbolCollection<ISymbol>>();

        private readonly IViewModelFactory viewModelFactory;
        private bool isConnected;
        private ObservableAsPropertyHelper<bool> isConnectedHelper;

        private ObservableCollection<IValueSymbol> observedSymbols;

        private string searchText;

        private ObservableCollection<ISymbol> treeNodes;

        /// <summary>
        /// The symbols a loaded watch set wants on the scope, until they turn up. A symbol reaches
        /// the list through a selection that is delivered on the dispatcher, so it does not exist
        /// yet when the file that asked for it has just been read.
        /// </summary>
        private readonly HashSet<string> awaitingGraph = new HashSet<string>();


        public ExploreViewModel(IClientService clientService,
            IViewModelFactory viewModelFactory, ISelectionService<ISymbol> symbolSelection)
        {
            this.clientService = clientService;
            this.viewModelFactory = viewModelFactory;
            this.symbolSelection = symbolSelection;
        }

        public ReactiveCommand<ISymbol, RxVoid> AddObserverCmd { get; set; }

        public ReactiveCommand<SymbolObservationViewModel, RxVoid> CmdAddGraph { get; set; }
        public ReactiveCommand<SymbolObservationViewModel, RxVoid> CmdDelete { get; set; }

        public ReactiveCommand<SymbolObservationViewModel, RxVoid> CmdRemoveGraph { get; set; }

        public GraphViewModel GraphViewModel { get; set; }

        public bool IsConnected
        {
            get { return isConnectedHelper.Value; }
            set
            {
                if (isConnectedHelper.Value == value)
                {
                    return;
                }

                isConnected = value;
                raisePropertyChanged();
            }
        }

        public ObservableCollection<IValueSymbol> ObservedSymbols
        {
            get => observedSymbols ?? (observedSymbols = new ObservableCollection<IValueSymbol>());
            set
            {
                if (value == observedSymbols) return;
                observedSymbols = value;
                raisePropertyChanged();
            }
        }

        public ObserverViewModel ObserverViewModel { get; set; }

        public ReactiveCommand<RxVoid, RxVoid> Read { get; set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdSaveWatchSet { get; set; }

        public ReactiveCommand<RxVoid, RxVoid> CmdLoadWatchSet { get; set; }

        public ObservableCollection<ISymbol> SearchResults { get; } = new ObservableCollection<ISymbol>();


        public string SearchText
        {
            get { return searchText; }
            set
            {
                if (searchText == value)
                {
                    return;
                }

                searchText = value;
                raisePropertyChanged();
            }
        }

        public ReactiveRelayCommand TextBoxEnterCommand { get; set; }

        public ObservableCollection<ISymbol> TreeNodes
        {
            get => treeNodes ?? (treeNodes = new ObservableCollection<ISymbol>());
            set
            {
                if (value == treeNodes)
                {
                    return;
                }

                treeNodes = value;
                raisePropertyChanged();
            }
        }

        public override void Init()
        {
            ObserverViewModel = viewModelFactory.Create<ObserverViewModel>();
            ObserverViewModel.AddDisposableTo(Disposables);


            variableSubject
                .ObserveOnDispatcher()
                .Do(UpdateTree)
                .Retry()
                .Subscribe()
                .AddDisposableTo(Disposables);

            var treeNodeChangeSet = TreeNodes
                .ToObservableChangeSet()
                .ObserveOnDispatcher();

            treeNodeChangeSet
                .Subscribe()
                .AddDisposableTo(Disposables);

            var connected = clientService.ConnectionState.Select(state => state == ConnectionState.Connected);

            clientService.ConnectionState
                .DistinctUntilChanged()
                .Where(state => state == ConnectionState.Connected)
                .Do(_ => variableSubject.OnNext(clientService.TreeViewSymbols))
                .Subscribe()
                .AddDisposableTo(Disposables);

            connected.ToProperty(this, x => x.IsConnected, out isConnectedHelper);

            AssignCommands(connected);

            GraphViewModel = viewModelFactory.CreateViewModel<GraphViewModel>();
            GraphViewModel.AddDisposableTo(Disposables);

            ObserverViewModel.ViewModels.CollectionChanged += OnObservedSymbolsChanged;
            Disposable.Create(() => ObserverViewModel.ViewModels.CollectionChanged -= OnObservedSymbolsChanged)
                .AddDisposableTo(Disposables);

            this.WhenAnyValue(x => x.ObservedSymbols).Subscribe().AddDisposableTo(Disposables);

            // Listen to all property change events on SearchText
            var searchTextChanged = Observable.FromEventPattern<PropertyChangedEventHandler, PropertyChangedEventArgs>(
                        ev => PropertyChanged += ev,
                        ev => PropertyChanged -= ev
                    )
                    .Where(ev => ev.EventArgs.PropertyName == "SearchText")
                ;

            // Transform the event stream into a stream of strings (the input values)
            var input = searchTextChanged
                .Where((ev => SearchText == null || SearchText.Length < 5))
                .Throttle(TimeSpan.FromSeconds(3))
                .Merge(searchTextChanged
                           .Where(ev => SearchText != null && SearchText.Length >= 5)
                           .Throttle(TimeSpan.FromMilliseconds(400)))
                .Select(args => SearchText)
                .Merge(
                    TextBoxEnterCommand.Executed.Select(e => SearchText))
                .DistinctUntilChanged();

            // Setup an Observer for the search operation
            var search = Observable.ToAsync<string, SearchResult>(DoSearch);


            // Chain the input event stream and the search stream, cancelling searches when input is received
            var results = from searchTerm in input
                from result in search(searchTerm).TakeUntil(input)
                select result;


            // Log the search result and add the results to the results collection
            results
                .ObserveOnDispatcher()
                .Subscribe(result =>
                    {
                        SearchResults.Clear();
                        result.Results.ToList().ForEach(item => SearchResults.Add(item));
                    }
                );
        }

        private void AssignCommands(IObservable<bool> connected)
        {
// Setup the command for the enter key on the textbox
            TextBoxEnterCommand = new ReactiveRelayCommand(obj => { });

            AddObserverCmd = ReactiveCommand.CreateFromTask<ISymbol, RxVoid>(RegisterSymbolObserver, canExecute: connected)
                .AddDisposableTo(Disposables);

            CmdDelete = ReactiveCommand.CreateFromTask<SymbolObservationViewModel, RxVoid>(DeleteSymbolObserver)
                .AddDisposableTo(Disposables);

            CmdAddGraph = ReactiveCommand.CreateFromTask<SymbolObservationViewModel, RxVoid>(AddGraph)
                .AddDisposableTo(Disposables);

            CmdRemoveGraph = ReactiveCommand.CreateFromTask<SymbolObservationViewModel, RxVoid>(RemoveGraph)
                .AddDisposableTo(Disposables);

            Read = ReactiveCommand.CreateFromTask(ReadVariables, canExecute: connected)
                .AddDisposableTo(Disposables);

            CmdSaveWatchSet = ReactiveCommand.CreateFromTask(SaveWatchSet)
                .AddDisposableTo(Disposables);

            CmdLoadWatchSet = ReactiveCommand.CreateFromTask(LoadWatchSet, canExecute: connected)
                .AddDisposableTo(Disposables);
        }

        private Task<RxVoid> AddGraph(SymbolObservationViewModel symbolObservationViewModel)
        {
            GraphViewModel.AddSymbol(symbolObservationViewModel);
            return Task.FromResult(RxVoid.Default);
        }

        private Task<RxVoid> DeleteSymbolObserver(SymbolObservationViewModel model)
        {
            try
            {
                ObserverViewModel.ViewModels.Remove(model);
                RemoveGraph(model);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(Resources.CouldNotDeleteObserverForSymbol0, model?.Name), ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private SearchResult DoSearch(string searchTerm)
        {
            var searchResult = new SearchResult {Results = new List<ISymbol>(), SearchTerm = searchTerm};
            try
            {
                var iterator = new SymbolIterator(clientService.FlatViewSymbols, s => s.InstancePath.ToLower().Contains(searchTerm.ToLower()));
                searchResult.Results = iterator;
            }
            catch (Exception ex)
            {
                Logger.Error(Resources.ErrorDuringSearch, ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return searchResult;
        }

        private async Task<RxVoid> ReadVariables()
        {
            try
            {
                await clientService.Reload();
            }
            catch (Exception ex)
            {
                Logger.Error(Resources.CouldNotReloadVariables, ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return RxVoid.Default;
        }


        private void OnObservedSymbolsChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems == null)
            {
                return;
            }

            foreach (var added in e.NewItems.OfType<SymbolObservationViewModel>())
            {
                if (awaitingGraph.Remove(added.FullName))
                {
                    GraphViewModel.AddSymbol(added);
                }
            }
        }

        /// <summary>
        /// Writes the watched symbols to a file: which ones, and which of them are on the scope.
        /// Values are deliberately not saved - a watch set says what to look at, and carrying stale
        /// readings around in the same file would invite reading them as measurements.
        /// </summary>
        private Task<RxVoid> SaveWatchSet()
        {
            try
            {
                var graphed = new HashSet<string>(GraphViewModel.Symbols.Select(symbol => symbol.FullName));

                var set = new WatchSet
                {
                    Variables = ObserverViewModel.ViewModels
                        .Select(symbol => new WatchSetEntry
                        {
                            Path = symbol.FullName,
                            Graph = graphed.Contains(symbol.FullName)
                        })
                        .ToList()
                };

                if (set.Variables.Count == 0)
                {
                    MessageBox.Show("There is nothing being watched to save.", "Watch set", MessageBoxButton.OK);
                    return Task.FromResult(RxVoid.Default);
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Json|*.json",
                    Title = "Save the watched symbols",
                    FileName = $"WatchSet_{DateTime.Now:yyyy-MM-dd-HHmmss}.json",
                    RestoreDirectory = true
                };

                if (dialog.ShowDialog() == true)
                {
                    File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(set, Formatting.Indented));
                    Logger.Debug($"Saved {set.Variables.Count} watched symbols to {dialog.FileName}");
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not save the watch set", ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return Task.FromResult(RxVoid.Default);
        }

        /// <summary>
        /// Reads a watch set and adds what it names. Paths the plc does not have are collected and
        /// reported together rather than passed over: a set written against another version of the
        /// program is exactly when knowing which symbols have gone matters.
        /// </summary>
        private Task<RxVoid> LoadWatchSet()
        {
            try
            {
                var dialog = new OpenFileDialog
                {
                    Filter = "Json|*.json",
                    Title = "Load a set of symbols to watch",
                    RestoreDirectory = true
                };

                if (dialog.ShowDialog() != true)
                {
                    return Task.FromResult(RxVoid.Default);
                }

                var set = JsonConvert.DeserializeObject<WatchSet>(File.ReadAllText(dialog.FileName));

                if (set?.Variables == null || set.Variables.Count == 0)
                {
                    MessageBox.Show("The file names no variables.", "Watch set", MessageBoxButton.OK);
                    return Task.FromResult(RxVoid.Default);
                }

                var missing = new List<string>();

                foreach (var entry in set.Variables)
                {
                    if (string.IsNullOrWhiteSpace(entry?.Path))
                    {
                        continue;
                    }

                    // Already watched: the observer answers a second request with a message box of its
                    // own, and a file naming twenty symbols would produce twenty of them.
                    var watched = ObserverViewModel.ViewModels
                        .FirstOrDefault(candidate => candidate.FullName == entry.Path);

                    if (watched != null)
                    {
                        if (entry.Graph)
                        {
                            GraphViewModel.AddSymbol(watched);
                        }

                        continue;
                    }

                    if (!TryResolveSymbol(entry.Path, out var symbol, out var reason))
                    {
                        missing.Add($"{entry.Path}  -  {reason}");
                        continue;
                    }

                    // A structure or an array has no single value to watch. Saying so beats dropping
                    // the line without a word, which is what registering it would have done.
                    if (symbol.IsContainerType)
                    {
                        missing.Add($"{entry.Path}  -  not a single value");
                        continue;
                    }

                    if (entry.Graph)
                    {
                        awaitingGraph.Add(entry.Path);
                    }

                    RegisterSymbolObserver(symbol);
                }

                if (missing.Count > 0)
                {
                    Logger.Warn($"{missing.Count} symbols from {dialog.FileName} could not be resolved");
                    MessageBox.Show(
                        "These could not be watched:" + Environment.NewLine + string.Join(Environment.NewLine, missing),
                        "Watch set",
                        MessageBoxButton.OK);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Could not load the watch set", ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return Task.FromResult(RxVoid.Default);
        }

        /// <summary>
        /// Finds the symbol a path names.
        ///
        /// The flat collection is only the symbol table the plc publishes, which is not everything
        /// that has a path: an element of an array, and anything below it, exists as a symbol only
        /// once the tree has been walked down to it. Those are exactly the symbols worth putting in
        /// a watch set on a machine built out of arrays of structures, and asking the flat table for
        /// them answers that the plc does not have them - which is not true.
        ///
        /// So the flat table is tried first, because it hands back the same instance the tree and the
        /// search use, and the server is asked directly when that fails.
        /// </summary>
        private bool TryResolveSymbol(string path, out ISymbol symbol, out string reason)
        {
            if (clientService.FlatViewSymbols.TryGetInstance(path, out symbol) && symbol != null)
            {
                reason = null;
                return true;
            }

            try
            {
                symbol = clientService.Client.ReadSymbol(path);
                reason = symbol == null ? "not on this plc" : null;
                return symbol != null;
            }
            catch (Exception ex)
            {
                symbol = null;
                reason = ex.Message;
                return false;
            }
        }

        private Task<RxVoid> RegisterSymbolObserver(ISymbol symbol)
        {
            try
            {
                if (symbol.SubSymbols.Any())
                {
                    return Task.FromResult(RxVoid.Default);
                }

                if (symbol.DataType.IsContainer)
                {
                    return Task.FromResult(RxVoid.Default);
                }

                symbolSelection.Select(symbol);
            }
            catch (Exception ex)
            {
                Logger.Error(string.Format(Resources.CouldNotRegisterObserverForSymbol0, symbol?.InstanceName), ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }

            return Task.FromResult(RxVoid.Default);
        }

        private Task<RxVoid> RemoveGraph(SymbolObservationViewModel symbolObservationViewModel)
        {
            GraphViewModel.RemoveSymbol(symbolObservationViewModel);
            return Task.FromResult(RxVoid.Default);
        }

        private void UpdateTree(ISymbolCollection<ISymbol> symbolList)
        {
            try
            {
                TreeNodes.Clear();
                foreach (var s in symbolList)
                {
                    TreeNodes.Add(s);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(Resources.CouldNotUpdateTree, ex);
                MessageBox.Show(ex.Message, ex.GetType().ToString(), MessageBoxButton.OK);
            }
            finally
            {
                raisePropertyChanged("TreeNodes");
            }
        }
    }
}