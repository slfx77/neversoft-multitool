#!/usr/bin/env python3
"""Render a PSX-animated GLB (skinned, CPU) to PNG for visual A/B checks.

Renders the skinned mesh at one or more times from two azimuths (front/side)
using matplotlib Poly3DCollection — no GPU or viewer needed. Companion to
psx_anim_verify.py (imports its Scene/GLB machinery).

Usage:
  python tools/diagnostics/psx_anim_render.py <file.glb> --anim 0 \
      [--times 0,0.5,1] [--out out.png] [--fps 30]

--times values are FRACTIONS of the clip duration (0..1).
"""
import argparse
import os
import sys

import numpy as np

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from psx_anim_verify import Scene  # noqa: E402

import matplotlib
matplotlib.use('Agg')
import matplotlib.pyplot as plt  # noqa: E402
from mpl_toolkits.mplot3d.art3d import Poly3DCollection  # noqa: E402


def skinned_world(scene, t):
    P, J, T3 = scene.skinned_vertices()
    Rw, Tw = scene.world(t)
    W = np.empty_like(P)
    for i in range(len(P)):
        j = J[i]
        W[i] = Rw[j] @ (scene.ibm_r[j] @ P[i] + scene.ibm_t[j]) + Tw[j]
    return W, T3


def contact_sheet(scene, out_stem, frac=0.4, cols=10, per_sheet=100):
    """One front-view thumbnail per animation — exhaustive visual QA."""
    n = len(scene.doc['animations'])
    sheets = (n + per_sheet - 1) // per_sheet
    for s in range(sheets):
        lo, hi = s * per_sheet, min(n, (s + 1) * per_sheet)
        rows = (hi - lo + cols - 1) // cols
        fig = plt.figure(figsize=(2 * cols, 2.2 * rows))
        for k, idx in enumerate(range(lo, hi)):
            scene.set_anim(idx)
            W, T3 = skinned_world(scene, frac * scene.duration)
            c = (W.min(axis=0) + W.max(axis=0)) / 2
            half = (W.max(axis=0) - W.min(axis=0)).max() / 2 * 1.05
            ax = fig.add_subplot(rows, cols, k + 1, projection='3d')
            pc = Poly3DCollection(W[T3][:, :, [0, 2, 1]], alpha=0.9,
                                  facecolor='#7799bb', edgecolor='#223344',
                                  linewidths=0.1)
            ax.add_collection3d(pc)
            ax.set_xlim(c[0] - half, c[0] + half)
            ax.set_ylim(c[2] - half, c[2] + half)
            ax.set_zlim(c[1] - half, c[1] + half)
            ax.view_init(elev=0, azim=90)
            ax.set_title(scene.anim_name, fontsize=6)
            ax.set_axis_off()
        out = f"{out_stem}_sheet{s}.png"
        fig.tight_layout()
        fig.savefig(out, dpi=90)
        plt.close(fig)
        print(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('glb')
    ap.add_argument('--anim', type=int, default=0)
    ap.add_argument('--times', default='0,0.5')
    ap.add_argument('--out')
    ap.add_argument('--sheet', action='store_true',
                    help='render one thumbnail per animation (contact sheets)')
    args = ap.parse_args()

    scene = Scene(args.glb)
    if args.sheet:
        stem = os.path.splitext(args.out or args.glb)[0]
        contact_sheet(scene, stem)
        return
    scene.set_anim(args.anim)
    fracs = [float(x) for x in args.times.split(',')]
    # PSX characters face +Z after the (X,-Y,-Z) glTF map, so the front view
    # looks from +Z (azim=90 in plot space); azim=-90 shows the BACK.
    views = [(0, 90), (0, 0)]  # front, side (elev, azim)

    fig = plt.figure(figsize=(4 * len(views), 4 * len(fracs)))
    for r, frac in enumerate(fracs):
        t = frac * scene.duration
        W, T3 = skinned_world(scene, t)
        lo, hi = W.min(axis=0), W.max(axis=0)
        c = (lo + hi) / 2
        half = (hi - lo).max() / 2 * 1.05
        for v, (elev, azim) in enumerate(views):
            ax = fig.add_subplot(len(fracs), len(views), r * len(views) + v + 1,
                                 projection='3d')
            pc = Poly3DCollection(W[T3], alpha=0.9, facecolor='#7799bb',
                                  edgecolor='#223344', linewidths=0.15)
            ax.add_collection3d(pc)
            ax.set_xlim(c[0] - half, c[0] + half)
            ax.set_ylim(c[2] - half, c[2] + half)
            ax.set_zlim(c[1] - half, c[1] + half)
            # glTF Y-up -> matplotlib Z-up: plot (x, z, y)
            pc.set_verts(W[T3][:, :, [0, 2, 1]])
            ax.view_init(elev=elev, azim=azim)
            ax.set_title(f"{scene.anim_name} t={t:.2f}s "
                         f"{'front' if azim == 90 else 'side'}", fontsize=8)
            ax.set_axis_off()
    out = args.out or os.path.splitext(args.glb)[0] + f"_anim{args.anim}.png"
    fig.tight_layout()
    fig.savefig(out, dpi=110)
    print(out)


if __name__ == '__main__':
    main()
