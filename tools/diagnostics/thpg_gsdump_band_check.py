#!/usr/bin/env python3
"""Read off wrapped-vs-unwrapped THPG skin positions from a GS dump.

Takes the per-vertex CSV produced by `gsdump --dump-vertices` (post-VU1 GS
vertices: screen XYZ + STQ) and a THPG .skin.ps2 file. Matches each vertex
batch in the file to its GS draw via UV fingerprints (the skin microprogram
outputs ST = uv*Q, so s/q recovers the raw uv/4096 texture coordinate).

Then, for "wrap-crossing" vertex pairs — unpack-adjacent vertices whose raw
Q4.12 fine positions differ by >12 units on exactly one axis while being <4
units apart on the others and <0.05 apart in UV (a mod-16 wrap between true
surface neighbours) — it compares their SCREEN distance against the normal
adjacent-pair screen distance in the same draw:

  - hardware rendered WRAPPED   -> crossing pairs ~16 model units apart on
                                   screen (orders of magnitude above normal)
  - hardware rendered UNWRAPPED -> crossing pairs look like every other pair

Usage:
  python tools/diagnostics/thpg_gsdump_band_check.py <vertices.csv> <skin.ps2> [more.ps2 ...]
"""
import csv
import struct
import sys
from collections import defaultdict


def s16(d, o):
    return struct.unpack_from('<h', d, o)[0]


def parse_batches(data):
    out = []
    i = 0
    end = len(data) - 8
    while i < end:
        if data[i] == 3 and data[i + 1] == 1 and data[i + 3] == 0x01:
            j = i + 4
            if j + 4 <= len(data) and (data[j + 3] & 0x6F) == 0x69:
                n = data[j + 2] or 256
                ps = ((6 * n + 3) >> 2) << 2
                k = j + 4 + ps
                if k + 4 <= len(data) and (data[k + 3] & 0x6F) == 0x6A and (data[k + 2] or 256) == n:
                    ns = ((3 * n + 3) >> 2) << 2
                    m = k + 4 + ns
                    if m + 4 <= len(data) and (data[m + 3] & 0x6F) == 0x6D and (data[m + 2] or 256) == n:
                        verts = []
                        for v in range(n):
                            fine = tuple(s16(data, j + 4 + v * 6 + c * 2) / 4096.0 for c in range(3))
                            u = s16(data, m + 4 + v * 8) / 4096.0
                            vv = s16(data, m + 4 + v * 8 + 2) / 4096.0
                            verts.append((fine, (u, vv)))
                        out.append(verts)
                        i = m + 4 + 8 * n
                        continue
        i += 4
    return out


def quant_uv(u, v):
    return (round(u * 512), round(v * 512))


def main():
    csv_path = sys.argv[1]
    skin_paths = sys.argv[2:]

    # GS draws: group by (vsync, giftag)
    draws = defaultdict(list)
    with open(csv_path, newline='') as f:
        for row in csv.DictReader(f):
            if row['prim'] != '4':
                continue
            q = float(row['q'])
            if q == 0:
                continue
            u = float(row['s']) / q
            v = float(row['t']) / q
            draws[(int(row['vsync']), int(row['giftag']))].append(
                (u, v, float(row['x']), float(row['y']), float(row['z']), row['nokick'] == '1'))
    # index draws by uv fingerprint
    draw_fp = {}
    for key, verts in draws.items():
        fp = frozenset(quant_uv(u, v) for u, v, *_ in verts)
        draw_fp[key] = fp
    print(f"GS tristrip draws: {len(draws)}")

    grand_cross = []
    grand_norm = []
    for path in skin_paths:
        data = open(path, 'rb').read()
        batches = parse_batches(data)
        name = path.replace('\\', '/').split('/')[-1]
        matched = 0
        for bi, verts in enumerate(batches):
            bfp = set(quant_uv(u, v) for _, (u, v) in verts)
            if len(bfp) < 8:
                continue
            best = None
            for key, fp in draw_fp.items():
                inter = len(bfp & fp)
                score = inter / len(bfp)
                if best is None or score > best[0]:
                    best = (score, key)
            if best is None or best[0] < 0.8:
                continue
            matched += 1
            _, key = best
            gs = draws[key]
            # map quantized uv -> screen positions (unique uv only)
            gs_by_uv = defaultdict(list)
            for u, v, x, y, z, nk in gs:
                gs_by_uv[quant_uv(u, v)].append((x, y))
            uv_screen = {k: v[0] for k, v in gs_by_uv.items() if len(set(v)) == 1}

            # adjacent pairs in unpack order
            for i in range(len(verts) - 1):
                (f1, uv1), (f2, uv2) = verts[i], verts[i + 1]
                duv = max(abs(uv1[0] - uv2[0]), abs(uv1[1] - uv2[1]))
                if duv > 0.05:
                    continue
                d = [abs(f1[c] - f2[c]) for c in range(3)]
                wrap_axes = [c for c in range(3) if d[c] > 12.0]
                near_axes = [c for c in range(3) if d[c] < 4.0]
                q1, q2 = quant_uv(*uv1), quant_uv(*uv2)
                if q1 not in uv_screen or q2 not in uv_screen or q1 == q2:
                    continue
                s1, s2 = uv_screen[q1], uv_screen[q2]
                sd = ((s1[0] - s2[0]) ** 2 + (s1[1] - s2[1]) ** 2) ** 0.5
                if len(wrap_axes) == 1 and len(near_axes) == 2:
                    grand_cross.append((name, bi, i, d, sd))
                elif not wrap_axes and max(d) < 2.0:
                    grand_norm.append(sd)
        print(f"{name}: {len(batches)} batches, {matched} matched to GS draws")

    if grand_norm:
        grand_norm.sort()
        med = grand_norm[len(grand_norm) // 2]
        p90 = grand_norm[int(len(grand_norm) * 0.9)]
        print(f"\nnormal adjacent pairs: {len(grand_norm)}, median screen dist {med:.1f}px, p90 {p90:.1f}px")
    print(f"wrap-crossing pairs: {len(grand_cross)}")
    for name, bi, i, d, sd in grand_cross[:20]:
        print(f"  {name} batch {bi} verts {i},{i + 1}: model delta=({d[0]:.2f},{d[1]:.2f},{d[2]:.2f}) -> screen {sd:.1f}px")


if __name__ == "__main__":
    main()
