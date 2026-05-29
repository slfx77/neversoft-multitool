"""Scan PCSX2 _context.txt files in a directory and print per-draw FRAME target.

Usage:
    python scan_pcsx2_frame_targets.py <pcsx2_rt_dir> [start_draw] [end_draw]

Highlights bloom-intermediate FBPs (02c80/03540/2bc0/2dc0/33c0) with a '*' marker.
"""
from __future__ import annotations

import os
import re
import sys


FRAME_PATTERN = re.compile(
    r"FRAME:\s*\n\s*FBP:\s*0x([0-9A-Fa-f]+).*?"
    r"\n\s*FBW:\s*(\d+).*?"
    r"\n\s*PSM:\s*0x([0-9A-Fa-f]+).*?"
    r"\n\s*FBMSK:\s*0x([0-9A-Fa-f]+)",
    re.S,
)

PRIM_ABE_PATTERN = re.compile(
    r"PRIM:\s*(\d+).*?\n.*?\n.*?\n.*?\n.*?\n\s*ABE:\s*(\d)",
    re.S,
)

BLOOM_FBPS = {"2c80", "3540", "2bc0", "2dc0", "33c0"}
PRIM_NAMES = {
    "0": "POINT",
    "1": "LINE",
    "2": "LINESTRIP",
    "3": "TRIANGLE",
    "4": "TRISTRIP",
    "5": "TRIFAN",
    "6": "SPRITE",
}


def main() -> int:
    if len(sys.argv) < 2:
        print(__doc__)
        return 2
    directory = sys.argv[1]
    start = int(sys.argv[2]) if len(sys.argv) > 2 else 1
    end = int(sys.argv[3]) if len(sys.argv) > 3 else start + 50

    for n in range(start, end + 1):
        fn = f"{n:05d}_context.txt"
        path = os.path.join(directory, fn)
        if not os.path.exists(path):
            continue
        text = open(path, encoding="utf-8").read()
        frame = FRAME_PATTERN.search(text)
        abe = PRIM_ABE_PATTERN.search(text)
        if not frame:
            continue
        fbp, fbw, psm, fbmsk = frame.groups()
        prim, ab = abe.groups() if abe else ("?", "?")
        prim_name = PRIM_NAMES.get(prim, prim)
        marker = "*" if fbp.lower() in BLOOM_FBPS else " "
        print(
            f"  {marker} draw {n}: FBP=0x{fbp:>4} FBW={fbw:>2} PSM=0x{psm:>2} "
            f"FBMSK=0x{fbmsk:>8} prim={prim_name:<9} ABE={ab}"
        )

    return 0


if __name__ == "__main__":
    sys.exit(main())
