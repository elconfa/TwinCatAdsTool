using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using TwinCAT.Ads;
using TwinCAT.TypeSystem;
using TwinCAT.TypeSystem.Generic;
using TwinCatAdsTool.Interfaces.Models;

namespace TwinCatAdsTool.Interfaces.Services
{
    public interface IPersistentVariableService
    {
        /// <summary>
        /// Reads every persistent variable of the plc. The returned report states, for each
        /// variable, whether it made it into the backup - a backup whose report is not complete
        /// must not be treated as a full one.
        /// </summary>
        Task<PersistentBackup> ReadPersistentVariables(AdsClient client,
            IEnumerable<ISymbol> symbols,
            CancellationToken cancel = default);

        /// <summary>
        /// Writes a backup back onto the plc and reports what happened to every persistent
        /// variable, including the ones the backup did not cover.
        /// </summary>
        Task<PersistentOperationReport> WritePersistentVariables(AdsClient client,
            IEnumerable<ISymbol> symbols,
            JObject backup,
            CancellationToken cancel = default);

        [Obsolete("Use ReadPersistentVariables, which also reports the variables that could not be read.")]
        Task<JObject> ReadGlobalPersistentVariables(AdsClient client, IInstanceCollection<ISymbol> symbols);

        /// <summary>
        /// Progress of the backup or restore that is currently running. Emits
        /// <see cref="OperationProgress.Idle"/> when nothing is in flight.
        /// </summary>
        IObservable<OperationProgress> CurrentTask { get; }
    }
}
