using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.TypeSystem.Generic;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Interfaces.Services;

namespace TwinCatAdsTool.Logic.Services
{
    public class PersistentVariableService : IPersistentVariableService
    {
        private readonly Subject<string> currentTaskSubject = new Subject<string>();
        private readonly PersistentVariableReader reader = new PersistentVariableReader();
        private readonly PersistentVariableWriter writer = new PersistentVariableWriter();

        public IObservable<string> CurrentTask => currentTaskSubject.AsObservable();

        public Task<PersistentBackup> ReadPersistentVariables(AdsClient client,
            IEnumerable<ISymbol> symbols,
            CancellationToken cancel = default)
            => reader.ReadAsync(client, symbols, Progress(), cancel);

        public Task<PersistentOperationReport> WritePersistentVariables(AdsClient client,
            IEnumerable<ISymbol> symbols,
            JObject backup,
            CancellationToken cancel = default)
            => writer.WriteAsync(client, symbols, backup, Progress(), cancel);

        [Obsolete("Use ReadPersistentVariables, which also reports the variables that could not be read.")]
        public async Task<JObject> ReadGlobalPersistentVariables(AdsClient client, IInstanceCollection<ISymbol> symbols)
        {
            var backup = await ReadPersistentVariables(client, symbols).ConfigureAwait(false);
            return backup.Data;
        }

        private IProgress<string> Progress() => new Progress<string>(task => currentTaskSubject.OnNext(task));
    }
}
