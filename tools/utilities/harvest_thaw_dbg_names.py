# harvest_thaw_dbg_names.py — extract QbKey checksum->name pairs from the debug
# name archives THAW ships (DATAP/pak/dbg.pak.* and data/pak/dbg.pak.wpc). Each
# pak entry is a plain-text .dbg file with a "[Checksums]" section of
# "0xHASH name" lines (Queen-Bee's QB debug format). Writes the merged, sorted
# dictionary to src/NeversoftMultitool/Core/QbKey/QbKeyNames.ThawDbg.txt in the
# loader's "name=0xHASH" format, skipping hashes already covered by the other
# QbKeyNames*.txt resources.
#
# Usage: python tools/utilities/harvest_thaw_dbg_names.py   (from the repo root)

import re
import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from extract_qb_corpus import walk_entries  # noqa: E402  (header-relative pak walker)

# Each group harvests into its own embedded resource, skipping hashes already
# covered by the resources listed before it (first-wins mirrors the loader).
HARVEST_GROUPS = [
    ("QbKeyNames.ThawDbg.txt", [
        "Sample/Builds/Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)/DATAP/pak/dbg.pak.ps2",
        "Sample/Builds/Tony Hawk's American Wasteland (2006-2-6, PC - Final)/Installed/program files/"
        "Aspyr Media, Inc/THAW/Game/data/pak/dbg.pak.wpc",
    ]),
    # THPG ships its own debug archives (P8 does not, but shares many assets).
    ("QbKeyNames.ThpgDbg.txt", [
        "Sample/Builds/Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)/DATAP/pak/dbg.pak.ps2",
        "Sample/Builds/Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)/DATAP/pak/dbgq.pak.ps2",
    ]),
]

QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"
EXISTING = [
    QBKEY_DIR / "QbKeyNames.txt",
    QBKEY_DIR / "QbKeyNames.ThawGcTextures.txt",
]

CHECKSUM_LINE = re.compile(r"^0x([0-9A-Fa-f]{1,8}) (.+)$")


def harvest(pak_path: Path) -> dict[int, str]:
    pak = pak_path.read_bytes()
    pab_path = Path(str(pak_path).replace(".pak.", ".pab."))
    pab = pab_path.read_bytes() if pab_path.exists() else b""
    big = pak_path.suffix == ".ngc"
    names: dict[int, str] = {}
    for hpos, off, size, flags, _thash, _name in walk_entries(pak, big):
        in_companion = big and not (flags & 0x80000000)
        if in_companion:
            src, pos = pab, off
        else:
            resolved = hpos + off
            src, pos = (pak, resolved) if resolved + size <= len(pak) else (pab, resolved - len(pak))
        if pos < 0 or pos + size > len(src):
            continue
        text = src[pos:pos + size].decode("latin1", errors="replace")
        section = text.find("[Checksums]")
        if section < 0:
            continue
        for line in text[section:].splitlines()[1:]:
            line = line.strip()
            if not line:
                continue
            if line.startswith("["):
                break
            m = CHECKSUM_LINE.match(line)
            if not m:
                continue
            name = m.group(2).strip()
            if not name or "=" in name or any(ord(c) < 0x20 for c in name):
                continue
            names[int(m.group(1), 16)] = name
    return names


def main() -> None:
    existing_hashes: set[int] = set()
    for path in EXISTING:
        if not path.exists():
            continue
        for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    existing_hashes.add(int(line[eq + 3:], 16))
                except ValueError:
                    pass

    for out_name, sources in HARVEST_GROUPS:
        merged: dict[int, str] = {}
        for source in sources:
            path = REPO / source
            if not path.exists():
                print(f"skip (missing): {source}")
                continue
            pairs = harvest(path)
            added = 0
            for crc, name in pairs.items():
                if crc not in merged:
                    merged[crc] = name
                    added += 1
            print(f"{path.name}: {len(pairs)} pairs ({added} new)")

        fresh = {crc: name for crc, name in merged.items() if crc not in existing_hashes}
        lines = [f"{name}=0x{crc:08X}" for crc, name in fresh.items()]
        lines.sort(key=str.lower)
        out_path = QBKEY_DIR / out_name
        out_path.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {len(lines)} entries to {out_path.relative_to(REPO)} "
              f"({len(merged) - len(fresh)} already covered by earlier dictionaries)")
        existing_hashes.update(fresh)


if __name__ == "__main__":
    main()
