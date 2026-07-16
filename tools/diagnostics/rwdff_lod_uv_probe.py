#!/usr/bin/env python3
"""
rwdff_lod_uv_probe.py -- Compare RW DFF geometry decode across a THPS3 LOD family.

Parses RenderWare 3.x DFF (Clump) .SKN files read-only and dumps, per geometry:
  - format flags word (raw u32: flags u16 + texSets u16), morph/tri/vert counts
  - UV-set count under BOTH interpretations:
      (a) shipped C# parser rule: version<0x34000 -> 1 UV set iff flags&0x04 (TEXTURED)
      (b) RW spec rule:           TEXTURED2 (0x80) -> 2 sets, TEXTURED (0x04) -> 1 set
  - UV ranges (min/max u,v) + mean for set 0 (and set 1 when present)
  - material list layout + per-material texture names
  - triangle material-id histogram
  - binmesh (Material Split PLG 0x050E) info from the geometry Extension, if present
  - binmesh-vs-triangle-list cross-check: the binmesh strips are an INDEPENDENT index
    stream in the same file; if the decoded triangle set (per material) matches the
    strip-decoded set exactly, the triangle/material decode is proven correct
  - leftover byte count between end-of-parse and end-of-struct (alignment check)

Findings from the 2026-07-16 LOD 2/3 texture-mapping audit (THPS3 PS2):
  All 322 unique .skn decode with exact struct consumption (leftover=0, single UV set,
  flags 0x75). GLB output is byte-faithful to the raw arrays. The LOD02/03 visual
  oddities (e.g. ped_canada_a striped face, pedestrian_a knee-length shorts becoming a
  skirt) are present in the shipped file data: binmesh reproduces the same triangles,
  normals and skin weights pair correctly with positions, and no permutation/offset/
  flip of the UV array improves UV coherence. The defect is authored (sloppy LOD UV
  generation), invisible at the draw distances the engine uses these LODs at.

Usage:
  python tools/diagnostics/rwdff_lod_uv_probe.py <base.skn> [lod01.skn lod02.skn ...]
  python tools/diagnostics/rwdff_lod_uv_probe.py --family <dir>/<stem>   (expands stem.skn + stem_LOD01..03.skn)
  python tools/diagnostics/rwdff_lod_uv_probe.py --glb <file.skn> <file.glb>
      Cross-checks a converted GLB against the raw DFF: matches every GLB vertex
      back to a raw vertex by position and verifies TEXCOORD_0 equals the raw UV
      (isolates parser-vs-writer if the two ever disagree).
"""

import struct
import sys
from pathlib import Path

RW_STRUCT = 0x0001
RW_EXTENSION = 0x0003
RW_MATERIAL = 0x0007
RW_MATERIAL_LIST = 0x0008
RW_FRAME_LIST = 0x000E
RW_GEOMETRY = 0x000F
RW_CLUMP = 0x0010
RW_ATOMIC = 0x0014
RW_GEOMETRY_LIST = 0x001A
RW_STRING = 0x0002
RW_TEXTURE = 0x0006
RW_BINMESH = 0x050E

GF_TRISTRIP = 0x01
GF_POSITIONS = 0x02
GF_TEXTURED = 0x04
GF_PRELIT = 0x08
GF_NORMALS = 0x10
GF_LIGHT = 0x20
GF_MODULATE = 0x40
GF_TEXTURED2 = 0x80


def u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def i32(d, o):
    return struct.unpack_from("<i", d, o)[0]


def u16(d, o):
    return struct.unpack_from("<H", d, o)[0]


def f32(d, o):
    return struct.unpack_from("<f", d, o)[0]


def read_header(d, o):
    return u32(d, o), u32(d, o + 4), u32(d, o + 8), o + 12


def uv_stats(uvs):
    if not uvs:
        return None
    us = [a for a, _ in uvs]
    vs = [b for _, b in uvs]
    return {
        "umin": min(us), "umax": max(us), "umean": sum(us) / len(us),
        "vmin": min(vs), "vmax": max(vs), "vmean": sum(vs) / len(vs),
    }


def fmt_stats(s):
    if s is None:
        return "  (none)"
    return (f"u[{s['umin']:9.4f},{s['umax']:9.4f}] mean {s['umean']:8.4f}  "
            f"v[{s['vmin']:9.4f},{s['vmax']:9.4f}] mean {s['vmean']:8.4f}")


