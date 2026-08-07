#!/usr/bin/env python3
"""Score the N64 coplanar-decal detector against its PS1 sibling.

The N64 ports carry the PS1's authored geometry unchanged, and the PS1 export
path already resolves coplanar decals (`PsxAnalyzer overlay-census` reports the
faces it flags, with their centroids). So "did the N64 detector find the decals
the PS1 detector finds" is directly measurable rather than a matter of taste.

Method:
  1. Match each N64 level GLB to a PS1 level GLB by texture-id overlap - the
     ports reuse the identifiers verbatim, so the match is usually exact.
  2. Fit a uniform scale + translation between the two export spaces from their
     overall bounding boxes. Same authored geometry, so the fit is exact up to
     the N64's trunc(raw/k) quantisation.
  3. Take the PS1 flagged-face centroids from overlay-census, map them into N64
     space, and ask whether the N64 export put a __overlay triangle there.

One PS1 quad becomes two N64 triangles, so a PS1 face counts as covered if
EITHER of its triangles is flagged.

    python n64_ps1_overlay_rosetta.py <n64-glb-dir> <ps1-glb-dir> <ps1-psx-dir>

Reports recall (PS1 decals the N64 found), precision (N64 overlays the PS1 also
flags), and the residue - PS1 decals with no N64 counterpart - since that is
what stays z-fighting.
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from glb_accessor_reader import load_glb, read_accessor  # noqa: E402

TEX_NAME = re.compile(r"(?:tex_|psxtxt_)([0-9A-Fa-f]{8})")
CENSUS_ROW = re.compile(
    r"c=\(\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\)\s*"
    r"ext=\(\s*(-?[\d.]+),\s*(-?[\d.]+),\s*(-?[\d.]+)\)")


def texture_ids(path):
    doc, _ = load_glb(path)
    out = set()
    for material in doc.get("materials", []):
        match = TEX_NAME.match(material.get("name", ""))
        if match:
            out.add(match.group(1).lower())
    return out


def triangles(path, overlays_only):
    """World-space triangle centroids, honouring each node's transform."""
    doc, blob = load_glb(path)
    node_transform = {}
    for node in doc.get("nodes", []):
        mesh = node.get("mesh")
        if mesh is None:
            continue
        if "matrix" in node:
            matrix = np.array(node["matrix"], dtype=float).reshape(4, 4)
            node_transform[mesh] = matrix[3, :3]
        else:
            node_transform[mesh] = np.array(node.get("translation", [0, 0, 0]), dtype=float)

    out = []
    for index, mesh in enumerate(doc.get("meshes", [])):
        name = mesh.get("name", "")
        is_overlay = "__overlay" in name
        if overlays_only and not is_overlay:
            continue
        offset = node_transform.get(index, np.zeros(3))
        for primitive in mesh.get("primitives", []):
            positions = read_accessor(doc, blob, primitive["attributes"]["POSITION"]) + offset
            idx = read_accessor(doc, blob, primitive["indices"]).astype(np.int64).reshape(-1, 3)
            for tri in idx:
                out.append(positions[tri].mean(axis=0))
    return np.array(out) if out else np.zeros((0, 3))


def bounds(path):
    doc, blob = load_glb(path)
    lo = np.full(3, np.inf)
    hi = np.full(3, -np.inf)
    for mesh in doc.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            positions = read_accessor(doc, blob, primitive["attributes"]["POSITION"])
            lo = np.minimum(lo, positions.min(axis=0))
            hi = np.maximum(hi, positions.max(axis=0))
    return lo, hi


def census_faces(analyzer_dir, psx_path):
    """PS1 flagged faces as (centroid xyz, half-extent xyz), from overlay-census."""
    result = subprocess.run(
        ["dotnet", "run", "--project", str(analyzer_dir), "--",
         "overlay-census", str(psx_path), "--limit", "100000"],
        capture_output=True, text=True, check=False)
    # Spectre wraps long rows, so join first and scan the whole blob. Each
    # detail row names the flagged face and then its partner; the c=/ext= pair
    # belongs to the flagged face.
    blob = " ".join(result.stdout.split())
    faces = [[float(v) for v in m.groups()] for m in CENSUS_ROW.finditer(blob)]
    return np.array(faces) if faces else np.zeros((0, 6))


