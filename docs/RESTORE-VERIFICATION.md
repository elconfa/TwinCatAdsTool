# Field verification of the restore

What was measured against a real PLC, and how to repeat it. Nothing here is inferred from reading
the code: every conclusion rests on a value read back from a PLC.

**A note on method, learned the hard way.** A backup restored onto a *running* PLC cannot be
compared leaf by leaf: what the program writes by itself is indistinguishable from what the restore
would have lost. Verifying the tool needs the PLC stopped. Ignoring this produced one wrong
conclusion before it was understood.

---

## Test 1 — Nested branches

### What was suspected

When the conversion layer enters a nested structure, the ADS library hands it a **copy** of the
buffer, not a view:

```csharp
// TwinCAT.Ads, DynamicValueFactory.CreateValue, decompiled
return new DynamicValue(symbol, sourceData.ToArray(), (DynamicValue)parent);
```

A restore would then write into the copy, which nobody copies back into the parent, and the value
sent to the PLC would stay the one that was read. The report would say "succeeded", because writes
into the copy return `true`.

### Why this variable

`GVL.PersVarGlobalUser1_1` is a `User1DUT`, which contains **both** cases:

| Member | Level | |
|---|---|---|
| `Int1`, `Int2`, `Bool1`, `Bool2`, `Bool3` | first level, primitive | should change |
| `InInVar.IntInIn1`, `InInVar.RealInIn1` | inside a nested struct | the case under test |

The comparison is internal to one variable, in one restore: if the first level changes and the
second does not, there is no alternative explanation — not the connection, not permissions, not the
wrong variable.

### Procedure

1. Start the PLC and connect the tool.
2. **Backup**, and keep the file. This is both the reference and the way back.
3. **Restore** → *Load* → `Tests/ManualVerification/restore-annidato.json` → *Write*.
4. **Backup** again, and compare the two files.

### Result: defect confirmed, then fixed

First run, with the old engine:

| Variable | Before | Asked for | After | |
|---|---|---|---|---|
| `GVL.PersVarGlobalArray` | `[6,7,8,0]` | `[11,22,33,44]` | `[11,22,33,44]` | written |
| `PersVarGlobalUser1_1.Int1` | 157 | 1001 | 1001 | written |
| `.Int2` | 168 | 1002 | 1002 | written |
| `.Bool1` `.Bool2` `.Bool3` | true, true, false | false, false, true | false, false, true | written |
| `.InInVar.IntInIn1` | 12 | **9999** | **12** | **lost** |
| `.InInVar.RealInIn1` | 123.2345 | **999.5** | **123.2345** | **lost** |

The first level was written, the nested branch was not, and the tool reported success.

The boundary is not "arrays yes, structures no": the array of `INT` at the root worked. The rule is
that **everything reached by passing through a non primitive child is lost**.

Second run, after the rewrite to leaf-wise writing: `InInVar.IntInIn1` = 9999 and
`InInVar.RealInIn1` = 999.5. **Closed.**

In the same round, a full backup restored onto the PLC it came from gave **20 ok, 0 failed**,
against 16 ok and 4 failed before — the four being `TIME` and `LTIME`, see `ValueCoercion`.

---

## Test 2 — Multidimensional arrays and arrays of DUT

Still open. The plant used for Test 3 does not contain multidimensional arrays.

The test project has no such types either. Create a new GVL in XAE and paste:

```iecst
{attribute 'qualified_only'}
VAR_GLOBAL PERSISTENT
    // array of structures: the main use case of the tool
    ArrOfDut    : ARRAY[0..2] OF UserInnerInnerDUT;

    // jagged array: rank 1 whose element is itself an array
    ArrJagged   : ARRAY[0..1] OF ARRAY[0..1] OF UserInnerInnerDUT;

    // multidimensional array: rank 2, a different type from the previous one
    ArrMulti    : ARRAY[0..1, 0..1] OF UserInnerInnerDUT;

    // same rank 2, but of primitives: a different code path
    ArrMultiInt : ARRAY[0..1, 0..1] OF INT;
END_VAR
```

Dimensions kept minimal on purpose, so the JSON stays readable by eye.

### Procedure

