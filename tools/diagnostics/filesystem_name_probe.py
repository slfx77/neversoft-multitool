# filesystem_name_probe.py — test whether hashing the ACTUAL on-disk filenames
# across every build recovers any unresolved pak entry keys. This is distinct from
# the existing harvests, which hash names stored INSIDE the archives (LE pak in-file
# names, QB strings, cut tokens, zip debug.log paths). Here the candidate pool is the
# real directory tree: every file's bare name (with/without last extension) and its
# build-relative path, hashed case-sensitive AND lowercased, then intersected with the
# unresolved GC/LE key set (excluding hashes already covered by the dictionaries).
#
# Read-only probe. Usage: python tools/diagnostics/filesystem_name_probe.py

import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(Path(__file__).resolve().parent))
from extract_qb_corpus import walk_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"

# GH-era builds whose paks carry hash-named entries.
PAK_TARGETS = [
    ("THAW GC", "Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.apk.ngc", True),
    ("THAW GC2", "Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.pak.ngc", True),
    ("THAW PS2", "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "*.pak.ps2", False),
    ("THAW PC", "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "*.pak.wpc", False),
    ("P8 PS2", "Tony Hawk's Project 8 (2006-9-21, PS2 - Final)", "*.pak.ps2", False),
    ("THPG PS2", "Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)", "*.pak.ps2", False),
]


def qb(s: str) -> int:
    return (zlib.crc32(s.encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def qb_lower(s: str) -> int:
    return (zlib.crc32(s.lower().encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def existing_hashes() -> set[int]:
    have: set[int] = set()
    for txt in QBKEY_DIR.glob("QbKeyNames*.txt"):
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    have.add(int(line[eq + 3:], 16))
                except ValueError:
                    pass
    return have


def collect_unresolved(have: set[int]) -> set[int]:
    unresolved: set[int] = set()
    for _label, build, pattern, big in PAK_TARGETS:
        base = BUILDS / build
        if not base.exists():
            continue
        for pak in base.rglob(pattern):
            data = pak.read_bytes()
            for hpos, _off, _size, _flags, _thash, name in walk_entries(data, big):
                if name:
                    continue
                if big:
                    key = struct.unpack_from(">I", data, hpos + 0x0C)[0]
                else:
                    key = struct.unpack_from("<I", data, hpos + 0x14)[0] \
                        or struct.unpack_from("<I", data, hpos + 0x10)[0]
                if key and key not in have:
                    unresolved.add(key)
    return unresolved


import os


def candidate_names() -> set[str]:
    """Every real file's name forms, deduplicated: bare (with/without last ext) +
    build-relative path (with/without last ext), backslash-joined, as the engine
    would spell them. Uses os.walk for speed over ~340k files."""
    cands: set[str] = set()
    for build_dir in BUILDS.iterdir():
        if not build_dir.is_dir():
            continue
        base = str(build_dir)
        for root, _dirs, files in os.walk(base):
            rel_dir = root[len(base):].lstrip("\\/").replace("/", "\\")
            for name in files:
                cands.add(name)
                dot = name.rfind(".")
                if dot > 0:
                    cands.add(name[:dot])
                rel = f"{rel_dir}\\{name}" if rel_dir else name
                cands.add(rel)
                rdot = rel.rfind(".")
                if rdot > rel.rfind("\\"):
                    cands.add(rel[:rdot])
    return cands


def main() -> None:
    have = existing_hashes()
    unresolved = collect_unresolved(have)
    print(f"unresolved pak keys (not in dictionaries): {len(unresolved):,}")

    proven: dict[int, str] = {}
    cands = candidate_names()
    for cand in cands:
        for h in (qb(cand), qb_lower(cand)):
            if h in unresolved and h not in proven:
                proven[h] = cand
    print(f"hashed {len(cands):,} unique filename candidates")
    print(f"NEW keys recovered by on-disk filenames: {len(proven):,}")

    by_ext = Counter()
    for name in proven.values():
        base = name.rsplit("\\", 1)[-1]
        by_ext[base[base.rfind("."):] if "." in base else "(none)"] += 1
    print("by extension:", dict(by_ext.most_common(12)))
    for h, n in list(proven.items())[:15]:
        print(f"  0x{h:08X} = {n}")


if __name__ == "__main__":
    main()
