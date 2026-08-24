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
    /// Restores persistent variables onto the plc.
    ///
    /// Every persistent variable declared on the plc is accounted for: it is either written, or
    /// reported as missing from the backup. Entries of the backup that no longer match anything
    /// on the plc are reported as well. Nothing is dropped quietly.
    /// </summary>
    public class PersistentVariableWriter
    {
        private const int MaxSymbolsPerBatch = 100;
        private const int MaxBytesPerBatch = 256 * 1024;

        private readonly ILog logger = LoggerFactory.GetLogger();
        private readonly PersistentSymbolScanner scanner = new PersistentSymbolScanner();

        public async Task<PersistentOperationReport> WriteAsync(IAdsConnection connection,
            IEnumerable<ISymbol> symbols,
            JObject backup,
            IProgress<string> progress,
            CancellationToken cancel)
        {
            var stopwatch = Stopwatch.StartNew();
            var results = new List<VariableOperationResult>();

            var scan = scanner.Scan(symbols);
            results.AddRange(scan.Skipped);

            var matched = new List<ISymbol>();
            foreach (var symbol in scan.Roots)
            {
                if (JsonPathBuilder.Find(backup, symbol.InstancePath) == null)
                {
                    results.Add(VariableOperationResult.Skipped(symbol.InstancePath,
                        "not present in the backup file, left unchanged on the plc"));
                    continue;
                }

                matched.Add(symbol);
            }

            results.AddRange(FindOrphans(backup, scan.Roots));

            var done = 0;
            foreach (var batch in Batch(matched))
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report($"Writing {done + 1}-{done + batch.Count} of {matched.Count} persistent variables...");

                await WriteBatchAsync(connection, batch, backup, results, cancel).ConfigureAwait(false);
                done += batch.Count;
            }

            stopwatch.Stop();
            progress?.Report(string.Empty);

            var report = new PersistentOperationReport(results, stopwatch.Elapsed);
            logger.Info($"Restore finished: {report.Summary}");

            return report;
        }

        private async Task WriteBatchAsync(IAdsConnection connection,
            IReadOnlyList<ISymbol> batch,
            JObject backup,
            List<VariableOperationResult> results,
            CancellationToken cancel)
        {
            // The current value tree is needed to know the declared type of every member before
            // the json values can be coerced into it.
            var values = new object[batch.Count];
            var writable = new List<int>();

            for (var i = 0; i < batch.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var symbol = batch[i];
                try
                {
                    if (!(symbol is IValueSymbol valueSymbol))
                    {
                        results.Add(VariableOperationResult.Failure(symbol.InstancePath, "symbol carries no writable value"));
                        continue;
                    }

                    var current = await valueSymbol.ReadValueAsync(cancel).ConfigureAwait(false);
                    var node = new DynamicValueNode(current);
                    var json = JsonPathBuilder.Find(backup, symbol.InstancePath);

                    if (!node.IsArray && !node.IsStruct)
                    {
                        // A scalar variable is written directly from its json value.
                        if (!TryWriteScalar(valueSymbol, current, json, symbol.InstancePath, results))
                        {
                            continue;
                        }

                        results.Add(VariableOperationResult.Success(symbol.InstancePath));
                        continue;
                    }

                    var applied = PlcJsonConverter.ApplyJson(node, json, symbol.InstancePath);

                    if (!applied.IsClean)
                    {
                        foreach (var mismatch in applied.Mismatches)
                        {
                            logger.Warn($"Restore mismatch: {mismatch}");
                        }
                    }

                    if (applied.AppliedCount == 0)
                    {
                        results.Add(VariableOperationResult.Failure(symbol.InstancePath,
                            $"nothing could be written - {FirstReasons(applied)}"));
                        continue;
                    }

                    values[i] = current;
                    writable.Add(i);

                    if (!applied.IsClean)
                    {
                        results.Add(VariableOperationResult.Failure(symbol.InstancePath,
                            $"written partially - {FirstReasons(applied)}"));
                    }
                }
                catch (Exception e)
                {
                    logger.Error($"Could not prepare '{symbol.InstancePath}' for restore", e);
                    results.Add(VariableOperationResult.Failure(symbol.InstancePath, e));
                }
            }

            if (!writable.Any())
            {
                return;
            }

            var symbolsToWrite = writable.Select(i => batch[i]).ToList();
            var valuesToWrite = writable.Select(i => values[i]).ToArray();
            var failedIndexes = new HashSet<int>();

            try
            {
                var sum = new SumSymbolWrite(connection, symbolsToWrite);
                var code = sum.TryWrite(valuesToWrite, out var returnCodes);

                if (code == AdsErrorCode.NoError)
                {
                    for (var i = 0; i < symbolsToWrite.Count; i++)
                    {
                        if (returnCodes != null && i < returnCodes.Length && returnCodes[i] != AdsErrorCode.NoError)
                        {
                            results.Add(VariableOperationResult.Failure(symbolsToWrite[i].InstancePath,
                                $"ads error {returnCodes[i]}"));
                            failedIndexes.Add(i);
                        }
                    }

                    ReportWritten(symbolsToWrite, failedIndexes, results);
                    return;
                }

                logger.Warn($"Sum write of {symbolsToWrite.Count} symbols failed with {code}, falling back to single writes");
            }
            catch (Exception e)
            {
                logger.Warn($"Sum write of {symbolsToWrite.Count} symbols is not usable, falling back to single writes", e);
            }

            for (var i = 0; i < symbolsToWrite.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    await ((IValueSymbol) symbolsToWrite[i]).WriteValueAsync(valuesToWrite[i], cancel).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    logger.Error($"Could not write '{symbolsToWrite[i].InstancePath}'", e);
                    results.Add(VariableOperationResult.Failure(symbolsToWrite[i].InstancePath, e));
                    failedIndexes.Add(i);
                }
            }

            ReportWritten(symbolsToWrite, failedIndexes, results);
        }

        private static void ReportWritten(IReadOnlyList<ISymbol> symbols, ICollection<int> failedIndexes,
            List<VariableOperationResult> results)
        {
            for (var i = 0; i < symbols.Count; i++)
            {
                if (failedIndexes.Contains(i))
                {
                    continue;
                }

                var path = symbols[i].InstancePath;

                // A partial write was already reported while the value tree was being filled.
                if (results.Any(r => r.InstancePath == path && r.State == VariableOperationState.Failed))
                {
                    continue;
                }

                results.Add(VariableOperationResult.Success(path));
            }
        }

        private bool TryWriteScalar(IValueSymbol symbol, object current, JToken json, string path,
            List<VariableOperationResult> results)
        {
            var managed = PlcJsonConverter.ToManaged(json);

            if (!ValueCoercion.TryCoerce(managed, ValueCoercion.Normalize(current), out var coerced) ||
                !ValueCoercion.TryCoerce(coerced, current, out var wrapped))
            {
                results.Add(VariableOperationResult.Failure(path,
                    $"backup value '{managed}' does not fit the plc type"));
                return false;
            }

            try
            {
                symbol.WriteValue(wrapped);
                return true;
            }
            catch (Exception e)
            {
                logger.Error($"Could not write '{path}'", e);
                results.Add(VariableOperationResult.Failure(path, e));
                return false;
            }
        }

        /// <summary>
        /// Finds entries of the backup file that do not correspond to any persistent variable on
        /// the plc - typically variables removed or renamed since the backup was taken.
        /// </summary>
        private static IEnumerable<VariableOperationResult> FindOrphans(JObject backup, IReadOnlyList<ISymbol> roots)
        {
            if (backup == null)
            {
                return Enumerable.Empty<VariableOperationResult>();
            }

            var knownPaths = new HashSet<string>(roots.Select(r => r.InstancePath), StringComparer.OrdinalIgnoreCase);
            var prefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in knownPaths)
            {
                var segments = path.Split('.');
                for (var i = 1; i < segments.Length; i++)
                {
                    prefixes.Add(string.Join(".", segments, 0, i));
                }
            }

            var orphans = new List<VariableOperationResult>();
            Collect(backup, string.Empty, knownPaths, prefixes, orphans);
            return orphans;
        }

        private static void Collect(JObject node, string path, ICollection<string> knownPaths,
            ICollection<string> prefixes, List<VariableOperationResult> orphans)
        {
            foreach (var property in node.Properties())
            {
                var childPath = string.IsNullOrEmpty(path) ? property.Name : $"{path}.{property.Name}";

                if (knownPaths.Contains(childPath))
                {
                    continue;
                }

                if (prefixes.Contains(childPath) && property.Value is JObject child)
                {
                    Collect(child, childPath, knownPaths, prefixes, orphans);
                    continue;
                }

                orphans.Add(VariableOperationResult.Skipped(childPath,
                    "present in the backup but not a persistent variable on this plc"));
            }
        }

        private static string FirstReasons(ValueApplyResult applied)
        {
            const int maxReasons = 3;
            var reasons = applied.Mismatches.Take(maxReasons).ToList();
            var more = applied.Mismatches.Count - reasons.Count;

            return more > 0
                ? $"{string.Join("; ", reasons)} (and {more} more)"
                : string.Join("; ", reasons);
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
