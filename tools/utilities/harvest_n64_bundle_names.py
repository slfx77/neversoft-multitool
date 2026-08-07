#!/usr/bin/env python3
"""Harvest real names for carved N64 model bundles from the PS1 corpus.

The N64 ports (Edge of Reality) re-encoded Neversoft's PS1 `.psx` containers
big-endian and stripped their geometry chunks, but the OBJECT TABLE and the
QbKey MESH-NAME HASH ARRAY survive intact. Measured 2026-08-07: matching a
bundle's mesh-name-hash SET for exact equality against the PS1 sibling corpus
identifies 89.7-95.4% of bundles outright (THPS1 62/65, THPS2 104/116,
THPS3 78/87, Spider-Man 164/182). The bundles ARE the PS1 files.

The PS1 corpus is not available at runtime, so identity is harvested HERE into
an embedded dictionary keyed by content, exactly as `ThawTextureNames` and
`HedDictionary` do. The key is a digest over the hash set, so it is derivable
from the carved shell alone.

WHY NOT boot.bin: each port's boot image does carry the original PS1 filename
tables, but its `.psx` entry count differs from the carve count on 3 of the 4
ROMs, so index->slot is provably not identity, and THPS1's single mixed table is
alphabetical (0/8 against an external node-count oracle). Order is unusable.
MEMBERSHIP is not - `--boot-filter` uses the tables as a candidate whitelist to
narrow ambiguous keys, never as an ordering.

AMBIGUITY IS REAL AND REPORTED: a hash set describes CONTENT, and several PS1
files can share content (Spider-Man's l9a3_o / l9a4_o / lba3_o / lba4_o are the
same 4-mesh set). Every candidate is stored; the consumer takes the first and
exposes the rest via ResolveAll. The census below prints how many keys are
ambiguous so the number is measured rather than hidden.

Usage:
    python tools/utilities/harvest_n64_bundle_names.py
    python tools/utilities/harvest_n64_bundle_names.py --no-boot-filter
    python tools/utilities/harvest_n64_bundle_names.py --selftest
    python tools/utilities/harvest_n64_bundle_names.py --check   # no write

Output: src/NeversoftMultitool/Core/Formats/Mesh/N64/N64BundleNames.txt
"""

from __future__ import annotations

import argparse
import pathlib
import struct
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))

from n64_trg_bundle_checksum_join import parse_psx_header  # noqa: E402

OUTPUT = (REPO / "src" / "NeversoftMultitool" / "Core" / "Formats" / "Mesh" / "N64"
          / "N64BundleNames.txt")

# Hashes below this are ordinals/sentinels, not QbKey name hashes. Same filter
# the join probe used, so the key is computed over the same population the
# 89.7-95.4% figures were measured on.
MINIMUM_MESH_NAME_HASH = 0x1_0000

FNV_BASIS = 0xCBF29CE484222325
FNV_PRIME = 0x100000001B3
MASK64 = 0xFFFF_FFFF_FFFF_FFFF


def fnv1a64(data: bytes) -> int:
    h = FNV_BASIS
    for b in data:
        h = ((h ^ b) * FNV_PRIME) & MASK64
    return h


def compute_key(hashes) -> int | None:
    """FNV-1a 64 over count(4B BE) then the DISTINCT qualifying hashes ascending.

    Order- and duplicate-invariant by construction: the C# side must produce the
    identical value, which `--selftest` pins.
    """
    distinct = sorted({h for h in hashes if h >= MINIMUM_MESH_NAME_HASH})
    if not distinct:
        return None
    buf = struct.pack(">I", len(distinct)) + b"".join(struct.pack(">I", h) for h in distinct)
    return fnv1a64(buf)


def harvest_ps1(builds: pathlib.Path) -> tuple[dict[int, set[str]], int, int]:
    """key -> {stem}. Walks every PS1-era `.psx` in the sample tree."""
    table: dict[int, set[str]] = {}
    scanned = 0
    parsed = 0
    for path in sorted(builds.rglob("*.psx")):
        if not path.is_file():
            continue
        scanned += 1
        try:
            result = parse_psx_header(path.read_bytes(), big_endian=False)
        except (OSError, struct.error):
            continue
        if result is None:
            continue
        _, _, _, hashes, _ = result
        key = compute_key(hashes)
        if key is None:
            continue
        parsed += 1
        table.setdefault(key, set()).add(path.stem.lower())
    return table, scanned, parsed


def boot_name_pool(carve_root: pathlib.Path) -> set[str]:
    """Union of every ROM's own boot.bin `.psx` name pool (MEMBERSHIP only)."""
    try:
        from n64_boot_name_tables import find_tables
    except ImportError:
        return set()

    pool: set[str] = set()
    if not carve_root.is_dir():
        return pool
    for rom in sorted(carve_root.iterdir()):
        boot = rom / "boot.bin"
        if not boot.is_file():
            continue
        for table in find_tables(boot.read_bytes()):
            for name in table["names"]:
                low = name.lower()
                if low.endswith(".psx"):
                    pool.add(low[:-4])
    return pool


