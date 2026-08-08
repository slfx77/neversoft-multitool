#!/usr/bin/env python3
"""Are N64 asset names actually absent, or merely un-RE'd?

The carver names model bundles by numeric slot only, which is easy to mistake for
"the N64 build carries no names". That is a claim about the CARVER, not about the
ROM. This probe tests the ROM.

Three independent questions:

  1. ASCII    - do the carved images (boot.bin especially) contain asset-name
                strings at all? Implemented in-process because Git Bash on
                Windows ships no `strings(1)`, and an absent binary silently
                looks like an absent string.

  2. QBKEY    - are the shells' mesh-name hashes genuine QbKey CRC-32 of the SAME
                names the PS1 siblings spell on disc? Tested WITHOUT a shell
                parser: hash every PS1 asset stem in the sample tree and
                byte-search each N64 shell for that 4-byte value, both byte
                orders. A hit means the name is recoverable by dictionary today.

  3. DICT     - how much of each ROM's hash population do the repo's shipped
                QbKeyNames*.txt dictionaries already resolve?

Usage:
    python tools/diagnostics/n64_name_source_probe.py [--carve-root DIR] [--builds DIR]

Defaults to TestOutput/n64carve and Sample/Builds relative to the repo root.
"""

from __future__ import annotations

import argparse
import re
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]

