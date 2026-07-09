#!/usr/bin/env python3
"""Structural probe for THAW GameCube scene files (.skin.ngc / .mdl.ngc / .scn.ngc).

Walks the full container per THUG GC source (Gfx/NGC/p_nx.cpp s_plat_load_scene_guts,
p_nxsector.cpp LoadFromFile, NX/mesh.cpp ApplyMeshScaling, NX/instance.cpp skinning),
with the THAW-era 64-byte extended sSceneHeader discovered 2026-07-08.

Layout (all big-endian):
  sSceneHeader (64B):
    0x00 u32 num_pos            0x04 u16 pad, u16 num_nrm
    0x08 u16 num_col, u16 num_tex
    0x0C u32 num_pool_bytes     0x10 u16 num_objects, u16 num_materials
    0x14 u32 num_shadow_faces   0x18 u16 num_blend_dls, u16 num_vc_wibbles
    0x1C u16 num_uv_wibbles, u16 num_pass_items
    0x20..0x3F THAW extension (contents probed here)
  sMaterialDL[num_blend_dls] (8B: u32 dl_size, u32 ptr-pad)
  sTextureDL[num_materials] (24B: u32 dl_size, u32 ptr-pad, s16 tex_off[4], s16 alpha_off[4])
  pad to 32-byte boundary of the two tables' byte total
  blend DL data (concatenated), texture DL data (concatenated)
  pool (num_pool_bytes): VC wibbles, sMaterialHeader[num_materials] (32B),
    sMaterialUVWibble[num_uv_wibbles] (32B), sMaterialPassHeader[num_pass_items] (32B),
    pos pool (num_pos * 3 f32), col pool (num_col * u32), tex pool (num_tex * 2 s16, /1024),
    nrm pool (num_nrm * 3 s16, /16384), shadow data
  per object: sObjectHeader (64B) + skin data (m_skin.num_bytes) +
    num_meshes * [ sDLHeader (64B) + GX display list (m_size) ]
  u32 num_hierarchy_objects + hierarchy array + trailing bytes (probed)

Skin data: single lists {u32 n, u32 mtx, n*(6 s16: pos/64, nrm/16384)},
  double lists {u32 n, u32 mtx(b0|b1<<8), n*12B, n*(2 s16 weights /16384)},
  add lists {u32 n, u32 mtx, n*12B, n*s16 weight, n*u16 target-index} (accumulate only).

GX DL: 0x08 CP loads (track VCD_LO=0x50, VCD_HI=0x60), 0x10 XF loads (skip),
  0x61 BP loads (skip), 0x80-0xBF draw ops (u16 count + indexed verts per VCD).
  VAT7: pos S16/64 (skins) or F32 (pools), nrm S16/16384, clr RGBA8, tex S16/1024.

Usage:
  python ngc_scene_probe.py <file.skin.ngc> [-v] [--obj out.obj]
  python ngc_scene_probe.py --batch <dir> [--limit N]
"""

import argparse
import struct
import sys
from pathlib import Path


def u8(b, o):
    return b[o]


def u16(b, o):
    return struct.unpack_from(">H", b, o)[0]


def s16(b, o):
    return struct.unpack_from(">h", b, o)[0]


def u32(b, o):
    return struct.unpack_from(">I", b, o)[0]


def f32(b, o):
    return struct.unpack_from(">f", b, o)[0]


class ProbeError(Exception):
    pass


def parse_header(b):
    h = {
        "num_pos": u32(b, 0x00),
        "pad": u16(b, 0x04),
        "num_nrm": u16(b, 0x06),
        "num_col": u16(b, 0x08),
        "num_tex": u16(b, 0x0A),
        "num_pool_bytes": u32(b, 0x0C),
        "num_objects": u16(b, 0x10),
        "num_materials": u16(b, 0x12),
        "num_shadow_faces": u32(b, 0x14),
        "num_blend_dls": u16(b, 0x18),
        "num_vc_wibbles": u16(b, 0x1A),
        "num_uv_wibbles": u16(b, 0x1C),
        "num_pass_items": u16(b, 0x1E),
        "ext": b[0x20:0x40].hex(),
    }
    return h


