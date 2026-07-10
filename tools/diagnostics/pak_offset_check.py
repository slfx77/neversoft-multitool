# pak_offset_check.py — validate Neversoft PAK entry-offset semantics.
#
# Queen-Bee (PakEditor.cs) resolves entry data at HeaderStart + FileOffset (offsets are
# relative to each entry's own header record), with companion .pab positions =
# resolved - pak_file_length. Our PakArchive historically treated offsets as absolute.
# This script scores both hypotheses per archive family:
#   in-bounds:   [resolved, resolved+size) fits in pak (or pak+pab virtual space)
#   non-overlap: resolved data ranges don't collide
#   sig hits:    payload signature checks (sectioned QB header, .ska 0x28, .img records)
#
# Usage:
#   python pak_offset_check.py <build-dir> [pattern]     # e.g. "*.pak.ps2" "*.apk.ngc"
#
# 2026-07-09: THAW PS2 qb.pak.ps2 scored 266/266 header-relative vs 2/266 absolute.

import struct
import sys
from pathlib import Path

SENT = 0xB524565F
QB_SIG = bytes.fromhex("1c0802041004080c0c080204140204")


def parse_entries(pak: bytes, big: bool):
    """Walk every sentinel-terminated table; yield (header_pos, offset, size, flags)."""
    fmt = ">I" if big else "<I"

    def u32(off):
        return struct.unpack_from(fmt, pak, off)[0]

    # find sentinels
    sentinels = [i for i in range(0, len(pak) - 31, 4) if u32(i) == SENT]
    for s in sentinels:
        # walk back
        pos = s
        while pos > 0:
            stepped = False
            for esz, has_name in ((0xC0, True), (0x20, False)):
                c = pos - esz
                if c < 0:
                    continue
                t = u32(c)
                f = u32(c + 0x1C)
                if t in (0, SENT):
                    continue
                if f & ~0x80000033:
                    continue
                if bool(f & 0x20) != has_name:
                    continue
                if u32(c + 0x08) == 0:
                    continue
                pos = c
                stepped = True
                break
            if not stepped:
                break
        # parse forward
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
            yield cur, off, size, f, t
            cur += 0xC0 if (f & 0x20) else 0x20


def sig_check(d: bytes, type_hash: int) -> bool | None:
    """Payload signature test; None = no checker for this type."""
    if len(d) < 16:
        return None
    if d[8:8 + 15] == QB_SIG:
        return True
    # .ska (LE or BE 0x28 first dword) — THAW cam anims
    first_le = struct.unpack_from("<I", d, 0)[0]
    first_be = struct.unpack_from(">I", d, 0)[0]
    if type_hash in (0x745DCD45,):  # QbKey(".ska")
        return first_le == 0x28 or first_be == 0x28
    return None


def score(root: Path, pattern: str):
    files = sorted(root.rglob(pattern))
    tot = {"abs": [0, 0, 0], "rel": [0, 0, 0]}  # [inbounds, sig_ok, sig_bad]
    n_paks = 0
    worse = []
    for f in files:
        pak = f.read_bytes()
        big = pattern.endswith("ngc")
        pab_path = None
        name = f.name
        for a, b in ((".pak.", ".pab."), (".apk.", ".mpk.")):
            if a in name:
                cand = f.with_name(name.replace(a, b))
                if cand.exists() and cand.stat().st_size > 32:
                    pab_path = cand
        pab = pab_path.read_bytes() if pab_path else b""
        virt_len = len(pak) + len(pab)

        entries = list(parse_entries(pak, big))
        if not entries:
            continue
        n_paks += 1

        def data_at(resolved, size, pab_absolute):
            if resolved + size <= len(pak):
                return pak[resolved:resolved + size]
            if pab_absolute:
                # companion read at raw offset (current GC behavior)
                if resolved + size <= len(pab):
                    return pab[resolved:resolved + size]
                return None
            p = resolved - len(pak)
            if 0 <= p and p + size <= len(pab):
                return pab[p:p + size]
            return None

        for hpos, off, size, flags, thash in entries:
            in_companion = big and not (flags & 0x80000000)
            for label, resolved in (("abs", off), ("rel", hpos + off)):
                if in_companion:
                    d = data_at(off if label == "abs" else resolved, size, pab_absolute=(label == "abs"))
                else:
                    d = data_at(resolved, size, pab_absolute=False)
                if d is None:
                    continue
                tot[label][0] += 1
                sc = sig_check(d, thash)
                if sc is True:
                    tot[label][1] += 1
                elif sc is False:
                    tot[label][2] += 1

    print(f"{pattern}: {n_paks} archives")
    for label in ("abs", "rel"):
        ib, ok, bad = tot[label]
        print(f"  {label}: in-bounds={ib} sig-ok={ok} sig-bad={bad}")


if __name__ == "__main__":
    root = Path(sys.argv[1])
    pattern = sys.argv[2] if len(sys.argv) > 2 else "*.pak.ps2"
    score(root, pattern)
