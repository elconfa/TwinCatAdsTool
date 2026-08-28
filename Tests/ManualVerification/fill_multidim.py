"""
Fills one multidimensional array of a backup, and leaves the rest of the file untouched.

Same two guarantees as fill_backup.py, whose pick()/token() this reuses: every leaf changes,
and no two sibling leaves get the same value. On top of that it marks the position, because
for a rank 2 array the question is not whether the values come back but whether they come back
in the right place: the backup flattens ARRAY[0..n,0..m] into a single list, and nothing in the
json says which order that flattening used.

The markers are redundant on purpose, with k the flat index, i = k // SIDE, j = k % SIDE:

    <name marker>   = "R<i>C<j>"   six characters, readable by eye
    <first marker>  = j + 1        1..31, fits any integer type
    <second marker> = i + 1

If the strings did not fit a short STRING(n), the two integers still say where each element
landed. Read a transposition straight off them: k=1 and k=SIDE swap.

The three constants below are the variable this was run against. Point them at another one to
repeat the test elsewhere; the marker members have to exist in the element type.

    python3 fill_multidim.py Backup_....json filled.json
"""
import json, zlib, sys

GVL = "GVL_PERSISTENT_T"
VARIABLE = "AAArray"
SIDE = 31  # ARRAY[0..30, 0..30]

NAME_MARKER = ("_Setting", "_NomeCassetto")
COLUMN_MARKER = ("_Setting", "_NumeroLogico")
ROW_MARKER = ("_Config", "_NumeroLogico")

ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
INT_MODULUS = 127
FLOAT_MODULUS = 1000


def seed(path):
    return zlib.crc32(path.encode("utf-8"))


def token(path, length, salt):
    n = seed(path) + salt * 7919
    out = []
    for _ in range(length):
        n = zlib.crc32(str(n).encode("ascii"))
        out.append(ALPHABET[n % len(ALPHABET)])
    return "".join(out)


def pick(node, path, taken):
    """A value differing from the current one and from the siblings already assigned."""
    if isinstance(node, bool):
        return not node

    for salt in range(256):
        if isinstance(node, int):
            candidate = (seed(path) + salt) % INT_MODULUS + 1
        elif isinstance(node, float):
            candidate = float((seed(path) + salt) % FLOAT_MODULUS) + 0.5
        elif isinstance(node, str):
            candidate = token(path, len(node) if node else 3, salt)
        else:
            return node

        if candidate != node and candidate not in taken:
            return candidate

    return candidate


def fill(node, path):
    if isinstance(node, dict):
        taken = set()
        out = {}
        for key, value in node.items():
            child = f"{path}.{key}"
            if isinstance(value, (dict, list)):
                out[key] = fill(value, child)
            else:
                chosen = pick(value, child, taken)
                taken.add(chosen)
                out[key] = chosen
        return out

    if isinstance(node, list):
        taken = set()
        out = []
        for index, value in enumerate(node):
            child = f"{path}[{index}]"
            if isinstance(value, (dict, list)):
                out.append(fill(value, child))
            else:
                chosen = pick(value, child, taken)
                taken.add(chosen)
                out.append(chosen)
        return out

    return node


source, target = sys.argv[1], sys.argv[2]
with open(source, encoding="utf-8") as f:
    data = json.load(f)

array = data[GVL][VARIABLE]
if len(array) != SIDE * SIDE:
    sys.exit(f"{VARIABLE}: expected {SIDE * SIDE} elements, found {len(array)}")

filled = []
for k, element in enumerate(array):
    i, j = divmod(k, SIDE)
    new = fill(element, f"{VARIABLE}[{k}]")
    new[NAME_MARKER[0]][NAME_MARKER[1]] = f"R{i:02d}C{j:02d}"
    new[COLUMN_MARKER[0]][COLUMN_MARKER[1]] = j + 1
    new[ROW_MARKER[0]][ROW_MARKER[1]] = i + 1
    filled.append(new)

data[GVL][VARIABLE] = filled

with open(target, "w", encoding="utf-8") as f:
    json.dump(data, f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"written {target}")
