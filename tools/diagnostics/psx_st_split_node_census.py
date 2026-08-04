#!/usr/bin/env python3
"""Sizes the PSX semi-transparent per-face node split (defect D, 2026-08-03).

For every converted level GLB in the given directories, counts the BLEND
(semi-transparent) primitives and their triangles PER NODE INSTANCE — the
triangle sum is a strict UPPER BOUND on the extra nodes a per-face split
would emit (each PSX face is 1 quad = 2 triangles or 1 triangle, and every
placement instance already appears as its own node in the GLB). Skips sky
(sky__*), ghost (*__ghost) and axial-billboard meshes, which are excluded
from the split.

Usage: python tools/diagnostics/psx_st_split_node_census.py <glb-dir> [...]
"""
import json
import struct
import sys
from pathlib import Path


def load_glb_json(path):
    with open(path, "rb") as f:
        f.read(12)
        clen, _ = struct.unpack("<II", f.read(8))
        return json.loads(f.read(clen).decode().rstrip("\x00"))


def census(path):
    d = load_glb_json(path)
    materials = d.get("materials", [])
    meshes = d.get("meshes", [])
    accessors = d.get("accessors", [])
    blend = {
        i for i, m in enumerate(materials) if m.get("alphaMode") == "BLEND"
    }

    def mesh_st_stats(mesh):
        name = mesh.get("name", "")
        if name.startswith("sky__") or "__ghost" in name:
            return 0, 0
        extras = mesh.get("extras", {})
        if extras.get("neversoftAxialBillboard"):
            return 0, 0
        prims = tris = 0
        for p in mesh.get("primitives", []):
            if p.get("material") in blend and "indices" in p:
                prims += 1
                tris += accessors[p["indices"]]["count"] // 3
        return prims, tris

    per_mesh = [mesh_st_stats(m) for m in meshes]
    node_prims = node_tris = 0
    for n in d.get("nodes", []):
        if "mesh" in n:
            p, t = per_mesh[n["mesh"]]
            node_prims += p
            node_tris += t
    return node_prims, node_tris


def main():
    rows = []
    for arg in sys.argv[1:]:
        for glb in sorted(Path(arg).rglob("*.glb")):
            prims, tris = census(glb)
            rows.append((tris, prims, glb.name))
    rows.sort(reverse=True)
    print(f"{'maxExtraNodes':>13} {'stPrims':>8}  file")
    for tris, prims, name in rows:
        print(f"{tris:>13} {prims:>8}  {name}")
    if rows:
        print(f"\nworst: {rows[0][2]} at {rows[0][0]} (upper bound; faces <= tris)")


if __name__ == "__main__":
    main()
