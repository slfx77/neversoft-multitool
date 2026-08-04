#!/usr/bin/env python3
"""Census of coincident coplanar geometry in EXPORTED GLB files.

The PS1 has no depth buffer: coincident geometry is resolved entirely by
ordering-table submission order, so the console never "fights". Every coincident
pair we export into a depth-tested renderer WILL fight unless the exporter
separates it (a geometric lift) or orders it (draw-order metadata the viewer
maps to renderOrder).

This measures the property directly, from the shipped GLB, in world space. It is
deliberately INDEPENDENT of PsxCoplanarOverlayDetector: it never calls the
detector and does not reimplement its branch structure, so it cannot agree with
the detector by construction. The round-3 audit's sharpest finding was that the
overlay census pins were characterisation snapshots of the detector's own output
-- a correct clipper and a broken one were indistinguishable to them. This is
the oracle that gap called for.

A pair is reported when two triangles are:
  * near-coplanar (unit normals parallel within --angle, same plane offset), and
  * overlapping in-plane (their projected areas actually intersect), and
  * NOT separated -- perpendicular gap below --gap.

Each reported pair is classified so a fix can target a cause rather than a
symptom:
  same-mesh          both triangles in one mesh -- no ordering is possible
  same-material      identical material: fights, but both sides paint the same
                     pixels, so it is invisible (safe to leave)
  cross-material     different materials: VISIBLE fighting, the actionable class
  mask-involved      at least one side alpha-tests (MASK); these write depth and
                     need ordering exactly as opaque does
  blend-involved     at least one side is BLEND (depth-write off in the viewer)

Usage:
    python glb_coincident_census.py <file.glb> [more.glb ...]
                                    [--gap 0.05] [--angle 3] [--limit 12]
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from collections import Counter, defaultdict
from pathlib import Path

COMPONENT = {5120: ('b', 1), 5121: ('B', 1), 5122: ('h', 2),
             5123: ('H', 2), 5125: ('I', 4), 5126: ('f', 4)}
COUNTS = {'SCALAR': 1, 'VEC2': 2, 'VEC3': 3, 'VEC4': 4}


def load(path: Path):
    blob = path.read_bytes()
    if blob[:4] != b'glTF':
        raise ValueError(f'not a GLB: {path}')
    length = struct.unpack_from('<I', blob, 12)[0]
    doc = json.loads(blob[20:20 + length].decode('utf-8', 'replace').rstrip('\0'))
    offset = 20 + length
    binlen = struct.unpack_from('<I', blob, offset)[0]
    return doc, blob[offset + 8:offset + 8 + binlen]


def accessor(doc, blob, index):
    acc = doc['accessors'][index]
    fmt, size = COMPONENT[acc['componentType']]
    n = COUNTS[acc['type']]
    view = doc['bufferViews'][acc['bufferView']]
    stride = view.get('byteStride') or size * n
    base = view.get('byteOffset', 0) + acc.get('byteOffset', 0)
    return [struct.unpack_from('<' + fmt * n, blob, base + i * stride)
            for i in range(acc['count'])]


def triangles(doc, blob):
    """Every triangle in world space, tagged with mesh index and material."""
    materials = doc.get('materials', [])
    out = []
    for node in doc.get('nodes', []):
        mesh_index = node.get('mesh')
        if mesh_index is None:
            continue
        matrix = node.get('matrix')
        if matrix:
            tx, ty, tz = matrix[12], matrix[13], matrix[14]
        else:
            tx, ty, tz = node.get('translation', (0.0, 0.0, 0.0))
        mesh = doc['meshes'][mesh_index]
        # Draw-order metadata rides on the MESH: PSX opaque overlays export
        # AUTHORED geometry plus a draw index rather than a baked lift, so a
        # correctly-resolved coincident pair legitimately has gap 0.0 and is
        # separated by renderOrder in the viewer, not by position. Reading only
        # the gap therefore counts resolved pairs as failures.
        draw = (mesh.get('extras') or {}).get('neversoftDrawIndex', 0)
        for prim in mesh['primitives']:
            pos = accessor(doc, blob, prim['attributes']['POSITION'])
            col = (accessor(doc, blob, prim['attributes']['COLOR_0'])
                   if 'COLOR_0' in prim['attributes'] else None)
            if 'indices' in prim:
                idx = [v[0] for v in accessor(doc, blob, prim['indices'])]
            else:
                idx = list(range(len(pos)))
            mat = prim.get('material')
            alpha = materials[mat].get('alphaMode', 'OPAQUE') if mat is not None else 'OPAQUE'
            two_sided = bool(materials[mat].get('doubleSided')) if mat is not None else False
            for k in range(0, len(idx) - 2, 3):
                tri = tuple(
                    (pos[i][0] + tx, pos[i][1] + ty, pos[i][2] + tz) for i in idx[k:k + 3])
                # Quantised vertex colour: PSX materials are TEXTURE-keyed, so
                # two faces can share a material and still paint different
                # pixels (baked light/shadow duplicates). Appearance is the
                # material AND the colours, never the material alone.
                shade = (tuple(tuple(round(c / 4096.0, 2) for c in col[i][:3])
                               for i in idx[k:k + 3]) if col else None)
                out.append((tri, mesh_index, mat, alpha, mesh.get('name') or '', shade, draw,
                            two_sided))
    return out


def _project(tri, drop):
    return [tuple(c for k, c in enumerate(p) if k != drop) for p in tri]


def _area2d(poly):
    total = 0.0
    for i in range(len(poly)):
        x1, y1 = poly[i]
        x2, y2 = poly[(i + 1) % len(poly)]
        total += x1 * y2 - x2 * y1
    return abs(total) / 2.0


def _clip(subject, clip):
    """Sutherland-Hodgman; both polygons convex (triangles)."""
    if _area2d(clip) <= 0:
        return []
    # Normalise clip winding to CCW so "inside" is consistently left-of-edge.
    signed = 0.0
    for i in range(len(clip)):
        x1, y1 = clip[i]
        x2, y2 = clip[(i + 1) % len(clip)]
        signed += x1 * y2 - x2 * y1
    if signed < 0:
        clip = clip[::-1]

    out = list(subject)
    for i in range(len(clip)):
        if not out:
            break
        ax, ay = clip[i]
        bx, by = clip[(i + 1) % len(clip)]
        current, out = out, []
        for k in range(len(current)):
            px, py = current[k]
            qx, qy = current[(k + 1) % len(current)]
            side_p = (bx - ax) * (py - ay) - (by - ay) * (px - ax)
            side_q = (bx - ax) * (qy - ay) - (by - ay) * (qx - ax)
            if side_p >= 0:
                out.append((px, py))
            if (side_p >= 0) != (side_q >= 0):
                denom = side_p - side_q
                if abs(denom) > 1e-12:
                    t = side_p / denom
                    out.append((px + (qx - px) * t, py + (qy - py) * t))
    return out


def shared_fraction(tri_a, tri_b, normal):
    """Intersection area as a fraction of the smaller triangle."""
    drop = max(range(3), key=lambda i: abs(normal[i]))
    pa, pb = _project(tri_a, drop), _project(tri_b, drop)
    area_a, area_b = _area2d(pa), _area2d(pb)
    smaller = min(area_a, area_b)
    if smaller <= 1e-9:
        return 0.0
    return _area2d(_clip(pa, pb)) / smaller


def aabb(tri):
    lo = tuple(min(p[k] for p in tri) for k in range(3))
    hi = tuple(max(p[k] for p in tri) for k in range(3))
    return lo, hi


def decline_reason(a, b, raw_a, raw_b):
    """Which of ClassifyPair's guards sent this actionable pair home.

    Mirrors the guard ORDER in PsxCoplanarOverlayDetector.ClassifyPair so the
    residue can be fixed by class instead of by symptom.
    """
    # Guard 1: back-to-back single-sided faces, culled apart, never flagged.
    if raw_a[0] * raw_b[0] + raw_a[1] * raw_b[1] + raw_a[2] * raw_b[2] < 0:
        return 'opposite-winding (guard 1)'
    # Guard 5 / guard 2's second arm: BoundsOverlap wants penetration > 0.25
    # on at least two axes. Coincident but THIN faces (a platform lip, a
    # railing) fail it even though they overlap completely in-plane.
    (alo, ahi), (blo, bhi) = aabb(a[0]), aabb(b[0])
    axes = sum(1 for k in range(3)
               if min(ahi[k], bhi[k]) - max(alo[k], blo[k]) > 0.25)
    if axes < 2:
        return 'thin face - BoundsOverlap 0.25 floor'
    if a[2] == b[2]:
        return 'same material, bounds overlap - falls through'
    return 'different material, bounds overlap - reaches area branches'


def detector_plane_key(tri):
    """PsxCoplanarOverlayDetector.TryCreateCandidate's PlaneKey, recomputed.

    The detector buckets faces by an EXACT integer key -- normal components
    rounded to 1/1000 and plane distance to 1/100 -- and only ever compares
    faces landing in the SAME bucket. Two genuinely coincident faces whose
    quantised key differs by one unit are never compared at all, so no rule
    can flag them. This recomputes that key so the census can tell "the
    detector considered this pair and declined it" apart from "the detector
    never saw this pair".

    Valid because both sides work in the same space: the writer emits the
    detector's points through ToGltfPosition, and the GLB triangle winding
    (0,2,1) reproduces the detector's own cross(p2-p0, p1-p0).
    """
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = tri
    ux, uy, uz = bx - ax, by - ay, bz - az
    vx, vy, vz = cx - ax, cy - ay, cz - az
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length < 1e-9:
        return None
    nx, ny, nz = nx / length, ny / length, nz / length
    first = nx if abs(nx) > 1e-6 else (ny if abs(ny) > 1e-6 else nz)
    if first < 0:
        nx, ny, nz = -nx, -ny, -nz
    distance = nx * ax + ny * ay + nz * az
    return (round(nx * 1000), round(ny * 1000), round(nz * 1000),
            round(distance * 100))


def normal_of(tri):
    (ax, ay, az), (bx, by, bz), (cx, cy, cz) = tri
    ux, uy, uz = bx - ax, by - ay, bz - az
    vx, vy, vz = cx - ax, cy - ay, cz - az
    nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
    length = math.sqrt(nx * nx + ny * ny + nz * nz)
    if length < 1e-9:
        return None, 0.0
    return (nx / length, ny / length, nz / length), length / 2.0


def centroid(tri):
    return (sum(p[0] for p in tri) / 3.0,
            sum(p[1] for p in tri) / 3.0,
            sum(p[2] for p in tri) / 3.0)


def same_appearance(a, b):
    """Do the two triangles paint the same pixels where they coincide?

    Requires the same material AND the same vertex colours: a shared texture
    with different baked shading (skware's day vs shadowed floor sections)
    dithers as visibly as any decal.
    """
    if a[2] != b[2] or a[2] is None:
        return False
    if a[5] is None or b[5] is None:
        return a[5] == b[5]
    return sorted(a[5]) == sorted(b[5])


def classify(a, b):
    """a, b are (tri, mesh, material, alpha, name, shade) records."""
    # Appearance first. A pair painting identical pixels cannot be SEEN to
    # fight regardless of its alpha mode, so testing MASK/BLEND ahead of this
    # promotes invisible pairs into the actionable classes.
    if same_appearance(a, b):
        return 'same-appearance'
    if a[1] == b[1]:
        return 'same-mesh'
    if 'BLEND' in (a[3], b[3]):
        return 'blend-involved'
    if 'MASK' in (a[3], b[3]):
        return 'mask-visible'
    return 'cross-material'


def census(path: Path, gap: float, angle_deg: float, limit: int,
           explain: bool = False) -> int:
    doc, blob = load(path)
    tris = triangles(doc, blob)
    cos_limit = math.cos(math.radians(angle_deg))

    # Bucket by quantised plane so the pair scan stays local.
    buckets = defaultdict(list)
    for rec in tris:
        normal, area = normal_of(rec[0])
        if normal is None or area < 1e-6:
            continue
        nx, ny, nz = normal
        # Fold antiparallel normals together: a coincident pair may be wound
        # either way, and both still fight.
        if (nx if abs(nx) > 1e-6 else (ny if abs(ny) > 1e-6 else nz)) < 0:
            nx, ny, nz = -nx, -ny, -nz
        offset = nx * rec[0][0][0] + ny * rec[0][0][1] + nz * rec[0][0][2]
        key = (round(nx, 1), round(ny, 1), round(nz, 1), round(offset / 4.0))
        buckets[key].append((rec, (nx, ny, nz), offset, centroid(rec[0]), area, normal))

    kinds = Counter()
    examples = []
    explanations = []
    pairs = 0
    resolved = 0
    culled = 0
    buckets_same = Counter()
    declines = Counter()
    for entries in buckets.values():
        for i in range(len(entries)):
            for j in range(i + 1, len(entries)):
                a, na, oa, ca, aa, raw_a = entries[i]
                b, nb, ob, cb, ab, raw_b = entries[j]
                if abs(na[0] * nb[0] + na[1] * nb[1] + na[2] * nb[2]) < cos_limit:
                    continue
                if abs(oa - ob) > gap:
                    continue
                # REAL in-plane overlap, not a centroid proxy: adjacent
                # triangles of one tessellated surface share an edge, sit on the
                # identical plane and have near-centroids, but they do not
                # overlap and do not fight. Clip one against the other and
                # require a meaningful shared area.
                if shared_fraction(a[0], b[0], na) < 0.10:
                    continue
                # Ordered apart by draw-order metadata: the viewer maps a
                # distinct neversoftDrawIndex to renderOrder, which is exactly
                # how the PS1's ordering table resolved it. Not a defect.
                if a[6] != b[6]:
                    resolved += 1
                    continue
                # Backface culling separates them: a wall authored once per
                # side lands on one plane but only ever rasterises one face per
                # viewpoint, so it cannot fight. This is exactly the pair
                # ClassifyPair's first guard declines, and declining it is
                # correct -- counting it as a defect blames the exporter for
                # the renderer doing its job. Double-sided faces still fight.
                if (raw_a[0] * raw_b[0] + raw_a[1] * raw_b[1] + raw_a[2] * raw_b[2] < 0
                        and not a[7] and not b[7]):
                    culled += 1
                    continue
                pairs += 1
                kind = classify(a, b)
                kinds[kind] += 1
                if kind in ('cross-material', 'mask-visible'):
                    ka = detector_plane_key(a[0])
                    kb = detector_plane_key(b[0])
                    buckets_same[ka is not None and ka == kb] += 1
                    declines[decline_reason(a, b, raw_a, raw_b)] += 1
                    if explain:
                        (alo, ahi), (blo, bhi) = aabb(a[0]), aabb(b[0])
                        pen = [round(min(ahi[k], bhi[k]) - max(alo[k], blo[k]), 3)
                               for k in range(3)]
                        explanations.append(
                            f'      {a[4][:26]:28} vs {b[4][:26]:28} '
                            f'gap={abs(oa - ob):.4f} areaRatio='
                            f'{min(aa, ab) / max(aa, ab):.3f} '
                            f'shared={shared_fraction(a[0], b[0], na):.3f} '
                            f'AABBpen={pen}')
                if len(examples) < limit and kind in ('cross-material', 'mask-visible'):
                    examples.append((kind, a[4], b[4], round(abs(oa - ob), 4)))

    print(f"\n=== {path.name}: {len(tris)} triangles, {len(buckets)} plane buckets")
    print(f"    coincident pairs resolved by draw order:  {resolved}")
    print(f"    coincident pairs separated by backface culling: {culled}")
    print(f"    UNRESOLVED coincident pairs (gap <= {gap}): {pairs}")
    for kind, n in kinds.most_common():
        note = {
            'cross-material': 'VISIBLE fighting - actionable',
            'mask-visible': 'alpha-test writes depth, differing pixels - actionable',
            'same-appearance': 'identical pixels - invisible, safe',
            'same-mesh': 'one mesh - cannot be ordered apart',
            'blend-involved': 'depth-write off in viewer - sorted, not fought',
        }.get(kind, '')
        print(f"      {kind:16} {n:6}   {note}")
    if buckets_same:
        print(f"    actionable pairs the detector COMPARED (same plane bucket): "
              f"{buckets_same[True]}")
        print(f"    actionable pairs the detector NEVER SAW (bucket differs):   "
              f"{buckets_same[False]}")
    if declines:
        print('    actionable declines by guard:')
        for reason, n in declines.most_common():
            print(f'      {n:5}  {reason}')
    for line in explanations[:40]:
        print(line)
    for kind, an, bn, d in examples:
        print(f"        {kind:15} {an[:30]:32} vs {bn[:30]:32} gap={d}")
    return kinds['cross-material'] + kinds['mask-visible']


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('files', nargs='+')
    parser.add_argument('--gap', type=float, default=0.05)
    parser.add_argument('--angle', type=float, default=3.0)
    parser.add_argument('--limit', type=int, default=12)
    parser.add_argument('--explain', action='store_true',
                        help='print per-pair metrics for every actionable pair')
    args = parser.parse_args()

    actionable = 0
    for name in args.files:
        actionable += census(Path(name), args.gap, args.angle, args.limit, args.explain)
    print(f"\nactionable pairs across all files: {actionable}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
