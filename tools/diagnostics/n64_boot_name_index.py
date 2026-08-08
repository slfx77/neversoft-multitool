#!/usr/bin/env python3
"""Find how an N64 boot image maps an asset SLOT to its filename.

The carved model bundles and triggers are numbered, but each port's boot.bin
carries the original PS1 filename strings. The open question is whether the ROM
itself associates a name with a slot, or whether the strings are only a blob the
loader searches.

KEY OBSERVATION driving this probe: the strings are NOT a fixed-stride array.
`skbul_t.trg` occupies 12 bytes and `skfactory_t.trg` needs 16, yet they sit in
one contiguous run. A packed variable-length blob cannot be indexed by
arithmetic, so if the game indexes it at all there must be a POINTER TABLE.

SEARCH (deliberately base-independent, because the boot load base is not round
and differs per ROM - THPS1's is 0x80016990):

  Scan for runs of consecutive big-endian u32 that all look like KSEG0/KSEG1
  pointers AND whose consecutive differences equal plausible string strides.
  A pointer table into a packed name blob has exactly that signature: each
  entry is base+offset, so successive entries differ by the length of the
  string between them.

  Any run found is then validated by subtracting a candidate base and checking
  that every entry lands exactly on the FIRST BYTE of a real string.

Usage:
    python tools/diagnostics/n64_boot_name_index.py [--carve-root DIR] [--rom NAME]
    python tools/diagnostics/n64_boot_name_index.py --min-run 6
"""

from __future__ import annotations

import argparse
import pathlib
import re
import struct
import sys
from collections import Counter

REPO = pathlib.Path(__file__).resolve().parents[2]

ASCII_RUN = re.compile(rb"[\x20-\x7E]{3,}")
KSEG_LO = 0x8000_0000
KSEG_HI = 0x8080_0000


def string_starts(boot: bytes) -> dict[int, str]:
    """offset -> string, for every NUL-terminated printable run."""
    out = {}
    for m in ASCII_RUN.finditer(boot):
        start, text = m.start(), m.group().decode("ascii")
        # A packed blob NUL-terminates each entry; a run that is not followed by
        # a NUL is part of something larger (a format string, a message).
        end = m.end()
        if end < len(boot) and boot[end] == 0:
            out[start] = text
    return out


def pointer_runs(boot: bytes, min_run: int, max_stride: int) -> list[tuple[int, list[int]]]:
    """Runs of consecutive BE u32 that look like pointers into a packed blob.

    Ascending-delta only. This is the BOOTSTRAP pass: its job is to surface
    candidate load bases cheaply, not to recover whole tables — a table stored
    in SLOT order is not sorted by address, so a monotone run stops at the first
    inversion. `resolvable_runs` does the real recovery once a base is known.
    """
    count = len(boot) // 4
    words = struct.unpack(f">{count}I", boot[: count * 4])

    runs = []
    i = 0
    while i < count:
        if not (KSEG_LO <= words[i] < KSEG_HI):
            i += 1
            continue

        j = i + 1
        while j < count and KSEG_LO <= words[j] < KSEG_HI:
            delta = words[j] - words[j - 1]
            if not (0 < delta <= max_stride):
                break
            j += 1

        if j - i >= min_run:
            runs.append((i * 4, list(words[i:j])))
            i = j
        else:
            i += 1

    return runs


def resolvable_runs(
    boot: bytes, base: int, starts: dict[int, str], min_run: int
) -> list[tuple[int, list[str]]]:
    """Maximal runs of consecutive u32 that ALL resolve to a string start.

    No ordering constraint whatsoever — which is the point. A table indexed by
    slot stores its pointers in slot order, so consecutive entries can move
    backwards in the blob. Requiring monotonicity (as the bootstrap pass does)
    truncates exactly the tables worth finding.
    """
    count = len(boot) // 4
    words = struct.unpack(f">{count}I", boot[: count * 4])

    runs = []
    current: list[str] = []
    start_index = 0
    for i, word in enumerate(words):
        name = starts.get(word - base)
        if name is None:
            if len(current) >= min_run:
                runs.append((start_index * 4, current))
            current = []
            continue
        if not current:
            start_index = i
        current.append(name)

    if len(current) >= min_run:
        runs.append((start_index * 4, current))
    return runs


def validate(run: list[int], starts: dict[int, str]) -> tuple[int, list[str]] | None:
    """Find a base making every entry land on a real string start."""
    # Candidate bases: assume entry[0] points at some string.
    candidates = Counter()
    for offset in starts:
        candidates[run[0] - offset] += 1

    for base in candidates:
        names = []
        for entry in run:
            offset = entry - base
            if offset not in starts:
                names = []
                break
            names.append(starts[offset])
        if names:
            return base, names
    return None


def analyse(boot: bytes, name: str, min_run: int, max_stride: int) -> None:
    starts = string_starts(boot)
    runs = pointer_runs(boot, min_run, max_stride)
    print(f"\n{'=' * 78}\n{name}: {len(boot)} bytes, {len(starts)} NUL-terminated strings, "
          f"{len(runs)} candidate pointer runs (min {min_run})")

    hits = []
    for offset, run in runs:
        result = validate(run, starts)
        if result is None:
            continue
        base, names = result
        assets = sum(1 for n in names if n.lower().endswith((".psx", ".trg")))
        hits.append((offset, base, names, assets))

    if not hits:
        print("  no run resolves entirely onto string starts")
        return

    hits.sort(key=lambda h: -len(h[2]))
    for offset, base, names, assets in hits[:6]:
        kinds = Counter(
            n.rsplit(".", 1)[-1].lower() if "." in n else "(bare)" for n in names)
        print(f"  seed @0x{offset:06X}  base=0x{base:08X}  n={len(names):<5} "
              f"assets={assets:<5} kinds={dict(list(kinds.items())[:4])}")

    # Every distinct base the bootstrap surfaced, re-run without the ordering
    # constraint. This is where whole tables actually come back.
    print("\n  --- unordered recovery, per candidate base ---")
    seen: set[tuple[int, int]] = set()
    for _, base, _, _ in hits:
        for offset, names in resolvable_runs(boot, base, starts, min_run):
            if (offset, len(names)) in seen:
                continue
            seen.add((offset, len(names)))
            assets = sum(1 for n in names if n.lower().endswith((".psx", ".trg")))
            if assets < min_run:
                continue
            kinds = Counter(n.rsplit(".", 1)[-1].lower() for n in names if "." in n)
            print(f"  TABLE @0x{offset:06X}  base=0x{base:08X}  n={len(names):<5} "
                  f"assets={assets:<5} {dict(kinds)}")
            print(f"      {names[:8]}")
            if len(names) > 8:
                print(f"      ... {names[-4:]}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--carve-root", type=pathlib.Path,
                    default=REPO / "TestOutput" / "n64carve")
    ap.add_argument("--rom", help="only this carve directory")
    ap.add_argument("--min-run", type=int, default=8)
    ap.add_argument("--max-stride", type=int, default=64,
                    help="largest plausible gap between adjacent packed strings")
    args = ap.parse_args()

    if not args.carve_root.is_dir():
        print(f"carve root not found: {args.carve_root}", file=sys.stderr)
        return 2

    for rom in sorted(p for p in args.carve_root.iterdir() if p.is_dir()):
        if args.rom and rom.name != args.rom:
            continue
        boot = rom / "boot.bin"
        if boot.is_file():
            analyse(boot.read_bytes(), rom.name, args.min_run, args.max_stride)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
