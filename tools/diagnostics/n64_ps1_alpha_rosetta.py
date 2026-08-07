#!/usr/bin/env python3
"""Join an N64 model's materials to its PS1 sibling's by texture id.

The N64 ports reuse the PS1 texture identifiers verbatim, so a level converted
from both discs gives a per-texture oracle for what the PS1 build believed
about transparency -- including the CLUT STP markers the N64 art conversion
threw away.

The question this answers: when an N64 face carries the PS1 ABR rate-0
"average" blend bit but its art holds no alpha at all, is that face actually
translucent (glass, water) or actually solid? The PS1 bake knows, because it
still has the markers.

    python n64_ps1_alpha_rosetta.py <n64.glb> <ps1.glb>

Reports a cross-tab of (N64 ABR rate, N64 art alpha) against (PS1 baked alpha),
plus the individual textures behind each cell.
"""

from __future__ import annotations

import argparse
import collections
import io
import pathlib
import re
import sys

import numpy as np
from PIL import Image

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent))
from glb_accessor_reader import load_glb, read_accessor  # noqa: E402

NAME = re.compile(r"(?:tex_|psxtxt_)([0-9A-Fa-f]{8})(?:__st([0-3]))?")


def alpha_profile(doc, blob, material):
    pbr = material.get("pbrMetallicRoughness", {})
    ref = pbr.get("baseColorTexture")
    if ref is None:
        return "no-texture"
    texture = doc["textures"][ref["index"]]
    image = doc["images"][texture["source"]]
    view = doc["bufferViews"][image["bufferView"]]
    start = view.get("byteOffset", 0)
    with Image.open(io.BytesIO(blob[start:start + view["byteLength"]])) as im:
        if im.mode != "RGBA":
            return "opaque"
        alpha = np.asarray(im)[:, :, 3]
    zero = int((alpha == 0).sum())
    partial = int(((alpha > 0) & (alpha < 255)).sum())
    if partial > alpha.size * 0.05:
        return "translucent"
    if zero:
        return "cutout"
    return "opaque"


def index(path):
    """texture id -> {rate: (profile, triangles)} for one export."""
    doc, blob = load_glb(path)
    materials = doc.get("materials", [])
    triangles = collections.Counter()
    for mesh in doc.get("meshes", []):
        for primitive in mesh.get("primitives", []):
            if primitive.get("material") is not None:
                triangles[primitive["material"]] += len(
                    read_accessor(doc, blob, primitive["indices"])) // 3

    out = collections.defaultdict(dict)
    for i, material in enumerate(materials):
        match = NAME.match(material.get("name", ""))
        if not match:
            continue
        key = match.group(1).lower()
        rate = int(match.group(2)) if match.group(2) else None
        out[key][rate] = (alpha_profile(doc, blob, material), triangles[i])
    return out


def texture_ids(indexed):
    return set(indexed)


def auto_pairs(n64_dir, ps1_dir, minimum_overlap=0.75):
    """Match each N64 model to the PS1 file sharing the most texture ids.

    The ports reuse the identifiers verbatim, so the match is usually exact
    (1.00 overlap); anything below `minimum_overlap` is not a sibling.
    """
    ps1 = {p: index(p) for p in sorted(ps1_dir.glob("*.glb"))}
    for path in sorted(n64_dir.glob("*.glb")):
        n64 = index(path)
        if len(n64) < 20:
            continue
        best, best_shared = None, 0
        for candidate, candidate_index in ps1.items():
            shared = len(texture_ids(n64) & texture_ids(candidate_index))
            if shared > best_shared:
                best, best_shared = candidate, shared
        if best is not None and best_shared >= minimum_overlap * len(n64):
            yield path, n64, ps1[best], best


def accumulate(n64, ps1, cross, examples):
    for key in sorted(set(n64) & set(ps1)):
        for rate, (profile, tris) in n64[key].items():
            # The PS1 side may carry the same texture at several ABR rates;
            # compare against the same rate, falling back to whatever it has.
            ps1_entry = ps1[key].get(rate) or next(iter(ps1[key].values()))
            cell = (f"st{rate}" if rate is not None else "opaque-face", profile, ps1_entry[0])
            cross[cell] += tris
            if len(examples[cell]) < 4 and key not in examples[cell]:
                examples[cell].append(key)


def report(cross, examples):
    print(f"\n{'N64 ABR':<12} {'N64 art':<12} {'PS1 baked':<12} {'tris':>8}  examples")
    print("-" * 74)
    for cell in sorted(cross, key=lambda c: (c[0], -cross[c])):
        abr, n64_profile, ps1_profile = cell
        print(f"{abr:<12} {n64_profile:<12} {ps1_profile:<12} {cross[cell]:>8}  "
              f"{', '.join(examples[cell])}")

    # The decisive question: an N64 face carries ABR rate 0 and its art holds
    # no alpha at all. Did the PS1 build, which still has the CLUT markers,
    # bake real translucency there?
    print("\nABR-0 faces whose N64 art has NO alpha (the glass test):")
    print(f"  PS1 baked TRANSLUCENT: {cross[('st0', 'opaque', 'translucent')]:>7} tris")
    print(f"  PS1 left solid:        {cross[('st0', 'opaque', 'opaque')]:>7} tris")
    print("\nABR-0 faces whose N64 art HAS holes (the cutout test):")
    print(f"  PS1 baked TRANSLUCENT: {cross[('st0', 'cutout', 'translucent')]:>7} tris")
    print(f"  PS1 kept it a cutout:  {cross[('st0', 'cutout', 'cutout')]:>7} tris")
    print("\nFaces with NO semi bit (the control):")
    print(f"  PS1 opaque:            {cross[('opaque-face', 'opaque', 'opaque')]:>7} tris")
    print(f"  PS1 translucent:       {cross[('opaque-face', 'opaque', 'translucent')]:>7} tris")


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("n64", type=pathlib.Path)
    parser.add_argument("ps1", type=pathlib.Path)
    args = parser.parse_args()

    cross = collections.Counter()
    examples = collections.defaultdict(list)

    if args.n64.is_dir():
        for path, n64, ps1, sibling in auto_pairs(args.n64, args.ps1):
            print(f"{path.name} <-> {sibling.name}")
            accumulate(n64, ps1, cross, examples)
    else:
        n64, ps1 = index(args.n64), index(args.ps1)
        print(f"{len(n64)} N64 textures, {len(ps1)} PS1 textures, "
              f"{len(set(n64) & set(ps1))} shared")
        accumulate(n64, ps1, cross, examples)

    report(cross, examples)


if __name__ == "__main__":
    main()
