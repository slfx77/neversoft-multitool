#!/usr/bin/env python3
"""Cross-tab the N64 render bank's per-triangle side table (blob B).

Blob B stores one u32 per triangle. The LOW half is the PS1 DISC face flag
word (established 2026-08-06). The HIGH half has never been decoded: an early
pass refuted it as a texture selector, but it could still be the RDP render
mode / combiner selector -- which is exactly what decides whether a face
flagged semi-transparent on the PS1 actually blends on the N64.

    python n64_face_flag_probe.py <carved-rom-dir>          # hi16 x ABR cross-tab
    python n64_face_flag_probe.py <carved-rom-dir> --bits   # per-bit correlation

Input is a carved ROM directory (`archive <rom>.z64 -o <dir>`); the probe walks
`group2/*.bin`.
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import struct

KIND_NON_DISPLAY_LIST = 0x8000
DESCRIPTOR_SIZE = 12


def read_table(data, offset, limit):
    """The recursive BE table shared by the whole N64 asset format."""
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


def iter_groups(data):
    """Yield (texture_slot, kind, [blobB words]) for every display-list group."""
    root = read_table(data, 0, len(data))
    if root is None:
        return
    for node_start, node_end in root:
        node = read_table(data, node_start, node_end)
        if node is None or len(node) != 3:
            continue
        groups = read_table(data, node[1][0], node[1][1])
        if groups is None:
            continue
        for group_start, group_end in groups:
            group = read_table(data, group_start, group_end)
            if group is None or len(group) != 3:
                continue
            if group[0][1] - group[0][0] != DESCRIPTOR_SIZE:
                continue
            (kind,) = struct.unpack_from(">H", data, group[0][0] + 6)
            if kind & KIND_NON_DISPLAY_LIST:
                continue
            slot = struct.unpack_from(">I", data, group[0][0])[0] if kind & 1 else 0

            b_start, b_end = group[2]
            if b_end - b_start < 4:
                continue
            (count,) = struct.unpack_from(">I", data, b_start)
            if count > (b_end - b_start - 4) // 4:
                continue
            words = struct.unpack_from(f">{count}I", data, b_start + 4) if count else ()
            yield slot, kind, words


def classify(low):
    """PS1 face-flag semantics: semi bit and ABR rate."""
    semi = bool(low & 0x0040)
    rate = (low & 0x0180) >> 7 if semi else None
    return f"st{rate}" if semi else "opaque"


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("carved", type=pathlib.Path)
    parser.add_argument("--bits", action="store_true", help="per-bit correlation with the semi flag")
    args = parser.parse_args()

    bank = args.carved / "group2"
    files = sorted(bank.glob("*.bin"))
    if not files:
        print(f"no group2/*.bin under {args.carved}")
        return

    cross = collections.Counter()
    hi_values = collections.Counter()
    bit_semi = collections.Counter()
    bit_total = collections.Counter()
    semi_total = 0
    total = 0

    for path in files:
        data = path.read_bytes()
        for _slot, _kind, words in iter_groups(data):
            for word in words:
                low, hi = word & 0xFFFF, word >> 16
                klass = classify(low)
                cross[(hi, klass)] += 1
                hi_values[hi] += 1
                total += 1
                semi = klass != "opaque"
                semi_total += semi
                for bit in range(16):
                    if hi & (1 << bit):
                        bit_total[bit] += 1
                        bit_semi[bit] += semi

    print(f"{total} triangle words across {len(files)} records; "
          f"{semi_total} semi-transparent ({semi_total / max(1, total):.1%})")
    print(f"distinct hi16 values: {len(hi_values)}\n")

    if args.bits:
        print(f"{'bit':>3} {'set':>9} {'of which semi':>14} {'semi rate':>10}  (corpus semi rate "
              f"{semi_total / max(1, total):.1%})")
        for bit in range(16):
            if not bit_total[bit]:
                continue
            print(f"{bit:>3} {bit_total[bit]:>9} {bit_semi[bit]:>14} "
                  f"{bit_semi[bit] / bit_total[bit]:>9.1%}")
        return

    classes = ["opaque", "st0", "st1", "st2", "st3"]
    print(f"{'hi16':>8} {'total':>9} " + " ".join(f"{c:>9}" for c in classes))
    print("-" * (18 + 10 * len(classes)))
    for hi, count in hi_values.most_common(30):
        row = " ".join(f"{cross[(hi, c)]:>9}" for c in classes)
        print(f"{hi:>#8x} {count:>9} {row}")


if __name__ == "__main__":
    main()
