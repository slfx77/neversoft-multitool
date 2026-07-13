# qtex_debuglog_probe.py — inspect the debug.log inside a THAW QTex .tex.zip bundle
# and show how the texturetool build lines map compiled-texture checksums to the
# original source-art names. Walks the clean LOCAL headers (the central directory is
# deliberately malformed — same reason QZipArchive exists).
#
# Usage: python tools/diagnostics/qtex_debuglog_probe.py [path-to.tex.zip.wpc]
#        (no arg → picks the first .tex.zip.wpc it finds under Sample/Builds)

import struct
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"


def iter_local_entries(data: bytes):
    """Yield (name, method, csize, usize, crc, payload) from local headers."""
    o = 0
    while o + 4 <= len(data) and data[o:o + 4] == b"PK\x03\x04":
        method, = struct.unpack_from("<H", data, o + 8)
        crc, csize, usize = struct.unpack_from("<III", data, o + 14)
        nlen, elen = struct.unpack_from("<HH", data, o + 26)
        name = data[o + 30:o + 30 + nlen].decode("latin1")
        body = o + 30 + nlen + elen
        payload = data[body:body + csize]
        yield name, method, csize, usize, crc, payload
        o = body + csize


def main() -> None:
    if len(sys.argv) > 1:
        path = Path(sys.argv[1])
    else:
        path = next(BUILDS.rglob("*.tex.zip.wpc"), None) or next(BUILDS.rglob("*.tex.zip.ngc"), None)
    if not path or not path.exists():
        print("no QTex zip found"); return

    print(f"=== {path.name} ===")
    for name, method, csize, usize, crc, payload in iter_local_entries(path.read_bytes()):
        print(f"  {name:40s} method={method} csize={csize} usize={usize} crc={crc:08X}")
        if name.endswith("debug.log"):
            print("  ----- debug.log -----")
            print("\n".join("    " + ln for ln in payload.decode("latin1").splitlines()))
            print("  ----- end debug.log -----")


if __name__ == "__main__":
    main()
