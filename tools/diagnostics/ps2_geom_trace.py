#!/usr/bin/env python3
"""
Diagnostic: Parse PS2 GEOM files (.geom.ps2) and dump CGeomNode tree structure.
Validates binary format understanding before C# implementation.

GEOM files contain pre-compiled CGeomNode rendering trees with embedded VIF/DMA chains.
This is the PS2 PIP (Pre-compiled In-Place) format used for level geometry.

File format (from THUG source geomnode.cpp sProcessInPlace):
  [0x00] u32: data_section_offset (skip to node data, typically 16)
  [0x04] u32: hierarchy_array_offset (from file start)
  [0x08] u32: reserved
  [0x0C] u32: hierarchy_array_count

  At data_section_offset:
    [+0x00] u32: root_node_offset (relative to data section)
    [+0x04...] DMA/VIF data, CGeomNode instances

Usage:
  python ps2_geom_trace.py <geom_file>
  python ps2_geom_trace.py --batch <directory>
"""

import struct
import sys
from pathlib import Path


def read_u8(data, offset):
    return data[offset]

def read_s8(data, offset):
    return struct.unpack_from('<b', data, offset)[0]

def read_u16(data, offset):
    return struct.unpack_from('<H', data, offset)[0]

def read_u32(data, offset):
    return struct.unpack_from('<I', data, offset)[0]

def read_s32(data, offset):
    return struct.unpack_from('<i', data, offset)[0]

def read_f32(data, offset):
    return struct.unpack_from('<f', data, offset)[0]


# CGeomNode flags (from geomnode.h)
NODEFLAG_ACTIVE   = 1 << 0
NODEFLAG_LEAF     = 1 << 1
NODEFLAG_OBJECT   = 1 << 2
NODEFLAG_SKINNED  = 1 << 11
NODEFLAG_SKELETAL = 1 << 11
NODEFLAG_ENVMAPPED = 1 << 14
NODEFLAG_BILLBOARD = 1 << 15

NODE_FLAGS = {
    0: "ACTIVE", 1: "LEAF", 2: "OBJECT", 3: "TRANSFORMED",
    4: "COLOURED", 5: "SKY", 6: "ZPUSH0", 7: "ZPUSH1",
    8: "NOSHADOW", 9: "UVWIBBLE", 10: "VCWIBBLE",
    11: "SKINNED", 12: "INSTANCE", 13: "OBJECT_LIGHTS",
    14: "ENVMAPPED", 15: "BILLBOARD", 16: "EXPLICIT_UVWIBBLE",
    17: "ZPUSH2", 18: "ZPUSH3", 19: "BLACKFOG",
}


def decode_node_flags(flags):
    names = []
    for bit, name in sorted(NODE_FLAGS.items()):
        if flags & (1 << bit):
            names.append(name)
    vis = (flags >> 24) & 0xFF
    if vis:
        names.append(f"VIS=0x{vis:02X}")
    return names


def vif_next_code(data, offset, end):
    """Advance past one VIF opcode. Returns next opcode offset.
    Port of vif::NextCode from vif.cpp."""
    if offset >= end or offset + 4 > len(data):
        return end

    cmd = data[offset + 3]

    if (cmd & 0x60) != 0x60:
        # Non-UNPACK commands
        code = cmd & 0x7F
        if code in (0x00, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
                    0x10, 0x11, 0x13, 0x14, 0x15, 0x17):
            return offset + 4
        elif code == 0x01:  # STCYCL
            return offset + 4
        elif code == 0x20:  # STMASK
            return offset + 8
        elif code in (0x30, 0x31):  # STROW, STCOL
            return offset + 20
        elif code == 0x4A:  # MPG
            return offset + (data[offset + 2] << 3) + 4
        elif code in (0x50, 0x51):  # DIRECT, DIRECTHL
            imm = read_u16(data, offset)
            return offset + (imm << 4) + 4
        else:
            return end  # unknown, bail
    else:
        # UNPACK
        vn = (cmd >> 2) & 3
        vl = cmd & 3
        num = data[offset + 2]
        if num == 0:
            num = 256
        dimension = vn + 1
        bitlength = 32 >> vl
        data_size = (((bitlength * dimension * num + 31) >> 5) << 2)
        return offset + 4 + data_size