def parse_geometry(d, o, end, version):
    """Parse a GEOMETRY chunk body (after chunk header). Returns dict."""
    g = {}
    t, size, ver, o = read_header(d, o)
    assert t == RW_STRUCT, f"geometry struct missing at 0x{o - 12:X}"
    struct_end = o + size

    flags = u16(d, o)
    tex_count_field = u16(d, o + 2)
    num_tris = i32(d, o + 4)
    num_verts = i32(d, o + 8)
    num_morphs = i32(d, o + 12)
    pos = o + 16

    g["flags"] = flags
    g["texCountField"] = tex_count_field
    g["numTris"] = num_tris
    g["numVerts"] = num_verts
    g["numMorphs"] = num_morphs

    # UV set counts under both interpretations
    shipped_sets = (1 if (flags & GF_TEXTURED) else 0) if version < 0x34000 else tex_count_field
    spec_sets = tex_count_field
    if version < 0x34000 or tex_count_field == 0:
        spec_sets = 2 if (flags & GF_TEXTURED2) else (1 if (flags & GF_TEXTURED) else 0)
    g["shippedUvSets"] = shipped_sets
    g["specUvSets"] = spec_sets

    if version < 0x34000:
        pos += 12  # surface properties

    if flags & GF_PRELIT:
        pos += 4 * num_verts

    # Read UVs under the SPEC interpretation (ground truth layout)
    uv_sets = []
    for _ in range(spec_sets):
        uvs = [(f32(d, pos + 8 * i), f32(d, pos + 8 * i + 4)) for i in range(num_verts)]
        uv_sets.append(uvs)
        pos += 8 * num_verts
    g["uvSets"] = uv_sets

    # ALSO simulate what the shipped parser reads as UV set 0 (same start offset,
    # but if shipped_sets==0 it reads no UVs; if shipped < spec the triangle read
    # below will start early in the shipped parser).
    g["shippedTriStart_delta"] = (spec_sets - shipped_sets) * 8 * num_verts

    tris = []
    mat_hist = {}
    for i in range(num_tris):
        v2, v1, mat, v3 = struct.unpack_from("<4H", d, pos)
        tris.append((v1, v2, v3, mat))
        mat_hist[mat] = mat_hist.get(mat, 0) + 1
        pos += 8
    g["matHist"] = mat_hist
    g["tris"] = tris

    g["positions"] = []
    for mt in range(num_morphs):
        bx, by, bz, br = struct.unpack_from("<4f", d, pos)
        has_v = i32(d, pos + 16)
        has_n = i32(d, pos + 20)
        pos += 24
        if mt == 0:
            g["bsphere"] = (bx, by, bz, br)
        if has_v:
            if mt == 0:
                g["positions"] = [
                    (f32(d, pos + 12 * i), f32(d, pos + 12 * i + 4), f32(d, pos + 12 * i + 8))
                    for i in range(num_verts)]
            pos += 12 * num_verts
        if has_n:
            pos += 12 * num_verts
    g["structLeftover"] = struct_end - pos

    # Children: material list + extension (binmesh)
    o = struct_end
    g["materials"] = []
    g["binmesh"] = None
    while o + 12 <= end:
        ct, cs, _, o2 = read_header(d, o)
        cend = o2 + cs
        if ct == RW_MATERIAL_LIST:
            g["materials"] = parse_material_list(d, o2, cend)
        elif ct == RW_EXTENSION:
            eo = o2
            while eo + 12 <= cend:
                pt, ps, _, eo2 = read_header(d, eo)
                if pt == RW_BINMESH:
                    tri_mode = u32(d, eo2)
                    n_splits = u32(d, eo2 + 4)
                    n_idx = u32(d, eo2 + 8)
                    splits = []
                    strip_tris = {}
                    sp = eo2 + 12
                    for _ in range(n_splits):
                        cnt = u32(d, sp)
                        mat = u32(d, sp + 4)
                        idx = [u32(d, sp + 8 + 4 * k) for k in range(cnt)]
                        splits.append((cnt, mat))
                        sp += 8 + 4 * cnt
                        tri_set = strip_tris.setdefault(mat, set())
                        if tri_mode == 1:  # tristrip: degenerates collapse
                            for k in range(cnt - 2):
                                a, b, c = idx[k], idx[k + 1], idx[k + 2]
                                if a != b and b != c and a != c:
                                    tri_set.add(frozenset((a, b, c)))
                        else:  # trilist
                            for k in range(0, cnt - 2, 3):
                                tri_set.add(frozenset(idx[k:k + 3]))
                    g["binmesh"] = {"mode": tri_mode, "splits": splits,
                                    "numIndices": n_idx, "tris": strip_tris}
                eo = eo2 + ps
        o = cend
    return g


