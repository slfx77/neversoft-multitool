# qb_localname_census.py — measure the untapped GLOBAL name source: the inline
# CHECKSUM_NAME (token 0x2B) pairs embedded throughout every game's QB scripts.
#
# The C# QbFile parser already collects these pairs, but only uses them FILE-LOCALLY
# (QbFile.LocalNames) — a checksum named in one script does not resolve in another.
# Aggregating them across the whole corpus would give a proven global dictionary
# (each pair is the game's own name->checksum registration; we re-hash to confirm and
# to detect misparses). This census reports, per build, how many net-new proven names
# such a harvest would yield beyond the dictionaries already shipped.
#
# Read-only. Usage: python tools/diagnostics/qb_localname_census.py

import struct
import sys
import zlib
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from filesystem_script_probe import (  # noqa: E402
    BUILDS, QBKEY_DIR, QB_GLOBS, ONE_BYTE, FIVE_BYTE, RANDOM_TOKENS,
    TOKEN_STRING, TOKEN_LOCALSTRING, TOKEN_VECTOR, TOKEN_PAIR, TOKEN_CHECKSUM_NAME,
    existing_hashes,
)
from thaw_qb_probe import lzss_decompress  # noqa: E402


def qb(s: str) -> int:
    return (zlib.crc32(s.encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def qb_lower(s: str) -> int:
    return (zlib.crc32(s.lower().encode("latin1", "replace")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def collect_checksum_names(blob: bytes, out: dict[int, str]) -> None:
    """Walk the classic token stream, capturing CHECKSUM_NAME (checksum -> name)."""
    pos, n = 0, len(blob)
    while pos < n:
        t = blob[pos]
        if t in ONE_BYTE:
            pos += 1
        elif t in FIVE_BYTE:
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
                pos += 1
                continue
            pos += 5 + length
        elif t == TOKEN_CHECKSUM_NAME:
            if pos + 5 > n:
                return
            checksum = struct.unpack_from("<I", blob, pos + 1)[0]
            end = blob.find(b"\x00", pos + 5)
            if end < 0 or end - (pos + 5) > 512:
                pos += 1
                continue
            name = blob[pos + 5:end].decode("latin1")
            if name:
                out.setdefault(checksum, name)
            pos = end + 1
        elif t in RANDOM_TOKENS:
            if pos + 5 > n:
                return
            count = struct.unpack_from("<I", blob, pos + 1)[0]
            if count <= 0 or count > 10000 or pos + 5 + 6 * count > n:
                pos += 1
                continue
            pos += 5 + 6 * count
        else:
            pos += 1


def build_local_names(build_dir: Path) -> dict[int, str]:
    pairs: dict[int, str] = {}
    for pattern in QB_GLOBS:
        for path in build_dir.rglob(pattern):
            data = path.read_bytes()
            if len(data) >= 4 and struct.unpack_from("<I", data)[0] == 0:
                pos = 0
                while True:
                    pos = data.find(b"\x01\x00\x00\x00", pos)
                    if pos < 0 or pos + 12 > len(data):
                        break
                    decomp = int.from_bytes(data[pos + 4:pos + 8], "little")
                    comp = int.from_bytes(data[pos + 8:pos + 12], "little")
                    if 0 < comp < decomp < 0x100000 and pos + 12 + comp <= len(data):
                        try:
                            collect_checksum_names(
                                lzss_decompress(data[pos + 12:pos + 12 + comp])[:decomp], pairs)
                        except Exception:
                            pass
                    pos += 4
            else:
                collect_checksum_names(data, pairs)
    return pairs


def main() -> None:
    have = existing_hashes()
    grand: dict[int, str] = {}
    valid_lower = valid_case = invalid = 0

    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir():
            continue
        pairs = build_local_names(build_dir)
        if not pairs:
            continue
        net_new = {c: n for c, n in pairs.items() if c not in have}
        # proven = the stored checksum equals the (lower or case-sensitive) CRC of the name
        proven = {c: n for c, n in net_new.items() if qb_lower(n) == c or qb(n) == c}
        print(f"{build_dir.name}")
        print(f"  CHECKSUM_NAME pairs: {len(pairs):,}  net-new: {len(net_new):,}  "
              f"proven-by-rehash: {len(proven):,}")
        for c, n in proven.items():
            grand.setdefault(c, n)

    # Global re-hash breakdown across everything we saw (including already-known).
    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir():
            continue
        for c, n in build_local_names(build_dir).items():
            if qb_lower(n) == c:
                valid_lower += 1
            elif qb(n) == c:
                valid_case += 1
            else:
                invalid += 1

    print("\n=== TOTALS ===")
    print(f"  net-new PROVEN checksum->name pairs (unique across builds): {len(grand):,}")
    print(f"  re-hash check (all pairs incl. known): lower-CRC {valid_lower:,}  "
          f"case-CRC {valid_case:,}  neither {invalid:,}")
    print("\nsample net-new proven names:")
    for i, (c, n) in enumerate(sorted(grand.items(), key=lambda kv: kv[1].lower())):
        if i >= 25:
            break
        print(f"  {n}=0x{c:08X}")


if __name__ == "__main__":
    main()
