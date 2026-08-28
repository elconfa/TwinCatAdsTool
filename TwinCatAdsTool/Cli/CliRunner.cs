using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ninject;
using TwinCatAdsTool.Interfaces;
using TwinCatAdsTool.Interfaces.Comparison;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;
using TwinCatAdsTool.Logic.Cli;

namespace TwinCatAdsTool.Cli
{
    /// <summary>
    /// What the process returns. A script has no other way of finding out how a run went, so the
    /// distinctions that matter are the ones a script would act on differently: a plc that could not
    /// be reached is worth retrying, a run that finished with variables missing is not.
    /// </summary>
    internal static class ExitCodes
    {
        public const int Ok = 0;
        public const int BadCommandLine = 1;
        public const int PlcUnreachable = 2;
        public const int Incomplete = 3;
        public const int Unexpected = 4;
        public const int Different = 5;
    }

    internal static class CliRunner
    {
        /// <summary>Long enough for a controller that is busy starting up, short enough for a script.</summary>
        private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

        /// <summary>Beyond this the list stops being read and starts being scrolled past.</summary>
        private const int MostDifferencesListed = 50;

        public static int Run(CliCommand command, IKernel kernel)
        {
            if (!command.IsValid)
            {
                Console.Error.WriteLine(command.Error);
                Console.Error.WriteLine();
                Console.Error.WriteLine("Run TwinCatAdsTool --help for the whole story.");
                return ExitCodes.BadCommandLine;
            }

            switch (command.Verb)
            {
                case CliVerb.Help:
                    Console.Out.WriteLine(CommandLine.Usage);
                    return ExitCodes.Ok;

                case CliVerb.Version:
                    Console.Out.WriteLine(Constants.Version);
                    return ExitCodes.Ok;

                default:
                    return Execute(command, kernel).GetAwaiter().GetResult();
            }
        }

        private static async Task<int> Execute(CliCommand command, IKernel kernel)
        {
            var clientService = kernel.Get<IClientService>();
            var persistentVariables = kernel.Get<IPersistentVariableService>();

            try
            {
                Console.Out.WriteLine($"Connecting to {command.AmsNetId} port {command.Port}");
                await clientService.Connect(command.AmsNetId, command.Port);
                await WaitUntilReady(clientService);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Could not reach the plc: {e.Message}");
                return ExitCodes.PlcUnreachable;
            }

            try
            {
                switch (command.Verb)
                {
                    case CliVerb.Backup:
                        return await Backup(command, clientService, persistentVariables);

                    case CliVerb.Restore:
                        return await Restore(command, clientService, persistentVariables);

                    case CliVerb.Compare:
                        return await Compare(command, clientService, persistentVariables);

                    default:
                        Console.Error.WriteLine($"{command.Verb} is not something this can do.");
                        return ExitCodes.BadCommandLine;
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"{e.GetType().Name}: {e.Message}");
                return ExitCodes.Unexpected;
            }
            finally
            {
                await clientService.Disconnect();
            }
        }

        /// <summary>
        /// Connect returns as soon as the ads client has been told to connect; the symbols are loaded
        /// by a subscription that fires when the connection reports itself as up. In the window that
        /// gap is invisible, but a command line that started reading straight away would find no
        /// symbols and report an empty plc as a successful backup.
        /// </summary>
        private static async Task WaitUntilReady(IClientService clientService)
        {
            await clientService.ConnectionState
                .Where(state => state == TwinCAT.ConnectionState.Connected)
                .Take(1)
                .Timeout(ConnectTimeout)
                .ToTask();

            if (clientService.TreeViewSymbols == null)
            {
                await clientService.Reload();
            }

            if (clientService.TreeViewSymbols == null)
            {
                throw new InvalidOperationException("connected, but the plc published no symbols.");
            }
        }

        private static async Task<int> Backup(CliCommand command, IClientService clientService,
            IPersistentVariableService persistentVariables)
        {
            var backup = await persistentVariables.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols);

            File.WriteAllText(command.File, backup.Data.ToString(Formatting.Indented));
            Console.Out.WriteLine($"Written to {command.File}");

            return Report(backup.Report);
        }

        private static async Task<int> Restore(CliCommand command, IClientService clientService,
            IPersistentVariableService persistentVariables)
        {
            var backup = ReadJson(command.File);

            var report = await persistentVariables.WritePersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols,
                backup);

            return Report(report);
        }

        private static async Task<int> Compare(CliCommand command, IClientService clientService,
            IPersistentVariableService persistentVariables)
        {
            var file = ReadJson(command.File);

            var live = await persistentVariables.ReadPersistentVariables(
                clientService.Client,
                clientService.TreeViewSymbols);

            // A comparison against a reading that is missing variables would report them as absent
            // from the plc, which is a different statement altogether.
            if (!live.Report.IsComplete)
            {
                Console.Error.WriteLine("The plc could not be read in full, so this comparison cannot be trusted:");
                Console.Error.WriteLine(live.Report.Details());
                return ExitCodes.Incomplete;
            }

            var differences = JsonDifference.Find(file, live.Data);

            if (differences.Count == 0)
            {
                Console.Out.WriteLine($"The plc matches {command.File}.");
                return ExitCodes.Ok;
            }

            Console.Out.WriteLine($"{differences.Count} differences, file -> plc:");

            foreach (var difference in differences.Take(MostDifferencesListed))
            {
                Console.Out.WriteLine($"  {difference}");
            }

            if (differences.Count > MostDifferencesListed)
            {
                Console.Out.WriteLine($"  and {differences.Count - MostDifferencesListed} more.");
            }

            return ExitCodes.Different;
        }

        private static JObject ReadJson(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"there is no file at {path}.", path);
            }

            return JObject.Parse(File.ReadAllText(path));
        }

        /// <summary>
        /// The same summary the window shows, and the failures in full. A run that did not process
        /// every variable is reported as such rather than as a success with a note.
        /// </summary>
        private static int Report(PersistentOperationReport report)
        {
            Console.Out.WriteLine(report.Summary);

            if (report.IsComplete)
            {
                return ExitCodes.Ok;
            }

            Console.Error.WriteLine(report.Details());
            return ExitCodes.Incomplete;
        }
    }
}
