# harvest_thaw_texture_names.py — build the THAW texture side map: compiled-texture
# checksum -> original source-art name, mined from the debug.log that ships inside
# every QTex .tex.zip bundle (a texturetool build transcript). These checksums are the
# per-texture keys in the matching .tex.wpc/.tex.ngc/.img headers, so the map lets THAW
# texture export name its output after the real art (cat_bg_new.png) instead of a hex id.
#
# NOT QbKeys: the checksums are opaque build-tool ids (verified: 0x5a11d8f1 is not
# QbKey of any source-name form). They therefore go in a SEPARATE resource
# (Core/Formats/Texture/ThawTextureNames.txt), never in QbKeyNames*.txt — mixing them
# in would poison every proven-name harvest's existing_hashes() and the coverage metric.
#
# Writes src/NeversoftMultitool/Core/Formats/Texture/ThawTextureNames.txt (name=0xHASH,
# sorted). Usage: python tools/utilities/harvest_thaw_texture_names.py   (from repo root)

import re
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "tools" / "diagnostics"))
from qtex_debuglog_probe import iter_local_entries  # noqa: E402

BUILDS = REPO / "Sample" / "Builds"
OUT_PATH = REPO / "src/NeversoftMultitool/Core/Formats/Texture/ThawTextureNames.txt"

# 0x<hex>: texturetool "<source path>" <flags>
LINE_RE = re.compile(rb'^\s*0x([0-9a-fA-F]+)\s*:\s*texturetool\s+"([^"]+)"', re.MULTILINE)


def source_stem(path: str) -> str:
    """Last path component of the authoring path, minus its extension."""
    base = re.split(r"[\\/]", path)[-1]
    dot = base.rfind(".")
    return base[:dot] if dot > 0 else base


def main() -> None:
    names: dict[int, str] = {}
    conflicts = 0
    zips = 0
    for pattern in ("*.tex.zip.wpc", "*.tex.zip.ngc"):
        for zpath in BUILDS.rglob(pattern):
            data = zpath.read_bytes()
            for name, _method, _cs, _us, _crc, payload in iter_local_entries(data):
                if not name.endswith("debug.log"):
                    continue
                zips += 1
                for m in LINE_RE.finditer(payload):
                    checksum = int(m.group(1), 16)
                    stem = source_stem(m.group(2).decode("latin1"))
                    if not stem:
                        continue
                    prev = names.get(checksum)
                    if prev is None:
                        names[checksum] = stem
                    elif prev.lower() != stem.lower():
                        conflicts += 1  # same id, different art — keep first, note it

    lines = sorted((f"{name}=0x{crc:08X}" for crc, name in names.items()), key=str.lower)
    OUT_PATH.parent.mkdir(parents=True, exist_ok=True)
    OUT_PATH.write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")
    print(f"scanned {zips:,} debug.logs")
    print(f"wrote {len(lines):,} checksum->name pairs to {OUT_PATH.relative_to(REPO)} "
          f"({conflicts} checksum conflicts, first kept)")


if __name__ == "__main__":
    main()
