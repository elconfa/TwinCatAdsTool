using System;
using System.Collections.Generic;
using System.Linq;
using TwinCAT.TypeSystem;
using TwinCatAdsTool.Interfaces.Models;

namespace TwinCatAdsTool.Logic.Services
{
    public class PersistentSymbolScan
    {
        public PersistentSymbolScan(IReadOnlyList<ISymbol> roots, IReadOnlyList<VariableOperationResult> skipped)
        {
            Roots = roots;
            Skipped = skipped;
        }

        /// <summary>
        /// Outermost persistent symbols. Each one is read or written as a whole, which is what
        /// keeps the number of ads round trips proportional to the number of declared persistent
        /// variables instead of the number of leaves inside them.
        /// </summary>
        public IReadOnlyList<ISymbol> Roots { get; }

        /// <summary>Symbols deliberately left out, each with the reason why.</summary>
        public IReadOnlyList<VariableOperationResult> Skipped { get; }
    }

    /// <summary>
    /// Walks the symbol tree once and collects the persistent variables to back up.
    /// </summary>
    public class PersistentSymbolScanner
    {
        /// <summary>
        /// Guards against symbol trees that reference themselves through pointers or references.
        /// </summary>
        private const int MaxDepth = 32;

        public PersistentSymbolScan Scan(IEnumerable<ISymbol> symbols)
        {
            var roots = new List<ISymbol>();
            var skipped = new List<VariableOperationResult>();

            foreach (var symbol in symbols ?? Enumerable.Empty<ISymbol>())
            {
                Walk(symbol, roots, skipped, 0);
            }

            return new PersistentSymbolScan(roots, skipped);
        }

        private static void Walk(ISymbol symbol, List<ISymbol> roots, List<VariableOperationResult> skipped, int depth)
        {
            if (symbol == null)
            {
                return;
            }

            if (depth > MaxDepth)
            {
                skipped.Add(VariableOperationResult.Skipped(symbol.InstancePath,
                    $"nesting deeper than {MaxDepth} levels, possibly a recursive type"));
                return;
            }

            var isArray = symbol.DataType?.Category == DataTypeCategory.Array;

            if (symbol.IsPersistent && HasOwnName(symbol))
            {
                // An indexed path can only turn up here when the array itself is not marked
                // persistent while an element is - report it rather than inventing a json key
                // that no backup format ever used.
                if (symbol.InstancePath.Contains("["))
                {
                    skipped.Add(VariableOperationResult.Skipped(symbol.InstancePath,
                        "single array elements cannot be backed up on their own"));
                    return;
                }

                // Taking the outermost persistent symbol means the whole structure or array
                // travels in one read; do not descend any further.
                roots.Add(symbol);
                return;
            }

            // Never expand array elements: a plc cannot declare a single element as persistent,
            // and materialising the sub symbols of a large array is expensive on its own.
            if (isArray)
            {
                return;
            }

            foreach (var sub in SubSymbolsOf(symbol))
            {
                Walk(sub, roots, skipped, depth + 1);
            }
        }

        /// <summary>
        /// Top level symbols - the programs and global variable lists themselves - are containers,
        /// never variables to back up. Only what lives inside them qualifies.
        /// </summary>
        private static bool HasOwnName(ISymbol symbol)
            => symbol.InstancePath?.Contains(".") == true;

        private static IEnumerable<ISymbol> SubSymbolsOf(ISymbol symbol)
        {
            try
            {
                return symbol.SubSymbols ?? Enumerable.Empty<ISymbol>();
            }
            catch (Exception)
            {
                // Some symbol kinds throw instead of returning an empty collection.
                return Enumerable.Empty<ISymbol>();
            }
        }
    }
}