def vcd_attr_sizes(vcd_lo, vcd_hi):
    """Return list of (name, kind, size) for one vertex per VCD.
    kind: 'direct8' (matrix idx), 'idx8', 'idx16', or None (absent)."""
    attrs = []
    if vcd_lo & 1:
        attrs.append(("posmtx", "direct8", 1))
    for t in range(8):
        if (vcd_lo >> (1 + t)) & 1:
            attrs.append((f"tex{t}mtx", "direct8", 1))

    def mode(v):
        return [None, "direct", "idx8", "idx16"][v]

    for name, val in (
        ("pos", (vcd_lo >> 9) & 3),
        ("nrm", (vcd_lo >> 11) & 3),
        ("clr0", (vcd_lo >> 13) & 3),
        ("clr1", (vcd_lo >> 15) & 3),
    ):
        m = mode(val)
        if m == "direct":
            raise ProbeError(f"direct {name} attribute not supported")
        if m:
            attrs.append((name, m, 1 if m == "idx8" else 2))
    for t in range(8):
        m = mode((vcd_hi >> (2 * t)) & 3)
        if m == "direct":
            raise ProbeError(f"direct tex{t} attribute not supported")
        if m:
            attrs.append((f"tex{t}", m, 1 if m == "idx8" else 2))
    return attrs


DRAW_OPS = {
    0x80: "quads",
    0x90: "tris",
    0x98: "strip",
    0xA0: "fan",
    0xA8: "lines",
    0xB0: "linestrip",
    0xB8: "points",
}


def parse_display_list(b, start, size, verbose=False):
    """Parse one GX display list; return dict with cp regs, strips (lists of vertex dicts)."""
    o = start
    end = start + size
    cp = {}
    prims = []
    while o < end:
        op = b[o]
        if op == 0x00:
            o += 1
            continue
        if op == 0x08:
            cp[b[o + 1]] = u32(b, o + 2)
            o += 6
            continue
        if op == 0x10:
            hdr = u32(b, o + 1)
            count = ((hdr >> 16) & 0xFFFF) + 1
            o += 5 + 4 * count
            continue
        if op == 0x61:
            o += 5
            continue
        if (op & 0xF8) in DRAW_OPS:
            vcd_lo = cp.get(0x50, 0)
            vcd_hi = cp.get(0x60, 0)
            attrs = vcd_attr_sizes(vcd_lo, vcd_hi)
            stride = sum(a[2] for a in attrs)
            count = u16(b, o + 1)
            o += 3
            verts = []
            for _ in range(count):
                v = {}
                vo = o
                for name, kind, sz in attrs:
                    v[name] = b[vo] if sz == 1 else u16(b, vo)
                    vo += sz
                verts.append(v)
                o += stride
            prims.append(
                {
                    "op": DRAW_OPS[op & 0xF8],
                    "vat": op & 7,
                    "count": count,
                    "verts": verts,
                    "stride": stride,
                }
            )
            continue
        raise ProbeError(f"unknown DL opcode 0x{op:02X} at 0x{o:X}")
    return {"cp": cp, "prims": prims}


