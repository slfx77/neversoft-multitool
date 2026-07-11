# harvest_cut_names.py — recover QbKey names from the THUG/THUG2 cutscene .cut
# libraries. The embedded QB script sub-file (extKey 0x2BBEA5C3) carries
# CHECKSUM_NAME tokens (byte 0x2B + u32 checksum + null-terminated name), and the
# bare-.cut plaintext placement section (extKey 0x2208E9E8) carries .q-style
# identifiers. Both are proven: a candidate is kept only when its Neversoft CRC-32
# (case-sensitive OR lowercased) equals the paired/stored checksum, so false
# positives are impossible.
#
# Writes src/NeversoftMultitool/Core/QbKey/QbKeyNames.CutScenes.txt with the newly
# proven pairs (skipping hashes already covered by other QbKeyNames*.txt), in the
# loader's "name=0xHASH" format.
#
# Usage: python tools/utilities/harvest_cut_names.py   (from the repo root)

import re
import struct
import zlib
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"
OUT_PATH = QBKEY_DIR / "QbKeyNames.CutScenes.txt"

CUT_BUILDS = [
    "Tony Hawk's Underground (2003-10-2, PS2 - Final)",
    "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)",
    "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)",
    "Tony Hawks Underground 2 (2004-10-4, Windows - Final)",
]
CUT_PATTERNS = ["*.cut", "*.cut.ps2", "*.cut.xbx"]

EXT_QB = 0x2BBEA5C3
EXT_TEXT = 0x2208E9E8
IDENTIFIER_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]{2,63}")


def crc(s: str) -> int:
    return (zlib.crc32(s.encode("latin1")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


def crc_lower(s: str) -> int:
    return (zlib.crc32(s.lower().encode("latin1")) ^ 0xFFFFFFFF) & 0xFFFFFFFF


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


def read_toc(data: bytes):
    if len(data) < 24:
        return None
    version, num_files = struct.unpack_from("<iI", data, 0)
    if version != 1 or not 0 < num_files < 5000:
        return None
    toc = []
    for i in range(num_files):
        off, size, name_key, ext_key = struct.unpack_from("<IiII", data, 8 + i * 16)
        toc.append((off, size, name_key, ext_key))
    return toc


def harvest_qb_checksum_names(blob: bytes, proven: dict[int, str]) -> None:
    """CHECKSUM_NAME token: 0x2B + u32 checksum + null-terminated name."""
    pos = 0
    while True:
        pos = blob.find(b"\x2b", pos)
        if pos < 0 or pos + 5 > len(blob):
            break
        checksum = struct.unpack_from("<I", blob, pos + 1)[0]
        end = blob.find(b"\x00", pos + 5)
        if 0 <= end - (pos + 5) <= 64:
            name = blob[pos + 5:end].decode("latin1", "replace")
            if name and all(0x20 <= ord(c) < 0x7F for c in name) \
                    and checksum in (crc(name), crc_lower(name)):
                proven.setdefault(checksum, name)
        pos += 1


def main() -> None:
    have = existing_hashes()
    proven: dict[int, str] = {}

    cuts: list[Path] = []
    for build in CUT_BUILDS:
        base = BUILDS / build
        if not base.exists():
            continue
        for pattern in CUT_PATTERNS:
            cuts.extend(base.rglob(pattern))
    cuts = sorted(set(cuts))

    # First pass: all TOC name keys across the corpus (validation targets for TEXT).
    toc_name_keys: set[int] = set()
    parsed = []
    for path in cuts:
        data = path.read_bytes()
        toc = read_toc(data)
        if toc is None:
            continue
        parsed.append((data, toc))
        toc_name_keys.update(nk for _o, _s, nk, _e in toc if nk)

    for data, toc in parsed:
        for off, size, _name_key, ext_key in toc:
            blob = data[off:off + size]
            if ext_key == EXT_QB:
                harvest_qb_checksum_names(blob, proven)
            elif ext_key == EXT_TEXT:
                text = blob.decode("latin1", "replace")
                for m in IDENTIFIER_RE.finditer(text):
                    name = m.group()
                    for h in (crc(name), crc_lower(name)):
                        if h in toc_name_keys:
                            proven.setdefault(h, name)

    fresh = {h: n for h, n in proven.items() if h not in have}
    lines = sorted((f"{name}=0x{crc_val:08X}" for crc_val, name in fresh.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"parsed {len(parsed)} cut files, {len(toc_name_keys):,} unique TOC name keys")
    print(f"proven {len(proven):,} pairs; wrote {len(lines):,} new to "
          f"{OUT_PATH.relative_to(REPO)} ({len(proven) - len(fresh)} already covered)")


if __name__ == "__main__":
    main()
