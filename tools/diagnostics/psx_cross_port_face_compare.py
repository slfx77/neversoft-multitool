#!/usr/bin/env python3
"""Compare material assignments for geometrically matching PSX mesh faces.

The Spider-Man PS1, Dreamcast, and PC releases use closely related PSX
containers, but texture identifiers are build-specific opaque values.  This
tool matches faces by their resolved world-space vertex positions and then
reports the observed texture-id correspondence instead of assuming that the
ids are portable between builds.

Create inputs with the shipping diagnostic command, for example:

  dotnet src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.dll \
    psx-mesh-dump l1a3_g.psx --json TestOutput/l1a3_psx.json
  python tools/diagnostics/psx_cross_port_face_compare.py \
    TestOutput/l1a3_psx.json TestOutput/l1a3_pc.json --source-texture 0x3CE37DB3

"""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import json
from pathlib import Path


def parse_hash(value: str) -> int:
    return int(value, 0)


def quantized_vertex(vertex: dict[str, float], quantum: float) -> tuple[int, int, int]:
    return tuple(round(vertex[axis] / quantum) for axis in ("X", "Y", "Z"))


def face_key(face: dict, quantum: float) -> tuple[tuple[int, int, int], ...]:
    return tuple(sorted(quantized_vertex(vertex, quantum)
                        for vertex in face["ResolvedWorldVertices"]))


def iter_faces(document: dict):
    for mesh in document["Meshes"]:
        for face in mesh["Faces"]:
            yield mesh["MeshIndex"], face


def texture_label(face: dict) -> str:
    if not face["IsTextured"]:
        return "untextured"
    return f"0x{face['TextureHash']:08X}"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="psx-mesh-dump JSON for the source build")
    parser.add_argument("reference", type=Path, help="psx-mesh-dump JSON for the comparison build")
    parser.add_argument("--quantum", type=float, default=0.5,
                        help="world-coordinate matching quantum (default: 0.5)")
    parser.add_argument("--source-texture", type=parse_hash,
                        help="limit detailed output to one source texture id")
    args = parser.parse_args()

    source = json.loads(args.source.read_text(encoding="utf-8"))
    reference = json.loads(args.reference.read_text(encoding="utf-8"))

    reference_by_key: dict[tuple, list[tuple[int, dict]]] = defaultdict(list)
    for mesh_index, face in iter_faces(reference):
        reference_by_key[face_key(face, args.quantum)].append((mesh_index, face))

    pairs: Counter[tuple[str, str]] = Counter()
    matched = 0
    ambiguous = 0
    unmatched = 0
    details = []
    for mesh_index, face in iter_faces(source):
        candidates = reference_by_key.get(face_key(face, args.quantum), [])
        if not candidates:
            unmatched += 1
            continue
        if len(candidates) > 1:
            ambiguous += 1
        reference_mesh, reference_face = candidates[0]
        matched += 1
        source_label = texture_label(face)
        reference_label = texture_label(reference_face)
        pairs[(source_label, reference_label)] += 1
        if args.source_texture is not None and face["TextureHash"] == args.source_texture:
            candidate_labels = sorted({
                texture_label(candidate_face)
                for _, candidate_face in candidates
            })
            details.append((mesh_index, face["FaceIndex"], reference_mesh,
                            reference_face["FaceIndex"], source_label, reference_label,
                            len(candidates), candidate_labels))

    total = matched + unmatched
    print(f"source={source['FileName']} reference={reference['FileName']} quantum={args.quantum:g}")
    print(f"matched={matched}/{total} ambiguous={ambiguous} unmatched={unmatched}")
    print("\nTexture correspondences (source -> reference):")
    for (source_label, reference_label), count in pairs.most_common():
        print(f"  {source_label:12s} -> {reference_label:12s}  {count:4d}")

    if args.source_texture is not None:
        print(f"\nMatches for source texture 0x{args.source_texture:08X}:")
        for (source_mesh, source_face, reference_mesh, reference_face,
             source_label, reference_label, candidate_count, candidate_labels) in details:
            print(f"  mesh {source_mesh:3d} face {source_face:3d} -> "
                  f"mesh {reference_mesh:3d} face {reference_face:3d}: "
                  f"{source_label} -> {reference_label} "
                  f"({candidate_count} candidate(s): {', '.join(candidate_labels)})")


if __name__ == "__main__":
    main()
