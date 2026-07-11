# filesystem_script_probe.py — the second half of the on-disk-filename experiment:
# instead of testing filename hashes against PAK ENTRY keys (filesystem_name_probe.py
# / harvest_disc_names.py), test them against the checksums the QB SCRIPTS REFERENCE.
# A script references an identifier with the Name token (byte 0x16 + u32 checksum,
# THUG tokens.h). Collecting those and intersecting with on-disk filename hashes
# recovers filenames that appear as identifiers in game code — a broader target than
# pak entries, and the payoff builds are THUG/THUG2/THPS3/Spider-Man, which ship no
# debug-name archives. The intersection is hash-proven, so false 0x16 bytes in data
# contribute random checksums that simply never match a real filename hash.
#
# Read-only probe. Usage: python tools/diagnostics/filesystem_script_probe.py

import os
import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from thaw_qb_probe import lzss_decompress  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"

NAME_TOKEN = 0x16  # QbTokenType.Name — 1 byte + u32 checksum
QB_GLOBS = ("*.qb", "*.qb.ps2", "*.qb.wpc", "*.qb.ngc", "*.qb.xbx")


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


def scan_name_tokens(blob: bytes, out: set[int]) -> None:
    """Collect u32 after every Name-token byte. Scripts are little-endian even on GC."""
    i = 0
    n = len(blob) - 5
    while i < n:
        if blob[i] == NAME_TOKEN:
            out.add(struct.unpack_from("<I", blob, i + 1)[0])
        i += 1


def collect_script_checksums() -> set[int]:
    refs: set[int] = set()
    qb_files: list[Path] = []
    for build_dir in BUILDS.iterdir():
        if not build_dir.is_dir():
            continue
        for pattern in QB_GLOBS:
            qb_files.extend(build_dir.rglob(pattern))
    for path in qb_files:
        data = path.read_bytes()
        scan_name_tokens(data, refs)  # classic streams are raw
        # THAW sectioned QB embeds LZSS script blobs; inflate opportunistically.
        pos = 0
        while True:
            pos = data.find(b"\x01\x00\x00\x00", pos)
            if pos < 0 or pos + 12 > len(data):
                break
            decomp = int.from_bytes(data[pos + 4:pos + 8], "little")
            comp = int.from_bytes(data[pos + 8:pos + 12], "little")
            if 0 < comp < decomp < 0x100000 and pos + 12 + comp <= len(data):
                try:
                    scan_name_tokens(lzss_decompress(data[pos + 12:pos + 12 + comp])[:decomp], refs)
                except Exception:
                    pass
            pos += 4
    print(f"scanned {len(qb_files):,} QB files -> {len(refs):,} referenced checksums")
    return refs


def candidate_names() -> set[str]:
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
    refs = collect_script_checksums()
    unnamed_refs = refs - have
    print(f"referenced checksums NOT already named: {len(unnamed_refs):,}")

    cands = candidate_names()
    print(f"unique on-disk filename candidates: {len(cands):,}")

    proven: dict[int, str] = {}
    for cand in cands:
        for h in (qb(cand), qb_lower(cand)):
            if h in unnamed_refs and h not in proven:
                proven[h] = cand

    with_ext = {h: n for h, n in proven.items() if "." in n.rsplit("\\", 1)[-1]}
    print(f"NEW names recovered via script references: {len(proven):,} "
          f"({len(with_ext):,} have a file extension)")
    by_ext = Counter()
    for name in proven.values():
        base = name.rsplit("\\", 1)[-1]
        by_ext[base[base.rfind("."):] if "." in base else "(none)"] += 1
    print("by extension:", dict(by_ext.most_common(12)))
    for _h, n in list(with_ext.items())[:15]:
        print(f"  {n}")


if __name__ == "__main__":
    main()
