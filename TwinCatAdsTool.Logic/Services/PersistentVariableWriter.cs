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

        /// <summary>
        /// Leaves are addressed one by one, so there are far more of them than there are
        /// persistent variables, and each one carries only a handful of bytes. Grouping more of
        /// them into a single sum command keeps the number of ads round trips down;
        /// <see cref="MaxBytesPerBatch"/> still caps what a single command may carry.
        /// </summary>
        private const int MaxLeavesPerBatch = 500;

        private const int MaxBytesPerBatch = 256 * 1024;

        private readonly ILog logger = LoggerFactory.GetLogger();
        private readonly PersistentSymbolScanner scanner = new PersistentSymbolScanner();

        public async Task<PersistentOperationReport> WriteAsync(IAdsConnection connection,
            IEnumerable<ISymbol> symbols,
            JObject backup,
            IProgress<OperationProgress> progress,
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
            foreach (var batch in Batch(matched, MaxSymbolsPerBatch, SizeOf))
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(
                    $"Writing {done + 1}-{done + batch.Count} of {matched.Count} persistent variables...",
                    done,
                    matched.Count));

                await WriteBatchAsync(connection, batch, backup, results, cancel).ConfigureAwait(false);
                done += batch.Count;
            }

            stopwatch.Stop();
            progress?.Report(OperationProgress.Idle);

            var report = new PersistentOperationReport(results, stopwatch.Elapsed);
            logger.Info($"Restore finished: {report.Summary}");

            return report;
        }

        /// <summary>
        /// Reads every variable of the batch to learn its declared types, works out which
        /// individual leaves the backup wants written, and writes those leaves.
        /// </summary>
        private async Task WriteBatchAsync(IAdsConnection connection,
            IReadOnlyList<ISymbol> batch,
            JObject backup,
            List<VariableOperationResult> results,
            CancellationToken cancel)
        {
            var plans = new List<RootPlan>();

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

                    // ReadValueAsync hands back a result object, not the value itself. Passing
                    // the wrapper on made DynamicValueNode find neither members nor elements, so
                    // every structure and array looked like a scalar and the restore reported
                    // "backup value does not fit the plc type" for all of them.
                    var read = await valueSymbol.ReadValueAsync(cancel).ConfigureAwait(false);
                    if (!read.Succeeded)
                    {
                        results.Add(VariableOperationResult.Failure(symbol.InstancePath,
                            $"could not read the current value - ads error {(AdsErrorCode) read.ErrorCode}"));
                        continue;
                    }

                    // The value that was just read is only consulted, never modified: it says
                    // which type every leaf holds so the json can be converted into it.
                    var node = new DynamicValueNode(read.Value);
                    var json = JsonPathBuilder.Find(backup, symbol.InstancePath);
                    var plan = PlcLeafPlanner.Plan(node, json, symbol.InstancePath);

                    var rootPlan = new RootPlan(symbol);
                    rootPlan.Problems.AddRange(plan.Mismatches);

                    foreach (var write in plan.Writes)
                    {
                        if (TryResolveLeaf(symbol, write, out var leaf, out var reason))
                        {
                            rootPlan.Leaves.Add(new LeafTarget(leaf, write));
                        }
                        else
                        {
                            rootPlan.Problems.Add($"{write.Path}: {reason}");
                        }
                    }

                    plans.Add(rootPlan);
                }
                catch (Exception e)
                {
                    logger.Error($"Could not prepare '{symbol.InstancePath}' for restore", e);
                    results.Add(VariableOperationResult.Failure(symbol.InstancePath, e));
                }
            }

            foreach (var problem in plans.SelectMany(p => p.Problems))
            {
                logger.Warn($"Restore mismatch: {problem}");
            }

            await WriteLeavesAsync(connection, plans.SelectMany(p => p.Leaves).ToList(), cancel).ConfigureAwait(false);

            results.AddRange(plans.Select(Describe));
        }

        /// <summary>
        /// Walks from a persistent variable down to the symbol that owns a single value.
        ///
        /// This is what makes a nested value arrive on the plc at all: the write goes to the leaf
        /// symbol itself, so nothing depends on the ads library carrying a change back up through
        /// the structure it was read into.
        /// </summary>
        private static bool TryResolveLeaf(ISymbol root, PlcLeafWrite write, out IValueSymbol leaf, out string reason)
        {
            leaf = null;
            reason = null;

            var current = root;

            foreach (var step in write.Steps)
            {
                var children = SubSymbolsOf(current);

                if (step.IsElement)
                {
                    // Elements are addressed by position rather than by declared index: the plc
                    // enumerates them in the same order the value tree does, whatever the lower
                    // bound is and however many dimensions the array has.
                    if (step.ElementPosition < 0 || step.ElementPosition >= children.Count)
                    {
                        reason = $"the plc reports {children.Count} elements under '{current.InstancePath}'";
                        return false;
                    }

                    current = children[step.ElementPosition];
                    continue;
                }

                var member = FindMember(children, current, step.MemberName);
                if (member == null)
                {
                    reason = $"'{step.MemberName}' is not a member of '{current.InstancePath}' on the plc";
                    return false;
                }

                current = member;
            }

            if (current.IsReadOnly)
            {
                reason = "the plc declares it read only";
                return false;
            }

            leaf = current as IValueSymbol;
            if (leaf == null)
            {
                reason = "the symbol carries no writable value";
                return false;
            }

            return true;
        }

        private static ISymbol FindMember(IList<ISymbol> children, ISymbol parent, string name)
        {
            if (children is ISymbolCollection<ISymbol> collection &&
                collection.TryGetInstance($"{parent.InstancePath}.{name}", out var found))
            {
                return found;
            }

            return children.FirstOrDefault(c => string.Equals(c.InstanceName, name, StringComparison.OrdinalIgnoreCase));
        }

        private static IList<ISymbol> SubSymbolsOf(ISymbol symbol)
        {
            try
            {
                return (IList<ISymbol>) symbol.SubSymbols ?? Array.Empty<ISymbol>();
            }
            catch (Exception)
            {
                // Some symbol kinds throw instead of returning an empty collection.
                return Array.Empty<ISymbol>();
            }
        }

        private async Task WriteLeavesAsync(IAdsConnection connection, IReadOnlyList<LeafTarget> leaves,
            CancellationToken cancel)
        {
            foreach (var chunk in Batch(leaves, MaxLeavesPerBatch, leaf => SizeOf(leaf.Symbol)))
            {
                cancel.ThrowIfCancellationRequested();
                await WriteLeafChunkAsync(connection, chunk, cancel).ConfigureAwait(false);
            }
        }

        private async Task WriteLeafChunkAsync(IAdsConnection connection, IReadOnlyList<LeafTarget> chunk,
            CancellationToken cancel)
        {
            try
            {
                var sum = new SumSymbolWrite(connection, chunk.Select(leaf => (ISymbol) leaf.Symbol).ToList());
                var code = sum.TryWrite(chunk.Select(leaf => leaf.Value).ToArray(), out var returnCodes);

                if (code == AdsErrorCode.NoError)
                {
                    for (var i = 0; i < chunk.Count; i++)
                    {
                        if (returnCodes != null && i < returnCodes.Length && returnCodes[i] != AdsErrorCode.NoError)
                        {
                            chunk[i].Failed($"ads error {returnCodes[i]}");
                        }
                    }

                    return;
                }

                logger.Warn($"Sum write of {chunk.Count} values failed with {code}, falling back to single writes");
            }
            catch (Exception e)
            {
                logger.Warn($"Sum write of {chunk.Count} values is not usable, falling back to single writes", e);
            }

            foreach (var leaf in chunk)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    // The write reports failure through its result, not by throwing: ignoring it
                    // would let a refused write be counted as a successful one.
                    var written = await leaf.Symbol.WriteValueAsync(leaf.Value, cancel).ConfigureAwait(false);

                    if (!written.Succeeded)
                    {
                        leaf.Failed($"ads error {(AdsErrorCode) written.ErrorCode}");
                    }
                }
                catch (Exception e)
                {
                    logger.Error($"Could not write '{leaf.Path}'", e);
                    leaf.Failed(e.Message);
                }
            }
        }

        private static VariableOperationResult Describe(RootPlan plan)
        {
            var refused = plan.Leaves.Where(leaf => leaf.Error != null).ToList();
            var problems = plan.Problems
                .Concat(refused.Select(leaf => $"{leaf.Path}: {leaf.Error}"))
                .ToList();

            if (problems.Count == 0)
            {
                return VariableOperationResult.Success(plan.Root.InstancePath);
            }

            var written = plan.Leaves.Count - refused.Count;

            return VariableOperationResult.Failure(plan.Root.InstancePath,
                written == 0
                    ? $"nothing could be written - {FirstReasons(problems)}"
                    : $"written partially - {FirstReasons(problems)}");
        }

        /// <summary>Everything one persistent variable needs written, and what went wrong with it.</summary>
        private class RootPlan
        {
            public RootPlan(ISymbol root)
            {
                Root = root;
            }

            public ISymbol Root { get; }
            public List<LeafTarget> Leaves { get; } = new List<LeafTarget>();

            /// <summary>Reasons a value never made it as far as being written.</summary>
            public List<string> Problems { get; } = new List<string>();
        }

        private class LeafTarget
        {
            public LeafTarget(IValueSymbol symbol, PlcLeafWrite write)
            {
                Symbol = symbol;
                Value = write.Value;
                Path = write.Path;
            }

            public IValueSymbol Symbol { get; }
            public object Value { get; }
            public string Path { get; }

            /// <summary>Null while the write is still considered successful.</summary>
            public string Error { get; private set; }

            public void Failed(string error) => Error = error;
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

        private static string FirstReasons(IReadOnlyList<string> problems)
        {
            const int maxReasons = 3;
            var reasons = problems.Take(maxReasons).ToList();
            var more = problems.Count - reasons.Count;

            return more > 0
                ? $"{string.Join("; ", reasons)} (and {more} more)"
                : string.Join("; ", reasons);
        }

        private static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> items, int maxCount, Func<T, int> sizeOf)
        {
            var current = new List<T>();
            var currentBytes = 0;

            foreach (var item in items)
            {
                var size = sizeOf(item);

                if (current.Count > 0 &&
                    (current.Count >= maxCount || currentBytes + size > MaxBytesPerBatch))
                {
                    yield return current;
                    current = new List<T>();
                    currentBytes = 0;
                }

                current.Add(item);
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
