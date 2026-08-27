using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Linq;
using System.Reactive;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCAT.Ads;
using TwinCAT.Ads.Reactive;
using TwinCAT.TypeSystem;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Logging;
using TwinCatAdsTool.Interfaces.Services;


namespace TwinCatAdsTool.Gui.ViewModels
{
    public abstract class SymbolObservationViewModel : ViewModelBase
    {
        protected readonly IClientService ClientService;
        private ObservableAsPropertyHelper<object> helper;

        /// <summary>
        /// Holds the current subscription to the symbol. Changing how often the plc is asked means
        /// registering the notification again, so the old one has to be given up first: the settings
        /// are read when the notification is created and are not revisited afterwards.
        /// </summary>
        private readonly SerialDisposable observation = new SerialDisposable();

        protected SymbolObservationViewModel(ISymbol model, IClientService clientService)
        {
            ClientService = clientService;
            Model = model;
        }

        public ReactiveCommand<RxVoid, RxVoid> CmdSubmit { get; private set; }
        public ISymbol Model { get; set; }
        public string Name { get; set; }
        public string FullName { get; set; }

        /// <summary>The plc type, as declared. Saves a trip back to the project to check it.</summary>
        public string TypeName => Model?.TypeName;

        /// <summary>
        /// Whether the plc keeps this variable across a power cycle. It is what the rest of this tool
        /// backs up and restores, so knowing it while watching a value is worth the column.
        /// </summary>
        public bool IsPersistent => Model?.IsPersistent ?? false;

        /// <summary>
        /// The path, and the comment from the declaration when there is one: the two things that
        /// answer "which one is this" without leaving the window.
        /// </summary>
        public string Description
        {
            get
            {
                var comment = Model?.Comment;
                return string.IsNullOrWhiteSpace(comment) ? FullName : $"{FullName}{Environment.NewLine}{comment}";
            }
        }

        public bool SupportsGraph => GetSupportsGraph();
        public bool SupportsSubmit => GetSupportsSubmit();

        public object Value => helper.Value;

        public override void Init()
        {
            Name = Model.InstanceName;
            FullName = Model.InstancePath;
            observation.AddDisposableTo(Disposables);

            try
            {
                Observe();

                CmdSubmit = ReactiveCommand.CreateFromTask(_ => SubmitSymbol(), 
                        ClientService.ConnectionState.Select(s => s == ConnectionState.Connected))
                    .AddDisposableTo(Disposables);
            }
            catch (Exception e)
            {
                Logger.Error($"Error while initializing vm for {Model.InstanceName}. Control will not be usable.", e);
            }
        }

        /// <summary>
        /// Asks the plc to report this symbol every <paramref name="cycle"/> milliseconds instead of
        /// every 200, which is the ads default and therefore what the tool has always used without
        /// saying so. The server still reports only on a change; the cycle is how often it looks, so
        /// it is the shortest event that can be seen at all.
        /// </summary>
        public void Resample(int cycle)
        {
            if (!(Model is IValueSymbol valueSymbol))
            {
                return;
            }

            try
            {
                valueSymbol.NotificationSettings = new NotificationSettings(AdsTransMode.OnChange, cycle, 0);
                Observe();
            }
            catch (Exception e)
            {
                Logger.Error($"Could not change how often {FullName} is sampled.", e);
            }
        }

        private void Observe()
        {
            var readSymbolInfo = ClientService.Client.ReadSymbol(Model.InstancePath);
            var initialValue = ClientService.Client.ReadValue(readSymbolInfo);
            var observable = ((IValueSymbol) Model).WhenValueChanged().StartWith(initialValue);

            var obsLogger = LoggerFactory.GetObserverLogger();

            var subscription = new CompositeDisposable();

            observable
                .Do(value => obsLogger.Debug($"{FullName} value changed to: '{value.ToString()}'"))
                .Subscribe()
                .AddDisposableTo(subscription);

            helper?.Dispose();
            helper = observable.ToProperty(this, m => m.Value);
            helper.AddDisposableTo(subscription);

            // Replacing what is in the serial disposable is what closes the previous notification.
            observation.Disposable = subscription;
            raisePropertyChanged(nameof(Value));
        }

        protected abstract bool GetSupportsGraph();
        protected abstract bool GetSupportsSubmit();
        protected abstract Task SubmitSymbol();
    }

    public class SymbolObservationDefaultViewModel : SymbolObservationViewModel
    {
        public SymbolObservationDefaultViewModel(ISymbol model, IClientService clientService) : base(model, clientService)
        {
        }

        protected override bool GetSupportsGraph()
        {
            return false;
        }

        protected override bool GetSupportsSubmit()
        {
            return false;
        }

        protected override Task SubmitSymbol()
        {
            throw new NotImplementedException();
        }
    }

    public class SymbolObservationViewModel<T> : SymbolObservationViewModel
    {
        private T newValue;

        public SymbolObservationViewModel(ISymbol model, IClientService clientService) : base(model, clientService)
        {
        }

        public T NewValue
        {
            get => newValue;
            set
            {
                newValue = value;
                raisePropertyChanged();
            }
        }

        public override void Init()
        {
            base.Init();
            NewValue = (T) Value;
        }

        protected override bool GetSupportsGraph()
        {
            return (typeof(T) == typeof(int))
                || (typeof(T) == typeof(short))
                || (typeof(T) == typeof(bool))
                || (typeof(T) == typeof(float))
                || (typeof(T) == typeof(double))
                || (typeof(T) == typeof(byte))
                || (typeof(T) == typeof(ushort))
                || (typeof(T) == typeof(uint))
                || (typeof(T) == typeof(sbyte));
        }

        protected override bool GetSupportsSubmit()
        {
            return true;
        }

        protected override Task SubmitSymbol()
        {
            Write(NewValue);
            return Task.FromResult(RxVoid.Default);
        }

        private void Write(T value)
        {
            if (Model.IsReadOnly)
            {
                MessageBox.Show(Resources.ThisValueIsReadOnly, Resources.ReadOnlyValue, MessageBoxButton.OK);
                return;
            }

            var variableHandle = ClientService.Client.CreateVariableHandle(Model.InstancePath);

            if (typeof(T) == typeof(string))
            {
                Logger.Debug(string.Format(Resources.TryingToWriteTo0WithValue1, Model?.InstancePath, (value as string)));
                ClientService.Client.WriteAnyString(variableHandle, value as string, (value as string).Length, Encoding.Default);
            }
            else
            {
                Logger.Debug(string.Format(Resources.TryingToWriteTo0WithValue1, Model?.InstancePath, value));
                ClientService.Client.WriteAny(variableHandle, value);
            }
        }
    }
}