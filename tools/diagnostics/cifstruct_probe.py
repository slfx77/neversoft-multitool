# cifstruct_probe.py — decode THUG2 CIF2 ("cifstruct", ext key 0x508AE2F2 = QbKey of
# that name, proven by the QbLocalNames harvest) cutscene payloads as the CStruct
# WriteToBuffer stream from THUG source Gel/Scripting/utils.cpp, and sanity-scan the
# whole corpus:
#   * field-name checksums resolved against the QbKeyNames dictionaries
#   * compressed-name masks (bit7/bit6 on the type byte) — the compression lookup
#     tables are game data, so their presence would block a standalone parser
#   * cut TOC nameQbKey resolution rate (was 0/426 before the 2026-07-12 harvests)
#
# Read-only. Usage: python tools/diagnostics/cifstruct_probe.py

import struct
import sys
from collections import Counter
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"
QBKEY_DIR = REPO / "src/NeversoftMultitool/Core/QbKey"

CIF2_KEY = 0x508AE2F2

# ESymbolType (THUG Gel/Scripting/symboltype.h; values confirmed against payload bytes)
T_NONE, T_INT, T_FLOAT, T_STRING, T_LOCALSTRING, T_PAIR, T_VECTOR = 0, 1, 2, 3, 4, 5, 6
T_STRUCT, T_ARRAY, T_NAME = 10, 12, 13
T_INT8, T_INT16, T_UINT8, T_UINT16, T_ZEROINT, T_ZEROFLOAT = 14, 15, 16, 17, 18, 19
MASK8, MASK16 = 0x80, 0x40


def load_names() -> dict[int, str]:
    names: dict[int, str] = {}
    for txt in sorted(QBKEY_DIR.glob("QbKeyNames*.txt")):
        for line in txt.read_text(encoding="utf-8", errors="replace").splitlines():
            eq = line.rfind("=0x")
            if eq > 0:
                try:
                    names.setdefault(int(line[eq + 3:], 16), line[:eq])
                except ValueError:
                    pass
    return names


NAMES = load_names()


def nm(crc: int) -> str:
    return NAMES.get(crc, f"#{crc:08X}")


class MaskedName(Exception):
    pass


def parse_struct(data: bytes, pos: int) -> tuple[list, int]:
    comps = []
    while True:
        t = data[pos]
        if t == T_NONE:
            return comps, pos + 1
        if t & (MASK8 | MASK16):
            raise MaskedName(f"compressed name at {pos:#x} (type byte {t:#04x})")
        name = struct.unpack_from("<I", data, pos + 1)[0]
        pos += 5
        if t in (T_INT, T_FLOAT, T_NAME):
            fmt = {T_INT: "<i", T_FLOAT: "<f", T_NAME: "<I"}[t]
            val = struct.unpack_from(fmt, data, pos)[0]
            pos += 4
        elif t in (T_STRING, T_LOCALSTRING):
            end = data.index(b"\x00", pos)
            val = data[pos:end].decode("latin1")
            pos = end + 1
        elif t == T_PAIR:
            val = struct.unpack_from("<2f", data, pos); pos += 8
        elif t == T_VECTOR:
            val = struct.unpack_from("<3f", data, pos); pos += 12
        elif t == T_STRUCT:
            val, pos = parse_struct(data, pos)
        elif t == T_ARRAY:
            val, pos = parse_array(data, pos)
        elif t in (T_INT8, T_UINT8):
            val = data[pos] if t == T_UINT8 else struct.unpack_from("<b", data, pos)[0]
            pos += 1
        elif t in (T_INT16, T_UINT16):
            fmt = "<h" if t == T_INT16 else "<H"
            val = struct.unpack_from(fmt, data, pos)[0]; pos += 2
        elif t == T_ZEROINT:
            val = 0
        elif t == T_ZEROFLOAT:
            val = 0.0
        else:
            raise ValueError(f"unknown component type {t} at {pos - 5:#x}")
        comps.append((name, t, val))


