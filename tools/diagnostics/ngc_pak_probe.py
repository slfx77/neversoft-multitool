#!/usr/bin/env python3
"""Probe THAW GameCube big-endian PAK archives (.apk.ngc / .pak.ngc).

Same structure as Neversoft .pak.ps2 (PakArchive.cs) with all fields
big-endian: variable-size entry table terminated by QbKey("last") =
0xB524565F. Entries are 32 bytes (compact) or 192 bytes (flag 0x20 adds a
160-byte filename field):
  +0x00 u32 extension QbKey   +0x04 u32 offset   +0x08 u32 size
  +0x0C u32 name QbKey        +0x10 u32 full-path QbKey
  +0x14 u32 name-only CRC     +0x1C u32 flags

Usage:
  python ngc_pak_probe.py <file.apk.ngc> [-v]
  python ngc_pak_probe.py --batch <dir>
"""

import argparse
import struct
from collections import Counter
from pathlib import Path

SENTINEL = 0xB524565F
KNOWN = {
    0x745DCD45: ".ska", 0x5D796624: ".sqb", 0xA7F505C4: ".qb",
    0x9BCC234D: ".mdl", 0x64112E85: ".skin", 0x8BFA5E8E: ".tex",
    0xDAD5E950: ".img", 0x72A6D78C: ".col", 0x365318B2: ".scripts",
    0x559566CC: ".dbg", 0x7330095C: ".ske", 0x1F3E0235: ".anm",
    0x9B22CA94: ".cam", 0x98F2AA1D: ".ped", 0x2C3B5ADC: ".scn",
    0x6C217288: ".pak", 0x2B0A3095: ".stex", 0x2F1A6A09: ".shd",
}


def u32(b, o):
    return struct.unpack_from(">I", b, o)[0]


def parse(path, verbose=False):
    b = Path(path).read_bytes()
    entries = []
    o = 0
    while o + 32 <= len(b):
        ftype = u32(b, o)
        if ftype == SENTINEL:
            break
        offset = u32(b, o + 4)
        size = u32(b, o + 8)
        name_key = u32(b, o + 0x0C)
        flags = u32(b, o + 0x1C)
        has_name = bool(flags & 0x20)
        name = None
        if has_name:
            raw = b[o + 32 : o + 192]
            name = raw.split(b"\0")[0].decode("ascii", "replace")
        entries.append(
            {
                "type": ftype,
                "ext": KNOWN.get(ftype, f".{ftype:08X}"),
                "offset": offset,
                "size": size,
                "name_key": name_key,
                "flags": flags,
                "name": name,
            }
        )
        o += 192 if has_name else 32
        if flags & ~0x80000033:
            raise ValueError(f"unexpected flags {flags:08X} at 0x{o:X}")
    else:
        raise ValueError("no sentinel found")

    for e in entries:
        if e["offset"] + e["size"] > len(b):
            raise ValueError(f"entry overruns file: {e}")

    if verbose:
        for e in entries:
            print(
                f"  {e['ext']:12s} off=0x{e['offset']:06X} size={e['size']:7d} "
                f"key={e['name_key']:08X} flags={e['flags']:02X} {e['name'] or ''}"
            )
    return entries


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path", nargs="?")
    ap.add_argument("--batch")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    if args.batch:
        files = sorted(Path(args.batch).rglob("*.apk.ngc")) + sorted(
            Path(args.batch).rglob("*.pak.ngc")
        )
        ok = fail = 0
        ext_counter = Counter()
        flag_counter = Counter()
        total_entries = 0
        fails = []
        for f in files:
            try:
                entries = parse(f)
                ok += 1
                total_entries += len(entries)
                for e in entries:
                    ext_counter[e["ext"]] += 1
                    flag_counter[e["flags"]] += 1
            except Exception as ex:
                fail += 1
                fails.append((f, str(ex)))
        print(f"parsed {ok + fail}: ok={ok} fail={fail}, entries={total_entries}")
        print("payload extensions:")
        for ext, n in ext_counter.most_common(20):
            print(f"  {n:6d}  {ext}")
        print(f"flags: {dict(flag_counter)}")
        for f, e in fails[:10]:
            print(f"FAIL {f}: {e}")
    else:
        entries = parse(args.path, args.verbose)
        print(f"{args.path}: {len(entries)} entries")


if __name__ == "__main__":
    main()
