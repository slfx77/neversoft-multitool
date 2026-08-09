#!/usr/bin/env python3
"""Extract per-material facts from GLB trees and compare two snapshots.

The historical material pass census is no longer retained. This tool therefore
reports every material change without deciding whether it is semantically
expected. Use ``diff --fail-on-diff`` as a reproducible exact-equality gate.
"""
import argparse
import csv
import hashlib
import json
import struct
from pathlib import Path


def read_glb(path: Path):
    with path.open("rb") as f:
        magic, _ver, _length = struct.unpack("<III", f.read(12))
        if magic != 0x46546C67:
            return None, None
        jlen, jtype = struct.unpack("<II", f.read(8))
        if jtype != 0x4E4F534A:
            return None, None
        doc = json.loads(f.read(jlen).decode("utf-8").rstrip("\x00"))
        bin_data = b""
        rest = f.read(8)
        if len(rest) == 8:
            blen, btype = struct.unpack("<II", rest)
            if btype == 0x004E4942:
                bin_data = f.read(blen)
        return doc, bin_data


def image_hash(doc, bin_data, image_index):
    try:
        image = doc["images"][image_index]
        bv = doc["bufferViews"][image["bufferView"]]
        off = bv.get("byteOffset", 0)
        data = bin_data[off:off + bv["byteLength"]]
        return hashlib.sha1(data).hexdigest()[:16]
    except (KeyError, IndexError):
        return ""


def material_rows(glb_path: Path, rel: str):
    doc, bin_data = read_glb(glb_path)
    if doc is None:
        return
    textures = doc.get("textures", [])
    for i, mat in enumerate(doc.get("materials", [])):
        tex_hash = ""
        pbr = mat.get("pbrMetallicRoughness", {})
        bct = pbr.get("baseColorTexture")
        if bct is not None and bct.get("index") is not None:
            t = textures[bct["index"]]
            if "source" in t:
                tex_hash = image_hash(doc, bin_data, t["source"])
        yield {
            "file": rel,
            "matIndex": i,
            "matName": mat.get("name", ""),
            "alphaMode": mat.get("alphaMode", "OPAQUE"),
            "alphaCutoff": mat.get("alphaCutoff", ""),
            "doubleSided": mat.get("doubleSided", False),
            "texSha1": tex_hash,
        }


def cmd_extract(tree: Path, out_csv: Path):
    rows = 0
    with out_csv.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=[
            "file", "matIndex", "matName", "alphaMode", "alphaCutoff", "doubleSided", "texSha1"])
        w.writeheader()
        for glb in sorted(tree.rglob("*.glb")):
            rel = glb.relative_to(tree).as_posix()
            for row in material_rows(glb, rel):
                w.writerow(row)
                rows += 1
    print(f"extracted {rows} material rows -> {out_csv}")


def cmd_diff(baseline_csv: Path, after_csv: Path, fail_on_diff: bool):
    def load(p):
        with p.open(encoding="utf-8") as f:
            return {(r["file"], r["matIndex"]): r for r in csv.DictReader(f)}

    base, after = load(baseline_csv), load(after_csv)
    modified = []
    for key in sorted(base.keys() & after.keys()):
        b, a = base[key], after[key]
        delta = [k for k in ("alphaMode", "alphaCutoff", "doubleSided", "texSha1") if b[k] != a[k]]
        if delta:
            modified.append((key, "+".join(delta)))
    added = sorted(after.keys() - base.keys())
    removed = sorted(base.keys() - after.keys())

    print(f"baseline rows: {len(base)}   after rows: {len(after)}")
    print(f"modified: {len(modified)}   added: {len(added)}   removed: {len(removed)}")
    for key, delta in modified[:40]:
        print(f"  MODIFIED {key[0]} mat#{key[1]}: {delta}")
    for key in added[:40]:
        print(f"  ADDED    {key[0]} mat#{key[1]}")
    for key in removed[:40]:
        print(f"  REMOVED  {key[0]} mat#{key[1]}")

    has_diff = bool(modified or added or removed)
    return 1 if fail_on_diff and has_diff else 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Extract per-material GLB facts and compare snapshots.",
        epilog=("The retired historical pass census is not available as an "
                "allowlist; diff reports all changes."),
    )
    commands = parser.add_subparsers(dest="command", required=True)

    extract = commands.add_parser("extract", help="extract material facts from a GLB tree")
    extract.add_argument("glb_tree", type=Path)
    extract.add_argument("out_csv", type=Path)

    diff = commands.add_parser("diff", help="compare two extracted CSV snapshots")
    diff.add_argument("baseline_csv", type=Path)
    diff.add_argument("after_csv", type=Path)
    diff.add_argument(
        "--fail-on-diff",
        action="store_true",
        help="exit 1 when any material is modified, added, or removed",
    )
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    if args.command == "extract":
        cmd_extract(args.glb_tree, args.out_csv)
        return 0
    return cmd_diff(args.baseline_csv, args.after_csv, args.fail_on_diff)


if __name__ == "__main__":
    raise SystemExit(main())