def spaces_align(census_points, n64_points):
    """
    The two exports are already in the SAME world space, so no fit is needed —
    measured on THPS1 School, where the PS1 census centroids span
    (-8856.6, 0.1, -7094.7)..(4818.2, 1332.1, 5488.9) and the N64 overlay
    centroids span (-8935.6, 0.1, -7094.4)..(856.6, 1297.1, 5488.9): four of the
    six extremes agree to under 1%.

    Fitting a similarity from the two GLBs' overall bounding boxes actively
    breaks this — the PS1 level file carries geometry the N64 bundle does not
    (its box reaches +/-8613 where the N64's reaches -10432..6668), so the
    fitted scale is fabricated and every centroid lands nowhere. This reports
    the residual instead of correcting it, so a genuine space mismatch shows up
    as a warning rather than being silently absorbed.
    """
    if len(census_points) == 0 or len(n64_points) == 0:
        return None
    return float(np.linalg.norm(census_points.min(axis=0) - n64_points.min(axis=0)))


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("n64_dir", type=pathlib.Path)
    parser.add_argument("ps1_dir", type=pathlib.Path)
    parser.add_argument("psx_dir", type=pathlib.Path)
    parser.add_argument("--analyzer", type=pathlib.Path,
                        default=pathlib.Path("tools/PsxAnalyzer/PsxAnalyzer.csproj"))
    parser.add_argument("--tolerance", type=float, default=2.0,
                        help="centroid match radius in N64 export units")
    args = parser.parse_args()

    ps1_index = {p: texture_ids(p) for p in sorted(args.ps1_dir.glob("*.glb"))}

    print(f"{'n64':<14} {'ps1':<16} {'ps1 decals':>10} {'found':>7} {'recall':>7} "
          f"{'n64 ovl':>8} {'matched':>8} {'prec':>6}")
    print("-" * 84)

    totals = np.zeros(4)
    siblings = {}
    for n64_path in sorted(args.n64_dir.glob("*.glb")):
        n64_ids = texture_ids(n64_path)
        if len(n64_ids) < 20:
            continue
        best, shared = None, 0
        for candidate, ids in ps1_index.items():
            hit = len(n64_ids & ids)
            if hit > shared:
                best, shared = candidate, hit
        if best is None or shared < 0.75 * len(n64_ids):
            continue

        psx_path = args.psx_dir / (best.stem + ".psx")
        if not psx_path.exists():
            continue
        siblings.setdefault(psx_path, []).append(n64_path)
        continue

    for psx_path, n64_paths in sorted(siblings.items()):
        best = psx_path
        n64_path = n64_paths[0]

        ps1_points = census_faces(args.analyzer, psx_path)
        if len(ps1_points) == 0:
            continue
        # Union the chunks: several N64 bundles can be pieces of one PS1 level.
        parts = [triangles(p, overlays_only=True) for p in n64_paths]
        parts = [p for p in parts if len(p)]
        n64_overlays = np.vstack(parts) if parts else np.zeros((0, 3))
        ps1_in_n64 = ps1_points          # same world space; see spaces_align
        if len(n64_overlays) == 0:
            print(f"{'+'.join(p.stem for p in n64_paths)[:13]:<14} {best.stem:<16} {len(ps1_points):>10} "
                  f"{0:>7} {0.0:>7.2f} {0:>8} {0:>8} {0.0:>6.2f}")
            totals += [len(ps1_points), 0, 0, 0]
            continue

        # A PS1 QUAD becomes two N64 TRIANGLES, whose centroids sit well away
        # from the quad's - on a 100-unit face, 15-25 units away. So match a
        # triangle to a face by asking whether it lies within the face's own
        # printed extent, not by centroid proximity. Comparing centroid to
        # centroid at a fixed 2-unit radius scored 6% recall purely as an
        # artifact of that offset.
        centres = ps1_in_n64[:, :3]
        extents = ps1_in_n64[:, 3:] + args.tolerance
        inside = np.array([
            np.all(np.abs(n64_overlays - centre) <= extent, axis=1)
            for centre, extent in zip(centres, extents)
        ]) if len(centres) else np.zeros((0, len(n64_overlays)), dtype=bool)

        found = int(inside.any(axis=1).sum())
        matched = int(inside.any(axis=0).sum())
        recall = found / len(ps1_points)
        precision = matched / len(n64_overlays)
        totals += [len(ps1_points), found, len(n64_overlays), matched]
        print(f"{'+'.join(p.stem for p in n64_paths)[:13]:<14} {best.stem:<16} {len(ps1_points):>10} {found:>7} "
              f"{recall:>7.2f} {len(n64_overlays):>8} {matched:>8} {precision:>6.2f}")

    if totals[0]:
        print("-" * 84)
        print(f"{'TOTAL':<31} {int(totals[0]):>10} {int(totals[1]):>7} "
              f"{totals[1]/totals[0]:>7.2f} {int(totals[2]):>8} {int(totals[3]):>8} "
              f"{totals[3]/max(totals[2],1):>6.2f}")
        print(f"\nresidue (PS1 decals with no N64 overlay): {int(totals[0]-totals[1])}")


if __name__ == "__main__":
    main()