def count_vertices_in_dma(data, dma_offset):
    """Count vertices by scanning for STMOD(1) + UNPACK pattern.
    Port of dma::GetNumVertices from dma.cpp."""
    if dma_offset + 8 > len(data):
        return 0, 0

    qwc = read_u16(data, dma_offset)
    p_start = dma_offset + 8
    p_end = dma_offset + 16 + (qwc << 4)

    if p_end > len(data):
        return 0, 0

    p_code = p_start
    num_verts = 0
    num_tris = 0
    num_batches = 0

    while p_code < p_end and p_code + 4 <= len(data):
        # Check for STMOD(1): byte3 & 0x7F == 0x05, byte0 == 1
        byte3 = data[p_code + 3]
        byte0 = data[p_code]
        if (byte3 & 0x7F) == 0x05 and byte0 == 1:
            # Advance past STMOD
            p_code = vif_next_code(data, p_code, p_end)
            if p_code >= p_end or p_code + 4 > len(data):
                break

            # Check for UNPACK V4_16 or V4_32
            ucmd = data[p_code + 3]
            if (ucmd & 0x7E) == 0x6C:
                batch_verts = data[p_code + 2]
                if batch_verts == 0:
                    batch_verts = 256
                num_verts += batch_verts
                num_batches += 1

                # Count triangles (non-ADC vertices)
                is_v4_16 = (ucmd & 0x01) != 0
                unpack_data_start = p_code + 4

                for v in range(batch_verts):
                    if is_v4_16:
                        # V4_16: ADC at 5th uint16 from unpack start
                        adc_off = unpack_data_start + v * 8 + 6
                        if adc_off + 2 <= len(data):
                            adc = read_u16(data, adc_off)
                            if (adc & 0x8000) == 0:
                                num_tris += 1
                    else:
                        # V4_32: ADC at 4th uint32
                        adc_off = unpack_data_start + v * 16 + 12
                        if adc_off + 4 <= len(data):
                            adc = read_u32(data, adc_off)
                            if (adc & 0x8000) == 0:
                                num_tris += 1

        p_code = vif_next_code(data, p_code, p_end)

    return num_verts, num_tris


def count_unpack_types(data, dma_offset):
    """Scan VIF chain and count UNPACK instructions by type."""
    if dma_offset + 8 > len(data):
        return {}

    qwc = read_u16(data, dma_offset)
    p_start = dma_offset + 8
    p_end = dma_offset + 16 + (qwc << 4)

    if p_end > len(data):
        return {}

    FORMAT_NAMES = {
        0x60: "S_32", 0x61: "S_16", 0x62: "S_8",
        0x64: "V2_32", 0x65: "V2_16", 0x66: "V2_8",
        0x68: "V3_32", 0x69: "V3_16", 0x6A: "V3_8",
        0x6C: "V4_32", 0x6D: "V4_16", 0x6E: "V4_8",
    }

    counts = {}
    p_code = p_start
    while p_code < p_end and p_code + 4 <= len(data):
        cmd = data[p_code + 3]
        if (cmd & 0x60) == 0x60:  # UNPACK
            fmt = cmd & 0x7F
            name = FORMAT_NAMES.get(fmt & 0x6F, f"UNK_0x{fmt:02X}")
            num = data[p_code + 2]
            if num == 0:
                num = 256
            counts[name] = counts.get(name, 0) + num
        p_code = vif_next_code(data, p_code, p_end)

    return counts


