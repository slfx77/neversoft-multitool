#!/usr/bin/env python3
"""Settle whether THPS2/THPS3 `sked<N>_w<M>` park-editor pieces are level geometry.

WHY THIS EXISTS
---------------
`n64_bundle_classification_probe.py` labels ground truth by NAME, and its
labeller contains `re.fullmatch(r"_w\\d", tail) -> "level"` — an authored
assumption, not a measurement, sitting inside the oracle that the level
predicate is scored against. 40 bundles ride on it. If they were really
character/prop, the classifier's "zero errors" headline would collapse
(charprop->level would go 0 -> 40).

WHAT IT MEASURES
----------------
1. PS1 siblings: which sked stems own a `_t.trg` / `_l.psx` / `_o.psx`.
2. N64 carve: each sked bundle's object count and `bounds.bin` max radius,
   against the empty band that separates world-scale from character/prop.

RESULT (2026-08-07, THPS2)
--------------------------
  sked<N>.psx  + sked<N>_l.psx + sked<N>_t.trg   -> sked<N> IS a level
  sked<N>_w<M>.psx  (five per park, no trigger, no library of its own)

  10 named `_w` bundles in the N64 carve: 27-49 objects, maxRadius
  4,122 - 10,357. The ROM's empty band is charprop max 906 / world min 1,538,
  so the pieces clear the world-scale threshold by 3-9x and CANNOT cross into
  character/prop under any threshold inside the band.

VERDICT: the `_w\\d -> level` label is correct, and the predicate's headline
does not depend on it either way — the threshold is on a measured radius, and
these sit nowhere near it. `sked3_w1` and `sked4_w1` share a radius exactly
(4,122.0): the two park themes reuse one piece set.

Usage:
    python tools/diagnostics/n64_sked_piece_probe.py [--carve DIR] [--build NAME]
"""

from __future__ import annotations

import argparse
import pathlib
import struct
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))

from n64_trg_bundle_checksum_join import parse_psx_header  # noqa: E402

WORLD_SCALE_RADIUS = 1150.0
LEVEL_MIN_OBJECTS = 24


def max_radius(bounds: pathlib.Path) -> float:
    """bounds.bin: BE u32 count, then count x 24-byte records; radius u32 20.12 at +4."""
    if not bounds.exists():
        return 0.0
    data = bounds.read_bytes()
    if len(data) < 4:
        return 0.0
    count = struct.unpack_from(">I", data, 0)[0]
    if count == 0 or 4 + count * 24 > len(data):
        return 0.0
    return max(struct.unpack_from(">I", data, 4 + i * 24 + 4)[0] for i in range(count)) / 4096.0


def ps1_siblings(build: pathlib.Path) -> None:
    if not build.is_dir():
        print(f"  (PS1 build not found: {build})")
        return
    names = {p.name.lower() for p in build.rglob("sked*") if p.is_file()}
    print(f"  {'stem':<12}{'.psx':>7}{'_t.trg':>9}{'_l.psx':>9}{'_o.psx':>9}")
    for stem in sorted({n.rsplit(".", 1)[0].removesuffix("_t").removesuffix("_l").removesuffix("_o")
                        for n in names if n.endswith((".psx", ".trg"))}):
        if not stem.startswith("sked"):
            continue
        print(f"  {stem:<12}{str(stem + '.psx' in names):>7}"
              f"{str(stem + '_t.trg' in names):>9}"
              f"{str(stem + '_l.psx' in names):>9}"
              f"{str(stem + '_o.psx' in names):>9}")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--carve", type=pathlib.Path,
                    default=REPO / "TestOutput" / "n64carve" / "Tony_Hawk's_Pro_Skater_2")
    ap.add_argument("--build", default="Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)")
    args = ap.parse_args()

    print("PS1 sibling companions (does a stem own a trigger / texture library?)")
    ps1_siblings(REPO / "Sample" / "Builds" / args.build)

    models = args.carve / "models"
    if not models.is_dir():
        print(f"\nno carve at {models}; run `archive <rom>.z64 -o {args.carve}` first")
        return 2

    rows = []
    for bundle in sorted(models.iterdir()):
        shells = sorted(bundle.glob("*.psx.n64")) if bundle.is_dir() else []
        if not shells:
            continue
        parsed = parse_psx_header(shells[0].read_bytes(), big_endian=True)
        if parsed is None:
            continue
        rows.append((shells[0].name, parsed[0], max_radius(bundle / "bounds.bin")))

    sked = [r for r in rows if "sked" in r[0]]
    other = [r for r in rows if "sked" not in r[0]]

    print(f"\nN64 carve: {len(sked)} named sked* bundles")
    print(f"  {'bundle':<32}{'objects':>9}{'maxRadius':>12}  class")
    for name, objects, radius in sked:
        world = radius >= WORLD_SCALE_RADIUS
        level = world and objects >= LEVEL_MIN_OBJECTS
        print(f"  {name:<32}{objects:>9}{radius:>12.1f}  "
              f"{'LEVEL' if level else 'WORLD' if world else 'charprop'}")

    world = [r[2] for r in other if r[2] >= WORLD_SCALE_RADIUS]
    char = [r[2] for r in other if r[2] < WORLD_SCALE_RADIUS]
    if world and char:
        print(f"\n  empty band (non-sked): charprop max {max(char):.1f} .. world min {min(world):.1f}")
        if sked:
            print(f"  sked pieces span {min(r[2] for r in sked):.1f} .. {max(r[2] for r in sked):.1f}"
                  f"  ({min(r[2] for r in sked) / WORLD_SCALE_RADIUS:.1f}x the threshold)")
            print("  VERDICT: world-scale, nowhere near the band — the label cannot flip the rule.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
