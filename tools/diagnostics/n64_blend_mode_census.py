#!/usr/bin/env python3
"""Census N64 glTF materials by alpha mode against their texture's real alpha.

Why: the N64 render bank carries the PS1 face flag word, whose bit 6 marks a
semi-transparent (ABE) face. The converter turned that bit straight into glTF
BLEND. But N64 art is RGBA5551 / CI with a ONE-BIT alpha -- there is no PS1 STP
runtime marker to say "blend this texel". When a BLEND material's texture only
ever has alpha 0 or 255 and the base colour is white, BLEND and MASK emit
IDENTICAL pixels; the only difference is that BLEND does not write depth, which
is exactly the medal front/back sorting artefact.

This measures how much of the corpus is in that state, and how much genuinely
carries partial alpha (where BLEND is the right call).

    python n64_blend_mode_census.py <dir-of-glb>          # census
    python n64_blend_mode_census.py <file.glb> --detail   # per-primitive dump

Detail mode also reports each primitive's plane (normal + offset) so coplanar
stacks -- decals sharing a surface with the sheet under them -- are visible.
"""

from __future__ import annotations

import argparse
import collections
import io
import json
import pathlib
import struct
import sys

import numpy as np

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from glb_accessor_reader import load_glb, read_accessor  # noqa: E402

try:
    from PIL import Image
except ImportError:  # pragma: no cover - diagnostic convenience
    Image = None


def texture_alpha_class(doc, blob, material):
    """Classify the material's base texture alpha: none / opaque / binary / graduated."""
    pbr = material.get("pbrMetallicRoughness", {})
    tex_ref = pbr.get("baseColorTexture")
    if tex_ref is None:
        return "no-texture", (0, 0, 0)
    if Image is None:
        return "pillow-missing", (0, 0, 0)

    texture = doc["textures"][tex_ref["index"]]
    image = doc["images"][texture["source"]]
    view = doc["bufferViews"][image["bufferView"]]
    start = view.get("byteOffset", 0)
    payload = blob[start : start + view["byteLength"]]

    with Image.open(io.BytesIO(payload)) as im:
        if im.mode != "RGBA":
            return "opaque", (0, 0, 0)
        alpha = np.asarray(im)[:, :, 3]

    zero = int((alpha == 0).sum())
    full = int((alpha == 255).sum())
    partial = int(alpha.size - zero - full)
    if partial:
        return "graduated", (zero, partial, full)
    if zero:
        return "binary", (zero, partial, full)
    return "opaque", (zero, partial, full)


def vertex_alpha_min(doc, blob, primitive):
    """Minimum COLOR_0 alpha over the primitive, or 1.0 when it carries none."""
    accessor = primitive["attributes"].get("COLOR_0")
    if accessor is None:
        return 1.0
    colours = read_accessor(doc, blob, accessor)
    if colours.ndim != 2 or colours.shape[1] < 4:
        return 1.0
    return float(colours[:, 3].min())


def iter_primitives(doc):
    for mesh_index, mesh in enumerate(doc.get("meshes", [])):
        for primitive in mesh.get("primitives", []):
            yield mesh_index, mesh.get("name", f"mesh{mesh_index}"), primitive


