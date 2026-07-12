# filesystem_script_probe.py — test on-disk filenames against the checksums that QB
# SCRIPTS REFERENCE (Name token 0x16), the second half of the disc-name experiment
# (the first half, pak ENTRY keys, shipped as harvest_disc_names.py).
#
# Rigor notes — a naive version of this probe is dominated by chance collisions:
#  * Scanning every 0x16 byte instead of walking the token stream inflates the target
#    set ~3x with garbage u32s, and CRC-32 chance hits scale linearly with target size.
#    This version implements the real tokenizer (sizes from THUG skiptoken.cpp via
#    QbFile.cs) and collects Name checksums only from correctly-aligned streams.
#  * Matching every build's filenames against every build's scripts multiplies
#    trials×targets ~10x for no reason: real references are within-build. This version
#    matches per build and reports the expected chance-hit count per class
#    (trials × targets / 2^32) next to the observed count.
#  * Only the lowercased CRC is used (THPS3+ scripts hash identifiers lowercased).
#
# Candidate classes (reported separately — ship only classes whose observed count
# far exceeds expectation):
#   path  — extension-bearing engine-anchored path suffix (models\..., anims\...)
#   name  — bare filename with extension (paintcan.tex)
#   stem  — bare filename without extension (paintcan) — short/generic, noisiest
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

QB_GLOBS = ("*.qb", "*.qb.ps2", "*.qb.wpc", "*.qb.ngc", "*.qb.xbx", "*.sqb", "*.sqb.ps2",
            "*.sqb.wpc", "*.sqb.ngc")

# Directory names the engine uses as path roots in scripts/asset systems.
ENGINE_ROOTS = frozenset([
    "models", "anims", "images", "textures", "levels", "sounds", "scripts",
    "cutscenes", "gameobjects", "skater", "sfx", "music", "fonts", "icons", "tex",
])

# Token sizes per THUG Gel/Scripting/skiptoken.cpp (mirrors QbFile.cs ParseTokens).
ONE_BYTE = frozenset([0, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18,
                      19, 20, 21, 29, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 44,
                      45, 48, 49, 50, 51, 52, 53, 54, 56, 57, 58, 59, 60, 61, 62, 63, 66])
FIVE_BYTE = frozenset([2, 22, 23, 24, 25, 26, 46, 67, 68])
TOKEN_NAME = 22
TOKEN_STRING, TOKEN_LOCALSTRING = 27, 28
TOKEN_VECTOR, TOKEN_PAIR = 30, 31
TOKEN_CHECKSUM_NAME = 43
RANDOM_TOKENS = frozenset([47, 55, 64, 65])


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


def collect_name_checksums(blob: bytes, out: set[int]) -> None:
    """Walk the classic token stream, collecting Name-token checksums."""
    pos = 0
    n = len(blob)
    while pos < n:
        t = blob[pos]
        if t in ONE_BYTE:
            pos += 1
        elif t in FIVE_BYTE:
            if pos + 5 > n:
                return
            if t == TOKEN_NAME:
                out.add(struct.unpack_from("<I", blob, pos + 1)[0])
            pos += 5
        elif t == TOKEN_VECTOR:
            pos += 13
        elif t == TOKEN_PAIR:
            pos += 9
        elif t in (TOKEN_STRING, TOKEN_LOCALSTRING):
            if pos + 5 > n:
                return
            length = struct.unpack_from("<I", blob, pos + 1)[0]
            if length <= 0 or length > 100000 or pos + 5 + length > n:
                pos += 1  # corrupted — resync, like QbFile.cs
                continue
            pos += 5 + length
        elif t == TOKEN_CHECKSUM_NAME:
            if pos + 5 > n:
                return
            end = blob.find(b"\x00", pos + 5)
            if end < 0 or end - (pos + 5) > 512:
                pos += 1
                continue
            pos = end + 1
        elif t in RANDOM_TOKENS:
            if pos + 5 > n:
                return
            count = struct.unpack_from("<I", blob, pos + 1)[0]
            if count <= 0 or count > 10000:
                pos += 1
                continue
            if pos + 5 + 6 * count > n:
                return
            pos += 5 + 6 * count
        else:
            pos += 1


