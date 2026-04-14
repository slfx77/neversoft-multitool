"""
Test theory: c2[7:0] of V4_16 UV data encodes the output strip position for each vertex.
Sorting vertices by c2[7:0] descending and splitting at gaps (diff != 3) should produce
the correct triangle topology matching the PC ground truth.

Usage: python thaw_ps2_strip_decode.py
"""
import struct, sys, os, math

POSITION_SCALE = 1.0 / 16.0
UV_SCALE = 1.0 / 4096.0

def vif_next_code(data, offset, end):
    if offset >= end or offset + 4 > len(data):
        return end
    cmd = data[offset + 3]
    if (cmd & 0x60) != 0x60:
        c = cmd & 0x7F
        if c in (0x00,0x01,0x02,0x03,0x04,0x05,0x06,0x07,
                 0x10,0x11,0x13,0x14,0x15,0x17):
            return offset + 4
        if c == 0x20: return offset + 8
        if c in (0x30, 0x31): return offset + 20
        if c == 0x4A: return offset + (data[offset + 2] << 3) + 4
        if c in (0x50, 0x51):
            nloop = struct.unpack_from('<H', data, offset)[0]
            return offset + (nloop << 4) + 4
        return end
    vn = (cmd >> 2) & 3
    vl = cmd & 3
    num = data[offset + 2]
    bit_len = 32 >> vl
    dim = vn + 1
    data_size = ((bit_len * dim * num + 31) >> 5) << 2
    return offset + 4 + data_size


def find_mesh_boundaries(data, start, end):
    boundaries = []
    i = start
    while i + 8 <= end:
        flush_word = struct.unpack_from('<I', data, i)[0]
        if flush_word in (0x10000000, 0x11000000):
            c2 = data[i + 7] & 0x7F
            if c2 in (0x50, 0x51):
                boundaries.append(i)
        i += 4
    return boundaries


def extract_batch(data, start, end):
    """Walk VIF opcodes and extract vertex data from one batch."""
    pos_off = nrm_off = uv_off = -1
    count = 0
    in_interleaved = False
    pCode = start

    while pCode < end and pCode + 4 <= len(data):
        cmd = data[pCode + 3]

        if (cmd & 0x7F) == 0x01:  # STCYCL
            cl, wl = data[pCode], data[pCode + 1]
            in_interleaved = (cl == 3 and wl == 1)
            pCode = vif_next_code(data, pCode, end)
            continue

        if (cmd & 0x7F) in (0x14, 0x15, 0x17):  # VU kick
            if pos_off >= 0 and count > 0:
                yield extract_vertices(data, pos_off, nrm_off, uv_off, count)
            pos_off = nrm_off = uv_off = -1
            count = 0
            in_interleaved = False
            pCode = vif_next_code(data, pCode, end)
            continue

        if (cmd & 0x60) == 0x60:  # UNPACK
            vn = (cmd >> 2) & 3
            vl = cmd & 3
            num = data[pCode + 2]
            data_off = pCode + 4
            if in_interleaved and num > 1:
                if vn == 2 and vl == 1: pos_off, count = data_off, num
                elif vn == 2 and vl == 2: nrm_off = data_off
                elif vn == 3 and vl == 1: uv_off = data_off

        pCode = vif_next_code(data, pCode, end)

    if pos_off >= 0 and count > 0:
        yield extract_vertices(data, pos_off, nrm_off, uv_off, count)


def extract_vertices(data, pos_off, nrm_off, uv_off, count):
    """Read positions, UVs, and c2/c3 from VIF batch data."""
    verts = []
    for i in range(count):
        o = pos_off + i * 6
        px = struct.unpack_from('<h', data, o)[0] * POSITION_SCALE
        py = struct.unpack_from('<h', data, o + 2)[0] * POSITION_SCALE
        pz = struct.unpack_from('<h', data, o + 4)[0] * POSITION_SCALE

        u = v = 0.0
        c2_raw = c3_raw = 0
        if uv_off >= 0:
            uo = uv_off + i * 8
            u = struct.unpack_from('<h', data, uo)[0] * UV_SCALE
            v = struct.unpack_from('<h', data, uo + 2)[0] * UV_SCALE
            c2_raw = struct.unpack_from('<H', data, uo + 4)[0]
            c3_raw = struct.unpack_from('<H', data, uo + 6)[0]

        verts.append({
            'pos': (px, py, pz),
            'uv': (u, v),
            'c2': c2_raw,
            'c3': c3_raw,
            'c2_lo': c2_raw & 0xFF,
            'c2_hi': (c2_raw >> 8) & 0x7F,
            'c2_b15': (c2_raw >> 15) & 1,
            'input_idx': i,
        })
    return verts


