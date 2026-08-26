"""
Fills a backup with one value per leaf, derived from the leaf's path.

Two guarantees, and they are what makes the comparison afterwards a proof rather than an
impression:

  1. every leaf changes with respect to its current value. A leaf left equal would be blind:
     if the restore lost it, the backup taken afterwards would match all the same.
  2. no two sibling leaves get the same value, as long as there are values to go round. This
     is what makes a swap between neighbouring array elements show up instead of cancelling out.

Constraint: the width the PLC declares is not visible from the json. An integer could be a
SINT (-128..127) as easily as a DINT, so values stay in 1..127, which fits any integer type.
Floats stay whole numbers plus 0.5: exact even in a single precision REAL, so a difference on
the way back is an error and not a rounding. Non-empty strings are replaced by others of the
same length, so they cannot overflow a STRING(n).

    python3 fill_backup.py Backup_....json filled.json
"""
import json, zlib, sys

ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
INT_MODULUS = 127
FLOAT_MODULUS = 1000


def seed(path):
    return zlib.crc32(path.encode("utf-8"))


def token(path, length, salt):
    n = seed(path) + salt * 7919
    out = []
    for i in range(length):
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

    # More siblings than available values: the only guarantee left is that it changes.
    return candidate


def fill(node, path):
    if isinstance(node, dict):
        taken = set()
        out = {}
        for key, value in node.items():
            child = f"{path}.{key}" if path else key
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

with open(target, "w", encoding="utf-8") as f:
    json.dump(fill(data, ""), f, indent=2, ensure_ascii=False)
    f.write("\n")

print(f"written {target}")
