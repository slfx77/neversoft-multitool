#!/usr/bin/env python3
"""Find coplanar overlapping triangle pairs in an exported N64 model.

The PS1 has no z-buffer, so Neversoft authored decals as faces sitting exactly
on the surface they mark and let the ordering table sequence them -- which is
why the PS1 path carries `PsxCoplanarOverlayDetector`. The N64 does have a
z-buffer, but the RDP resolves exactly-coplanar decals with a dedicated DECAL
render mode rather than a depth offset, so the same authored geometry ships
coincident and z-fights once it is exported to glTF.

This measures how much of that is actually present, using the same standard the
PS1 detector settles on: same plane, and a REAL polygon intersection covering at
least 1% of the smaller face (an AABB-overlap test alone flags diagonal
neighbours that merely share an edge).

    python n64_coplanar_probe.py <file.glb | dir-of-glb> [--limit N]
"""

from __future__ import annotations

import argparse
import collections
import pathlib
import sys

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from glb_accessor_reader import load_glb, read_accessor  # noqa: E402

PLANE_TOLERANCE = 0.05      # world units; authored decals are exactly coincident
NORMAL_TOLERANCE = 0.02     # unit-vector components
MIN_SHARED_FRACTION = 0.01


def dominant_axes(normal):
    """The two axes to project onto: drop the largest normal component."""
    drop = int(np.argmax(np.abs(normal)))
    return [a for a in range(3) if a != drop]


def cross2(u, v):
    """2-D scalar cross product (numpy 2.0 dropped the 2-vector np.cross)."""
    return float(u[0] * v[1] - u[1] * v[0])


def clip_polygon(subject, clip):
    """Sutherland-Hodgman clip of subject against convex clip, both 2-D CCW."""
    output = list(subject)
    for i in range(len(clip)):
        if not output:
            return []
        a, b = clip[i], clip[(i + 1) % len(clip)]
        edge = b - a
        inputs, output = output, []
        for j in range(len(inputs)):
            cur, prev = inputs[j], inputs[j - 1]
            cur_in = cross2(edge, cur - a) >= 0
            prev_in = cross2(edge, prev - a) >= 0
            if cur_in:
                if not prev_in:
                    output.append(intersect(prev, cur, a, b))
                output.append(cur)
            elif prev_in:
                output.append(intersect(prev, cur, a, b))
    return output


def intersect(p, q, a, b):
    d1, d2 = q - p, b - a
    denom = cross2(d2, d1)
    if abs(denom) < 1e-12:
        return p
    t = cross2(d2, a - p) / denom
    return p + d1 * t


def area(polygon):
    if len(polygon) < 3:
        return 0.0
    total = 0.0
    for i in range(len(polygon)):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % len(polygon)]
        total += x1 * y2 - x2 * y1
    return abs(total) * 0.5


def ensure_ccw(polygon):
    total = 0.0
    for i in range(len(polygon)):
        x1, y1 = polygon[i]
        x2, y2 = polygon[(i + 1) % len(polygon)]
        total += x1 * y2 - x2 * y1
    return polygon if total >= 0 else polygon[::-1]


def shared_fraction(first, second, axes):
    a = ensure_ccw([np.array([v[axes[0]], v[axes[1]]], dtype=float) for v in first])
    b = ensure_ccw([np.array([v[axes[0]], v[axes[1]]], dtype=float) for v in second])
    area_a, area_b = area(a), area(b)
    if min(area_a, area_b) <= 1e-9:
        return 0.0
    return area(clip_polygon(a, b)) / min(area_a, area_b)


def collect_faces(doc, blob):
    """Every triangle in the document, in world space, tagged with its material."""
    node_matrix = {}
    for node in doc.get("nodes", []):
        mesh = node.get("mesh")
        if mesh is None:
            continue
        translation = np.array(node.get("translation", [0.0, 0.0, 0.0]), dtype=float)
        node_matrix.setdefault(mesh, translation)

    faces = []
    for mesh_index, mesh in enumerate(doc.get("meshes", [])):
        offset = node_matrix.get(mesh_index, np.zeros(3))
        for primitive in mesh.get("primitives", []):
            positions = read_accessor(doc, blob, primitive["attributes"]["POSITION"]) + offset
            indices = read_accessor(doc, blob, primitive["indices"]).astype(np.int64).reshape(-1, 3)
            material = primitive.get("material", -1)
            for tri in indices:
                faces.append((positions[tri], material, mesh_index))
    return faces


def probe(path, limit):
    doc, blob = load_glb(path)
    faces = collect_faces(doc, blob)
    materials = doc.get("materials", [])

    buckets = collections.defaultdict(list)
    for index, (corners, material, mesh_index) in enumerate(faces):
        normal = np.cross(corners[1] - corners[0], corners[2] - corners[0])
        length = np.linalg.norm(normal)
        if length < 1e-9:
            continue
        normal = normal / length
        # Fold antiparallel normals into one bucket so a two-sided sheet lands
        # together with its own back face; `facing` remembers which side.
        facing = 1
        if normal[int(np.argmax(np.abs(normal)))] < 0:
            normal, facing = -normal, -1
        offset = float(np.dot(normal, corners[0]))
        key = (
            round(normal[0] / NORMAL_TOLERANCE),
            round(normal[1] / NORMAL_TOLERANCE),
            round(normal[2] / NORMAL_TOLERANCE),
            round(offset / PLANE_TOLERANCE),
        )
        buckets[key].append((index, corners, normal, facing, material, mesh_index))

    pairs = []
    for entries in buckets.values():
        if len(entries) < 2:
            continue
        if len(entries) > limit:
            continue  # a flat floor tessellated into hundreds of tris: skip, not a decal stack
        for i in range(len(entries)):
            for j in range(i + 1, len(entries)):
                _, first, normal, face_a, mat_a, mesh_a = entries[i]
                _, second, _, face_b, mat_b, mesh_b = entries[j]
                if mat_a == mat_b and mesh_a == mesh_b:
                    continue  # same surface, same material: ordinary tessellation
                fraction = shared_fraction(first, second, dominant_axes(normal))
                if fraction >= MIN_SHARED_FRACTION:
                    pairs.append((fraction, mat_a, mat_b, face_a == face_b))

    name = lambda m: materials[m].get("name", "?") if 0 <= m < len(materials) else "-"  # noqa: E731
    # Only SAME-FACING coplanar pairs can z-fight. An opposite-facing pair is a
    # two-sided sheet built from two single-sided quads (how the medals are
    # made): backface culling shows exactly one of them, so it is not a defect.
    fighting = [p for p in pairs if p[3]]
    by_material = collections.Counter((name(p[1]), name(p[2])) for p in fighting)
    print(f"{path.name}: {len(faces)} triangles, {len(pairs)} coplanar pairs "
          f"({len(fighting)} same-facing = z-fighting risk, "
          f"{len(pairs) - len(fighting)} back-to-back sheets)")
    for (a, b), count in by_material.most_common(10):
        print(f"    {count:>5}  {a}  <->  {b}")
    return len(fighting)


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("target", type=pathlib.Path)
    parser.add_argument("--limit", type=int, default=64,
                        help="max faces per plane bucket to test pairwise (default 64)")
    args = parser.parse_args()

    paths = [args.target] if args.target.is_file() else sorted(args.target.rglob("*.glb"))
    total = 0
    affected = 0
    for path in paths:
        count = probe(path, args.limit)
        total += count
        affected += count > 0
    if len(paths) > 1:
        print(f"\n{total} pairs across {affected}/{len(paths)} files")


if __name__ == "__main__":
    main()
