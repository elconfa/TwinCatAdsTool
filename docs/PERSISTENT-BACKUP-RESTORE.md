# Persistent variable backup and restore

The engine was rewritten twice: first for speed and reporting, then for correctness, once a real
PLC showed that restoring structured variables never worked.

This document covers the design. The evidence is in [RESTORE-VERIFICATION.md](RESTORE-VERIFICATION.md),
and the defects are summarised in the [README](../README.md).

## The two original problems

**1. Ten minutes on a plant with arrays of structures.**
The tool delegated to `TwinCAT.JsonExtension` (`ReadJson` / `WriteJson`), whose `ReadRecursive`
walks down to every leaf and runs `ReadSymbol` + `ReadValue` on each: **two ADS round trips per
leaf**. An `ARRAY[1..500] OF ST_Dati` with 20 members is 10,000 leaves, so roughly 20,000 sequential
telegrams. On the client side, `iterator.Contains(s.Parent)` inside a `Where` made the scan O(n²),
re-enumerating the symbol tree for every symbol.

**2. Variables lost with no warning.**
The defect was in the *backup*, not in the restore. The per-variable `catch` wrote a log line and
the variable did not go into the JSON. The file was saved as though it were complete. At restore
time the variable was simply not there, so nothing was written and there was nothing to report.

## How they were solved

Each persistent variable is transferred **whole** — the ADS library rebuilds the value tree on the
client side — and variables are grouped into **sum commands**, which move a batch in a single
telegram. Sum commands also return a **per-symbol error code**, which is what makes complete
reporting possible.

Every persistent variable now produces an outcome: written, failed with its ADS error, or skipped
with the reason. Backup and restore both show the report in the interface.

## Writing: leaves, not whole variables

The restore does **not** modify the value it read in order to write it back. That approach cannot
work below the first level, for a reason that is worth stating precisely, because nothing about it
is visible from the API:

```csharp
// TwinCAT.Ads, DynamicValueFactory.CreateValue, decompiled
return new DynamicValue(symbol, sourceData.ToArray(), (DynamicValue)parent);
```

Entering a struct member or an array element yields a **copy** of the buffer. Writes into a nested
branch went into that copy, which nobody copied back into the parent. Worse, `TrySetMemberValue`
only assigns `if (val2.IsPrimitiveType)` and returns `true` without doing anything for everything
else, so the operation reported success.

`PlcLeafPlanner` therefore turns a backup entry into the list of individual leaves to write, and
each value is written **to the symbol that owns it**, in sum commands. The value that was read is
only consulted, to learn the declared type of each leaf.

```
PersistentVariableWriter
  ├─ read the variable            → declared types of every leaf
  ├─ PlcLeafPlanner.Plan(...)     → which leaves the backup wants written, with what value
  ├─ TryResolveLeaf(...)          → walk ISymbol.SubSymbols down to the owning symbol
  └─ SumSymbolWrite in batches    → one telegram per batch
```

Three consequences:

- **The report names the leaf.** `GVL.Var.InInVar.IntInIn1: ads error ...` rather than just the
  variable.
- **Array elements are addressed by position** among the child symbols, not by a computed index.
  This is what removes `ArrayIndexConverter`'s `Dimensions mismatch!` from the write path for
  multidimensional arrays. The backup still flattens them, and it was not obvious that the two
  orders agree; measured on a plant, they do — 92,256 leaves of an `ARRAY[0..30, 0..30] OF DUT`
  restored with none out of place. Test 2 in [RESTORE-VERIFICATION.md](RESTORE-VERIFICATION.md).
- **The conversion is a pure function**, so it is testable without a PLC —
  `PlcLeafPlannerTests.cs`. This matters: the previous design could not be covered, because
  `FakeValueNode.TryGetMutableMember` returned the same object where the real library returns a
  copy. No unit test could ever have caught the defect.

The mutable half of the value tree (`IMutablePlcValueNode`, `PlcJsonConverter.ApplyJson` and the
setters on `DynamicValueNode`) was removed once nothing used it. Leaving a known-broken write API in
place would have meant keeping green tests on a path the product does not take.

## Reading: DynamicTree, not VirtualTree

`ClientService.UpdateSymbols` loads symbols with `SymbolsLoadMode.DynamicTree`. Under `VirtualTree`
the library hands a STRUCT back as `byte[]`, and the engine was written for a kind of value the
loader never gave it — so a structure came out of the backup as a list of numbers while the report
said everything succeeded.

## New components

| File | Role |
|---|---|
| `Interfaces/Models/VariableOperationResult.cs` | outcome of a single variable |
| `Interfaces/Models/PersistentOperationReport.cs` | overall report, `IsComplete`, `Details()` |
| `Interfaces/Models/OperationProgress.cs` | progress with `Done` / `Total`, so a progress bar can be driven |
| `Interfaces/Values/IPlcValueNode.cs` | read-only abstraction of the value tree |
| `Logic/Values/PlcJsonConverter.cs` | value tree to JSON, and JSON tokens to plain values |
| `Logic/Values/PlcLeafPlanner.cs` | which leaves a restore has to write, and with what value |
| `Logic/Values/ValueCoercion.cs` | fits JSON types to the declared PLC types |
| `Logic/Values/DynamicValueNode.cs` | adapter over the ADS library's `DynamicValue` |
| `Logic/Services/PersistentSymbolScanner.cs` | finds the root persistent variables, O(n) |
| `Logic/Services/JsonPathBuilder.cs` | builds the JSON tree from dotted paths |
| `Logic/Services/PersistentVariableReader.cs` | backup, with sum commands and a fallback |
| `Logic/Services/PersistentVariableWriter.cs` | restore, with sum commands and a fallback |

## Other defects fixed along the way

- **Paths with repeated names.** `InstancePath.Replace("." + localName, "")` replaces *every*
  occurrence, not the last segment: `GVL.Axis.Axis` collapsed to `GVL` and the value landed in the
  wrong node. The path is now split on its separators.
- **`ReadValueAsync` returns a result object**, not the value. The wrapper was being passed on, so
  the conversion layer found neither members nor elements and every structure looked like a scalar.
  The same call in the reader's fallback path had the same defect.
- **`WriteValueAsync`'s result was ignored**, so a write the PLC refused counted as a success.
- **Type coercion.** JSON only knows `long`, `double`, `bool` and `string`; without conversion to
  the declared type, a restore fails on every `INT`, `BYTE` or `DT`.
- **`TimeSpan` is not `IConvertible`.** `TIME`, `LTIME` and `TOD` all normalise to a `TimeSpan`, and
  JSON has no notion of one, so a backup read back from disk hands the value over as a string. The
  general `Convert.ChangeType` threw on it. Timestamps were spared only because Newtonsoft re-parses
  them as a date token.
- **PlcOpen timestamps.** What has to survive a backup and restore is the instant, not its written
  form, so a value is interpreted through its own `Kind` rather than being forced into a zone.
- **Arrays of primitive PLC types** come back as a plain managed array (`bool[]`, `byte[]`), not as
  a `DynamicValue`. They were treated as leaves, so the whole array went to `JValue`, which cannot
  type it, and the variable dropped out of the backup — reported, but still lost.
- **Arrays of different length** between file and PLC are now reported, and the intersection is
  written, rather than failing or truncating silently.

## Adjacent defect, deliberately untouched

`RestoreViewModel.DisplayVariables`: the setter assigns to `liveVariables` instead of
`displayVariables`. It does not show, because the code only uses the getter, but it is a mine.
