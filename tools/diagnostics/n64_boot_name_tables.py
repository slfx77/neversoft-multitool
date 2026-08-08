#!/usr/bin/env python3
"""Recover carved-asset NAMES for the N64 ports from boot.bin's filename tables.

Background
----------
`N64AssetCarver` names model bundles and triggers by numeric slot
(`models/007/geometry_007.psx.n64`, `triggers/003.trg.n64`) because nothing in the
carved payload spells a name. That is a property of the CARVER, not of the ROM:
each port's boot image ships the original PS1 filename tables, packed and ordered.

This probe locates those tables and tests the one thing that makes them usable:
that TABLE ORDER EQUALS CARVE SLOT ORDER. The test is external and unfitted -
compare each N64 trigger's node count against the PS1 sibling `<name>_t.trg` the
table claims it is. THPS2 scores 12/14 exact (slot 008 skny differs by a single
authored node, 1014 vs 1015; slot 013 skfactory has no PS1 counterpart shipped).

Table inventory as measured (2026-08-07):

    ROM         trigger table   carved trg   psx table   carved models
    THPS1       (one MIXED table of 89 entries)   9              80
    THPS2       14              14           133         141
    THPS3       13              13           103         112
    Spider-Man  56 (+3 extensionless)  59     258         261

THPS1 - the earliest port - stores ONE combined table; the later three separate
triggers from models. Some entries are stored WITHOUT the extension (Spider-Man's
`dem1_t`), which is why a strict `\\.trg$` filter undercounts.

Usage:
    python tools/diagnostics/n64_boot_name_tables.py [--carve-root DIR] [--builds DIR]
                                                     [--json OUT.json]
"""

from __future__ import annotations

import argparse
import json
import re
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

ASCII_RUN = re.compile(rb"[\x20-\x7E]{3,}")
ASSET_RE = re.compile(r"\.(psx|trg)$", re.I)
# Extensionless table entries look like a bare stem; Spider-Man stores several
# trigger names that way (`dem1_t`).
STEM_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_]{2,15}$")

# A new table starts when the packed run breaks. Entries sit on a 12/16-byte
# stride, so anything past 40 bytes is a different structure.
TABLE_GAP = 40


def is_trigger_name(name: str) -> bool:
    """A trigger table entry, with or without its extension.

    Neversoft's trigger companion is always `<levelstem>_t`, so the bare stem is
    unambiguous even when the extension is absent.
    """
    low = name.lower()
    return low.endswith(".trg") or low.endswith("_t")


def ps1_trigger_file_name(entry: str) -> str:
    """The PS1 sibling file an entry names."""
    return entry if entry.lower().endswith(".trg") else entry + ".trg"


def trg_node_count(path: Path) -> int | None:
    """Node count from a TRG header, either byte order (magic decides)."""
    data = path.read_bytes()
    if len(data) < 12:
        return None
    magic = data[:4]
    if magic == b"_TRG":
        return struct.unpack_from("<I", data, 8)[0]
    if magic == b"GRT_":
        return struct.unpack_from(">I", data, 8)[0]
    return None


def find_tables(boot: bytes) -> list[dict]:
    """Contiguous runs of asset-name strings, in file order."""
    hits = []
    for m in ASCII_RUN.finditer(boot):
        s = m.group().decode("ascii")
        if ASSET_RE.search(s) and len(s) > 5:
            hits.append((m.start(), s))
    if not hits:
        return []

    tables, cur = [], [hits[0]]
    for prev, nxt in zip(hits, hits[1:]):
        if nxt[0] - prev[0] > TABLE_GAP:
            tables.append(cur)
            cur = [nxt]
        else:
            cur.append(nxt)
    tables.append(cur)

    out = []
    for t in tables:
        if len(t) < 2:  # format strings like "%s.psx" are not tables
            continue
        # Re-scan the table's own byte span for EVERY string, not just the
        # extension-bearing ones: some entries ship without the extension
        # (Spider-Man's `dem1_t`), and dropping them shifts every later index.
        lo, hi = t[0][0], t[-1][0] + len(t[-1][1]) + 1
        names = []
        for m in ASCII_RUN.finditer(boot[lo:hi]):
            s = m.group().decode("ascii")
            if ASSET_RE.search(s) or STEM_RE.fullmatch(s):
                names.append(s)
        trg = sum(1 for n in names if is_trigger_name(n))
        psx = sum(1 for n in names if n.lower().endswith(".psx"))
        kind = "trg" if trg == len(names) else "psx" if psx == len(names) else "mixed"
        out.append({
            "offset": t[0][0],
            "count": len(names),
            "kind": kind,
            "names": names,
        })
    return out


