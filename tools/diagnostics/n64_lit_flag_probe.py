#!/usr/bin/env python3
"""Look for the flag that says a render-bank pool's trailing bytes are NORMALS.

F3DEX2 reuses the last four bytes of a Vtx for a lit surface normal or an RGBA
colour, chosen at runtime by G_LIGHTING -- which the packed display list never
encodes. The converter currently guesses from magnitude ("is it unit length in
signed-byte space"), and that guess is wrong on level geometry: a dark grey
(73,73,73) and a bright (200,190,180) both land in the window, so authored
colours get read as normals and export as pure WHITE.

Ground truth here is GEOMETRY: if the stored vectors really are normals they
must agree with the triangles' own face normals. This computes that agreement
per node (expanding the display list), then cross-tabs it against every
candidate flag -- the group descriptor's `kind` bits and blob B's face-flag
word -- to find one that predicts it.

    python n64_lit_flag_probe.py <carved-rom-dir> [--record N]
"""

from __future__ import annotations

import argparse
import collections
import math
import pathlib
import struct

VERTEX_STRIDE = 16
VERTEX_CACHE = 32
KIND_NON_DISPLAY_LIST = 0x8000


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


def decode_pool(data, start, end, bounds):
    if end - start < 8:
        return None
    (count,) = struct.unpack_from(">I", data, start)
    if count == 0 or count > (end - start - 8) // VERTEX_STRIDE:
        return None
    body = data[start + 8: start + 8 + count * VERTEX_STRIDE]

    def decode(transposed):
        out = []
        for i in range(count):
            rec = (bytes(body[k * count + i] for k in range(VERTEX_STRIDE)) if transposed
                   else body[i * VERTEX_STRIDE:(i + 1) * VERTEX_STRIDE])
            x, y, z = struct.unpack_from(">3h", rec)
            out.append((x, y, z, rec[12], rec[13], rec[14]))
        return out

    plain, transposed = decode(False), decode(True)
    if len(bounds) < 6:
        return transposed

    def error(vs):
        xs = [v[0] for v in vs]; ys = [v[1] for v in vs]; zs = [v[2] for v in vs]
        measured = (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))
        return sum((f - g) ** 2 for f, g in zip(measured, bounds[:6]))

    return transposed if error(transposed) <= error(plain) else plain


def expand(tokens, cache, cursor, triangles):
    """Minimal display-list walk: only G_VTX and triangle tokens matter here."""
    p = 0
    while p < len(tokens):
        op = tokens[p]
        if op == 0x00:
            break
        if op & 0x80:
            if p + 1 >= len(tokens):
                break
            word = (op << 8) | tokens[p + 1]
            corners = (cache[(word >> 10) & 31], cache[(word >> 5) & 31], cache[word & 31])
            if all(c >= 0 for c in corners):
                triangles.append(corners)
            p += 2
        elif (op & 0xE0) == 0x20:
            if p + 1 >= len(tokens):
                break
            word = (op << 8) | tokens[p + 1]
            n = (word & 31) or 32
            v0 = (word >> 5) & 31
            for k in range(n):
                if v0 + k < VERTEX_CACHE:
                    cache[v0 + k] = cursor
                cursor += 1
            p += 2
        elif (op & 0xE0) == 0x40:
            p += 2
        elif (op & 0xE0) == 0x60:
            p += 5
        else:
            break
    return cursor


def agreement(pool, triangles):
    """Mean |cos| between each triangle's stored vector and its face normal."""
    total, hits = 0.0, 0
    for a, b, c in triangles:
        if max(a, b, c) >= len(pool):
            continue
        pa, pb, pc = pool[a], pool[b], pool[c]
        ux, uy, uz = pb[0] - pa[0], pb[1] - pa[1], pb[2] - pa[2]
        vx, vy, vz = pc[0] - pa[0], pc[1] - pa[1], pc[2] - pa[2]
        gx, gy, gz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
        gl = math.sqrt(gx * gx + gy * gy + gz * gz)
        if gl < 1e-6:
            continue
        sx, sy, sz = (sum((v[i] - 256 if v[i] > 127 else v[i]) for v in (pa, pb, pc)) / 3.0
                      for i in (3, 4, 5))
        sl = math.sqrt(sx * sx + sy * sy + sz * sz)
        if sl < 1e-6:
            continue
        total += abs((gx * sx + gy * sy + gz * sz) / (gl * sl))
        hits += 1
    return (total / hits, hits) if hits else (None, 0)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("carved", type=pathlib.Path)
    parser.add_argument("--record", type=int)
    args = parser.parse_args()

    files = sorted((args.carved / "group2").glob("*.bin"))
    if args.record is not None:
        files = [f for f in files if int(f.stem) == args.record]

    by_kind = collections.defaultdict(lambda: [0, 0.0])
    verdicts = collections.Counter()
    for path in files:
        data = path.read_bytes()
        root = read_table(data, 0, len(data))
        if root is None:
            continue
        for node_start, node_end in root:
            node = read_table(data, node_start, node_end)
            if node is None or len(node) != 3:
                continue
            n = (node[0][1] - node[0][0]) // 4
            bounds = struct.unpack_from(f">{n}f", data, node[0][0]) if n else ()
            pool = decode_pool(data, node[2][0], node[2][1], bounds)
            if not pool:
                continue

            groups = read_table(data, node[1][0], node[1][1]) or []
            cache = [-1] * VERTEX_CACHE
            cursor = 0
            triangles = []
            kinds = set()
            for group_start, group_end in groups:
                group = read_table(data, group_start, group_end)
                if group is None or len(group) != 3 or group[0][1] - group[0][0] != 12:
                    continue
                (kind,) = struct.unpack_from(">H", data, group[0][0] + 6)
                if kind & KIND_NON_DISPLAY_LIST:
                    continue
                kinds.add(kind)
                cursor = expand(
                    data[group[1][0]:group[1][1]], cache, cursor, triangles)

            score, hits = agreement(pool, triangles)
            if score is None or hits < 4:
                continue
            verdict = "NORMALS" if score > 0.75 else ("colour" if score < 0.6 else "ambiguous")
            verdicts[verdict] += 1
            for kind in kinds:
                entry = by_kind[kind & ~0x0001]  # drop the texture-enable bit
                entry[0] += 1
                entry[1] += score

    print(f"per-node geometric agreement verdicts: {dict(verdicts)}\n")
    print(f"{'kind (tex bit masked off)':<28} {'nodes':>7} {'mean agreement':>16}")
    print("-" * 54)
    for kind, (count, total) in sorted(by_kind.items(), key=lambda kv: -kv[1][0])[:24]:
        print(f"{kind:#010x}{'':<18} {count:>7} {total / count:>16.3f}")


if __name__ == "__main__":
    main()