def parse_skin(b, start, nbytes, n_single, n_double, n_add):
    """Return (verts, adds): verts = list of dicts (pos, nrm, bones, weights)."""
    o = start
    verts = []
    adds = []
    for _ in range(n_single):
        n, mtx = u32(b, o), u32(b, o + 4)
        o += 8
        for i in range(n):
            px, py, pz, nx, ny, nz = struct.unpack_from(">6h", b, o + i * 12)
            verts.append(
                {
                    "pos": (px / 64.0, py / 64.0, pz / 64.0),
                    "nrm": (nx / 16384.0, ny / 16384.0, nz / 16384.0),
                    "bones": [mtx],
                    "weights": [1.0],
                }
            )
        o += n * 12
    for _ in range(n_double):
        n, mtx = u32(b, o), u32(b, o + 4)
        o += 8
        w_off = o + n * 12
        for i in range(n):
            px, py, pz, nx, ny, nz = struct.unpack_from(">6h", b, o + i * 12)
            w0, w1 = struct.unpack_from(">2h", b, w_off + i * 4)
            verts.append(
                {
                    "pos": (px / 64.0, py / 64.0, pz / 64.0),
                    "nrm": (nx / 16384.0, ny / 16384.0, nz / 16384.0),
                    "bones": [mtx & 255, (mtx >> 8) & 255],
                    "weights": [w0 / 16384.0, w1 / 16384.0],
                }
            )
        o = w_off + n * 4
    for _ in range(n_add):
        n, mtx = u32(b, o), u32(b, o + 4)
        o += 8
        w_off = o + n * 12
        i_off = w_off + n * 2
        for i in range(n):
            w = s16(b, w_off + i * 2)
            idx = u16(b, i_off + i * 2)
            adds.append({"target": idx, "bone": mtx, "weight": w / 16384.0})
        o = i_off + n * 2
    used = o - start
    return verts, adds, used


