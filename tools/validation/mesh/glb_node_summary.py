#!/usr/bin/env python3
"""Dump GLB node/mesh summary: node names, mesh names, per-mesh triangle counts.

python tools/validation/mesh/glb_node_summary.py <file.glb> [name-substring]
"""
import json
import struct
import sys


def load(path):
    with open(path, "rb") as f:
        f.read(12)
        clen, _ = struct.unpack("<II", f.read(8))
        return json.loads(f.read(clen).decode("utf-8").rstrip("\x00"))


def tri_count(d, mesh):
    n = 0
    for prim in mesh.get("primitives", []):
        idx = prim.get("indices")
        if idx is None:
            n += d["accessors"][prim["attributes"]["POSITION"]]["count"] // 3
        else:
            n += d["accessors"][idx]["count"] // 3
    return n


def main():
    d = load(sys.argv[1])
    needle = sys.argv[2].lower() if len(sys.argv) > 2 else None
    meshes = d.get("meshes", [])
    tris = [tri_count(d, m) for m in meshes]
    print(f"{sys.argv[1]}: nodes={len(d.get('nodes', []))} meshes={len(meshes)} "
          f"materials={len(d.get('materials', []))} textures={len(d.get('textures', []))} "
          f"triangles={sum(tris)}")
    counts = {}
    for i, node in enumerate(d.get("nodes", [])):
        name = node.get("name", "")
        if needle and needle not in name.lower():
            continue
        mi = node.get("mesh")
        t = tris[mi] if mi is not None else 0
        counts[name] = counts.get(name, 0) + 1
        print(f"  node[{i}] {name!r} mesh={meshes[mi]['name'] if mi is not None else None} tris={t}")


if __name__ == "__main__":
    main()
