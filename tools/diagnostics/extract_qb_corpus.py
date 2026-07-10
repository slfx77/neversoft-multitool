# extract_qb_corpus.py — pull every .qb/.sqb-typed entry out of THAW-family PAK
# archives (header-relative offsets, companion-aware) into a corpus directory for
# QB parser sweeps. Complements the shipped C# extractor for bulk re-validation
# without regenerating the whole Sample tree.
#
# Usage: python extract_qb_corpus.py <build-dir> <out-dir> <pattern> [pattern...]
#   e.g. python extract_qb_corpus.py "Sample/Builds/... (PS2 - Final)" TestOutput/qb_corpus/ps2 "*.pak.ps2"

import struct
import sys
from pathlib import Path

SENT = 0xB524565F
QB_TYPES = {0xA7F505C4: ".qb", 0x5D796624: ".sqb"}


def walk_entries(pak: bytes, big: bool):
    fmt = ">I" if big else "<I"

    def u32(off):
        return struct.unpack_from(fmt, pak, off)[0]

    sentinels = [i for i in range(0, len(pak) - 31, 4) if u32(i) == SENT]
    for s in sentinels:
        pos = s
        while pos > 0:
            stepped = False
            for esz, has_name in ((0xC0, True), (0x20, False)):
                c = pos - esz
                if c < 0:
                    continue
                t = u32(c)
                f = u32(c + 0x1C)
                if t in (0, SENT) or (f & ~0x80000033) or bool(f & 0x20) != has_name or u32(c + 0x08) == 0:
                    continue
                pos = c
                stepped = True
                break
            if not stepped:
                break
        cur = pos
        while cur < s and cur + 0x20 <= len(pak):
            t = u32(cur)
            if t == SENT:
                break
            f = u32(cur + 0x1C)
            if f & ~0x80000033:
                break
            off, size = u32(cur + 0x04), u32(cur + 0x08)
            if size == 0:
                break
            name = ""
            if f & 0x20:
                name = pak[cur + 0x20:cur + 0xC0].split(b"\0")[0].decode("latin1")
            yield cur, off, size, f, t, name
            cur += 0xC0 if (f & 0x20) else 0x20


def main():
    build = Path(sys.argv[1])
    out = Path(sys.argv[2])
    patterns = sys.argv[3:]
    ext_suffix = ""
    total = 0
    for pattern in patterns:
        big = pattern.endswith("ngc")
        plat = ".ngc" if big else (".wpc" if "wpc" in pattern else ".ps2")
        for pak_path in sorted(build.rglob(pattern)):
            pak = pak_path.read_bytes()
            companion = None
            name = pak_path.name
            for a, b in ((".pak.", ".pab."), (".apk.", ".mpk.")):
                if a in name:
                    cand = pak_path.with_name(name.replace(a, b))
                    if cand.exists() and cand.stat().st_size > 32:
                        companion = cand.read_bytes()
            for hpos, off, size, flags, thash, ename in walk_entries(pak, big):
                if thash not in QB_TYPES:
                    continue
                in_companion = big and not (flags & 0x80000000)
                if in_companion:
                    src, p = companion, off
                else:
                    resolved = hpos + off
                    if resolved + size <= len(pak):
                        src, p = pak, resolved
                    else:
                        src, p = companion, resolved - len(pak)
                if src is None or p < 0 or p + size > len(src):
                    continue
                blob = src[p:p + size]
                if ename:
                    rel = ename.replace("\\", "/")
                    if not rel.lower().endswith(plat):
                        rel += plat
                else:
                    rel = f"{pak_path.stem}/{p:08X}{QB_TYPES[thash]}{plat}"
                dest = out / pak_path.stem / rel
                dest.parent.mkdir(parents=True, exist_ok=True)
                dest.write_bytes(blob)
                total += 1
    print(f"extracted {total} qb/sqb payloads to {out}")


if __name__ == "__main__":
    main()