def probe(path, verbose=False, obj_out=None):
    b = Path(path).read_bytes()
    h = parse_header(b)
    r = {"file": str(path), "size": len(b), "header": h, "ok": False}

    o = 0x40
    blend_dls = []
    for i in range(h["num_blend_dls"]):
        blend_dls.append(u32(b, o))
        o += 8
    tex_dls = []
    for i in range(h["num_materials"]):
        tex_dls.append({"size": u32(b, o), "tex_off": [s16(b, o + 8 + 2 * k) for k in range(4)]})
        o += 24
    table_bytes = h["num_blend_dls"] * 8 + h["num_materials"] * 24
    o += (-table_bytes) % 32  # pad tables to 32

    dl_data_start = o
    for s in blend_dls:
        o += s
    for t in tex_dls:
        o += t["size"]

    pool_start = o
    # VC wibbles
    for _ in range(h["num_vc_wibbles"]):
        frames = struct.unpack_from(">i", b, o)[0]
        o += 8 + frames * 8
    materials = []
    for i in range(h["num_materials"]):
        materials.append(
            {
                "checksum": u32(b, o),
                "passes": u8(b, o + 4),
                "draw_order": f32(b, o + 8),
                "pass_item": u16(b, o + 12),
                "name_checksum": u32(b, o + 24),
            }
        )
        o += 32
    o += h["num_uv_wibbles"] * 32
    passes = []
    for i in range(h["num_pass_items"]):
        passes.append({"texture": u32(b, o), "flags": u8(b, o + 4), "blend": u8(b, o + 6)})
        o += 32
    pos_pool_off = o
    o += h["num_pos"] * 12
    col_pool_off = o
    o += h["num_col"] * 4
    tex_pool_off = o
    o += h["num_tex"] * 4
    nrm_pool_off = o
    o += h["num_nrm"] * 6
    if h["num_shadow_faces"]:
        o += h["num_shadow_faces"] * 6
        o = (o + 1) & ~1
        o += h["num_shadow_faces"] * 6
    pool_used = o - pool_start
    r["pool_used"] = pool_used
    r["pool_declared"] = h["num_pool_bytes"]
    if pool_used > h["num_pool_bytes"]:
        raise ProbeError(f"pool overrun: used {pool_used} > declared {h['num_pool_bytes']}")
    obj_start = pool_start + h["num_pool_bytes"]

    # Objects
    o = obj_start
    objects = []
    total_tris = 0
    all_tris = []  # (posidx list) for obj export
    vcd_seen = set()
    array_bases = set()
    for _ in range(h["num_objects"]):
        oh = {
            "num_meshes": u16(b, o),
            "billboard": u16(b, o + 2),
            "skin_bytes": u32(b, o + 4),
            "num_skin_verts": u16(b, o + 8),
            "num_double_lists": u16(b, o + 10),
            "num_single_lists": u8(b, o + 12),
            "num_add_lists": u8(b, o + 13),
            "bone_index": s16(b, o + 14),
            "sphere": struct.unpack_from(">4f", b, o + 48),
        }
        skin_start = o + 64
        skin_verts, adds, skin_used = ([], [], 0)
        if oh["skin_bytes"]:
            skin_verts, adds, skin_used = parse_skin(
                b,
                skin_start,
                oh["skin_bytes"],
                oh["num_single_lists"],
                oh["num_double_lists"],
                oh["num_add_lists"],
            )
            if skin_used > oh["skin_bytes"]:
                raise ProbeError(f"skin overrun {skin_used} > {oh['skin_bytes']}")
            if len(skin_verts) != oh["num_skin_verts"]:
                raise ProbeError(
                    f"skin vert count {len(skin_verts)} != header {oh['num_skin_verts']}"
                )
        oh["skin_verts"] = len(skin_verts)
        oh["adds"] = len(adds)

        o = skin_start + oh["skin_bytes"]
        meshes = []
        for _ in range(oh["num_meshes"]):
            dlh = {
                "size": u32(b, o),
                "mat_checksum": u32(b, o + 4),
                "flags": u32(b, o + 8),
                "checksum": u32(b, o + 12),
                "sphere": struct.unpack_from(">4f", b, o + 16),
                "index_offset": u16(b, o + 36),
                "index_stride": u16(b, o + 38),
                "num_indices": u16(b, o + 40),
                "color_offset": u8(b, o + 42),
                "array_base": u32(b, o + 56),
            }
            array_bases.add(dlh["array_base"])
            dl_start = o + 64
            if dlh["size"]:
                dl = parse_display_list(b, dl_start, dlh["size"], verbose)
                dlh["vcd_lo"] = dl["cp"].get(0x50, 0)
                dlh["vcd_hi"] = dl["cp"].get(0x60, 0)
                vcd_seen.add((dlh["vcd_lo"], dlh["vcd_hi"]))
                tris = 0
                nverts = 0
                pos_max = -1
                tex_max = -1
                for p in dl["prims"]:
                    nverts += p["count"]
                    for v in p["verts"]:
                        if "pos" in v:
                            pos_max = max(pos_max, v["pos"])
                        if "tex0" in v:
                            tex_max = max(tex_max, v["tex0"])
                    if p["op"] == "strip":
                        tris += max(0, p["count"] - 2)
                        if obj_out:
                            idx = [v.get("pos", 0) for v in p["verts"]]
                            for i in range(len(idx) - 2):
                                t = (
                                    (idx[i], idx[i + 1], idx[i + 2])
                                    if i % 2 == 0
                                    else (idx[i + 1], idx[i], idx[i + 2])
                                )
                                all_tris.append((t, len(skin_verts) > 0))
                    elif p["op"] == "tris":
                        tris += p["count"] // 3
                dlh["prims"] = len(dl["prims"])
                dlh["tris"] = tris
                dlh["dl_verts"] = nverts
                dlh["pos_idx_max"] = pos_max
                dlh["tex_idx_max"] = tex_max
                total_tris += tris
            meshes.append(dlh)
            o = dl_start + dlh["size"]
        oh["meshes"] = meshes
        oh["_skin_verts_data"] = skin_verts
        objects.append(oh)

    hier_off = o
    num_hobj = u32(b, o)
    o += 4
    # CHierarchyObject size unknown here; record trailing bytes
    r["num_hierarchy"] = num_hobj
    r["trailing_after_hier_count"] = len(b) - o
    r["trailing_hex"] = b[o : min(o + 64, len(b))].hex()
    r["objects"] = objects
    r["total_tris"] = total_tris
    r["vcd_seen"] = sorted(vcd_seen)
    r["array_bases"] = sorted(array_bases)
    r["num_pos_pool"] = h["num_pos"]
    r["pos_pool_off"] = pos_pool_off
    r["tex_pool_off"] = tex_pool_off
    r["nrm_pool_off"] = nrm_pool_off
    r["ok"] = True

    if obj_out and objects:
        with open(obj_out, "w") as f:
            f.write(f"# ngc_scene_probe {path}\n")
            skin_verts = objects[0]["_skin_verts_data"]
            if skin_verts:
                for v in skin_verts:
                    f.write(f"v {v['pos'][0]} {v['pos'][1]} {v['pos'][2]}\n")
            else:
                for i in range(h["num_pos"]):
                    x = f32(b, pos_pool_off + i * 12)
                    y = f32(b, pos_pool_off + i * 12 + 4)
                    z = f32(b, pos_pool_off + i * 12 + 8)
                    f.write(f"v {x} {y} {z}\n")
            for (t, _skinned) in all_tris:
                f.write(f"f {t[0]+1} {t[1]+1} {t[2]+1}\n")
        print(f"wrote {obj_out}: {len(all_tris)} tris")

    return r