def read_node_at(data, base, node_offset):
    """Read a single CGeomNode at the given offset. Returns node_info dict."""
    abs_offset = base + node_offset
    if abs_offset + 80 > len(data):
        return None

    sx = read_f32(data, abs_offset + 0)
    sy = read_f32(data, abs_offset + 4)
    sz = read_f32(data, abs_offset + 8)
    sr = read_f32(data, abs_offset + 12)
    flags = read_u32(data, abs_offset + 28)
    u1 = read_s32(data, abs_offset + 32)
    sibling = read_s32(data, abs_offset + 40)
    u3 = read_u32(data, abs_offset + 44)
    checksum = read_u32(data, abs_offset + 48)
    colour = read_u32(data, abs_offset + 60)
    texture_ck = read_u32(data, abs_offset + 68)
    next_lod = read_s32(data, abs_offset + 76)

    flag_names = decode_node_flags(flags)
    is_leaf = bool(flags & NODEFLAG_LEAF)

    node_info = {
        'offset': node_offset,
        'abs_offset': abs_offset,
        'checksum': checksum,
        'flags': flags,
        'flag_names': flag_names,
        'is_leaf': is_leaf,
        'sphere': (sx, sy, sz, sr),
        'colour': colour,
        'group_ck': u3,
        'texture_ck': texture_ck,
        'num_verts': 0,
        'num_tris': 0,
        'unpack_types': {},
        'child_offset': u1 if (not is_leaf and u1 != -1) else -1,
        'dma_raw': u1 if (is_leaf and u1 != -1) else -1,
        'sibling_offset': sibling,
        'next_lod_offset': next_lod,
    }

    if is_leaf and u1 != -1:
        dma_abs = base + u1
        if dma_abs + 8 <= len(data):
            verts, tris = count_vertices_in_dma(data, dma_abs)
            node_info['num_verts'] = verts
            node_info['num_tris'] = tris
            node_info['dma_offset'] = u1
            node_info['unpack_types'] = count_unpack_types(data, dma_abs)

    return node_info


def parse_node_tree(data, base, root_offset):
    """Iteratively walk the CGeomNode tree. Returns list of (depth, node_info) tuples."""
    results = []
    # Stack of (node_offset, depth)
    stack = [(root_offset, 0)]
    visited = set()

    while stack:
        node_offset, depth = stack.pop()

        if node_offset == -1 or node_offset in visited:
            continue
        visited.add(node_offset)

        node = read_node_at(data, base, node_offset)
        if node is None:
            continue

        results.append((depth, node))

        # Push in reverse order so children are processed before siblings
        # (stack is LIFO, so push sibling first, then LOD, then child)
        if node['next_lod_offset'] != -1:
            stack.append((node['next_lod_offset'], depth))
        if node['sibling_offset'] != -1:
            stack.append((node['sibling_offset'], depth))
        if node['child_offset'] != -1:
            stack.append((node['child_offset'], depth + 1))

    return results


def parse_geom(filepath, verbose=False):
    """Parse a .geom.ps2 file and return summary info."""
    data = Path(filepath).read_bytes()
    if len(data) < 20:
        return {'error': f'File too small: {len(data)} bytes'}

    # Header
    data_section_offset = read_u32(data, 0)
    hier_offset = read_u32(data, 4)
    reserved = read_u32(data, 8)
    hier_count = read_u32(data, 12)

    if data_section_offset > len(data):
        return {'error': f'data_section_offset 0x{data_section_offset:X} beyond file size 0x{len(data):X}'}

    base = data_section_offset
    root_node_offset = read_s32(data, base)

    if root_node_offset < 0 or base + root_node_offset + 80 > len(data):
        return {'error': f'root_node_offset 0x{root_node_offset:X} invalid'}

    # Walk node tree
    nodes = parse_node_tree(data, base, root_node_offset)

    total_leaves = sum(1 for _, n in nodes if n['is_leaf'])
    total_verts = sum(n['num_verts'] for _, n in nodes if n['is_leaf'])
    total_tris = sum(n['num_tris'] for _, n in nodes if n['is_leaf'])
    total_nodes = len(nodes)

    # Aggregate UNPACK types across all leaves
    all_unpack_types = {}
    for _, n in nodes:
        if n['is_leaf']:
            for k, v in n.get('unpack_types', {}).items():
                all_unpack_types[k] = all_unpack_types.get(k, 0) + v

    result = {
        'file_size': len(data),
        'data_section_offset': data_section_offset,
        'hier_offset': hier_offset,
        'hier_count': hier_count,
        'root_node_offset': root_node_offset,
        'total_nodes': total_nodes,
        'total_leaves': total_leaves,
        'total_verts': total_verts,
        'total_tris': total_tris,
        'unpack_types': all_unpack_types,
    }

    if verbose:
        result['nodes'] = nodes

    return result


