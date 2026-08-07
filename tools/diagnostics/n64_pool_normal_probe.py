#!/usr/bin/env python3
"""Test whether a render-bank pool's trailing four bytes are normals or colours.

F3DEX2 reuses the last four bytes of a Vtx for either a lit surface normal or
an RGBA colour, and the packed display list never encodes G_LIGHTING -- so the
converter decides from the data, currently by "is it unit length in signed-byte
space" (magnitude 100..150).

That test has a false-positive trap: a DARK GREY colour is also ~unit length.
(73,73,73) has magnitude 126, dead centre of the window. A level authored at
~0.3 brightness sits exactly there, and every pool it misreads exports pure
white -- rendering that geometry at full texture brightness against a level
shaded to 30%.

This measures the discriminators the magnitude test throws away:
  * are the three components equal (grey colour) or independent (normal)?
  * read as SIGNED bytes, do the components take negative values? A normal set
    must point in many directions; a colour set is all-positive under 128.
  * is the distribution of directions spread over the sphere, or clustered?

    python n64_pool_normal_probe.py <carved-rom-dir> [--record N] [--verbose]
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import struct

VERTEX_STRIDE = 16


def read_table(data, offset, limit):
    if offset < 0 or offset + 8 > limit or limit > len(data):
        return None
    (count,) = struct.unpack_from(">I", data, offset)
    if count == 0 or count > 65535:
        return None
    header = 4 + 4 * (count + 1)
    if offset + header > limit:
        return None
    offsets = struct.unpack_from(f">{count + 1}I", data, offset + 4)
    if offsets[0] not in (header, header + 4):
        return None
    if any(offsets[i + 1] < offsets[i] for i in range(count)):
        return None
    if offset + offsets[count] > limit:
        return None
    return [(offset + offsets[i], offset + offsets[i + 1]) for i in range(count)]


def read_floats(data, start, end):
    n = (end - start) // 4
    return struct.unpack_from(f">{n}f", data, start) if n else ()


def decode_pool(data, start, end, bounds):
    """Return the pool's trailing 4-byte tuples, choosing the layout by bounds."""
    if end - start < 8:
        return None
    (count,) = struct.unpack_from(">I", data, start)
    if count == 0 or count > (end - start - 8) // VERTEX_STRIDE:
        return None
    body = data[start + 8: start + 8 + count * VERTEX_STRIDE]

    def decode(transposed):
        out = []
        for i in range(count):
            if transposed:
                rec = bytes(body[k * count + i] for k in range(VERTEX_STRIDE))
            else:
                rec = body[i * VERTEX_STRIDE:(i + 1) * VERTEX_STRIDE]
            x, y, z = struct.unpack_from(">3h", rec)
            out.append((x, y, z, rec[12], rec[13], rec[14], rec[15]))
        return out

    plain, transposed = decode(False), decode(True)
    if len(bounds) < 6:
        return transposed

    def error(vs):
        xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
        return (sum((f - g) ** 2 for f, g in zip(
            (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs)), bounds[:6])))

    return transposed if error(transposed) <= error(plain) else plain


def iter_pools(data):
    root = read_table(data, 0, len(data))
    if root is None:
        return
    for node_index, (node_start, node_end) in enumerate(root):
        node = read_table(data, node_start, node_end)
        if node is None or len(node) != 3:
            continue
        bounds = read_floats(data, node[0][0], node[0][1])
        pool = decode_pool(data, node[2][0], node[2][1], bounds)
        if pool:
            yield node_index, pool


def classify(pool):
    """The current magnitude rule, plus the discriminators it ignores."""
    unit = grey = negative = 0
    for _, _, _, r, g, b, _ in pool:
        sr, sg, sb = (c - 256 if c > 127 else c for c in (r, g, b))
        if 100 <= (sr * sr + sg * sg + sb * sb) ** 0.5 <= 150:
            unit += 1
        if r == g == b:
            grey += 1
        if sr < 0 or sg < 0 or sb < 0:
            negative += 1
    total = len(pool)
    return {
        "count": total,
        "unit": unit / total,
        "grey": grey / total,
        "negative": negative / total,
        "verdict_current": unit * 5 >= total * 3,
    }


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("carved", type=pathlib.Path)
    parser.add_argument("--record", type=int, help="only this group2 record id")
    parser.add_argument("--verbose", action="store_true", help="one line per misread-looking pool")
    args = parser.parse_args()

    files = sorted((args.carved / "group2").glob("*.bin"))
    if args.record is not None:
        files = [f for f in files if int(f.stem) == args.record]

    buckets = collections.Counter()
    vertex_buckets = collections.Counter()
    for path in files:
        for node_index, pool in iter_pools(path.read_bytes()):
            stats = classify(pool)
            if not stats["verdict_current"]:
                key = "read as COLOUR"
            elif stats["negative"] < 0.02 and stats["grey"] > 0.5:
                key = "read as NORMAL but grey + no negatives (colour!)"
            elif stats["negative"] < 0.02:
                key = "read as NORMAL but no negative components (suspect)"
            else:
                key = "read as NORMAL (has negatives)"
            buckets[key] += 1
            vertex_buckets[key] += stats["count"]
            if args.verbose and "colour!" in key:
                print(f"  {path.stem} node {node_index}: n={stats['count']} "
                      f"unit={stats['unit']:.2f} grey={stats['grey']:.2f} "
                      f"neg={stats['negative']:.2f}")

    print(f"\n{'classification':<48} {'pools':>7} {'vertices':>10}")
    print("-" * 68)
    for key, count in buckets.most_common():
        print(f"{key:<48} {count:>7} {vertex_buckets[key]:>10}")


if __name__ == "__main__":
    main()
