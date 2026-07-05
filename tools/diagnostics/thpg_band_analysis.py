#!/usr/bin/env python3
"""Analyze THPG Q4.12 position bands against the P8 oracle (same character file).

THPG .skin.ps2 stores positions as sint16 Q4.12 (pos*4096), which wraps mod 65536
because meshes exceed the +/-8 unit range. P8 stores the same character's positions
as Q12.4 (pos*16, no wrap). This tool computes, per vertex, the integer "band"
    band = round((P8_pos_units - THPG_fine_units) / 16)
that THPG's VU program must reconstruct, then tests what predicts it:
  - bone index (c2[14:8] of the byte-identical V4_16 uv/adc array)
  - strip output address (c2[7:0] / c2[9:0])
  - batch-local continuity (UNPACK order)

Usage: python tools/diagnostics/thpg_band_analysis.py <p8.skin.ps2> <thpg.skin.ps2>
"""
import struct
import sys
from collections import defaultdict


def enumerate_batches(data):
    """Find interleaved vertex batches: STCYCL(CL=3,WL=1) + UNPACK V3_16/V3_8/V4_16.

    Returns list of (n, pos_off, nrm_off, uv_off) byte offsets of payload starts.
    Scans byte-by-byte so gap chunks between meshes don't desync the walk.
    """
    batches = []
    i = 0
    end = len(data) - 8
    while i < end:
        # STCYCL CL=3 WL=1: bytes [03 01 xx 01]
        if data[i] == 3 and data[i + 1] == 1 and data[i + 3] == 0x01:
            j = i + 4
            # UNPACK V3_16 (cmd 0x69, allow mask bit 0x10)
            if j + 4 <= len(data) and (data[j + 3] & 0x6F) == 0x69:
                n = data[j + 2] or 256
                pos_off = j + 4
                pos_size = ((6 * n + 3) >> 2) << 2
                k = pos_off + pos_size
                if k + 4 <= len(data) and (data[k + 3] & 0x6F) == 0x6A and (data[k + 2] or 256) == n:
                    nrm_off = k + 4
                    nrm_size = ((3 * n + 3) >> 2) << 2
                    m = nrm_off + nrm_size
                    if m + 4 <= len(data) and (data[m + 3] & 0x6F) == 0x6D and (data[m + 2] or 256) == n:
                        uv_off = m + 4
                        batches.append((n, pos_off, nrm_off, uv_off))
                        i = uv_off + 8 * n
                        continue
        i += 4
    return batches


def s16(data, off):
    return struct.unpack_from('<h', data, off)[0]


def u16(data, off):
    return struct.unpack_from('<H', data, off)[0]


def main(p8_path, tg_path):
    p8 = open(p8_path, 'rb').read()
    tg = open(tg_path, 'rb').read()
    b_p8 = enumerate_batches(p8)
    b_tg = enumerate_batches(tg)
    print(f"P8 batches: {len(b_p8)}, THPG batches: {len(b_tg)}")
    if [(n, o1) for n, o1, _, _ in b_p8] != [(n, o1) for n, o1, _, _ in b_tg]:
        print("WARNING: batch layout differs between files!")
    total = 0
    bad_resid = 0
    band_by_bone = defaultdict(set)   # bone -> set of (coord, band)
    band_hist = defaultdict(int)
    max_resid = 0.0
    print(f"\n{'batch':>5} {'n':>3}  bands(y) by unpack order / bone / outaddr")
    for bi, ((n, po, no, uo), (_, tpo, _, tuo)) in enumerate(zip(b_p8, b_tg)):
        rows = []
        for i in range(n):
            p8_pos = [s16(p8, po + i * 6 + c * 2) / 16.0 for c in range(3)]
            tg_fine = [s16(tg, tpo + i * 6 + c * 2) / 4096.0 for c in range(3)]
            c2 = u16(p8, uo + i * 8 + 4)
            c3 = u16(p8, uo + i * 8 + 6)
            bone = (c2 >> 8) & 0x7F
            outaddr = c2 & 0xFF
            bands = []
            for c in range(3):
                bf = (p8_pos[c] - tg_fine[c]) / 16.0
                b = round(bf)
                resid = abs(bf - b) * 16.0  # units
                if resid > 0.04:  # > P8 quantization (1/16 = .0625 half=.031) + eps
                    bad_resid += 1
                nonlocal_max = resid
                bands.append(b)
                band_hist[b] += 1
                band_by_bone[bone].add((c, b))
                globals()['_mr'] = max(globals().get('_mr', 0.0), resid)
            rows.append((i, bands, bone, outaddr, (c2 >> 15) & 1))
            total += 1
        if bi < 6 or bi in (20, 40):
            ys = [r[1][1] for r in rows]
            bones = sorted({r[2] for r in rows})
            print(f"{bi:>5} {n:>3}  y-bands={sorted(set(ys))} bones={bones}")
            # show band transitions along unpack order
            trans = ''.join(str(b % 10) for b in ys)
            print(f"       unpack-order y-band digits: {trans}")
    print(f"\ntotal verts: {total}, residuals > 0.04 units: {bad_resid}, max resid: {globals().get('_mr', 0):.4f}")
    print(f"band histogram (all coords): {dict(sorted(band_hist.items()))}")
    # bone -> band consistency: does one bone always imply one band per coord?
    multi = {bone: sorted(v) for bone, v in band_by_bone.items()
             if len({b for c, b in v if c == 1}) > 1}
    print(f"\nbones with MULTIPLE y-bands (bone-relative hypothesis fails if many):")
    for bone, v in sorted(multi.items()):
        ybs = sorted({b for c, b in v if c == 1})
        print(f"  bone {bone}: y-bands {ybs}")
    if not multi:
        print("  none — every bone maps to exactly one y-band (bone-anchored encoding!)")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