def carved_counts(carve: Path) -> tuple[list[Path], list[Path]]:
    triggers = sorted((carve / "triggers").glob("*.trg.n64")) if (carve / "triggers").exists() else []
    models = sorted(p for p in (carve / "models").iterdir() if p.is_dir()) if (carve / "models").exists() else []
    return triggers, models


def ps1_trigger_index(builds: Path) -> dict[str, int]:
    """PS1 `<name>.trg` -> node count, across every sample build."""
    index: dict[str, int] = {}
    if not builds.exists():
        return index
    for path in builds.rglob("*.trg"):
        try:
            n = trg_node_count(path)
        except OSError:
            continue
        if n is not None:
            index.setdefault(path.name.lower(), n)
    return index


def validate_trigger_order(triggers: list[Path], names: list[str], ps1: dict[str, int]) -> dict:
    """The unfitted external check: does slot i really carry names[i]?"""
    rows, exact, compared = [], 0, 0
    for i, path in enumerate(triggers):
        n64 = trg_node_count(path)
        name = names[i] if i < len(names) else None
        ref = ps1.get(ps1_trigger_file_name(name).lower()) if name else None
        status = "no-ps1-sibling"
        if ref is not None:
            compared += 1
            if ref == n64:
                exact += 1
                status = "exact"
            else:
                status = f"differs ({n64} vs {ref})"
        rows.append({"slot": path.name.split(".")[0], "n64Nodes": n64, "name": name,
                     "ps1Nodes": ref, "status": status})
    return {"rows": rows, "exact": exact, "compared": compared}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--carve-root", type=Path, default=REPO / "TestOutput" / "n64carve")
    ap.add_argument("--builds", type=Path, default=REPO / "Sample" / "Builds")
    ap.add_argument("--json", type=Path, help="write the slot->name mapping here")
    args = ap.parse_args()

    if not args.carve_root.exists():
        print(f"carve root not found: {args.carve_root}", file=sys.stderr)
        return 2

    print("Indexing PS1 trigger node counts for the order check...")
    ps1 = ps1_trigger_index(args.builds)
    print(f"  {len(ps1)} PS1 .trg files indexed\n")

    report = {}
    for carve in sorted(p for p in args.carve_root.iterdir() if p.is_dir()):
        boot_path = carve / "boot.bin"
        if not boot_path.exists():
            continue
        tables = find_tables(boot_path.read_bytes())
        triggers, models = carved_counts(carve)

        print("=" * 78)
        print(f"{carve.name}   carved: {len(triggers)} triggers, {len(models)} models")
        print("-" * 78)
        for t in tables:
            print(f"  table @0x{t['offset']:06X}  n={t['count']:<4} kind={t['kind']:<6} "
                  f"{t['names'][0]!r} .. {t['names'][-1]!r}")

        trg_table = next((t for t in tables if t["kind"] == "trg"), None)
        mixed = next((t for t in tables if t["kind"] == "mixed"), None)
        source = trg_table or mixed
        entry = {"tables": [{k: v for k, v in t.items() if k != "names"} for t in tables],
                 "carvedTriggers": len(triggers), "carvedModels": len(models)}

        if source and triggers:
            names = [n for n in source["names"] if is_trigger_name(n)]
            check = validate_trigger_order(triggers, names, ps1)
            print(f"\n  trigger order check: {check['exact']}/{check['compared']} exact "
                  f"node-count matches against PS1 siblings")
            for r in check["rows"]:
                print(f"    {r['slot']:<5} {str(r['n64Nodes']):<7} {str(r['name']):<20} {r['status']}")
            entry["triggerOrderCheck"] = check
            entry["triggerNames"] = names
        else:
            print("\n  (no separable trigger table found)")

        psx_table = next((t for t in tables if t["kind"] == "psx"), None)
        if psx_table:
            entry["modelNames"] = psx_table["names"]
        elif mixed:
            entry["modelNames"] = [n for n in mixed["names"] if n.lower().endswith(".psx")]

        report[carve.name] = entry
        print()

    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps(report, indent=2), encoding="utf-8")
        print(f"wrote {args.json}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
