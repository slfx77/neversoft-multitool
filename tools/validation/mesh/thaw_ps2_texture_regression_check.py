#!/usr/bin/env python3
"""THAW PS2 texture-regression checker.

Reproduces the 2026-07-15 investigation that convicted commit 884d018's
MASK-cutoff change (AlphaRef=1 always-pass GS default exported as a glTF
MASK cutoff of 1/128) of the "textures on meshes are largely corrupted"
report. Given two or more built CLI versions it:

  1. extracts DATAP.WAD with every CLI and hash-compares the trees
     (extraction parity — the WAD holds the 332 loose .skin.ps2/.tex.ps2);
  2. converts the extracted models/ tree with every CLI (`mesh` command);
  3. compares the GLBs pairwise against the FIRST (baseline) CLI:
       - embedded texture pixels, separating RGB changes from pure
         alpha-domain rescales (the intentional 884d018 alpha x255/128 fix);
       - material->texture joins;
       - material alpha semantics (alphaMode + alphaCutoff) — the axis on
         which the real regression lives: MASK@0.5(default) -> MASK@~1/128
         renders engine-blended semi-transparent overlays fully opaque;
  4. prints a verdict table.

Usage:
  python tools/validation/mesh/thaw_ps2_texture_regression_check.py \
      --wad "<path>/DATAP.WAD" \
      --cli v120=<worktree-v120>/src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.exe \
      --cli current=src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.exe \
      [--out TestOutput/thaw_regression_check] [--skip-extract]

The first --cli is the baseline. Requires Pillow (pip install Pillow).
"""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import struct
import subprocess
import sys
from collections import Counter

try:
    from PIL import Image
except ImportError:  # pragma: no cover
    print("ERROR: Pillow is required (pip install Pillow)", file=sys.stderr)
    sys.exit(2)


# ---------------------------------------------------------------- helpers


def run_cli(cli: str, args: list[str]) -> None:
    proc = subprocess.run([cli, *args], capture_output=True, text=True)
    if proc.returncode != 0:
        tail = "\n".join((proc.stdout + proc.stderr).splitlines()[-4:])
        raise RuntimeError(f"{os.path.basename(cli)} {' '.join(args[:2])} failed:\n{tail}")