def print_result(filepath, result, verbose=False):
    name = Path(filepath).name
    print(f"=== {name} ===")

    if 'error' in result:
        print(f"  ERROR: {result['error']}")
        return

    print(f"  Size: {result['file_size']:,} bytes")
    print(f"  Header: data_off=0x{result['data_section_offset']:X}, "
          f"hier_off=0x{result['hier_offset']:X}, hier_count={result['hier_count']}")
    print(f"  Root node offset: 0x{result['root_node_offset']:X}")
    print(f"  Nodes: {result['total_nodes']} total, {result['total_leaves']} leaves")
    print(f"  Vertices: {result['total_verts']:,}")
    print(f"  Triangles: {result['total_tris']:,}")
    if result['unpack_types']:
        types_str = ", ".join(f"{k}={v}" for k, v in sorted(result['unpack_types'].items()))
        print(f"  UNPACK types: {types_str}")

    if verbose and 'nodes' in result:
        print()
        for depth, node in result['nodes']:
            indent = "  " + "  " * depth
            kind = "LEAF" if node['is_leaf'] else "NODE"
            flags_str = ",".join(node['flag_names'])
            ck_str = f"0x{node['checksum']:08X}" if node['checksum'] else "0"
            line = f"{indent}{kind} ck={ck_str} flags=[{flags_str}]"

            if node['is_leaf']:
                line += f" verts={node['num_verts']} tris={node['num_tris']}"
                if node['texture_ck']:
                    line += f" tex=0x{node['texture_ck']:08X}"
                if node['group_ck']:
                    line += f" grp=0x{node['group_ck']:08X}"
                if node.get('unpack_types'):
                    line += f" [{','.join(f'{k}={v}' for k,v in sorted(node['unpack_types'].items()))}]"
            else:
                sx, sy, sz, sr = node['sphere']
                line += f" sphere=({sx:.1f},{sy:.1f},{sz:.1f},r={sr:.1f})"

            print(line)


def batch_process(directory):
    geom_files = sorted(Path(directory).rglob("*.geom.ps2"))
    if not geom_files:
        print(f"No .geom.ps2 files found in {directory}")
        return

    total = len(geom_files)
    success = 0
    failed = 0
    empty = 0
    total_verts = 0
    total_tris = 0
    total_leaves = 0

    for filepath in geom_files:
        try:
            result = parse_geom(filepath)
            if 'error' in result:
                print(f"  FAIL: {filepath.name}: {result['error']}")
                failed += 1
            elif result['total_verts'] == 0:
                empty += 1
                success += 1
            else:
                success += 1
                total_verts += result['total_verts']
                total_tris += result['total_tris']
                total_leaves += result['total_leaves']
        except Exception as e:
            print(f"  FAIL: {filepath.name}: {e}")
            failed += 1

    print(f"\n=== Batch Summary ===")
    print(f"  Files: {total}")
    print(f"  Success: {success} ({success*100//total}%)")
    print(f"  Failed: {failed}")
    print(f"  Empty (0 verts): {empty}")
    print(f"  Total leaves: {total_leaves:,}")
    print(f"  Total vertices: {total_verts:,}")
    print(f"  Total triangles: {total_tris:,}")


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <geom_file> [-v]")
        print(f"       {sys.argv[0]} --batch <directory>")
        sys.exit(1)

    if sys.argv[1] == '--batch':
        if len(sys.argv) < 3:
            print("Usage: --batch <directory>")
            sys.exit(1)
        batch_process(sys.argv[2])
    else:
        verbose = '-v' in sys.argv
        filepath = sys.argv[1]
        result = parse_geom(filepath, verbose=verbose)
        print_result(filepath, result, verbose=verbose)