def parse_material_list(d, o, end):
    t, size, _, o = read_header(d, o)
    assert t == RW_STRUCT
    num = i32(d, o)
    o += size
    mats = []
    for _ in range(num):
        if o + 12 > end:
            break
        ct, cs, _, o2 = read_header(d, o)
        cend = o2 + cs
        if ct != RW_MATERIAL:
            o = cend
            continue
        st, ss, _, so = read_header(d, o2)
        r, gcol, b, a = d[so + 4], d[so + 5], d[so + 6], d[so + 7]
        textured = i32(d, so + 12)
        name = None
        mo = so + ss
        if textured:
            while mo + 12 <= cend:
                mt2, ms2, _, mo2 = read_header(d, mo)
                if mt2 == RW_TEXTURE:
                    tt, ts, _, to = read_header(d, mo2)
                    to += ts  # skip texture struct (filter flags)
                    nt, ns, _, no = read_header(d, to)
                    if nt == RW_STRING:
                        name = d[no:no + ns].split(b"\0")[0].decode("ascii", "replace")
                    break
                mo = mo2 + ms2
        mats.append({"rgba": (r, gcol, b, a), "textured": textured, "name": name})
        o = cend
    return mats


def parse_dff(path):
    d = Path(path).read_bytes()
    t, size, version, o = read_header(d, 0)
    assert t == RW_CLUMP, f"{path}: not a clump (0x{t:X})"
    clump_end = o + size

    st, ss, _, o = read_header(d, o)
    num_atomics = i32(d, o)
    o += ss

    result = {"version": version, "numAtomics": num_atomics,
              "geometries": [], "atomics": [], "path": str(path)}

    while o + 12 <= clump_end:
        ct, cs, cver, o2 = read_header(d, o)
        cend = o2 + cs
        if ct == RW_GEOMETRY_LIST:
            gt, gs, _, go = read_header(d, o2)
            n_geo = i32(d, go)
            go += gs
            for _ in range(n_geo):
                ggt, ggs, ggver, go2 = read_header(d, go)
                gend = go2 + ggs
                if ggt == RW_GEOMETRY:
                    result["geometries"].append(parse_geometry(d, go2, gend, ggver))
                go = gend
        elif ct == RW_ATOMIC:
            at, asz, _, ao = read_header(d, o2)
            result["atomics"].append({
                "frame": i32(d, ao), "geometry": i32(d, ao + 4), "flags": i32(d, ao + 8)})
        o = cend
    return result


def flag_names(flags):
    names = []
    for bit, nm in [(GF_TRISTRIP, "TRISTRIP"), (GF_POSITIONS, "POS"), (GF_TEXTURED, "TEXTURED"),
                    (GF_PRELIT, "PRELIT"), (GF_NORMALS, "NORMALS"), (GF_LIGHT, "LIGHT"),
                    (GF_MODULATE, "MODULATE"), (GF_TEXTURED2, "TEXTURED2")]:
        if flags & bit:
            names.append(nm)
    return "|".join(names)


