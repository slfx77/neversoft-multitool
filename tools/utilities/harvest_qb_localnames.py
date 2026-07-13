# harvest_qb_localnames.py — recover proven QbKey names from the inline CHECKSUM_NAME
# (token 0x2B) registrations embedded throughout every game's QB scripts.
#
# The C# QbFile parser already reads these pairs, but only uses them FILE-LOCALLY
# (QbFile.LocalNames): a checksum named in one script does not resolve in another.
# The retail PS2 THPS3/THPS4/THUG/THUG2 scripts are full of them (unlike THAW retail,
# which was stripped — hence THAW's separate dbg.pak). Aggregating them globally is a
# huge, ZERO-false-positive win: every pair is kept only when it re-hashes to its stored
# checksum (QbKey lower- or case-CRC), so it is as proven as the disc-name harvest.
#
# These ARE CRC(name) pairs, so they belong with the other QbKey dictionaries. Written
# to src/NeversoftMultitool/Core/QbKey/QbKeyNames.QbLocalNames.txt (auto-loaded by the
# StartsWith("QbKeyNames") glob). Net-new only: hashes already covered by another
# QbKeyNames*.txt are skipped, so the file stays a pure addition.
#
# Usage: python tools/utilities/harvest_qb_localnames.py   (from the repo root)

import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from qb_localname_census import BUILDS, build_local_names, qb, qb_lower  # noqa: E402
from filesystem_script_probe import QBKEY_DIR  # noqa: E402

OUT_PATH = QBKEY_DIR / "QbKeyNames.QbLocalNames.txt"


def existing_hashes() -> set[int]:
    """All hashes already covered by other QbKeyNames*.txt (excluding our own output)."""
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


def loadable(name: str) -> bool:
    """The loader splits on the first '=' then parses hex; reject names that break it."""
    return bool(name) and "=" not in name and all(c >= " " and c != "\x7f" for c in name)


def main() -> None:
    have = existing_hashes()
    proven: dict[int, str] = {}
    for build_dir in sorted(BUILDS.iterdir()):
        if not build_dir.is_dir():
            continue
        for checksum, name in build_local_names(build_dir).items():
            if checksum in have or checksum in proven or not loadable(name):
                continue
            if qb_lower(name) == checksum or qb(name) == checksum:
                proven[checksum] = name

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in proven.items()), key=str.lower)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"wrote {len(lines):,} proven net-new names to {OUT_PATH.relative_to(REPO)}")


if __name__ == "__main__":
    main()
