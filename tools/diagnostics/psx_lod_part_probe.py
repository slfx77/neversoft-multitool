#!/usr/bin/env python3
"""Analyze a psx-mesh-dump JSON for duplicate/overlapping part meshes.

Prints the object table, per-mesh LOD header fields, and pairwise world-space
bounding-box overlap between meshes to identify duplicate part surfaces (e.g.
the THPS1-proto hawk twin arm meshes). Overlap is scored as
intersection-volume / min(volume-a, volume-b), so a mesh fully inside another
scores 1.0.

Usage:
  dotnet run --project src/NeversoftMultitool --framework net10.0 -- \
    psx-mesh-dump <file.psx> --json dump.json
  python tools/diagnostics/psx_lod_part_probe.py dump.json [--threshold 0.5]
"""

from __future__ import annotations

import argparse
import json


def mesh_world_bbox(mesh, obj_by_index):
    del obj_by_index  # dump vertices already carry attachment-resolved world pos
    xs, ys, zs = [], [], []
    for v in mesh["Vertices"]:
        if not v.get("AttachmentResolved", True):
            continue
        w = v["WorldPosition"]
        xs.append(w["X"])
        ys.append(w["Y"])
        zs.append(w["Z"])
    if not xs:
        return None
    return (min(xs), min(ys), min(zs), max(xs), max(ys), max(zs))


def overlap_score(a, b):
    ix = min(a[3], b[3]) - max(a[0], b[0])
    iy = min(a[4], b[4]) - max(a[1], b[1])
    iz = min(a[5], b[5]) - max(a[2], b[2])
    if ix <= 0 or iy <= 0 or iz <= 0:
        return 0.0
    inter = ix * iy * iz
    va = (a[3] - a[0]) * (a[4] - a[1]) * (a[5] - a[2])
    vb = (b[3] - b[0]) * (b[4] - b[1]) * (b[5] - b[2])
    denom = min(va, vb)
    return inter / denom if denom > 0 else 0.0


def print_stitch_trace(d, mesh_indices):
    att = {a["AttachmentIndex"]: a for a in d["Attachments"]}
    srcs = {}
    for a in d["Attachments"]:
        srcs.setdefault(a["MeshIndex"], []).append(a["AttachmentIndex"])
    print(f"total attachments: {len(att)}")
    print("source index ranges by mesh: "
          + ", ".join(f"m{k}:[{min(v)}..{max(v)}]x{len(v)}"
                      for k, v in sorted(srcs.items())))
    for mi in mesh_indices:
        m = d["Meshes"][mi]
        print(f"\n--- mesh {mi} stitched refs ---")
        for v in m["Vertices"]:
            if v["Type"] != 2:
                continue
            ai = v.get("AttachmentTargetIndex")
            src = att.get(ai)
            w = v["WorldPosition"]
            src_mesh = src["MeshIndex"] if src else None
            print(f"  v{v['VertexIndex']:3d} rawY(attIdx)={v['RawY']:4d} "
                  f"tgt={ai} -> srcMesh={src_mesh}  "
                  f"world=({w['X']:7.2f},{w['Y']:7.2f},{w['Z']:7.2f}) "
                  f"resolved={v['AttachmentResolved']}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("dump", help="psx-mesh-dump --json output")
    ap.add_argument("--threshold", type=float, default=0.5)
    ap.add_argument("--stitch", type=int, nargs="*",
                    help="trace stitched-ref resolution for these mesh indices")
    ap.add_argument("--faces", type=int, nargs="*",
                    help="dump face connectivity/texture summary for these mesh indices")
    args = ap.parse_args()

    d = json.load(open(args.dump))

    if args.stitch:
        print_stitch_trace(d, args.stitch)
        return

    if args.faces:
        for mi in args.faces:
            m = d["Meshes"][mi]
            vtypes = {v["VertexIndex"]: v["Type"] for v in m["Vertices"]}
            print(f"\n--- mesh {mi} faces ({len(m['Faces'])}) ---")
            for f in m["Faces"]:
                ys = [w["Y"] for w in f["ResolvedWorldVertices"]]
                idx = f["Indices"][: 4 if f["IsQuad"] else 3]
                marked = ",".join(
                    f"{i}{'*' if vtypes.get(i) == 2 else ''}" for i in idx)
                print(f"  f{f['FaceIndex']:3d} flags=0x{f['Flags']:04X} "
                      f"tex=0x{f['TextureHash']:08X} v=[{marked}]  "
                      f"worldY {min(ys):7.2f}..{max(ys):7.2f}")
        return
    objs = {o["ObjectIndex"]: o for o in d["Objects"]}
    meshes = d["Meshes"]

    print(f"# {d['FileName']}  v{d['Version']}  HasHierarchy={d['HasHierarchy']}  "
          f"objects={len(objs)}  meshes={len(meshes)}")

    print("\n## Objects (index, meshIndex, parent, position)")
    for o in d["Objects"]:
        p = o["Position"]
        print(f"  obj {o['ObjectIndex']:2d} -> mesh {o['MeshIndex']:2d}  "
              f"parent={o['ParentIndex']:2d}  flags=0x{o['Flags']:04X}  "
              f"pos=({p['X']:8.3f},{p['Y']:8.3f},{p['Z']:8.3f})")

    ref_counts = {}
    for o in d["Objects"]:
        ref_counts[o["MeshIndex"]] = ref_counts.get(o["MeshIndex"], 0) + 1

    print("\n## Meshes (LOD fields + counts)")
    boxes = {}
    for m in meshes:
        bbox = mesh_world_bbox(m, objs)
        boxes[m["MeshIndex"]] = bbox
        refs = ref_counts.get(m["MeshIndex"], 0)
        lod_next = m["LodNextMeshIndex"]
        lod_s = "  root" if lod_next == 0xFFFF else f"->{lod_next:3d}"
        bb = (f"bbox=({bbox[0]:7.2f},{bbox[1]:7.2f},{bbox[2]:7.2f})..("
              f"{bbox[3]:7.2f},{bbox[4]:7.2f},{bbox[5]:7.2f})" if bbox else "bbox=EMPTY")
        print(f"  mesh {m['MeshIndex']:2d}  obj={m['ObjectIndex']:2d} refs={refs}  "
              f"verts={m['VertexCount']:4d} faces={m['FaceCount']:4d} "
              f"(raw {m['RawFaceCount']:4d})  zMax={m['LodDepth']:6d} "
              f"NextLOD={lod_s}  st_src={m['StitchSourceCount']:3d} "
              f"st_ref={m['StitchedReferenceCount']:3d}  {bb}")

    print(f"\n## Overlapping mesh pairs (score >= {args.threshold})")
    ids = sorted(boxes)
    found = False
    for i, a in enumerate(ids):
        for b in ids[i + 1:]:
            if boxes[a] is None or boxes[b] is None:
                continue
            s = overlap_score(boxes[a], boxes[b])
            if s >= args.threshold:
                found = True
                ma = meshes[a]
                mb = meshes[b]
                print(f"  mesh {a:2d} (v{ma['VertexCount']:4d}/f{ma['FaceCount']:4d}) "
                      f"~ mesh {b:2d} (v{mb['VertexCount']:4d}/f{mb['FaceCount']:4d})  "
                      f"overlap={s:.2f}")
    if not found:
        print("  none")


if __name__ == "__main__":
    main()
