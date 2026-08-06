#!/usr/bin/env python3
"""Map carved N64 boot.bin file offsets to MIPS RAM addresses, and back.

The Edge of Reality N64 ports (THPS1/2/3 + Spider-Man) keep TWO distinct
code regions and confusing them wastes hours:

  * ROM 0x1000.. -> RAM 0x80000400   the uncompressed loader + the ERZ
                                     decompressor. NOT part of boot.bin.
                                     (tools/diagnostics/erz_emu_decode.py
                                     works on this region, from the .z64.)
  * boot.bin     -> RAM ~0x80016B20  the decompressed game image, i.e. every
                                     asset loader including the group2
                                     render-bank consumer.

The per-ROM base of the second region was derived 2026-08-06 two independent
ways and is baked in below:

  1. Correlation: at the stated base, ~91-94% of all `jal` targets land inside
     the image and ~95% of those land immediately after a `jr $ra` + delay
     slot, i.e. on real function entries. Runner-up offsets score ~10x worse.
  2. The ROM's own boot code builds the same constant as a lui/addiu pair
     around RAM 0x80000408..0x80000908 - it is the loader's destination.

Usage
-----
  n64_boot_map.py <game>                      show the mapping and verify it
  n64_boot_map.py <game> --ram 0x800A1234     RAM   -> file offset
  n64_boot_map.py <game> --off 0x8A834        file  -> RAM address
  n64_boot_map.py <game> --verify             re-derive the base from the data
  n64_boot_map.py --all                       table for every carved ROM

<game> is thps1 | thps2 | thps3 | spiderman, or a path to a boot.bin.
Carved trees are expected under TestOutput/n64/<game>/boot.bin.
"""
from __future__ import annotations

import argparse
import os
import struct
import sys

REPO_ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CARVE_ROOT = os.path.join(REPO_ROOT, "TestOutput", "n64")

# game -> (RAM base of boot.bin offset 0, expected size in bytes)
BOOT_BASES = {
    "thps1": (0x80016990, 904_832),
    "thps2": (0x80016B20, 949_776),
    "thps3": (0x80016B20, 965_680),
    "spiderman": (0x80016AE0, 995_056),
}

# The other address space, for reference only.
LOADER_ROM_OFFSET = 0x1000
LOADER_RAM_BASE = 0x80000400


def boot_path(game: str) -> str:
    if os.path.isfile(game):
        return game
    return os.path.join(CARVE_ROOT, game, "boot.bin")


def load(game: str) -> tuple[bytes, int]:
    path = boot_path(game)
    if not os.path.isfile(path):
        sys.exit(f"no boot.bin at {path} (carve the ROM first)")
    key = game if game in BOOT_BASES else os.path.basename(os.path.dirname(path))
    if key not in BOOT_BASES:
        sys.exit(f"unknown game '{game}'; expected one of {', '.join(BOOT_BASES)}")
    return open(path, "rb").read(), BOOT_BASES[key][0]


def words(data: bytes):
    """Iterate (offset, big-endian u32) over the image."""
    for off in range(0, len(data) - 3, 4):
        yield off, struct.unpack_from(">I", data, off)[0]


def score_base(data: bytes, base: int) -> tuple[int, int, int]:
    """Score a candidate base: (jal targets in range, of those on a prologue,
    total jal). A correct base maximizes both ratios.

    'On a prologue' = the target is either the first instruction of the image
    or is preceded by a `jr $ra` and its delay slot, which is how MIPS
    functions abut in a compiled image.
    """
    total = in_range = on_prologue = 0
    end = base + len(data)
    for _, w in words(data):
        if (w >> 26) != 0x03:  # jal
            continue
        total += 1
        target = (w & 0x03FFFFFF) << 2
        # jal keeps the top 4 bits of the delay-slot PC; the whole image sits
        # in one 256MB segment, so reconstructing with the base is exact.
        target |= base & 0xF0000000
        if not (base <= target < end):
            continue
        in_range += 1
        off = target - base
        if off == 0:
            on_prologue += 1
            continue
        # two instructions back should be `jr $ra` (0x03E00008)
        if off >= 8 and struct.unpack_from(">I", data, off - 8)[0] == 0x03E00008:
            on_prologue += 1
    return in_range, on_prologue, total


def verify(game: str, sweep: bool) -> None:
    data, base = load(game)
    in_range, on_prologue, total = score_base(data, base)
    print(f"{game}: base {base:#010x} size {len(data)} ({len(data):#x})")
    print(f"  jal total {total}, in range {in_range} ({in_range / max(total,1):.1%}),"
          f" on prologue {on_prologue} ({on_prologue / max(in_range,1):.1%} of in-range)")
    if not sweep:
        return
    print("  sweeping nearby bases (step 0x10, +/-0x200):")
    best = []
    for delta in range(-0x200, 0x201, 0x10):
        cand = base + delta
        r, p, _ = score_base(data, cand)
        best.append((p, r, cand))
    best.sort(reverse=True)
    for p, r, cand in best[:5]:
        mark = " <== configured" if cand == base else ""
        print(f"    {cand:#010x}  in-range {r:6d}  prologue {p:6d}{mark}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("game", nargs="?", help="thps1|thps2|thps3|spiderman or a boot.bin path")
    ap.add_argument("--ram", help="RAM address (hex) -> file offset")
    ap.add_argument("--off", help="file offset (hex) -> RAM address")
    ap.add_argument("--verify", action="store_true", help="re-derive/justify the base from the data")
    ap.add_argument("--all", action="store_true", help="show the table for every carved ROM")
    args = ap.parse_args()

    if args.all:
        print(f"{'game':<11}{'boot base':<13}{'size':>9}   {'RAM range'}")
        for game, (base, size) in BOOT_BASES.items():
            path = boot_path(game)
            actual = os.path.getsize(path) if os.path.isfile(path) else None
            note = "" if actual in (None, size) else f"  (on disk {actual}!)"
            end = base + (actual or size)
            print(f"{game:<11}{base:#010x}   {actual or size:>9}   {base:#010x}-{end:#010x}{note}")
        print(f"\nseparate space: ROM {LOADER_ROM_OFFSET:#x}.. -> RAM {LOADER_RAM_BASE:#010x} "
              f"(loader + ERZ core; not in boot.bin)")
        return

    if not args.game:
        ap.error("give a game (or --all)")

    if args.ram or args.off:
        _, base = load(args.game)
        if args.ram:
            ram = int(args.ram, 16)
            print(f"RAM {ram:#010x} -> boot.bin offset {ram - base:#x}")
        if args.off:
            off = int(args.off, 16)
            print(f"boot.bin offset {off:#x} -> RAM {base + off:#010x}")
        return

    verify(args.game, sweep=args.verify)


if __name__ == "__main__":
    main()
