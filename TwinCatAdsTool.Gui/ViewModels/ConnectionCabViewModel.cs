using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Reactive.Linq;
using DynamicData;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using TwinCAT;
using TwinCAT.Ads;
using TwinCatAdsTool.Gui.Extensions;
using TwinCatAdsTool.Gui.Properties;
using TwinCatAdsTool.Interfaces.Extensions;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;

namespace TwinCatAdsTool.Gui.ViewModels
{
    public class ConnectionCabViewModel : ViewModelBase
    {
        private readonly IClientService clientService;
        private ObservableAsPropertyHelper<ConnectionState> connectionStateHelper;
        private int port = 851;
        private string selectedNetId;
        private NetId selectedAmsNetId;
        private ObservableAsPropertyHelper<string> adsStatusHelper;
        private string connectionError;


        public ConnectionCabViewModel(IClientService clientService)
        {
            this.clientService = clientService;
        }

        public ObservableCollection<NetId> AmsNetIds { get; set; } = new ObservableCollection<NetId>();
        public ReactiveCommand<RxVoid, RxVoid> Connect { get; set; }

        public ConnectionState ConnectionState => connectionStateHelper.Value;
        public ReactiveCommand<RxVoid, RxVoid> Disconnect { get; set; }

        public int Port
        {
            get => port;
            set
            {
                if (value == port) return;
                port = value;
                raisePropertyChanged();
            }
        }

        public NetId SelectedAmsNetId
        {
            get { return selectedAmsNetId; }

            set
            {
                if (selectedAmsNetId != value)
                {
                    selectedAmsNetId = value;
                    raisePropertyChanged();
                }
            }
        }


        public string SelectedNetId
        {
            get => selectedNetId;
            set
            {
                if (selectedNetId != value)
                {
                    selectedNetId = value;
                    raisePropertyChanged();
                }
            }
        }


        public override void Init()
        {
            Connect = ReactiveCommand.CreateFromTask(ConnectClient, canExecute: clientService.ConnectionState.Select(state => state != ConnectionState.Connected))
                .AddDisposableTo(Disposables).SetupErrorHandling(Logger, Disposables);
            Disconnect = ReactiveCommand.CreateFromTask(DisconnectClient, canExecute: clientService.ConnectionState.Select(state => state == ConnectionState.Connected))
                .AddDisposableTo(Disposables).SetupErrorHandling(Logger, Disposables);
            
            connectionStateHelper = clientService
                .ConnectionState
                .ObserveOnDispatcher()
                .ToProperty(this, model => model.ConnectionState);

            adsStatusHelper = clientService
                .AdsState
                .ObserveOnDispatcher()
                .ToProperty(this, model => model.AdsStatus);


            clientService.DevicesFound
                .Where(d => d != null)
                .ObserveOnDispatcher()
                .Do(devices => AmsNetIds.AddRange(devices))
                .Subscribe()
                .AddDisposableTo(Disposables);
            
            AmsNetIds.Add(new NetId(){Address = "", Name = "*"});
            SelectedAmsNetId = AmsNetIds.FirstOrDefault();

            this.WhenAnyValue(vm => vm.SelectedAmsNetId)
                .ObserveOn(Dispatcher.CurrentDispatcher)
                .Do(s => SelectedNetId = s.Address)
                .Subscribe()
                .AddDisposableTo(Disposables);
            
        }

        public string AdsStatus => adsStatusHelper.Value;

        /// <summary>
        /// Why the last connection attempt failed. Until this existed the exception went to the
        /// log file and nowhere else, so a failed connect looked exactly like a button that does
        /// nothing at all.
        /// </summary>
        public string ConnectionError
        {
            get => connectionError;
            set
            {
                if (value == connectionError) return;
                connectionError = value;
                raisePropertyChanged();
                raisePropertyChanged(nameof(HasConnectionError));
            }
        }

        /// <summary>
        /// Needs a setter: the info bar binds IsOpen two way and writes false into it when the
        /// user dismisses the bar. A getter only property makes wpf refuse the binding outright,
        /// which takes the whole window down at startup.
        /// </summary>
        public bool HasConnectionError
        {
            get => !string.IsNullOrEmpty(ConnectionError);
            set
            {
                if (!value)
                {
                    ConnectionError = null;
                }
            }
        }

        /// <summary>
        /// Unwraps the ads exceptions, whose outer message is usually generic while the inner one
        /// names the actual problem - a missing router, a refused route, a wrong port.
        /// </summary>
        private static string Describe(Exception exception)
        {
            var parts = new List<string>();

            for (var current = exception; current != null; current = current.InnerException)
            {
                var text = $"{current.GetType().Name}: {current.Message}";
                if (!parts.Contains(text))
                {
                    parts.Add(text);
                }
            }

            return string.Join(" - ", parts);
        }

        private async Task ConnectClient()
        {
            ConnectionError = null;

            try
            {
                Logger.Debug($"Connecting to '{SelectedNetId}' on port {Port}");
                await clientService.Connect(SelectedNetId, Port);
                Logger.Debug(string.Format(Resources.ClientConnectedToDevice0WithAddress1, SelectedAmsNetId?.Name,
                    SelectedAmsNetId?.Address));
            }
            catch (Exception ex) when (IsMissingAdsDriver(ex))
            {
                Logger.Error("Dll not found TwinCAT.Ads", ex);
                ConnectionError = "The TwinCAT ADS driver is not installed on this machine. " +
                                  "The tool needs a local ADS router, not just a route on the plc.";
            }
            catch (Exception ex)
            {
                // Anything else - refused route, wrong ams net id, closed port - used to end up
                // in the log alone and left the user staring at an unchanged window.
                Logger.Error($"Could not connect to '{SelectedNetId}' on port {Port}", ex);
                ConnectionError = Describe(ex);
            }
        }

        /// <summary>
        /// The driver may be missing as the inner exception or as the exception itself, depending
        /// on where the load fails. The previous filter only matched the first case, so the more
        /// common one produced no message at all.
        /// </summary>
        private static bool IsMissingAdsDriver(Exception exception)
        {
            for (var current = exception; current != null; current = current.InnerException)
            {
                if (current is DllNotFoundException || current is TypeInitializationException)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task DisconnectClient()
        {
            ConnectionError = null;
            await clientService.Disconnect();
            Logger.Debug("Client disconnected");
        }
    }
}