#!/usr/bin/env python3
"""Localize GS-replay error: side-by-side + abs-diff heatmap + region MAE breakdown.

Usage: python tools/validation/gsdump/frame_diff_heatmap.py <render_dir> [stem_substr]
Finds *.render.png and *.pcsx2.png in <render_dir>, builds a montage
(render | pcsx2 | heatmap) and prints the MAE of an 8x8 grid of regions so the
hottest cells are obvious.
"""
import sys, glob
from PIL import Image


def load_pair(d, sub):
    r = [p for p in glob.glob(d + "/*.render.png") if sub in p]
    p = [p for p in glob.glob(d + "/*.pcsx2.png") if sub in p]
    if not r or not p:
        raise SystemExit(f"missing render/pcsx2 in {d} (sub={sub!r})")
    return Image.open(r[0]).convert("RGB"), Image.open(p[0]).convert("RGB")


def main(argv):
    d = argv[0]
    sub = argv[1] if len(argv) > 1 else ""
    render, pcsx2 = load_pair(d, sub)
    w = min(render.width, pcsx2.width)
    h = min(render.height, pcsx2.height)
    render = render.crop((0, 0, w, h))
    pcsx2 = pcsx2.crop((0, 0, w, h))
    rp = render.load()
    pp = pcsx2.load()

    heat = Image.new("RGB", (w, h))
    hp = heat.load()
    GX, GY = 8, 8
    cell = [[0.0, 0] for _ in range(GX * GY)]
    total = 0.0
    for y in range(h):
        for x in range(w):
            r0 = rp[x, y]
            p0 = pp[x, y]
            dr = abs(r0[0] - p0[0]) + abs(r0[1] - p0[1]) + abs(r0[2] - p0[2])
            d3 = dr / 3.0
            total += d3
            v = min(255, int(d3 * 2))
            hp[x, y] = (v, 0, 255 - v) if v > 20 else (0, 0, 0)
            ci = (y * GY // h) * GX + (x * GX // w)
            cell[ci][0] += d3
            cell[ci][1] += 1

    print(f"global MAE = {total / (w * h):.2f}   ({w}x{h})")
    print("region MAE grid (8x8, row-major, y=0 at top):")
    cells = []
    for gy in range(GY):
        row = []
        for gx in range(GX):
            s, n = cell[gy * GX + gx]
            m = s / n if n else 0
            row.append(m)
            cells.append((m, gx, gy))
        print("  " + " ".join(f"{v:5.1f}" for v in row))
    cells.sort(reverse=True)
    print("hottest cells (MAE, gx, gy)  [each cell ~%dx%d px]:" % (w // GX, h // GY))
    for m, gx, gy in cells[:6]:
        px0, py0 = gx * w // GX, gy * h // GY
        print(f"  MAE={m:5.1f} cell({gx},{gy}) px~({px0}..{px0 + w // GX},{py0}..{py0 + h // GY})")

    gap = 6
    mon = Image.new("RGB", (w * 3 + gap * 2, h), (40, 40, 40))
    mon.paste(render, (0, 0))
    mon.paste(pcsx2, (w + gap, 0))
    mon.paste(heat, (2 * (w + gap), 0))
    out = d + "/diff_heatmap.png"
    mon.save(out)
    print(f"wrote {out}  (render | pcsx2 | heatmap)")


if __name__ == "__main__":
    main(sys.argv[1:])