def summarize(r, verbose=False):
    h = r["header"]
    print(f"== {Path(r['file']).name} ({r['size']} bytes)")
    print(
        f"   pos={h['num_pos']} nrm={h['num_nrm']} col={h['num_col']} tex={h['num_tex']} "
        f"objects={h['num_objects']} materials={h['num_materials']} passes={h['num_pass_items']}"
    )
    print(f"   ext: {h['ext']}")
    print(
        f"   pool {r['pool_used']}/{r['pool_declared']}  tris={r['total_tris']}  "
        f"hier={r['num_hierarchy']} trailing={r['trailing_after_hier_count']}"
    )
    print(f"   vcd={['%04X/%04X' % v for v in r['vcd_seen']]} array_bases={r['array_bases']}")
    if r["trailing_after_hier_count"]:
        print(f"   trailing hex: {r['trailing_hex']}")
    if verbose:
        for i, oh in enumerate(r["objects"]):
            print(
                f"   obj{i}: meshes={oh['num_meshes']} skin_verts={oh['skin_verts']} "
                f"adds={oh['adds']} bone={oh['bone_index']} bb={oh['billboard']} "
                f"sphere={tuple(round(x,2) for x in oh['sphere'])}"
            )
            for m in oh["meshes"]:
                if m["size"]:
                    print(
                        f"      mesh mat={m['mat_checksum']:08X} tris={m.get('tris')} "
                        f"dlverts={m.get('dl_verts')} n_idx={m['num_indices']} "
                        f"posmax={m.get('pos_idx_max')} texmax={m.get('tex_idx_max')} "
                        f"vcd={m.get('vcd_lo',0):04X}/{m.get('vcd_hi',0):04X}"
                    )


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("path", nargs="?")
    ap.add_argument("--batch", help="probe all .skin.ngc/.mdl.ngc/.scn.ngc under dir")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("-v", "--verbose", action="store_true")
    ap.add_argument("--obj", help="write OBJ of first object")
    args = ap.parse_args()

    if args.batch:
        files = []
        for pat in ("*.skin.ngc", "*.mdl.ngc", "*.scn.ngc"):
            files.extend(Path(args.batch).rglob(pat))
        files.sort()
        if args.limit:
            files = files[: args.limit]
        ok = fail = 0
        exts = {}
        vcds = {}
        bases = set()
        trailing = {}
        fails = []
        for f in files:
            try:
                r = probe(f)
                ok += 1
                exts[r["header"]["ext"]] = exts.get(r["header"]["ext"], 0) + 1
                for v in r["vcd_seen"]:
                    key = "%04X/%04X" % v
                    vcds[key] = vcds.get(key, 0) + 1
                bases.update(r["array_bases"])
                tkey = r["trailing_after_hier_count"]
                trailing[tkey] = trailing.get(tkey, 0) + 1
            except Exception as e:
                fail += 1
                fails.append((f, str(e)))
        print(f"probed {ok+fail}: ok={ok} fail={fail}")
        print("ext variants:")
        for k, v in sorted(exts.items(), key=lambda kv: -kv[1])[:10]:
            print(f"  {v:5d}  {k}")
        print(f"vcd variants: {vcds}")
        print(f"array_bases: {sorted(bases)[:20]}")
        print(f"trailing byte counts: {dict(sorted(trailing.items()))}")
        for f, e in fails[:20]:
            print(f"FAIL {f}: {e}")
    else:
        r = probe(args.path, args.verbose, args.obj)
        summarize(r, args.verbose)


if __name__ == "__main__":
    main()