def quantize_pos(pos, scale=16.0):
    return (round(pos[0] * scale) / scale,
            round(pos[1] * scale) / scale,
            round(pos[2] * scale) / scale)


def build_pc_triangle_set(pc_path):
    """Parse PC file and return set of sorted position triples."""
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from thaw_ps2_pc_compare import parse_pc_scene, analyze_strip_structure, triangulate_strip

    pc_data = open(pc_path, 'rb').read()
    pc_info, pc_materials, pc_sectors = parse_pc_scene(pc_data)

    tris = set()
    for sector in pc_sectors:
        for mesh in sector['meshes']:
            strips = analyze_strip_structure(mesh['raw_indices'])
            for strip in strips:
                for tri in triangulate_strip(strip):
                    positions = tuple(sorted(
                        [quantize_pos(mesh['vertices'][tri[k]]['pos']) for k in range(3)]
                    ))
                    tris.add(positions)
    return tris


def reorder_strip_triangles(verts):
    """Sort vertices by c2[7:0] descending, split at gaps, produce triangles."""
    # Sort by c2_lo descending (highest output address first)
    sorted_verts = sorted(verts, key=lambda v: v['c2_lo'], reverse=True)

    # Split into sub-strips where c2_lo gap != 3
    strips = []
    current_strip = [sorted_verts[0]]
    for i in range(1, len(sorted_verts)):
        diff = current_strip[-1]['c2_lo'] - sorted_verts[i]['c2_lo']
        if diff == 3:
            current_strip.append(sorted_verts[i])
        else:
            strips.append(current_strip)
            current_strip = [sorted_verts[i]]
    strips.append(current_strip)

    # Generate triangles from each sub-strip
    all_tris = []
    for strip in strips:
        for f in range(2, len(strip)):
            if f % 2 == 0:
                i0, i1, i2 = f-2, f-1, f
            else:
                i0, i1, i2 = f-2, f, f-1
            tri_pos = [strip[i0]['pos'], strip[i1]['pos'], strip[i2]['pos']]
            if tri_pos[0] != tri_pos[1] and tri_pos[1] != tri_pos[2] and tri_pos[0] != tri_pos[2]:
                all_tris.append(tri_pos)
    return all_tris, strips


def continuous_strip_triangles(verts):
    """Generate triangles from continuous input order (no reordering)."""
    tris = []
    for f in range(2, len(verts)):
        if f % 2 == 0:
            i0, i1, i2 = f-2, f-1, f
        else:
            i0, i1, i2 = f-2, f, f-1
        tri_pos = [verts[i0]['pos'], verts[i1]['pos'], verts[i2]['pos']]
        if tri_pos[0] != tri_pos[1] and tri_pos[1] != tri_pos[2] and tri_pos[0] != tri_pos[2]:
            tris.append(tri_pos)
    return tris


def check_triangles(tris, pc_tri_set):
    """Check how many triangles match the PC ground truth."""
    match = miss = 0
    for tri_pos in tris:
        sorted_pos = tuple(sorted([quantize_pos(p) for p in tri_pos]))
        if sorted_pos in pc_tri_set:
            match += 1
        else:
            miss += 1
    return match, miss