1. **Backup** and open the JSON. Compare the shape:

   | Variable | Expected shape |
   |---|---|
   | `ArrJagged` | array of 2 arrays of 2 → `[[{...},{...}],[{...},{...}]]` |
   | `ArrMulti` | **flat** array of 4 → `[{...},{...},{...},{...}]` |
   | `ArrMultiInt` | **flat** array of 4 → `[0,0,0,0]` |

   If `ArrMulti` comes out nested like `ArrJagged`, the analysis of the shape is wrong; report it.

2. Change one value inside `ArrOfDut[0]`, one inside `ArrJagged`, one in `ArrMulti` and one in
   `ArrMultiInt`. **Restore** with the PLC stopped, then read back.

### How to read the result

| Observation | Conclusion |
|---|---|
| All four change | Leaf-wise writing covers multidimensional arrays too. |
| `ArrOfDut` and `ArrJagged` change, `ArrMulti` and `ArrMultiInt` do not | Only the shape of the **backup** is left to fix: it flattens rank 2, and the restore puts the values back in the wrong order or fails. |
| Something reports success but does not change on the PLC | Report it immediately: it means a write is accepted and has no effect, a case Test 1 does not cover. |

Note: the write path addresses array elements **by position** among the child symbols rather than by
a computed index, so `Dimensions mismatch!` can no longer occur there. Whether that is enough is to
be measured, not assumed — the backup still produces them flattened, and it has not been verified
that the order of the child symbols matches that flattening.

---

## Test 3 — Real plant, a distinct value on every leaf

The decisive test, and the only one that covers the types a real installation uses.

A backup of a real plant — **48 persistent variables, 10,978 leaves, nesting up to eight levels
deep, 465 arrays, 833 structures**, of which **10,960 leaves lie below the second level** — filled
with a distinct value per leaf, restored, and backed up again for a leaf by leaf comparison.

### Generating the file

`Tests/ManualVerification/fill_backup.py` guarantees two things, without which the comparison would
prove nothing:

- **every leaf changes** with respect to its current value. A leaf left equal is blind: if the
  restore lost it, the returned backup would match anyway;
- **no two sibling leaves get the same value**, so a swap between neighbouring array elements shows
  up instead of cancelling out.

Both were violated by a first attempt — 1450 leaves happened to keep their value, and two adjacent
elements of the same array drew the same number — which is why the script enforces them explicitly.

Constraints on the values, because the declared width is not visible from the JSON: integers in
1..127 (they fit any integer type, `SINT` included), floats as whole numbers plus 0.5 (exact even in
a single precision `REAL`, so a difference is an error and not a rounding), strings of the same
length as the original (they cannot overflow a `STRING(n)`).

```
python3 Tests/ManualVerification/fill_backup.py Backup_....json filled.json
```

Two unknowns remain that the JSON cannot resolve: whether some integer is really an enum, and
whether some empty string is a `STRING(1)` or `STRING(2)`. If the report shows errors exactly there,
it is a limit of the test file, not of the tool — and the paths make it obvious which.

### Result

| Run | PLC | Leaves correct | Divergent |
|---|---|---|---|
| 1 | in **Run** | 10,874 / 10,978 | 104 |
| 2 | in **Stop** | **10,978 / 10,978** | **0** |

The 104 in the first run were all of the leaves of `Pers_CC[0]` (96) and `Pers_CC_Axia[0]` (8),
while indices 1 and up of the same arrays were correct — as were index 0 of `Pers_4bit_Strobe`,
`Pers_NN._Setting`, `Pers_NN._Config`, `Pers_NN_H._Setting`, `Pers_NN_H._Config` and
`Pers_TracciaPk`. No structural rule separates those two arrays from the others.

What pointed at the answer: `Pers_CC[0]._Setting._ConteggioCicli` held 105, a value **no leaf in the
file asked for** (the 46 leaves that did ask for 105 had all arrived). So it was not a value that
landed in the wrong place — it had appeared on its own, meaning the program was writing into that
slot. Slot 0 of those two arrays is the working slot the program keeps cleared.

Repeated with the PLC stopped, the divergence disappears entirely.

**Conclusion: the backup → restore → backup cycle is verified end to end on a real plant**,
including structures nested eight levels deep, arrays of structures, arrays inside structures inside
arrays, strings, REALs and BOOLs. Together with Test 1 that adds `TIME`, `LTIME` and `DT`.

Restore of all 10,978 leaves took **2.5 seconds**.

---

## Restoring the plant afterwards

Test 3 overwrites the real settings with meaningless numbers. The backup taken at step 2 is the way
back: restore it before putting the PLC into Run.
