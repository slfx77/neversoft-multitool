# harvest_disc_names.py — recover unresolved pak entry keys by hashing the ACTUAL
# on-disk filenames across every build. Distinct from the other harvests, which hash
# names stored INSIDE the archives: this walks the real directory tree (every file's
# bare name, name-minus-extension, and build-relative path — case-sensitive AND
# lowercased) and intersects with the unresolved GC/LE key set. It works because the
# GC pak key is QbKey(lowercased full path minus last extension) and the extracted
# PC/PS2 build trees hold the same assets at their real relative paths — the on-disk
# path IS the key's canonical form. Every hit is hash-proven, so no false positives.
#
# Writes src/NeversoftMultitool/Core/QbKey/QbKeyNames.DiscNames.txt (skipping hashes
# already covered by other QbKeyNames*.txt), in the loader's "name=0xHASH" format.
#
# Usage: python tools/utilities/harvest_disc_names.py   (from the repo root)

import os
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from extract_qb_corpus import walk_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"
OUT_PATH = QBKEY_DIR / "QbKeyNames.DiscNames.txt"

# GH-era builds whose paks carry hash-named entries (both endiannesses).
PAK_TARGETS = [
    ("Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.apk.ngc", True),
    ("Tony Hawk's American Wasteland (2005-8-22, GC - Final)", "*.pak.ngc", True),
    ("Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "*.pak.ps2", False),
    ("Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "*.pak.wpc", False),
    ("Tony Hawk's Project 8 (2006-9-21, PS2 - Final)", "*.pak.ps2", False),
    ("Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)", "*.pak.ps2", False),
]


def qb(s: str) -> int:
    return (zlib.crc32(s.encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def qb_lower(s: str) -> int:
    return (zlib.crc32(s.lower().encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def existing_hashes() -> set[int]:
    have: set[int] = set()
    for txt in QBKEY_DIR.glob("QbKeyNames*.txt"):
        if txt.name == OUT_PATH.name:
            continue
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
    for build, pattern, big in PAK_TARGETS:
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


def candidate_names() -> set[str]:
    """Deduplicated name forms for every file on disk: bare name, name-minus-last-ext,
    build-relative path, and relative-path-minus-ext (backslash-joined)."""
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

    cands = candidate_names()
    print(f"unique on-disk filename candidates: {len(cands):,}")

    proven: dict[int, str] = {}
    for cand in cands:
        for h in (qb(cand), qb_lower(cand)):
            if h in unresolved and h not in proven:
                proven[h] = cand

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in proven.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")

    by_ext = Counter()
    for name in proven.values():
        base = name.rsplit("\\", 1)[-1]
        by_ext[base[base.rfind("."):] if "." in base else "(none)"] += 1
    print(f"wrote {len(lines):,} proven names to {OUT_PATH.relative_to(REPO)}")
    print("by extension:", dict(by_ext.most_common(10)))


if __name__ == "__main__":
    main()