def report(path):
    r = parse_dff(path)
    print(f"\n=== {Path(path).name} (version 0x{r['version']:X}, {r['numAtomics']} atomics) ===")
    for ai, a in enumerate(r["atomics"]):
        print(f"  atomic[{ai}]: frame={a['frame']} geometry={a['geometry']}")
    for gi, g in enumerate(r["geometries"]):
        print(f"  geometry[{gi}]: flags=0x{g['flags']:02X} ({flag_names(g['flags'])}) "
              f"texCountField={g['texCountField']} tris={g['numTris']} verts={g['numVerts']} "
              f"morphs={g['numMorphs']}")
        print(f"    UV sets: shipped-parser={g['shippedUvSets']} spec={g['specUvSets']} "
              f"(shipped tri-read offset delta = {g['shippedTriStart_delta']} bytes)"
              f"  structLeftover={g['structLeftover']}")
        for si, uvs in enumerate(g["uvSets"]):
            print(f"    UV[{si}]: {fmt_stats(uv_stats(uvs))}")
        print(f"    tri material histogram: {dict(sorted(g['matHist'].items()))}")
        mats = g["materials"]
        print(f"    materials ({len(mats)}):")
        for mi, m in enumerate(mats):
            print(f"      [{mi}] rgba={m['rgba']} textured={m['textured']} name={m['name']}")
        if g["binmesh"]:
            bm = g["binmesh"]
            print(f"    binmesh: mode={bm['mode']} splits={bm['splits']}")
            # Cross-check: triangle list (per material) vs strip-decoded binmesh set
            tl = {}
            for (v1, v2, v3, mat) in g["tris"]:
                if len({v1, v2, v3}) == 3:
                    tl.setdefault(mat, set()).add(frozenset((v1, v2, v3)))
            for mat in sorted(set(tl) | set(bm["tris"])):
                a = tl.get(mat, set())
                b = bm["tris"].get(mat, set())
                status = "MATCH" if a == b else "MISMATCH"
                print(f"    binmesh-xcheck mat{mat}: trilist={len(a)} strips={len(b)} "
                      f"common={len(a & b)} -> {status}")
        else:
            print("    binmesh: (none)")


def check_glb(skn_path, glb_path):
    """Match GLB vertices to raw DFF vertices by position; verify TEXCOORD_0 fidelity."""
    import json

    r = parse_dff(skn_path)
    posmap = {}
    for g in r["geometries"]:
        uvs = g["uvSets"][0] if g["uvSets"] else None
        for i, p in enumerate(g["positions"]):
            key = tuple(round(c, 4) for c in p)
            if uvs:
                posmap.setdefault(key, set()).add((round(uvs[i][0], 4), round(uvs[i][1], 4)))

    d = Path(glb_path).read_bytes()
    clen, _ = struct.unpack_from("<II", d, 12)
    j = json.loads(d[20:20 + clen].decode().rstrip("\x00"))
    bin_off = 20 + clen + 8

    def accessor(idx):
        acc = j["accessors"][idx]
        bv = j["bufferViews"][acc["bufferView"]]
        base = bin_off + bv.get("byteOffset", 0) + acc.get("byteOffset", 0)
        ncomp = {"VEC2": 2, "VEC3": 3}[acc["type"]]
        stride = bv.get("byteStride", 4 * ncomp)
        return [struct.unpack_from(f"<{ncomp}f", d, base + i * stride)
                for i in range(acc["count"])]

    total = matched = uv_ok = 0
    worst = 0.0
    for mesh in j["meshes"]:
        for prim in mesh["primitives"]:
            attrs = prim["attributes"]
            if "POSITION" not in attrs or "TEXCOORD_0" not in attrs:
                continue
            for p, uv in zip(accessor(attrs["POSITION"]), accessor(attrs["TEXCOORD_0"])):
                total += 1
                key = tuple(round(c, 4) for c in p)
                if key not in posmap:
                    continue
                matched += 1
                err = min(max(abs(uv[0] - ru), abs(uv[1] - rv)) for ru, rv in posmap[key])
                worst = max(worst, err)
                if err < 1e-3:
                    uv_ok += 1
    print(f"{Path(glb_path).name}: {total} glb verts, {matched} position-matched, "
          f"{uv_ok} UV-exact, worst matched UV err {worst:.5f}")


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        sys.exit(1)
    if args[0] == "--glb":
        check_glb(args[1], args[2])
        return
    if args[0] == "--family":
        stem = Path(args[1])
        files = [stem.with_suffix(".skn")]
        for n in (1, 2, 3):
            p = stem.parent / f"{stem.name}_LOD{n:02d}.skn"
            if p.exists():
                files.append(p)
    else:
        files = [Path(a) for a in args]
    for f in files:
        report(f)


if __name__ == "__main__":
    main()
