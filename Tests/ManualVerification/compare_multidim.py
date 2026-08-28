"""
Compares the array written by fill_multidim.py against the backup taken after the restore.

    python3 compare_multidim.py filled.json Backup_after.json

It does not stop at counting divergences. If an element came back in the wrong place it says so,
by reading the position markers, and it checks the likeliest failure of a rank 2 array on its
own: row and column swapped, which is what a flattening order the write path does not share
would look like.

The constants have to match the ones in fill_multidim.py.
"""
import json, sys
from collections import Counter

GVL = "GVL_PERSISTENT_T"
VARIABLE = "AAArray"
SIDE = 31

NAME_MARKER = ("_Setting", "_NomeCassetto")
COLUMN_MARKER = ("_Setting", "_NumeroLogico")
ROW_MARKER = ("_Config", "_NumeroLogico")


def leaves(node, path=""):
    if isinstance(node, dict):
        for key, value in node.items():
            yield from leaves(value, f"{path}.{key}")
    elif isinstance(node, list):
        for index, value in enumerate(node):
            yield from leaves(value, f"{path}[{index}]")
    else:
        yield path, node


def marker(element, member):
    return element[member[0]][member[1]]


with open(sys.argv[1], encoding="utf-8") as f:
    asked = json.load(f)[GVL][VARIABLE]
with open(sys.argv[2], encoding="utf-8") as f:
    found = json.load(f)[GVL][VARIABLE]

if len(asked) != len(found):
    sys.exit(f"different lengths: {len(asked)} against {len(found)}")

ok = divergent = 0
by_element = Counter()
by_leaf = Counter()
for k, (a, b) in enumerate(zip(asked, found)):
    for (path, va), (_, vb) in zip(leaves(a), leaves(b)):
        if va == vb:
            ok += 1
        else:
            divergent += 1
            by_element[k] += 1
            by_leaf[path] += 1

print(f"leaves correct {ok} / {ok + divergent}   divergent {divergent}")

if divergent:
    print("\nmost frequently divergent leaves:")
    for path, n in by_leaf.most_common(10):
        print(f"  {n:6d}  {path}")

    print("\nwhere the elements landed (first 20 divergent):")
    for k, _ in sorted(by_element.items())[:20]:
        i, j = divmod(k, SIDE)
        element = found[k]
        row = marker(element, ROW_MARKER) - 1
        column = marker(element, COLUMN_MARKER) - 1
        print(f"  position {k:3d} = R{i:02d}C{j:02d}  ->  found "
              f"{marker(element, NAME_MARKER)!r} (integers: R{row:02d}C{column:02d})")

    transposed = sum(
        1 for k, element in enumerate(found)
        if marker(element, NAME_MARKER) == "R%02dC%02d" % tuple(reversed(divmod(k, SIDE)))
    )
    print(f"\nelements that came back transposed: {transposed} / {len(found)}")