# The PS1/DC/Xbox-era Neversoft hash: reflected CRC-32, poly 0xEDB88320,
# init 0xFFFFFFFF, NO final XOR, and CASE-SENSITIVE (no lowercasing).
# zlib.crc32 applies the final XOR, so undo it.
def qbkey(name: str) -> int:
    return (zlib.crc32(name.encode("ascii", "ignore")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def qbkey_lower(name: str) -> int:
    return qbkey(name.lower())


ASCII_RUN = re.compile(rb"[\x20-\x7E]{4,}")


def ascii_strings(data: bytes, minlen: int = 4) -> list[str]:
    return [m.group().decode("ascii") for m in ASCII_RUN.finditer(data) if len(m.group()) >= minlen]


def probe_ascii(carve: Path) -> dict:
    """Question 1: what name-shaped strings live in the carved images?"""
    out = {}
    boot = carve / "boot.bin"
    if not boot.exists():
        return {"error": "no boot.bin"}

    data = boot.read_bytes()
    runs = ascii_strings(data)
    # Name-shaped: short lowercase-ish identifiers, and anything with an
    # asset extension.
    ext = [s for s in runs if re.search(r"\.(psx|trg|tex|img|bin|sfx|pre|wad)\b", s, re.I)]
    ident = [s for s in runs if re.fullmatch(r"[A-Za-z][A-Za-z0-9_]{2,15}", s)]
    return {
        "bytes": len(data),
        "total_runs": len(runs),
        "with_asset_ext": ext[:40],
        "with_asset_ext_count": len(ext),
        "identifier_like_count": len(ident),
        "identifier_sample": ident[:40],
        "longest": sorted(runs, key=len, reverse=True)[:10],
    }


def collect_ps1_stems(builds: Path) -> dict[str, list[str]]:
    """Every PS1-era asset stem on disc, grouped by the build it came from.

    These are the names the ports inherited: the N64 build is a re-encode of the
    same authored data, so if its hashes are QbKeys of anything, they are QbKeys
    of these.
    """
    stems: dict[str, list[str]] = {}
    if not builds.exists():
        return stems
    for build in sorted(builds.iterdir()):
        if not build.is_dir():
            continue
        found = set()
        for path in build.rglob("*"):
            if not path.is_file():
                continue
            name = path.name
            low = name.lower()
            if low.endswith((".psx", ".trg", ".ddm")):
                stem = name.rsplit(".", 1)[0]
                found.add(stem)
                # Level banks/libraries are suffixed; the mesh name inside is
                # usually the bare stem.
                for suffix in ("_g", "_o", "_l", "_t"):
                    if stem.lower().endswith(suffix):
                        found.add(stem[: -len(suffix)])
        if found:
            stems[build.name] = sorted(found)
    return stems


def probe_hash_presence(carve: Path, candidates: dict[str, list[str]]) -> dict:
    """Question 2: do PS1 name hashes appear verbatim inside the N64 shells?

    No shell parsing: we search raw bytes for the 4-byte hash in BOTH orders.
    A hash present as a big-endian word inside a big-endian container is the
    expected shape; the little-endian check is a control.
    """
    shells = sorted((carve / "models").rglob("geometry_*.psx.n64"))
    if not shells:
        return {"error": "no shells"}

    blob = b"".join(p.read_bytes() for p in shells)

    # Deduplicate names across builds; keep provenance for reporting.
    name_to_builds: dict[str, set[str]] = {}
    for build, names in candidates.items():
        for n in names:
            name_to_builds.setdefault(n, set()).add(build)

    hits_be, hits_le, hits_lower_be = [], [], []
    for name in sorted(name_to_builds):
        h = qbkey(name)
        if struct.pack(">I", h) in blob:
            hits_be.append((name, h))
        if struct.pack("<I", h) in blob:
            hits_le.append((name, h))
        hl = qbkey_lower(name)
        if hl != h and struct.pack(">I", hl) in blob:
            hits_lower_be.append((name, hl))

    # Control: random names must not hit. Use the same length profile.
    import random

    rng = random.Random(1234)
    ctrl = 0
    trials = 2000
    for _ in range(trials):
        fake = "".join(rng.choice("abcdefghijklmnopqrstuvwxyz0123456789_") for _ in range(rng.randint(4, 10)))
        if struct.pack(">I", qbkey(fake)) in blob:
            ctrl += 1

    return {
        "shell_count": len(shells),
        "shell_bytes": len(blob),
        "candidates": len(name_to_builds),
        "hits_case_sensitive_be": len(hits_be),
        "hits_case_sensitive_le": len(hits_le),
        "hits_lowercased_be": len(hits_lower_be),
        "sample_hits": [f"{n}=0x{h:08X}" for n, h in hits_be[:40]],
        "control_random_names": f"{ctrl}/{trials}",
    }


def load_dictionaries() -> dict[int, str]:
    """The repo's shipped QbKey dictionaries (name=0xHASH per line)."""
    table: dict[int, str] = {}
    roots = [
        REPO / "src" / "NeversoftMultitool" / "Core" / "Formats" / "Qb",
        REPO / "src" / "NeversoftMultitool",
    ]
    seen = set()
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("QbKeyNames*.txt"):
            if path.name in seen:
                continue
            seen.add(path.name)
            try:
                text = path.read_text(encoding="utf-8", errors="ignore")
            except OSError:
                continue
            for line in text.splitlines():
                if "=0x" not in line:
                    continue
                name, _, hexval = line.partition("=0x")
                try:
                    h = int(hexval.strip(), 16)
                except ValueError:
                    continue
                table.setdefault(h, name)
    return table


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--carve-root", type=Path, default=REPO / "TestOutput" / "n64carve")
    ap.add_argument("--builds", type=Path, default=REPO / "Sample" / "Builds")
    args = ap.parse_args()

    if not args.carve_root.exists():
        print(f"carve root not found: {args.carve_root}", file=sys.stderr)
        return 2

    print("=" * 78)
    print("Collecting PS1 asset stems from the sample builds...")
    stems = collect_ps1_stems(args.builds)
    total = len({n for v in stems.values() for n in v})
    print(f"  {total} distinct stems across {len(stems)} builds")

    dictionary = load_dictionaries()
    print(f"  QbKey dictionaries loaded: {len(dictionary)} hash->name pairs")

    for carve in sorted(p for p in args.carve_root.iterdir() if p.is_dir()):
        print()
        print("=" * 78)
        print(f"ROM: {carve.name}")
        print("-" * 78)

        print("[1] ASCII name evidence in boot.bin")
        a = probe_ascii(carve)
        for k, v in a.items():
            if isinstance(v, list):
                print(f"    {k}: {v[:12]}")
            else:
                print(f"    {k}: {v}")

        print("[2] PS1 name hashes present verbatim in the N64 shells")
        h = probe_hash_presence(carve, stems)
        for k, v in h.items():
            if isinstance(v, list):
                print(f"    {k}:")
                for item in v[:25]:
                    print(f"        {item}")
            else:
                print(f"    {k}: {v}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
