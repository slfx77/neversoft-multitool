"""
Analyze GLB vertex positions to check geometry quality.
Reads accessor data from the binary buffer to compute bounding boxes,
vertex density, and detect outliers.

Usage:
  python tools/validation/mesh/analyze_glb_geometry.py <path.glb>
"""
import argparse
from collections import Counter
import json
import math
from pathlib import Path
import struct

def read_glb(path):
    with open(path, "rb") as f:
        magic, version, length = struct.unpack("<III", f.read(12))
        # JSON chunk
        json_len, json_type = struct.unpack("<II", f.read(8))
        json_data = json.loads(f.read(json_len).decode().rstrip("\x00"))
        # BIN chunk
        bin_len, bin_type = struct.unpack("<II", f.read(8))
        bin_data = f.read(bin_len)
    return json_data, bin_data

def get_accessor_data(d, bin_data, accessor_idx, component_type=5126):
    acc = d["accessors"][accessor_idx]
    bv = d["bufferViews"][acc["bufferView"]]
    offset = bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
    count = acc["count"]
    atype = acc["type"]

    components = {"SCALAR": 1, "VEC2": 2, "VEC3": 3, "VEC4": 4}[atype]

    if acc["componentType"] == 5126:  # FLOAT
        stride = bv.get("byteStride", components * 4)
        data = []
        for i in range(count):
            pos = offset + i * stride
            vals = struct.unpack_from(f"<{components}f", bin_data, pos)
            data.append(vals)
        return data
    elif acc["componentType"] == 5123:  # UNSIGNED_SHORT
        stride = bv.get("byteStride", components * 2)
        data = []
        for i in range(count):
            pos = offset + i * stride
            vals = struct.unpack_from(f"<{components}H", bin_data, pos)
            data.append(vals)
        return data
    elif acc["componentType"] == 5125:  # UNSIGNED_INT
        stride = bv.get("byteStride", components * 4)
        data = []
        for i in range(count):
            pos = offset + i * stride
            vals = struct.unpack_from(f"<{components}I", bin_data, pos)
            data.append(vals)
        return data
    return []

def analyze(path):
    d, bin_data = read_glb(path)
    meshes = d.get("meshes", [])
    materials = d.get("materials", [])

    print(f"=== Geometry Analysis: {Path(path).name} ===")
    print(f"Meshes: {len(meshes)}, Materials: {len(materials)}")

    all_positions = []

    for mi, mesh in enumerate(meshes):
        name = mesh.get("name", f"mesh_{mi}")
        mesh_positions = []
        mesh_tris = 0

        for prim in mesh.get("primitives", []):
            pos_idx = prim.get("attributes", {}).get("POSITION")
            idx_idx = prim.get("indices")

            if pos_idx is not None:
                positions = get_accessor_data(d, bin_data, pos_idx)
                mesh_positions.extend(positions)
                all_positions.extend(positions)

            if idx_idx is not None:
                acc = d["accessors"][idx_idx]
                mesh_tris += acc["count"] // 3

        if mesh_positions:
            xs = [p[0] for p in mesh_positions]
            ys = [p[1] for p in mesh_positions]
            zs = [p[2] for p in mesh_positions]

            min_x, max_x = min(xs), max(xs)
            min_y, max_y = min(ys), max(ys)
            min_z, max_z = min(zs), max(zs)

            size_x = max_x - min_x
            size_y = max_y - min_y
            size_z = max_z - min_z

            center_x = (min_x + max_x) / 2
            center_y = (min_y + max_y) / 2
            center_z = (min_z + max_z) / 2

            print(f"\n[{mi}] {name}  ({len(mesh_positions)} verts, {mesh_tris} tris)")
            print(f"  BBox: ({min_x:.1f}, {min_y:.1f}, {min_z:.1f}) to ({max_x:.1f}, {max_y:.1f}, {max_z:.1f})")
            print(f"  Size: {size_x:.1f} x {size_y:.1f} x {size_z:.1f}")
            print(f"  Center: ({center_x:.1f}, {center_y:.1f}, {center_z:.1f})")

            # Check for origin-centered geometry
            if abs(center_x) < 10 and abs(center_y) < 10 and abs(center_z) < 10:
                max_dim = max(size_x, size_y, size_z)
                if max_dim > 500:
                    print(f"  *** SUSPICIOUS: origin-centered, max dim = {max_dim:.1f}")

    # Overall stats
    if all_positions:
        xs = [p[0] for p in all_positions]
        ys = [p[1] for p in all_positions]
        zs = [p[2] for p in all_positions]

        print(f"\n=== Overall ({len(all_positions)} vertices) ===")
        print(f"  X: [{min(xs):.1f}, {max(xs):.1f}]  range={max(xs)-min(xs):.1f}")
        print(f"  Y: [{min(ys):.1f}, {max(ys):.1f}]  range={max(ys)-min(ys):.1f}")
        print(f"  Z: [{min(zs):.1f}, {max(zs):.1f}]  range={max(zs)-min(zs):.1f}")

        # Vertex density histogram (distance from center)
        center_x = sum(xs) / len(xs)
        center_y = sum(ys) / len(ys)
        center_z = sum(zs) / len(zs)

        distances = [math.sqrt((x-center_x)**2 + (y-center_y)**2 + (z-center_z)**2)
                     for x, y, z in all_positions]

        # Distance histogram
        buckets = [0, 100, 200, 500, 1000, 2000, 5000, 10000, 50000]
        counts = Counter()
        for dist in distances:
            for i in range(len(buckets)-1):
                if buckets[i] <= dist < buckets[i+1]:
                    counts[f"{buckets[i]}-{buckets[i+1]}"] += 1
                    break
            else:
                counts[f">={buckets[-1]}"] += 1

        print(f"\n  Vertex distance from centroid ({center_x:.0f}, {center_y:.0f}, {center_z:.0f}):")
        for bucket in [f"{buckets[i]}-{buckets[i+1]}" for i in range(len(buckets)-1)] + [f">={buckets[-1]}"]:
            if counts[bucket] > 0:
                pct = counts[bucket] / len(distances) * 100
                bar = "#" * int(pct / 2)
                print(f"    {bucket:>12s}: {counts[bucket]:6d} ({pct:5.1f}%) {bar}")

def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Report bounds, density, and outliers for a GLB's geometry."
    )
    parser.add_argument("glb", type=Path, help="GLB file to analyze")
    args = parser.parse_args(argv)
    analyze(args.glb)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