def census(paths):
    counts = collections.Counter()
    tri_counts = collections.Counter()
    files_with_pointless_blend = set()

    for path in paths:
        doc, blob = load_glb(path)
        materials = doc.get("materials", [])
        alpha_class = [texture_alpha_class(doc, blob, m) for m in materials]

        for _, _, primitive in iter_primitives(doc):
            material_index = primitive.get("material")
            if material_index is None:
                continue
            material = materials[material_index]
            mode = material.get("alphaMode", "OPAQUE")
            klass = alpha_class[material_index][0]
            tris = len(read_accessor(doc, blob, primitive["indices"])) // 3
            vmin = vertex_alpha_min(doc, blob, primitive)

            # ABR rates 1-3 (additive / subtractive / quarter-additive)
            # composite by EQUATION, so they blend whatever their alpha holds.
            name = material.get("name", "")
            equation = any(f"__st{rate}" in name for rate in (1, 2, 3))
            bucket = (mode, klass, "vtx-alpha" if vmin < 0.999 else "vtx-opaque", equation)
            counts[bucket] += 1
            tri_counts[bucket] += tris

            if (mode == "BLEND" and not equation
                    and klass in ("binary", "opaque", "no-texture") and vmin >= 0.999):
                files_with_pointless_blend.add(path.name)

    print(f"{'alphaMode':<8} {'texture alpha':<14} {'vertex':<10} {'abr':<10} {'prims':>7} {'tris':>9}")
    print("-" * 64)
    for bucket in sorted(counts, key=lambda b: -tri_counts[b]):
        mode, klass, vtx, equation = bucket
        abr = "equation" if equation else "-"
        print(f"{mode:<8} {klass:<14} {vtx:<10} {abr:<10} {counts[bucket]:>7} {tri_counts[bucket]:>9}")

    unjustified = sum(
        t for b, t in tri_counts.items()
        if b[0] == "BLEND" and not b[3] and b[1] in ("binary", "opaque", "no-texture") and b[2] == "vtx-opaque"
    )
    equation_blend = sum(t for b, t in tri_counts.items() if b[0] == "BLEND" and b[3])
    alpha_blend = sum(
        t for b, t in tri_counts.items()
        if b[0] == "BLEND" and not b[3] and (b[1] == "graduated" or b[2] == "vtx-alpha")
    )
    print()
    print(f"BLEND, additive/subtractive equation (must blend):            {equation_blend}")
    print(f"BLEND, genuine partial alpha (must blend):                    {alpha_blend}")
    print(f"BLEND with NO reason to blend (depth write lost for nothing): {unjustified}")
    print(f"Files affected: {len(files_with_pointless_blend)} / {len(paths)}")


def detail(path):
    doc, blob = load_glb(path)
    materials = doc.get("materials", [])
    print(f"{path.name}: {len(materials)} materials, {len(doc.get('meshes', []))} meshes\n")

    header = f"{'mesh':<28} {'material':<34} {'mode':<7} {'tris':>5} {'texalpha':<10} {'vtxA':>5}  plane"
    print(header)
    print("-" * len(header))

    for _, mesh_name, primitive in iter_primitives(doc):
        material_index = primitive.get("material")
        material = materials[material_index] if material_index is not None else {}
        mode = material.get("alphaMode", "OPAQUE")
        klass, hist = texture_alpha_class(doc, blob, material)

        positions = read_accessor(doc, blob, primitive["attributes"]["POSITION"])
        indices = read_accessor(doc, blob, primitive["indices"]).astype(np.int64).reshape(-1, 3)
        tri = positions[indices[0]]
        normal = np.cross(tri[1] - tri[0], tri[2] - tri[0])
        length = np.linalg.norm(normal)
        normal = normal / length if length > 1e-9 else normal
        offset = float(np.dot(normal, tri[0]))
        vmin = vertex_alpha_min(doc, blob, primitive)

        plane = f"n=({normal[0]:+.2f},{normal[1]:+.2f},{normal[2]:+.2f}) d={offset:+8.2f}"
        print(
            f"{mesh_name:<28} {material.get('name', '?'):<34} {mode:<7} "
            f"{len(indices):>5} {klass:<10} {vmin:>5.2f}  {plane}  "
            f"a0={hist[0]} apart={hist[1]} a255={hist[2]}"
        )


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("target", type=pathlib.Path)
    parser.add_argument("--detail", action="store_true", help="per-primitive dump for a single file")
    args = parser.parse_args()

    if args.detail or args.target.is_file():
        detail(args.target)
        return

    paths = sorted(args.target.rglob("*.glb"))
    if not paths:
        print(f"no .glb under {args.target}")
        return
    census(paths)


if __name__ == "__main__":
    main()