def tree_hashes(root: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for dirpath, _, files in os.walk(root):
        for name in files:
            path = os.path.join(dirpath, name)
            rel = os.path.relpath(path, root).replace(os.sep, "/").lower()
            digest = hashlib.sha1()
            with open(path, "rb") as handle:
                for chunk in iter(lambda: handle.read(1 << 20), b""):
                    digest.update(chunk)
            out[rel] = digest.hexdigest()
    return out


def read_glb(path: str):
    with open(path, "rb") as handle:
        handle.read(12)
        json_len, _ = struct.unpack("<II", handle.read(8))
        doc = json.loads(handle.read(json_len).decode("utf-8").rstrip("\x00"))
        rest = handle.read()
        payload = b""
        if len(rest) >= 8:
            bin_len, _ = struct.unpack("<II", rest[:8])
            payload = rest[8 : 8 + bin_len]
    return doc, payload


def glb_images(doc, payload) -> list[tuple[str, bytes]]:
    images = []
    for index, image in enumerate(doc.get("images", [])):
        view = doc["bufferViews"][image["bufferView"]]
        offset = view.get("byteOffset", 0)
        images.append((image.get("name", f"img{index}"), payload[offset : offset + view["byteLength"]]))
    return images


def glb_material_semantics(doc) -> dict[str, tuple]:
    """material name -> (texture image name, alphaMode, alphaCutoff)."""
    result = {}
    for material in doc.get("materials", []):
        texture_name = None
        base = material.get("pbrMetallicRoughness", {}).get("baseColorTexture")
        if base is not None:
            source = doc["textures"][base["index"]].get("source")
            if source is not None:
                texture_name = doc["images"][source].get("name", f"img{source}")
        mode = material.get("alphaMode", "OPAQUE")
        cutoff = round(material.get("alphaCutoff", 0.5), 4) if mode == "MASK" else None
        result[material.get("name", "?")] = (texture_name, mode, cutoff)
    return result


def rgb_identical(a_png: bytes, b_png: bytes) -> bool:
    a = Image.open(io.BytesIO(a_png)).convert("RGBA")
    b = Image.open(io.BytesIO(b_png)).convert("RGBA")
    if a.size != b.size:
        return False
    a_bytes, b_bytes = a.tobytes(), b.tobytes()
    return all(a_bytes[i] == b_bytes[i] for i in range(len(a_bytes)) if i % 4 != 3)


# ---------------------------------------------------------------- pipeline


def extract_stage(names, clis, wad, out_dir, skip_extract):
    trees = {}
    for name in names:
        target = os.path.join(out_dir, f"wad_{name}")
        if not (skip_extract and os.path.isdir(target)):
            print(f"[extract] {name}: DATAP.WAD -> {target}")
            run_cli(clis[name], ["archive", wad, "-o", target])
        trees[name] = target
    baseline_name = names[0]
    baseline_hashes = tree_hashes(trees[baseline_name])
    parity = {}
    for name in names[1:]:
        other = tree_hashes(trees[name])
        mismatched = sum(
            1 for key, value in baseline_hashes.items() if other.get(key) != value
        ) + sum(1 for key in other if key not in baseline_hashes)
        parity[name] = (len(baseline_hashes), mismatched)
    return trees, parity


def convert_stage(names, clis, trees, out_dir, skip_extract):
    glb_dirs = {}
    for name in names:
        models = os.path.join(trees[name], "DATAP", "models")
        target = os.path.join(out_dir, f"glb_{name}")
        if not (skip_extract and os.path.isdir(target)):
            print(f"[convert] {name}: models/ -> {target}")
            run_cli(clis[name], ["mesh", models, "-o", target])
        glb_dirs[name] = target
    return glb_dirs


def compare_stage(baseline_dir: str, other_dir: str):
    stats = Counter()
    harmful_files = []
    cutoff_transitions = Counter()
    glbs = sorted(
        set(os.listdir(baseline_dir)) & set(os.listdir(other_dir))
    )
    for glb_name in (g for g in glbs if g.endswith(".glb")):
        doc_a, bin_a = read_glb(os.path.join(baseline_dir, glb_name))
        doc_b, bin_b = read_glb(os.path.join(other_dir, glb_name))

        images_a, images_b = glb_images(doc_a, bin_a), glb_images(doc_b, bin_b)
        if len(images_a) != len(images_b):
            stats["texcount-diff"] += 1
        else:
            for (_, data_a), (_, data_b) in zip(images_a, images_b):
                if data_a == data_b:
                    continue
                if rgb_identical(data_a, data_b):
                    stats["alpha-only-pixel-diff"] += 1
                else:
                    stats["RGB-PIXEL-DIFF"] += 1

        mats_a, mats_b = glb_material_semantics(doc_a), glb_material_semantics(doc_b)
        harmful_here = 0
        for key in set(mats_a) & set(mats_b):
            tex_a, mode_a, cut_a = mats_a[key]
            tex_b, mode_b, cut_b = mats_b[key]
            if tex_a != tex_b:
                stats["JOIN-DIFF"] += 1
            if (mode_a, cut_a) != (mode_b, cut_b):
                cutoff_transitions[((mode_a, cut_a), (mode_b, cut_b))] += 1
                # The convicted 884d018 class: default-0.5 MASK becoming a
                # near-zero pass-through cutoff (AREF=1 -> 1/128).
                if (
                    mode_a == "MASK"
                    and mode_b == "MASK"
                    and cut_a is not None
                    and cut_b is not None
                    and abs(cut_a - 0.5) < 1e-6
                    and cut_b < 0.02
                ):
                    harmful_here += 1
        if harmful_here:
            harmful_files.append((glb_name, harmful_here))
            stats["harmful-materials"] += harmful_here
    return stats, harmful_files, cutoff_transitions, len(glbs)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--wad", required=True, help="Path to THAW PS2 DATAP.WAD")
    parser.add_argument(
        "--cli",
        action="append",
        required=True,
        metavar="NAME=PATH",
        help="Named CLI build (first is the baseline); repeatable",
    )
    parser.add_argument("--out", default=os.path.join("TestOutput", "thaw_regression_check"))
    parser.add_argument(
        "--skip-extract",
        action="store_true",
        help="Reuse existing extraction/conversion directories when present",
    )
    args = parser.parse_args()

    clis: dict[str, str] = {}
    names: list[str] = []
    for spec in args.cli:
        name, _, path = spec.partition("=")
        if not path:
            parser.error(f"--cli must be NAME=PATH, got: {spec}")
        clis[name] = path
        names.append(name)
    if len(names) < 2:
        parser.error("need at least two --cli entries (baseline + candidate)")

    os.makedirs(args.out, exist_ok=True)

    trees, parity = extract_stage(names, clis, args.wad, args.out, args.skip_extract)
    glb_dirs = convert_stage(names, clis, trees, args.out, args.skip_extract)

    print("\n================ VERDICT ================")
    print(f"baseline: {names[0]}")
    print("\n-- extraction parity (WAD -> files) --")
    for name, (total, mismatched) in parity.items():
        flag = "OK (byte-identical)" if mismatched == 0 else f"MISMATCH ({mismatched} files)"
        print(f"  {names[0]} vs {name}: {total} files, {flag}")

    for name in names[1:]:
        stats, harmful, transitions, glb_count = compare_stage(glb_dirs[names[0]], glb_dirs[name])
        print(f"\n-- converter comparison: {names[0]} -> {name} ({glb_count} GLBs) --")
        print(f"  RGB pixel diffs:            {stats['RGB-PIXEL-DIFF']}")
        print(f"  alpha-only pixel diffs:     {stats['alpha-only-pixel-diff']} (intentional 884d018 rescale)")
        print(f"  material->texture joins:    {stats['JOIN-DIFF']} differ")
        print(f"  texture-count diffs:        {stats['texcount-diff']}")
        print(
            f"  HARMFUL cutoff transitions: {stats['harmful-materials']} materials "
            f"in {len(harmful)} files (MASK@0.5 -> MASK@<0.02; engine-blended "
            "overlays now render fully opaque)"
        )
        if transitions:
            print("  alpha-semantic transitions (top 10):")
            for (before, after), count in transitions.most_common(10):
                print(f"    {before} -> {after}: {count}")
        if harmful:
            print("  worst files:")
            for glb_name, count in sorted(harmful, key=lambda item: -item[1])[:10]:
                print(f"    {glb_name}: {count}")

    print(
        "\nInterpretation: if extraction parity is OK, RGB/join diffs are 0, and the"
        "\nonly signal is the harmful MASK@0.5 -> MASK@~1/128 class, the corruption is"
        "\nthe 884d018 AlphaRef=1 (GS always-pass default) cutoff export in"
        "\nPs2MaterialWriter.ApplyPs2Material — not the archive readers or texture decode."
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
