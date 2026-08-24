using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Newtonsoft.Json.Linq;
using TwinCAT.Ads;
using TwinCAT.Ads.SumCommand;
using TwinCAT.TypeSystem;
using TwinCatAdsTool.Interfaces.Logging;
using TwinCatAdsTool.Interfaces.Models;
using TwinCatAdsTool.Logic.Values;

namespace TwinCatAdsTool.Logic.Services
{
    /// <summary>
    /// Reads persistent variables from the plc.
    ///
    /// The previous implementation walked every structure down to its leaves and issued two ads
    /// requests per leaf, which turned an array of structures into tens of thousands of round
    /// trips. Here each persistent variable is transferred whole, and variables are grouped into
    /// sum commands so that a batch of them costs a single telegram.
    /// </summary>
    public class PersistentVariableReader
    {
        /// <summary>Upper bound on the symbols packed into one sum command.</summary>
        private const int MaxSymbolsPerBatch = 100;

        /// <summary>Upper bound on the payload of one sum command, to stay inside the ads buffer.</summary>
        private const int MaxBytesPerBatch = 256 * 1024;

        private readonly ILog logger = LoggerFactory.GetLogger();
        private readonly PersistentSymbolScanner scanner = new PersistentSymbolScanner();

        public async Task<PersistentBackup> ReadAsync(IAdsConnection connection,
            IEnumerable<ISymbol> symbols,
            IProgress<string> progress,
            CancellationToken cancel)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = new List<VariableOperationResult>();
            var root = new JObject();

            var scan = scanner.Scan(symbols);
            results.AddRange(scan.Skipped);

            foreach (var skipped in scan.Skipped)
            {
                logger.Warn($"Skipping persistent variable '{skipped.InstancePath}': {skipped.Error}");
            }

            var done = 0;
            foreach (var batch in Batch(scan.Roots))
            {
                cancel.ThrowIfCancellationRequested();

                progress?.Report($"Reading {done + 1}-{done + batch.Count} of {scan.Roots.Count} persistent variables...");

                var values = await ReadBatchAsync(connection, batch, results, cancel).ConfigureAwait(false);

                for (var i = 0; i < batch.Count; i++)
                {
                    var symbol = batch[i];
                    if (values[i] == null)
                    {
                        continue;
                    }

                    try
                    {
                        var json = PlcJsonConverter.ToJson(new DynamicValueNode(values[i]));
                        JsonPathBuilder.Insert(root, symbol.InstancePath, json);
                        results.Add(VariableOperationResult.Success(symbol.InstancePath));
                    }
                    catch (Exception e)
                    {
                        logger.Error($"Could not convert '{symbol.InstancePath}' to json", e);
                        results.Add(VariableOperationResult.Failure(symbol.InstancePath, e));
                    }
                }

                done += batch.Count;
            }

            stopwatch.Stop();
            progress?.Report(string.Empty);

            var report = new PersistentOperationReport(results, stopwatch.Elapsed);
            logger.Info($"Backup finished: {report.Summary}");

            return new PersistentBackup(root, report);
        }

        /// <summary>
        /// Reads one batch with a single sum command. Symbols the sum command could not deliver
        /// are retried one by one so that the failure can be attributed to the right variable
        /// with a meaningful ads error instead of disappearing from the backup.
        /// </summary>
        private async Task<object[]> ReadBatchAsync(IAdsConnection connection,
            IReadOnlyList<ISymbol> batch,
            List<VariableOperationResult> results,
            CancellationToken cancel)
        {
            var values = new object[batch.Count];
            var pending = new List<int>();

            try
            {
                var sum = new SumSymbolRead(connection, batch.ToList());
                var code = sum.TryRead(out var read, out var returnCodes);

                if (code == AdsErrorCode.NoError && read != null)
                {
                    for (var i = 0; i < batch.Count; i++)
                    {
                        if (returnCodes != null && i < returnCodes.Length && returnCodes[i] != AdsErrorCode.NoError)
                        {
                            results.Add(VariableOperationResult.Failure(batch[i].InstancePath,
                                $"ads error {returnCodes[i]}"));
                            continue;
                        }

                        values[i] = i < read.Length ? read[i] : null;
                        if (values[i] == null)
                        {
                            pending.Add(i);
                        }
                    }

                    return values;
                }

                logger.Warn($"Sum read of {batch.Count} symbols failed with {code}, falling back to single reads");
                pending.AddRange(Enumerable.Range(0, batch.Count));
            }
            catch (Exception e)
            {
                logger.Warn($"Sum read of {batch.Count} symbols is not usable, falling back to single reads", e);
                pending.AddRange(Enumerable.Range(0, batch.Count));
            }

            foreach (var index in pending)
            {
                cancel.ThrowIfCancellationRequested();
                values[index] = await ReadSingleAsync(batch[index], results, cancel).ConfigureAwait(false);
            }

            return values;
        }

        /// <summary>
        /// Reads one symbol as a whole. Still a single ads transfer for the entire structure,
        /// because the library unmarshals the value tree on the client side.
        /// </summary>
        private async Task<object> ReadSingleAsync(ISymbol symbol,
            List<VariableOperationResult> results,
            CancellationToken cancel)
        {
            try
            {
                if (!(symbol is IValueSymbol valueSymbol))
                {
                    results.Add(VariableOperationResult.Failure(symbol.InstancePath, "symbol carries no readable value"));
                    return null;
                }

                return await valueSymbol.ReadValueAsync(cancel).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                logger.Error($"Could not read '{symbol.InstancePath}'", e);
                results.Add(VariableOperationResult.Failure(symbol.InstancePath, e));
                return null;
            }
        }

        private static IEnumerable<IReadOnlyList<ISymbol>> Batch(IReadOnlyList<ISymbol> symbols)
        {
            var current = new List<ISymbol>();
            var currentBytes = 0;

            foreach (var symbol in symbols)
            {
                var size = SizeOf(symbol);

                if (current.Count > 0 &&
                    (current.Count >= MaxSymbolsPerBatch || currentBytes + size > MaxBytesPerBatch))
                {
                    yield return current;
                    current = new List<ISymbol>();
                    currentBytes = 0;
                }

                current.Add(symbol);
                currentBytes += size;
            }

            if (current.Count > 0)
            {
                yield return current;
            }
        }

        private static int SizeOf(ISymbol symbol)
        {
            try
            {
                return symbol.ByteSize;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }

}