def apply_boot_filter(table: dict[int, set[str]], pool: set[str]) -> int:
    """Drop candidates absent from the ROMs' own name pools, when that helps.

    Only narrows a key that is BOTH ambiguous and has at least one candidate in
    the pool - so a key whose candidates are all absent keeps every one of them
    rather than collapsing to nothing.
    """
    if not pool:
        return 0
    resolved = 0
    for key, stems in table.items():
        if len(stems) < 2:
            continue
        kept = {s for s in stems if s in pool}
        if kept and len(kept) < len(stems):
            table[key] = kept
            resolved += 1
    return resolved


def coverage_report(table: dict[int, set[str]], carve_root: pathlib.Path) -> None:
    """Per-ROM resolved/parsed, against the measured baseline."""
    baseline = {
        "Tony_Hawk's_Pro_Skater": (62, 65),
        "Tony_Hawk's_Pro_Skater_2": (104, 116),
        "Tony_Hawk's_Pro_Skater_3": (78, 87),
        "Spider-Man": (164, 182),
    }
    if not carve_root.is_dir():
        print("\n(no carve root; skipping the coverage check)")
        return

    print("\nCoverage against carved shells")
    print(f"  {'ROM':<28}{'resolved':>10}{'parsed':>8}{'rate':>9}   baseline")
    for rom in sorted(carve_root.iterdir()):
        models = rom / "models"
        if not models.is_dir():
            continue
        resolved = parsed = 0
        for bundle in sorted(models.iterdir()):
            shells = sorted(bundle.glob("*.psx.n64")) if bundle.is_dir() else []
            if not shells:
                continue
            try:
                result = parse_psx_header(shells[0].read_bytes(), big_endian=True)
            except (OSError, struct.error):
                continue
            if result is None:
                continue
            parsed += 1
            key = compute_key(result[3])
            if key is not None and key in table:
                resolved += 1
        want = baseline.get(rom.name)
        mark = ""
        if want:
            mark = f"   {want[0]}/{want[1]}" + ("  OK" if resolved >= want[0] else "  BELOW")
        rate = f"{resolved / parsed:.1%}" if parsed else "-"
        print(f"  {rom.name:<28}{resolved:>10}{parsed:>8}{rate:>9}{mark}")


def write_table(table: dict[int, set[str]], path: pathlib.Path) -> None:
    lines = [
        "# N64 carved-bundle names, keyed by mesh-name-hash-set digest.",
        "# Generated by tools/utilities/harvest_n64_bundle_names.py - do not hand-edit.",
        "#",
        "# These keys are FNV-1a digests of hash SETS, NOT CRC(name) pairs. They must",
        "# never be merged into QbKeyNames*.txt: doing so would poison every",
        "# proven-name harvest's existing_hashes() and the coverage metric.",
        "#",
        "# KEY(16 hex)=stem[|stem...]   multiple stems = PS1 files with identical content.",
    ]
    for key in sorted(table):
        lines.append(f"{key:016X}={'|'.join(sorted(table[key]))}")
    path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


def selftest() -> int:
    """Pinned vector shared with N64BundleNamesTests so both sides agree."""
    vector = [0x0001_0000, 0x0002_0000]
    key = compute_key(vector)
    print(f"ComputeKey([0x00010000, 0x00020000]) = 0x{key:016X}")
    # Order- and duplicate-invariance, asserted here as well as in C#.
    assert compute_key([0x0002_0000, 0x0001_0000, 0x0001_0000]) == key
    # Sub-minimum hashes are ignored.
    assert compute_key([0x0001_0000, 0x0002_0000, 0x0000_0005]) == key
    assert compute_key([0x0000_0005]) is None
    print("invariants OK (order, duplicates, minimum-hash filter)")
    return 0


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--builds", type=pathlib.Path, default=REPO / "Sample" / "Builds")
    ap.add_argument("--carve-root", type=pathlib.Path,
                    default=REPO / "TestOutput" / "n64carve")
    ap.add_argument("--no-boot-filter", action="store_true",
                    help="skip the boot.bin membership narrowing")
    ap.add_argument("--check", action="store_true", help="report only, do not write")
    ap.add_argument("--selftest", action="store_true")
    args = ap.parse_args()

    if args.selftest:
        return selftest()

    if not args.builds.is_dir():
        print(f"sample builds not found: {args.builds}", file=sys.stderr)
        return 2

    print(f"Scanning PS1 .psx corpus under {args.builds} ...")
    table, scanned, parsed = harvest_ps1(args.builds)
    print(f"  {scanned} .psx files scanned, {parsed} with a usable hash set")
    print(f"  {len(table)} distinct content keys")

    ambiguous = {k: v for k, v in table.items() if len(v) > 1}
    print(f"  {len(ambiguous)} ambiguous keys covering "
          f"{sum(len(v) for v in ambiguous.values())} files")

    if not args.no_boot_filter:
        pool = boot_name_pool(args.carve_root)
        if pool:
            narrowed = apply_boot_filter(table, pool)
            still = sum(1 for v in table.values() if len(v) > 1)
            print(f"  boot.bin membership pool: {len(pool)} names; "
                  f"narrowed {narrowed} keys, {still} still ambiguous")
        else:
            print("  (no boot.bin pool available; skipping the membership filter)")

    coverage_report(table, args.carve_root)

    if args.check:
        print("\n--check: not writing")
        return 0

    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    write_table(table, OUTPUT)
    print(f"\nwrote {OUTPUT.relative_to(REPO)} ({len(table)} keys)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
