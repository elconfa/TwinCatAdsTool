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
        /// persistent variables, and each one carries only a handful of bytes.
        ///
        /// What a restore costs turns out to be set by the number of commands and hardly at all by
        /// how many values each one carries: on a real plant, twenty-two commands took 6.4 s and
        /// six took 1.6 s for the same eleven thousand values. So the ceiling is deliberately high
        /// and <see cref="MaxBytesPerBatch"/> is the bound that actually decides. A command the plc
        /// refuses is halved and retried rather than abandoned, which is what makes a high ceiling
        /// safe on a controller with tighter limits than this one.
        /// </summary>
        private const int MaxLeavesPerBatch = 10000;

        /// <summary>Below this, halving again buys less than the extra round trip costs.</summary>
        private const int SmallestChunk = 50;

        private const int MaxBytesPerBatch = 256 * 1024;

        private readonly ILog logger = LoggerFactory.GetLogger();
        private readonly PersistentSymbolScanner scanner = new PersistentSymbolScanner();

        /// <param name="scope">Whether <paramref name="backup"/> is meant to account for the whole
        /// of every variable it names, or holds only some of the values on purpose. A comparison that
        /// writes a few chosen differences back onto the plc passes
        /// <see cref="PlanScope.OnlyValuesPresent"/>: everything it leaves out was left out
        /// deliberately, so it must not be reported as missing.</param>
        public async Task<PersistentOperationReport> WriteAsync(IAdsConnection connection,
            IEnumerable<ISymbol> symbols,
            JObject backup,
            PlanScope scope,
            IProgress<OperationProgress> progress,
            CancellationToken cancel)
        {
            var whole = scope == PlanScope.WholeVariable;
            var stopwatch = Stopwatch.StartNew();
            var results = new List<VariableOperationResult>();
            var phases = new Phases();

            var scan = scanner.Scan(symbols);
            phases.Scan = stopwatch.Elapsed;

            // Variables the scan refused. When only a subset is being written, the ones it was
            // never asked about are none of this run's business: reporting them would say a merge
            // of three values was incomplete because of variables nobody touched.
            results.AddRange(whole
                ? scan.Skipped
                : scan.Skipped.Where(skip => JsonPathBuilder.Find(backup, skip.InstancePath) != null));

            var matched = new List<ISymbol>();
            foreach (var symbol in scan.Roots)
            {
                if (JsonPathBuilder.Find(backup, symbol.InstancePath) == null)
                {
                    if (whole)
                    {
                        results.Add(VariableOperationResult.Skipped(symbol.InstancePath,
                            "not present in the backup file, left unchanged on the plc"));
                    }

                    continue;
                }

                matched.Add(symbol);
            }

            // Reported whichever way this is called. An entry with nothing to match on the plc is a
            // value that was asked for and cannot be delivered - which matters more, not less, when
            // it was picked one at a time out of a comparison.
            results.AddRange(FindOrphans(backup, scan.Roots));

            var done = 0;
            foreach (var batch in Batch(matched, MaxSymbolsPerBatch, SizeOf))
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report(new OperationProgress(
                    $"Writing {done + 1}-{done + batch.Count} of {matched.Count} " +
                    (whole ? "persistent variables..." : "variables..."),
                    done,
                    matched.Count));

                await WriteBatchAsync(connection, batch, backup, scope, results, phases, cancel).ConfigureAwait(false);
                done += batch.Count;
            }

            stopwatch.Stop();
            progress?.Report(OperationProgress.Idle);

            var what = whole ? "Restore" : "Merge";
            var report = new PersistentOperationReport(results, stopwatch.Elapsed);
            logger.Info($"{what} finished: {report.Summary}");
            logger.Info($"{what} timing: {phases}");

            return report;
        }

        /// <summary>
        /// Reads every variable of the batch to learn its declared types, works out which
        /// individual leaves the backup wants written, and writes those leaves.
        /// </summary>
        private async Task WriteBatchAsync(IAdsConnection connection,
            IReadOnlyList<ISymbol> batch,
            JObject backup,
            PlanScope scope,
            List<VariableOperationResult> results,
            Phases phases,
            CancellationToken cancel)
        {
            var plans = new List<RootPlan>();
            var clock = Stopwatch.StartNew();

            // One sum command for the whole batch rather than a read per variable. The values are
            // only consulted - they say which type every leaf holds so the json can be converted
            // into it - but reading them one at a time cost an ads round trip per persistent
            // variable, and on a controller that answers slowly, or one that is busy running a
            // program, that dominated the whole restore. The backup has always read this way.
            var current = await ReadCurrentValuesAsync(connection, batch, results, cancel).ConfigureAwait(false);
            phases.Read += Lap(clock);

            for (var i = 0; i < batch.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();

                var symbol = batch[i];

                // Null means the value could not be read; whoever could not read it has already
                // said why.
                if (current[i] == null)
                {
                    continue;
                }

                try
                {
                    var node = new DynamicValueNode(current[i]);
                    var json = JsonPathBuilder.Find(backup, symbol.InstancePath);
                    var plan = PlcLeafPlanner.Plan(node, json, symbol.InstancePath, scope);

                    var rootPlan = new RootPlan(symbol);
                    rootPlan.Problems.AddRange(plan.Mismatches);

                    // Every leaf is reached by walking down from the variable, and the leaves of one
                    // variable share nearly all of that walk. Remembering the nodes already reached
                    // turns a descent per leaf into one per node: asking a symbol for its children
                    // builds that collection afresh each time, so a structure holding an array of a
                    // hundred elements was having those hundred rebuilt once per leaf underneath it.
                    var resolved = new Dictionary<string, ISymbol>(StringComparer.Ordinal);

                    foreach (var write in plan.Writes)
                    {
                        if (TryResolveLeaf(symbol, write, resolved, out var leaf, out var reason))
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

            phases.Plan += Lap(clock);

            foreach (var problem in plans.SelectMany(p => p.Problems))
            {
                logger.Warn($"Restore mismatch: {problem}");
            }

            await WriteLeavesAsync(connection, plans.SelectMany(p => p.Leaves).ToList(), phases, cancel).ConfigureAwait(false);

            results.AddRange(plans.Select(Describe));
        }

        /// <summary>How long since the last lap, and start counting again.</summary>
        private static TimeSpan Lap(Stopwatch clock)
        {
            var elapsed = clock.Elapsed;
            clock.Restart();
            return elapsed;
        }

        /// <summary>
        /// Where a restore spent its time. It goes in the log because "the restore is slow" is
        /// otherwise impossible to act on: reading the current values, working out which leaves to
        /// write and writing them are three different costs with three different remedies.
        /// </summary>
        private class Phases
        {
            public TimeSpan Scan { get; set; }

            /// <summary>Reading the current value of each persistent variable.</summary>
            public TimeSpan Read { get; set; }

            /// <summary>Matching the backup against them and resolving every leaf symbol.</summary>
            public TimeSpan Plan { get; set; }

            /// <summary>Building the write commands, handles included.</summary>
            public TimeSpan Prepare { get; set; }

            /// <summary>The write telegrams themselves.</summary>
            public TimeSpan Transfer { get; set; }

            /// <summary>How many single values were written, and in how many commands. Without both
            /// numbers the time above cannot be read: the same total means opposite things when it
            /// is spent per command and when it is spent per value.</summary>
            public int Leaves { get; set; }

            public int Chunks { get; set; }

            public override string ToString()
                => $"scan {Scan.TotalSeconds:F2} s, read {Read.TotalSeconds:F2} s, " +
                   $"plan {Plan.TotalSeconds:F2} s, prepare {Prepare.TotalSeconds:F2} s, " +
                   $"transfer {Transfer.TotalSeconds:F2} s for {Leaves} values in {Chunks} commands";
        }

        /// <summary>
        /// The current value of every variable in the batch, with a null wherever it could not be
        /// read - the reason having been recorded against that variable.
        ///
        /// The reader carries an equivalent of this for the backup. The two are deliberately not
        /// shared yet: that path is verified against a plant, and unifying them is a change worth
        /// making on its own rather than inside a fix for something else.
        /// </summary>
        private async Task<object[]> ReadCurrentValuesAsync(IAdsConnection connection,
            IReadOnlyList<ISymbol> batch,
            List<VariableOperationResult> results,
            CancellationToken cancel)
        {
            var values = new object[batch.Count];
            var readable = new List<ISymbol>();
            var positions = new List<int>();

            for (var i = 0; i < batch.Count; i++)
            {
                if (batch[i] is IValueSymbol)
                {
                    readable.Add(batch[i]);
                    positions.Add(i);
                }
                else
                {
                    results.Add(VariableOperationResult.Failure(batch[i].InstancePath, "symbol carries no writable value"));
                }
            }

            if (readable.Count == 0)
            {
                return values;
            }

            var pending = new List<int>();

            try
            {
                var sum = new SumSymbolRead(connection, readable);
                var code = sum.TryRead(out var read, out var returnCodes);

                if (code == AdsErrorCode.NoError && read != null)
                {
                    for (var i = 0; i < readable.Count; i++)
                    {
                        if (returnCodes != null && i < returnCodes.Length && returnCodes[i] != AdsErrorCode.NoError)
                        {
                            results.Add(VariableOperationResult.Failure(readable[i].InstancePath,
                                $"could not read the current value - ads error {returnCodes[i]}"));
                            continue;
                        }

                        var value = i < read.Length ? read[i] : null;

                        if (value == null)
                        {
                            pending.Add(i);
                            continue;
                        }

                        values[positions[i]] = value;
                    }
                }
                else
                {
                    logger.Warn($"Sum read of {readable.Count} symbols failed with {code}, falling back to single reads");
                    pending.AddRange(Enumerable.Range(0, readable.Count));
                }
            }
            catch (Exception e)
            {
                logger.Warn($"Sum read of {readable.Count} symbols is not usable, falling back to single reads", e);
                pending.AddRange(Enumerable.Range(0, readable.Count));
            }

            // Whatever the sum command could not deliver is read on its own, so that a failure is
            // attributed to the right variable instead of taking the whole batch down with it.
            foreach (var i in pending)
            {
                cancel.ThrowIfCancellationRequested();

                try
                {
                    // ReadValueAsync hands back a result object, not the value itself. Passing the
                    // wrapper on made DynamicValueNode find neither members nor elements, so every
                    // structure and array looked like a scalar and the restore reported "backup
                    // value does not fit the plc type" for all of them.
                    var read = await ((IValueSymbol) readable[i]).ReadValueAsync(cancel).ConfigureAwait(false);

                    if (read.Succeeded)
                    {
                        values[positions[i]] = read.Value;
                    }
                    else
                    {
                        results.Add(VariableOperationResult.Failure(readable[i].InstancePath,
                            $"could not read the current value - ads error {(AdsErrorCode) read.ErrorCode}"));
                    }
                }
                catch (Exception e)
                {
                    results.Add(VariableOperationResult.Failure(readable[i].InstancePath, e));
                }
            }

            return values;
        }

        /// <summary>
        /// Walks from a persistent variable down to the symbol that owns a single value.
        ///
        /// This is what makes a nested value arrive on the plc at all: the write goes to the leaf
        /// symbol itself, so nothing depends on the ads library carrying a change back up through
        /// the structure it was read into.
        /// </summary>
        private static bool TryResolveLeaf(ISymbol root, PlcLeafWrite write,
            Dictionary<string, ISymbol> resolved, out IValueSymbol leaf, out string reason)
        {
            leaf = null;
            reason = null;

            var current = root;
            var key = root.InstancePath;

            foreach (var step in write.Steps)
            {
                key = step.IsElement ? $"{key}[{step.ElementPosition}]" : $"{key}.{step.MemberName}";

                if (resolved.TryGetValue(key, out var already))
                {
                    current = already;
                    continue;
                }

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
                    resolved[key] = current;
                    continue;
                }

                var member = FindMember(children, current, step.MemberName);
                if (member == null)
                {
                    reason = $"'{step.MemberName}' is not a member of '{current.InstancePath}' on the plc";
                    return false;
                }

                current = member;
                resolved[key] = current;
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
            Phases phases, CancellationToken cancel)
        {
            phases.Leaves += leaves.Count;

            foreach (var chunk in Batch(leaves, MaxLeavesPerBatch, leaf => SizeOf(leaf.Symbol)))
            {
                cancel.ThrowIfCancellationRequested();
                await WriteLeafChunkAsync(connection, chunk, phases, cancel).ConfigureAwait(false);
            }
        }

        private async Task WriteLeafChunkAsync(IAdsConnection connection, IReadOnlyList<LeafTarget> chunk,
            Phases phases, CancellationToken cancel)
        {
            var clock = Stopwatch.StartNew();
            phases.Chunks++;

            try
            {
                // Building the command and sending it are timed apart on purpose. The ads library
                // needs a handle for every symbol it writes by name, and whether it acquires those
                // one at a time or in one go is not visible from here - but it is the difference
                // between a telegram per leaf and a telegram per chunk, and the two look the same
                // from outside unless they are measured separately.
                var sum = new SumSymbolWrite(connection, chunk.Select(leaf => (ISymbol) leaf.Symbol).ToList());
                var values = chunk.Select(leaf => leaf.Value).ToArray();
                phases.Prepare += Lap(clock);

                var code = sum.TryWrite(values, out var returnCodes);
                phases.Transfer += Lap(clock);

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

                logger.Warn($"Sum write of {chunk.Count} values failed with {code}");
            }
            catch (Exception e)
            {
                phases.Transfer += Lap(clock);
                logger.Warn($"Sum write of {chunk.Count} values is not usable", e);
            }

            // A refused command is usually refused for being too big for that controller, so it is
            // halved and tried again. Dropping straight to a write per value would turn one refusal
            // into thousands of round trips, which is the slowest possible answer to a limit that
            // could have been met by asking for less at a time.
            if (chunk.Count > SmallestChunk)
            {
                var half = chunk.Count / 2;
                logger.Warn($"Retrying as two commands of about {half} values");

                await WriteLeafChunkAsync(connection, chunk.Take(half).ToList(), phases, cancel).ConfigureAwait(false);
                await WriteLeafChunkAsync(connection, chunk.Skip(half).ToList(), phases, cancel).ConfigureAwait(false);
                return;
            }

            logger.Warn($"Falling back to single writes for {chunk.Count} values");

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
