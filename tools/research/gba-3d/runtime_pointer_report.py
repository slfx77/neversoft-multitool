#!/usr/bin/env python3
"""Report ROM pointers retained in a captured GBA RAM image.

The BizHawk capture helper writes EWRAM/IWRAM snapshots.  This probe identifies
aligned words that still point into the cartridge and groups both their source
sites and target regions, which is useful for finding live level descriptors.
"""
from __future__ import annotations

import argparse
import struct
from collections import Counter
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("dump", type=Path)
    parser.add_argument("--ram-base", type=lambda value: int(value, 0), default=0x02000000)
    parser.add_argument("--rom-size", type=lambda value: int(value, 0), default=0x02000000)
    parser.add_argument("--bucket", type=lambda value: int(value, 0), default=0x10000)
    parser.add_argument("--limit", type=int, default=80)
    args = parser.parse_args()

    data = args.dump.read_bytes()
    hits: list[tuple[int, int]] = []
    for offset in range(0, len(data) - 3, 4):
        value = struct.unpack_from("<I", data, offset)[0]
        if 0x08000000 <= value < 0x08000000 + args.rom_size:
            hits.append((args.ram_base + offset, value))

    print(f"{args.dump}: {len(hits)} aligned ROM pointers")
    buckets = Counter((target - 0x08000000) // args.bucket * args.bucket for _, target in hits)
    for region, count in buckets.most_common(args.limit):
        print(f"  ROM 0x{region:06X}..0x{region + args.bucket - 1:06X}: {count}")

    print("\nsource runs:")
    run: list[tuple[int, int]] = []
    for hit in hits:
        if run and hit[0] != run[-1][0] + 4:
            if len(run) >= 2:
                preview = " ".join(f"{target:08X}" for _, target in run[:8])
                suffix = " ..." if len(run) > 8 else ""
                print(f"  {run[0][0]:08X} ({len(run):3d}): {preview}{suffix}")
            run = []
        run.append(hit)
    if len(run) >= 2:
        preview = " ".join(f"{target:08X}" for _, target in run[:8])
        suffix = " ..." if len(run) > 8 else ""
        print(f"  {run[0][0]:08X} ({len(run):3d}): {preview}{suffix}")


if __name__ == "__main__":
    main()
