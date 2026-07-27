#!/usr/bin/env python3
"""glb_material_diff_sweep.py — extract per-material facts from GLB trees and diff them.

Phase-1 regression proof for the Xbox/PC blend fixes: convert a corpus to GLB
before and after the converter change, extract every material's (alphaMode,
alphaCutoff, doubleSided, texture pixel SHA1) and diff. Acceptance = every
changed material is in the expected set (pass-0 blend mode 1-4 or an eligible
multi-pass overlay per tools/XbxPassSurvey CSV); everything else identical.

Usage:
  extract:  python glb_material_diff_sweep.py extract <glb_tree> <out.csv>
  diff:     python glb_material_diff_sweep.py diff <baseline.csv> <after.csv> [--expected <passes.csv>]

The --expected file is the XbxPassSurvey passes.csv; a changed material is
"expected" when its material checksum appears there with pass0 blendMode 1-4 or
any pass-k (k>=1) row with texChecksum != 0 and blendMode 1-6.
"""
import csv
import hashlib
import json
import struct
import sys
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


def load_expected(passes_csv: Path):
    """Material NAMES whose change is expected (survey: pass0 mode 1-4, or an
    eligible pass-k overlay). Matches on the converter's material naming, which
    embeds the checksum when unresolved (mat_XXXXXXXX) or the QbKey name."""
    expected = set()
    with passes_csv.open(encoding="utf-8") as f:
        for row in csv.DictReader(f):
            mode = int(row["blendMode"])
            pidx = int(row["passIndex"])
            tex = int(row["texChecksum"], 16)
            if (pidx == 0 and 1 <= mode <= 4) or (pidx >= 1 and tex != 0 and 1 <= mode <= 6):
                expected.add(row["matChecksum"].upper())
    return expected


def cmd_diff(baseline_csv: Path, after_csv: Path, passes_csv: Path | None):
    def load(p):
        with p.open(encoding="utf-8") as f:
            return {(r["file"], r["matIndex"]): r for r in csv.DictReader(f)}

    base, after = load(baseline_csv), load(after_csv)
    expected = load_expected(passes_csv) if passes_csv else None

    changed, unexpected = [], []
    for key, b in base.items():
        a = after.get(key)
        if a is None:
            changed.append((key, "REMOVED"))
            continue
        delta = [k for k in ("alphaMode", "alphaCutoff", "doubleSided", "texSha1") if b[k] != a[k]]
        if not delta:
            continue
        changed.append((key, "+".join(delta)))
        if expected is not None:
            # Material names carry the checksum when unresolved; also accept
            # the __add/__sub/__mp suffixes the fix appends to texture names.
            name = b["matName"].upper()
            if not any(chk in name for chk in expected) and "__" not in a["matName"]:
                unexpected.append((key, delta, b, a))
    added = [k for k in after if k not in base]

    print(f"baseline rows: {len(base)}   after rows: {len(after)}")
    print(f"changed: {len(changed)}   added: {len(added)}   removed: "
          f"{sum(1 for _, d in changed if d == 'REMOVED')}")
    if expected is not None:
        print(f"UNEXPECTED changes (not in survey's expected set): {len(unexpected)}")
        for key, delta, b, a in unexpected[:40]:
            print(f"  {key[0]} mat#{key[1]} [{b['matName']}]: {delta} "
                  f"({b['alphaMode']}->{a['alphaMode']}, tex {b['texSha1'][:8]}->{a['texSha1'][:8]})")
        return 1 if unexpected else 0
    for key, d in changed[:40]:
        print(f"  {key[0]} mat#{key[1]}: {d}")
    return 0


def main():
    if len(sys.argv) < 4:
        print(__doc__)
        return 2
    cmd = sys.argv[1]
    if cmd == "extract":
        cmd_extract(Path(sys.argv[2]), Path(sys.argv[3]))
        return 0
    if cmd == "diff":
        passes = None
        if "--expected" in sys.argv:
            passes = Path(sys.argv[sys.argv.index("--expected") + 1])
        return cmd_diff(Path(sys.argv[2]), Path(sys.argv[3]), passes)
    print(__doc__)
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
