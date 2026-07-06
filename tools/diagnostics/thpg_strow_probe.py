#!/usr/bin/env python3
"""Test the STROW hypothesis for THPG Q4.12 position banding.

THPG .skin.ps2 V3_16 positions wrap mod 65536 (Q4.12). The VIF stream contains
STROW/STMOD commands absent from P8's stream. Hypothesis: STMOD=1 (offset mode)
makes UNPACK add the STROW row registers per component, so

    true_int32 = sext16(raw) + STROW[c]     (c = 0,1,2 for X,Y,Z)
    position   = true_int32 / 4096

with the game guaranteeing every vertex in a batch lies within +/-8 units of the
STROW base point. If correct, this replaces ALL heuristic band placement.

Walks the VIF byte stream with a state machine (STROW rows + STMOD mode),
re-syncing at batch signatures the same way ThpgPositionUnwrapper does, and
scores reconstructed positions against the P8 oracle (same file, Q12.4).

Usage: python tools/diagnostics/thpg_strow_probe.py <p8.skin.ps2> <thpg.skin.ps2>
"""
import struct
import sys
from collections import defaultdict


def s16(data, off):
    return struct.unpack_from('<h', data, off)[0]


def i32(data, off):
    return struct.unpack_from('<i', data, off)[0]


def enumerate_batches(data):
    """Byte-signature scan for STCYCL(3,1)+V3_16+V3_8+V4_16 batches (same as
    thpg_band_analysis.py). Returns [(n, stcycl_off, pos_off, nrm_off, uv_off)]."""
    batches = []
    i = 0
    end = len(data) - 8
    while i < end:
        if data[i] == 3 and data[i + 1] == 1 and data[i + 3] == 0x01:
            j = i + 4
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
                        batches.append((n, i, pos_off, nrm_off, uv_off))
                        i = uv_off + 8 * n
                        continue
        i += 4
    return batches


def find_strows(data):
    """Byte-scan for STROW commands: cmd byte 0x30 at word-aligned offset+3,
    followed by 4 data words. Returns [(off, r0, r1, r2, r3)]."""
    hits = []
    for i in range(0, len(data) - 20, 4):
        if data[i + 3] == 0x30 and data[i] == 0 and data[i + 1] == 0 and data[i + 2] == 0:
            rows = [i32(data, i + 4 + c * 4) for c in range(4)]
            hits.append((i, rows))
    return hits


def find_stmods(data):
    """Byte-scan for STMOD commands (cmd 0x05, imm = mode in low 2 bits)."""
    hits = []
    for i in range(0, len(data) - 4, 4):
        if data[i + 3] == 0x05 and data[i + 1] == 0 and data[i + 2] == 0:
            hits.append((i, data[i] & 3))
    return hits


def main(p8_path, tg_path):
    p8 = open(p8_path, 'rb').read()
    tg = open(tg_path, 'rb').read()
    b_p8 = enumerate_batches(p8)
    b_tg = enumerate_batches(tg)
    print(f"P8 batches: {len(b_p8)}, THPG batches: {len(b_tg)}")

    strows = find_strows(tg)
    stmods = find_stmods(tg)
    strows_p8 = find_strows(p8)
    print(f"THPG STROW hits: {len(strows)}, STMOD hits: {len(stmods)}, P8 STROW hits: {len(strows_p8)}")
    for off, rows in strows[:12]:
        units = [r / 4096.0 for r in rows[:3]]
        print(f"  STROW @0x{off:05X}: rows={rows}  as-units=({units[0]:8.2f},{units[1]:8.2f},{units[2]:8.2f})")
    print(f"  STMOD offsets/modes: {[(hex(o), m) for o, m in stmods[:16]]}")

    # Assign each batch the most recent preceding STROW; score against oracle.
    total = 0
    exact = 0
    by_batch_fail = defaultdict(int)
    err_hist = defaultdict(int)
    si = 0
    active = None
    strow_iter = sorted(strows)
    for bi, ((n, _, po, _, _), (_, t_st, tpo, _, _)) in enumerate(zip(b_p8, b_tg)):
        while si < len(strow_iter) and strow_iter[si][0] < t_st:
            active = strow_iter[si][1]
            si += 1
        for i in range(n):
            ok = True
            for c in range(3):
                oracle = s16(p8, po + i * 6 + c * 2) / 16.0
                raw = s16(tg, tpo + i * 6 + c * 2)
                base = active[c] if active else 0
                recon = (raw + base) / 4096.0
                err = recon - oracle
                band_err = round(err / 16.0)
                if abs(err - band_err * 16.0) > 0.05:
                    band_err = 99  # non-band residual
                if band_err != 0:
                    ok = False
                    err_hist[(c, band_err)] += 1
            total += 1
            if ok:
                exact += 1
            else:
                by_batch_fail[bi] += 1
    print(f"\nSTROW reconstruction: {exact}/{total} exact ({exact / max(total, 1):.1%})")
    if err_hist:
        print("residual band errors (coord, bands): count")
        for k, v in sorted(err_hist.items(), key=lambda kv: -kv[1])[:12]:
            print(f"  {k}: {v}")
        worst = sorted(by_batch_fail.items(), key=lambda kv: -kv[1])[:10]
        print(f"worst batches: {worst}")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