def main():
    base = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    ps2_path = os.path.join(base, "Sample", "Builds",
        "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN", "skater_lasek.skin.ps2")
    pc_path = os.path.join(base, "Sample", "Builds",
        "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "SKIN", "skater_lasek.skin.wpc")

    ps2_data = open(ps2_path, 'rb').read()

    # Parse PS2 header
    num_objects = struct.unpack_from('<I', ps2_data, 0)[0]
    mesh2 = struct.unpack_from('<I', ps2_data, 8)[0]
    data_size = struct.unpack_from('<I', ps2_data, 12)[0]
    entry_off = 32 + num_objects * 8
    vif_start = entry_off + mesh2 * 64
    vif_end = min(16 + data_size, len(ps2_data))
    mesh_starts = find_mesh_boundaries(ps2_data, vif_start, vif_end)

    # Build PC ground truth
    pc_tris = build_pc_triangle_set(pc_path)
    print(f"PC ground truth: {len(pc_tris)} unique triangles")
    print()

    total_continuous_match = total_continuous_miss = 0
    total_reorder_match = total_reorder_miss = 0
    total_reorder_tris = 0
    total_continuous_tris = 0

    for mesh_idx in range(len(mesh_starts)):
        ms = mesh_starts[mesh_idx]
        me = mesh_starts[mesh_idx + 1] if mesh_idx + 1 < len(mesh_starts) else vif_end

        print(f"=== MESH {mesh_idx} ===")
        batch_idx = 0
        for verts in extract_batch(ps2_data, ms, me):
            # Method 1: Continuous strip (current behavior)
            cont_tris = continuous_strip_triangles(verts)
            cont_match, cont_miss = check_triangles(cont_tris, pc_tris)

            # Method 2: Reorder by c2[7:0]
            reord_tris, strips = reorder_strip_triangles(verts)
            reord_match, reord_miss = check_triangles(reord_tris, pc_tris)

            strip_sizes = [len(s) for s in strips]

            print(f"  Batch {batch_idx}: {len(verts)} verts")
            print(f"    Continuous: {len(cont_tris)} tris, {cont_match} MATCH, {cont_miss} MISS")
            print(f"    c2-reorder: {len(reord_tris)} tris, {reord_match} MATCH, {reord_miss} MISS "
                  f"({len(strips)} strips: {strip_sizes})")

            if reord_match > cont_match:
                print(f"    >>> REORDER IS BETTER (+{reord_match - cont_match} matches)")
            elif reord_match < cont_match:
                print(f"    >>> CONTINUOUS IS BETTER (+{cont_match - reord_match} matches)")

            # Show detailed c2_lo mapping for first batch
            if mesh_idx == 0 and batch_idx == 0:
                max_c2_lo = max(v['c2_lo'] for v in verts)
                print(f"\n    Detailed c2_lo mapping (max={max_c2_lo}):")
                print(f"    {'input':>5} {'c2':>6} {'c2_lo':>5} {'out_pos':>7} {'c2_hi':>5}")
                for v in verts:
                    out_pos = (max_c2_lo - v['c2_lo']) // 3
                    print(f"    {v['input_idx']:5d} 0x{v['c2']:04X} 0x{v['c2_lo']:02X}   {out_pos:5d}   0x{v['c2_hi']:02X}")

                print(f"\n    Reordered sub-strips:")
                for si, strip in enumerate(strips):
                    c2_los = [v['c2_lo'] for v in strip]
                    input_idxs = [v['input_idx'] for v in strip]
                    print(f"      Strip {si}: {len(strip)} verts, "
                          f"input_idxs={input_idxs}, c2_lo={[hex(c) for c in c2_los]}")

            total_continuous_match += cont_match
            total_continuous_miss += cont_miss
            total_reorder_match += reord_match
            total_reorder_miss += reord_miss
            total_reorder_tris += len(reord_tris)
            total_continuous_tris += len(cont_tris)
            batch_idx += 1

    print(f"\n{'='*70}")
    print(f"TOTALS:")
    print(f"  Continuous: {total_continuous_tris} tris, "
          f"{total_continuous_match} MATCH ({total_continuous_match*100//(total_continuous_match+total_continuous_miss)}%), "
          f"{total_continuous_miss} MISS")
    print(f"  c2-reorder: {total_reorder_tris} tris, "
          f"{total_reorder_match} MATCH ({total_reorder_match*100//(total_reorder_match+total_reorder_miss) if total_reorder_match+total_reorder_miss > 0 else 0}%), "
          f"{total_reorder_miss} MISS")
    print(f"  PC truth:   {len(pc_tris)} tris")
    improvement = total_reorder_match - total_continuous_match
    print(f"  Improvement: {improvement:+d} matches ({'+' if improvement >= 0 else ''}{improvement*100//(total_continuous_match) if total_continuous_match > 0 else 0}%)")


if __name__ == '__main__':
    main()
