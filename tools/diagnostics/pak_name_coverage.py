# pak_name_coverage.py — measure how many PAK entry names resolve across the
# GH-era builds (THAW PS2/PC/GC, P8, THPG). Entries either carry a filename
# string (0xC0 entries) or a name QbKey (LE: short-name CRC at +0x14 with the
# full-path key at +0x10 as fallback; GC: +0x0C) that must resolve through the
# QbKeyNames*.txt dictionaries. Reports per build: total entries, named-in-file,
# key-resolved, unresolved — and shows the effect of each dictionary tier so
# harvest passes (e.g. the THPG dbg wordlist) can be evaluated.
#
# Usage: python tools/diagnostics/pak_name_coverage.py   (from the repo root)

import struct
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from extract_qb_corpus import walk_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"

TARGETS = [
    ("THAW PS2", "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "*.pak.ps2", False),
    ("THAW GC ", "Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.apk.ngc", True),
    ("THAW GC2", "Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.pak.ngc", True),
    ("THAW PC ", "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "*.pak.wpc", False),
    ("P8   PS2", "Tony Hawk's Project 8 (2006-9-21, PS2 - Final)", "*.pak.ps2", False),
    ("THPG PS2", "Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)", "*.pak.ps2", False),
]


def load_dict(path: Path) -> set[int]:
    hashes: set[int] = set()
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        eq = line.rfind("=0x")
        if eq > 0:
            try:
                hashes.add(int(line[eq + 3:], 16))
            except ValueError:
                pass
    return hashes


def main() -> None:
    tiers: list[tuple[str, set[int]]] = []
    for txt in sorted(QBKEY_DIR.glob("QbKeyNames*.txt")):
        tiers.append((txt.stem.replace("QbKeyNames.", "").replace("QbKeyNames", "main"), load_dict(txt)))
    all_hashes = set().union(*(h for _, h in tiers))

    print(f"dictionaries: {', '.join(f'{n} ({len(h):,})' for n, h in tiers)}")
    print(f"{'build':9s} {'entries':>8s} {'in-file':>8s} {'resolved':>9s} {'unresolved':>10s}   per-tier new resolves")

    grand = Counter()
    for label, build, pattern, big in TARGETS:
        build_dir = BUILDS / build
        if not build_dir.exists():
            continue
        stats = Counter()
        tier_hits = Counter()
        unresolved_keys = Counter()
        for pak_path in build_dir.rglob(pattern):
            data = pak_path.read_bytes()
            fmt = ">I" if big else "<I"
            for hpos, off, size, flags, thash, name in walk_entries(data, big):
                stats["entries"] += 1
                if name:
                    stats["in_file"] += 1
                    continue
                if big:
                    key = struct.unpack_from(fmt, data, hpos + 0x0C)[0]
                else:
                    key = struct.unpack_from(fmt, data, hpos + 0x14)[0] \
                        or struct.unpack_from(fmt, data, hpos + 0x10)[0]
                if key == 0:
                    stats["no_key"] += 1
                elif key in all_hashes:
                    stats["resolved"] += 1
                    for tier_name, hashes in tiers:
                        if key in hashes:
                            tier_hits[tier_name] += 1
                            break
                else:
                    stats["unresolved"] += 1
                    unresolved_keys[key] += 1
        unres = stats["unresolved"] + stats["no_key"]
        print(f"{label:9s} {stats['entries']:8,d} {stats['in_file']:8,d} {stats['resolved']:9,d} "
              f"{unres:10,d}   {dict(tier_hits.most_common(4))}")
        grand.update(stats)

    total = grand["entries"]
    named = grand["in_file"] + grand["resolved"]
    print(f"\nTOTAL: {total:,} entries — {named:,} named ({named / total * 100:.1f}%), "
          f"{grand['no_key']:,} keyless (offset-named), "
          f"{grand['unresolved']:,} with unresolved keys")


if __name__ == "__main__":
    main()