def build_script_refs(build_dir: Path) -> set[int]:
    refs: set[int] = set()
    for pattern in QB_GLOBS:
        for path in build_dir.rglob(pattern):
            data = path.read_bytes()
            if len(data) >= 4 and struct.unpack_from("<I", data)[0] == 0:
                # THAW sectioned QB: token streams live in LZSS script blobs (LE even
                # on GC); the section tree itself is not a token stream.
                pos = 0
                while True:
                    pos = data.find(b"\x01\x00\x00\x00", pos)
                    if pos < 0 or pos + 12 > len(data):
                        break
                    decomp = int.from_bytes(data[pos + 4:pos + 8], "little")
                    comp = int.from_bytes(data[pos + 8:pos + 12], "little")
                    if 0 < comp < decomp < 0x100000 and pos + 12 + comp <= len(data):
                        try:
                            collect_name_checksums(
                                lzss_decompress(data[pos + 12:pos + 12 + comp])[:decomp], refs)
                        except Exception:
                            pass
                    pos += 4
            else:
                collect_name_checksums(data, refs)
    return refs


def build_candidates(build_dir: Path) -> dict[str, str]:
    """candidate string -> class (path | name | stem), deduplicated per build."""
    cands: dict[str, str] = {}
    base = str(build_dir)
    for root, _dirs, files in os.walk(base):
        rel_dir = root[len(base):].lstrip("\\/").replace("/", "\\")
        segments = rel_dir.lower().split("\\") if rel_dir else []
        anchors = [i for i, seg in enumerate(segments) if seg in ENGINE_ROOTS]
        for name in files:
            dot = name.rfind(".")
            if dot > 0:
                cands.setdefault(name, "name")
                cands.setdefault(name[:dot], "stem")
            for i in anchors:
                suffix = "\\".join(rel_dir.split("\\")[i:]) + "\\" + name
                cands.setdefault(suffix, "path")
                sdot = suffix.rfind(".")
                if sdot > suffix.rfind("\\"):
                    cands.setdefault(suffix[:sdot], "path")
    return cands


def main() -> None:
    have = existing_hashes()
    grand: dict[str, tuple[str, str]] = {}  # checksum -> (name, class)
    total_expected = Counter()
    total_hits = Counter()

    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir():
            continue
        refs = build_script_refs(build_dir)
        targets = refs - have
        if not targets:
            continue
        cands = build_candidates(build_dir)

        hits = Counter()
        for cand, cls in cands.items():
            h = qb_lower(cand)
            if h in targets:
                hits[cls] += 1
                if h not in grand:
                    grand[h] = (cand, cls)
        trials = Counter(cands.values())
        print(f"{build_dir.name}")
        print(f"  script Name refs: {len(refs):,} (unnamed: {len(targets):,})")
        for cls in ("path", "name", "stem"):
            expected = trials[cls] * len(targets) / 2 ** 32
            total_expected[cls] += expected
            total_hits[cls] += hits[cls]
            print(f"  {cls:5s}: trials {trials[cls]:7,}  expected-chance {expected:7.2f}  observed {hits[cls]:5,}")

    print("\n=== TOTALS (unique across builds) ===")
    by_class = Counter(cls for _n, cls in grand.values())
    for cls in ("path", "name", "stem"):
        print(f"  {cls:5s}: observed {by_class[cls]:5,}  expected-chance {total_expected[cls]:7.2f}")
    print("\nsample path/name hits:")
    shown = 0
    for h, (n, cls) in grand.items():
        if cls in ("path", "name"):
            print(f"  [{cls}] 0x{h:08X} = {n}")
            shown += 1
            if shown >= 20:
                break


if __name__ == "__main__":
    main()
