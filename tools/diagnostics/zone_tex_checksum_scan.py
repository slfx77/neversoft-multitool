#!/usr/bin/env python3
"""Scan a build tree for files containing a given texture checksum (little-endian bytes).

Phase-3 A1 diagnostic: locate every file on disc that references a zone-TEX
checksum, to find where a runtime-streamed texture variant ships.

Usage:
    python tools/diagnostics/zone_tex_checksum_scan.py <build_root> <hex_checksum> [--ext .pak.ps2 ...]
"""

import argparse
import struct
import sys
from pathlib import Path


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("root")
    parser.add_argument("checksum")
    parser.add_argument("--ext", nargs="*", default=[".pak.ps2", ".tex.ps2", ".img.ps2", ".pab.ps2"])
    args = parser.parse_args()

    value = int(args.checksum, 16)
    needle = struct.pack("<I", value)
    root = Path(args.root)
    hits = 0

    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        name = path.name.lower()
        if not any(name.endswith(ext) for ext in args.ext):
            continue
        try:
            data = path.read_bytes()
        except OSError:
            continue
        count = data.count(needle)
        if count:
            hits += 1
            offsets = []
            start = 0
            for _ in range(min(count, 6)):
                idx = data.find(needle, start)
                offsets.append(f"0x{idx:X}")
                start = idx + 1
            print(f"{path.relative_to(root)}  x{count}  [{', '.join(offsets)}]")

    print(f"\n{hits} file(s) contain 0x{value:08X}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
