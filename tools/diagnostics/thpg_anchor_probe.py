#!/usr/bin/env python3
"""Test whether the per-batch V4_16 preamble quadruples are Q12.4 anchor points
that recover THPG's wrapped Q4.12 position bands.

For each batch: parse the preamble V4_16 table (mask bit 15 per component),
interpret entries as (x,y,z,w) Q12.4 points, and compute the assignment-free
upper bound: for every vertex, does SOME anchor entry give
    band_c = round((anchor_c - fine_c)/16)  ==  oracle band_c   (all 3 axes)?
Also reports per-vertex best-anchor index so assignment patterns (runs, bone
slots) become visible.

Usage: python tools/diagnostics/thpg_anchor_probe.py <p8.skin.ps2> <thpg.skin.ps2> [--batches 1,2,3]
"""
import struct
import sys


def s16(d, o):
    return struct.unpack_from('<h', d, o)[0]


def enumerate_batches(data):
    batches = []
    i = 0
    end = len(data) - 8
    while i < end:
        if data[i] == 3 and data[i + 1] == 1 and data[i + 3] == 0x01:
            j = i + 4
            if j + 4 <= len(data) and (data[j + 3] & 0x6F) == 0x69:
                n = data[j + 2] or 256
                pos_size = ((6 * n + 3) >> 2) << 2
                k = j + 4 + pos_size
                if k + 4 <= len(data) and (data[k + 3] & 0x6F) == 0x6A and (data[k + 2] or 256) == n:
                    nrm_size = ((3 * n + 3) >> 2) << 2
                    m = k + 4 + nrm_size
                    if m + 4 <= len(data) and (data[m + 3] & 0x6F) == 0x6D and (data[m + 2] or 256) == n:
                        uv_end = m + 4 + 8 * n
                        batches.append({'n': n, 'stcycl': i, 'pos': j + 4, 'uv': m + 4, 'end': uv_end})
                        i = uv_end
                        continue
        i += 4
    return batches


def parse_preamble_v416(data, start, end):
    """Find UNPACK V4_16 (cmd 0x6D/0x7D w/ FLG) inside [start,end); return raw sint16 rows."""
    rows = []
    i = (start + 3) & ~3
    while i + 4 <= end:
        num = data[i + 2]
        cmd = data[i + 3]
        base = cmd & 0x6F
        if base == 0x6D and num > 0:
            n = num
            for k in range(n):
                off = i + 4 + k * 8
                if off + 8 <= len(data):
                    rows.append(struct.unpack_from('<4h', data, off))
            i += 4 + ((8 * n + 3) >> 2 << 2)
            return rows  # first V4_16 table only
        elif (cmd & 0x60) == 0x60:
            fmt_sizes = {0x60: 4, 0x61: 2, 0x62: 1, 0x64: 8, 0x65: 4, 0x66: 2,
                         0x68: 12, 0x69: 6, 0x6A: 3, 0x6C: 16, 0x6D: 8, 0x6E: 4, 0x6F: 2}
            sz = fmt_sizes.get(base, 4)
            n = num or 256
            i += 4 + (((sz * n) + 3) >> 2 << 2)
        else:
            i += 4
    return rows


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    sel = None
    for a in sys.argv[1:]:
        if a.startswith('--batches='):
            sel = {int(x) for x in a.split('=', 1)[1].split(',')}
    p8 = open(args[0], 'rb').read()
    tg = open(args[1], 'rb').read()
    b_p8 = enumerate_batches(p8)
    b_tg = enumerate_batches(tg)
    print(f"batches: P8={len(b_p8)} THPG={len(b_tg)}")

    grand_total = 0
    grand_ok = 0
    prev_end = 0
    for bi, (bp, bt) in enumerate(zip(b_p8, b_tg)):
        window = (prev_end, bt['stcycl'])
        prev_end = bt['end']
        if bi == 0:
            continue  # window 0 is the file header, not a preamble
        rows = parse_preamble_v416(tg, window[0], window[1])
        n = bp['n']
        # anchors: interpret each row's (x,y,z) masked Q12.4
        anchors = [tuple(((v & 0x7FFF) if v < 0 else v) / 16.0 for v in r[:3]) for r in rows]
        ok = 0
        best_idx = []
        for i in range(n):
            oracle = [s16(p8, bp['pos'] + i * 6 + c * 2) / 16.0 for c in range(3)]
            fine = [s16(tg, bt['pos'] + i * 6 + c * 2) / 4096.0 for c in range(3)]
            hit = -1
            for ai, a in enumerate(anchors):
                good = True
                for c in range(3):
                    band = round((a[c] - fine[c]) / 16.0)
                    if abs(fine[c] + band * 16.0 - oracle[c]) > 0.05:
                        good = False
                        break
                if good:
                    hit = ai
                    break
            if hit >= 0:
                ok += 1
            best_idx.append(hit)
        grand_total += n
        grand_ok += ok
        if sel is None or bi in sel:
            print(f"\nbatch {bi}: n={n}, anchors={len(anchors)}, upper-bound match {ok}/{n}")
            for ai, (a, r) in enumerate(zip(anchors, rows)):
                flags = ''.join('F' if v < 0 else '.' for v in r)
                w = (r[3] & 0x7FFF) if r[3] < 0 else r[3]
                print(f"  anchor {ai}: ({a[0]:7.2f},{a[1]:7.2f},{a[2]:7.2f}) w={w / 16.0:7.2f} flags={flags} raw={r}")
            print(f"  best-anchor per vertex: {''.join(str(b) if b >= 0 else 'X' for b in best_idx)}")
    print(f"\nTOTAL upper bound: {grand_ok}/{grand_total} ({grand_ok / max(grand_total, 1):.1%})")


if __name__ == "__main__":
    main()