def parse_array(data: bytes, pos: int) -> tuple[list, int]:
    etype = data[pos]
    count = struct.unpack_from("<H", data, pos + 1)[0]
    pos += 3
    vals = []
    for _ in range(count):
        if etype in (T_INT, T_NAME):
            vals.append(struct.unpack_from("<I" if etype == T_NAME else "<i", data, pos)[0]); pos += 4
        elif etype == T_FLOAT:
            vals.append(struct.unpack_from("<f", data, pos)[0]); pos += 4
        elif etype in (T_STRING, T_LOCALSTRING):
            end = data.index(b"\x00", pos); vals.append(data[pos:end].decode("latin1")); pos = end + 1
        elif etype == T_PAIR:
            vals.append(struct.unpack_from("<2f", data, pos)); pos += 8
        elif etype == T_VECTOR:
            vals.append(struct.unpack_from("<3f", data, pos)); pos += 12
        elif etype == T_STRUCT:
            v, pos = parse_struct(data, pos); vals.append(v)
        elif etype == T_ARRAY:
            v, pos = parse_array(data, pos); vals.append(v)
        elif etype == T_NONE:
            pass
        else:
            raise ValueError(f"unknown array element type {etype} at {pos:#x}")
    return vals, pos


def iter_cut_entries(path: Path):
    d = path.read_bytes()
    ver, n = struct.unpack_from("<Ii", d, 0)
    for i in range(n):
        off, size, name, ext = struct.unpack_from("<IiII", d, 8 + 16 * i)
        yield name, ext, d[off:off + size]


def fmt_component(name: int, t: int, val, indent: str = "  ") -> str:
    if t == T_NAME:
        return f"{indent}{nm(name)} = {nm(val)}"
    return f"{indent}{nm(name)} = {val!r}"


def main() -> None:
    files = sorted(BUILDS.rglob("*.cut")) + sorted(BUILDS.rglob("*.cut.ps2")) + sorted(BUILDS.rglob("*.cut.xbx"))
    parsed = failed = masked = 0
    toc_total = toc_named = 0
    field_names = Counter()
    first_dump = True

    for path in files:
        for name_key, ext_key, payload in iter_cut_entries(path):
            if name_key:
                toc_total += 1
                if name_key in NAMES:
                    toc_named += 1
            if ext_key != CIF2_KEY:
                continue
            try:
                comps, end = parse_struct(payload, 0)
                assert end == len(payload), f"trailing bytes: {len(payload) - end}"
                parsed += 1
                def walk(cs):
                    for n, t, v in cs:
                        field_names[nm(n)] += 1
                        if t == T_STRUCT:
                            walk(v)
                        elif t == T_ARRAY and v and isinstance(v[0], list):
                            for e in v:
                                walk(e)
                walk(comps)
                if first_dump:
                    first_dump = False
                    print(f"=== decoded {path.name} cifstruct ===")
                    for n, t, v in comps:
                        if t == T_ARRAY and v and isinstance(v[0], list):
                            print(f"  {nm(n)} = array[{len(v)}] of struct, first 3:")
                            for e in v[:3]:
                                print("    {" + ", ".join(fmt_component(*c, indent="") for c in e) + "}")
                        else:
                            print(fmt_component(n, t, v))
            except MaskedName as e:
                masked += 1
                print(f"MASKED {path.name}: {e}")
            except Exception as e:
                failed += 1
                print(f"FAIL {path.name}: {e}")

    print(f"\ncifstruct payloads: parsed {parsed}, failed {failed}, masked-name {masked}")
    print(f"cut TOC named entries resolving now: {toc_named}/{toc_total}")
    print("\nfield-name usage across all payloads:")
    for fname, cnt in field_names.most_common(20):
        print(f"  {cnt:6,}  {fname}")


if __name__ == "__main__":
    main()
