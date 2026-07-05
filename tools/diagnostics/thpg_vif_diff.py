#!/usr/bin/env python3
"""Diff the VIF command streams of two THAW-family .skin.ps2 files (P8 vs THPG oracle).

Walks VIF opcodes in both, decoding STCYCL(CL,WL) and UNPACK(vn,vl,num,addr,flg,usn),
and prints them side-by-side so an encoding divergence (stride, format, count) is obvious.

Usage: python tools/diagnostics/thpg_vif_diff.py <fileA.skin.ps2> <fileB.skin.ps2> [max_codes]
"""
import struct, sys, os

UNPACK_FMT = {  # (vn<<2)|vl -> name
    0x0: "S_32", 0x1: "S_16", 0x2: "S_8",
    0x4: "V2_32", 0x5: "V2_16", 0x6: "V2_8",
    0x8: "V3_32", 0x9: "V3_16", 0xA: "V3_8",
    0xC: "V4_32", 0xD: "V4_16", 0xE: "V4_8", 0xF: "V4_5",
}


def vif_next(data, off, end):
    if off >= end or off + 4 > len(data):
        return end
    cmd = data[off + 3]
    if (cmd & 0x60) != 0x60:
        c = cmd & 0x7F
        if c in (0, 1, 2, 3, 4, 5, 6, 7, 0x10, 0x11, 0x13, 0x14, 0x15, 0x17):
            return off + 4
        if c == 0x20:
            return off + 8
        if c in (0x30, 0x31):
            return off + 20
        if c == 0x4A:
            return off + (data[off + 2] << 3) + 4
        if c in (0x50, 0x51):
            return off + (struct.unpack_from('<H', data, off)[0] << 4) + 4
        return end
    vn = (cmd >> 2) & 3
    vl = cmd & 3
    num = data[off + 2] or 256
    return off + 4 + ((((32 >> vl) * (vn + 1) * num + 31) >> 5) << 2)


def decode(data, off):
    cmd = data[off + 3]
    imm = struct.unpack_from('<H', data, off)[0]
    num = data[off + 2]
    if (cmd & 0x60) == 0x60:
        vn = (cmd >> 2) & 3
        vl = cmd & 3
        fmt = UNPACK_FMT.get((vn << 2) | vl, f"?{vn}{vl}")
        addr = imm & 0x3FF
        flg = (imm >> 15) & 1
        usn = (imm >> 14) & 1
        return f"UNPACK {fmt:5s} num={num:3d} addr={addr:4d} flg={flg} usn={usn}"
    c = cmd & 0x7F
    if c == 0x01:
        return f"STCYCL CL={data[off]:2d} WL={data[off+1]:2d}"
    if c == 0x10:
        return "FLUSHE"
    if c == 0x11:
        return "FLUSH"
    if c == 0x14:
        return "MSCAL"
    if c == 0x17:
        return "MSCNT"
    if c == 0x20:
        return f"STMASK 0x{struct.unpack_from('<I', data, off+4)[0]:08x}"
    if c in (0x30, 0x31):
        return "STROW/STCOL"
    if c in (0x50, 0x51):
        return f"DIRECT qwc={imm}"
    if c == 0x00:
        return "NOP"
    return f"cmd=0x{c:02x} imm={imm}"


def find_vif_start(data, entry_end, vif_end):
    """First FLUSH(0x10/0x11)+DIRECT(0x50/0x51) marker, matching the decoder's
    ThawPs2SkinVifLayout.FindRawSetupBoundaryFlushOffsets — start VIF just after it."""
    off = entry_end
    while off + 8 <= vif_end and off + 8 <= len(data):
        word = struct.unpack_from('<I', data, off)[0]
        if word in (0x10000000, 0x11000000) and (data[off + 7] & 0x7F) in (0x50, 0x51):
            return off + 4
        off += 4
    return entry_end


def walk(path):
    data = open(path, 'rb').read()
    nObj, tm1, tm2, dSize = struct.unpack_from('<4I', data, 0)
    entry_end = 32 + nObj * 8 + tm2 * 64
    vif_end = min(dSize + 16, len(data))
    vif_start = find_vif_start(data, entry_end, vif_end)
    codes = []
    off = vif_start
    while off + 4 <= vif_end:
        codes.append((off, decode(data, off)))
        nxt = vif_next(data, off, vif_end)
        if nxt <= off:
            codes.append((off, f"[STOP nxt<=off cmd=0x{data[off+3]&0x7f:02x}]"))
            break
        off = nxt
    return codes, vif_start, vif_end


def main(a, b, maxc=400):
    ca, ea, va = walk(a)
    cb, eb, vb = walk(b)
    print(f"A={os.path.basename(a)}  entry_end=0x{ea:x} vif_end=0x{va:x}  codes={len(ca)}")
    print(f"B={os.path.basename(b)}  entry_end=0x{eb:x} vif_end=0x{vb:x}  codes={len(cb)}")
    print(f"\n{'#':>4} {'offA':>7} {'A':38s} {'offB':>7} {'B':38s} {'DIFF'}")
    first_diff = None
    for i in range(min(len(ca), len(cb), maxc)):
        oa, sa = ca[i]
        ob, sb = cb[i]
        d = "" if sa == sb else "  <<<"
        if d and first_diff is None:
            first_diff = i
        print(f"{i:>4} {oa:#7x} {sa:38s} {ob:#7x} {sb:38s}{d}")
    print(f"\nfirst structural diff at code index: {first_diff}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]) if len(sys.argv) > 3 else 400)
