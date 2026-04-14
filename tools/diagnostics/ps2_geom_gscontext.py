#!/usr/bin/env python3
"""
Diagnostic: Decode GS context register writes from THPS4 GEOM DMA chains.

THPS4 GEOM leaf nodes have texture_checksum=0 at CGeomNode +0x44.
Instead, texture references are encoded as GS register writes in the
DMA chain's VU1 input data:

  1. UNPACK V4_32 NUM=1 → GIF tag (A_D, NLOOP=N)
  2. UNPACK V3_32 NUM=N → N × (data_lo32, data_hi32, reg_addr)

Register writes typically include:
  ALPHA_1, TEST_1, TEX0_1, TEX1_1, CLAMP_1, MIPTBP1_1, MIPTBP2_1

The TEX0_1 register contains the texture base pointer (TBP0) which
identifies which texture is being used.

Usage:
  python ps2_geom_gscontext.py <geom_file> [-v]
  python ps2_geom_gscontext.py --batch <directory> [--summary]
"""

import struct
import sys
from pathlib import Path
from collections import Counter

GS_REG = {
    0x00: 'PRIM', 0x01: 'RGBAQ', 0x02: 'ST', 0x03: 'UV',
    0x04: 'XYZF2', 0x05: 'XYZ2', 0x06: 'TEX0_1', 0x07: 'TEX0_2',
    0x08: 'CLAMP_1', 0x09: 'CLAMP_2', 0x0A: 'FOG',
    0x14: 'TEX1_1', 0x15: 'TEX1_2',
    0x34: 'MIPTBP1_1', 0x36: 'MIPTBP2_1',
    0x42: 'ALPHA_1', 0x47: 'TEST_1',
    0x4C: 'FRAME_1', 0x4E: 'ZBUF_1',
}

PSM = {
    0: 'PSMCT32', 1: 'PSMCT24', 2: 'PSMCT16', 10: 'PSMCT16S',
    19: 'PSMT8', 20: 'PSMT4', 27: 'PSMT8H', 36: 'PSMT4HL', 44: 'PSMT4HH',
}


def read_u16(data, off):
    return struct.unpack_from('<H', data, off)[0]

def read_u32(data, off):
    return struct.unpack_from('<I', data, off)[0]

def read_u64(data, off):
    return struct.unpack_from('<Q', data, off)[0]

def read_f32(data, off):
    return struct.unpack_from('<f', data, off)[0]


def decode_tex0(val):
    """Decode 64-bit TEX0 register value."""
    return {
        'TBP0': val & 0x3FFF,
        'TBW': (val >> 14) & 0x3F,
        'PSM': (val >> 20) & 0x3F,
        'TW': (val >> 26) & 0xF,
        'TH': (val >> 30) & 0xF,
        'width': 1 << ((val >> 26) & 0xF),
        'height': 1 << ((val >> 30) & 0xF),
        'TCC': (val >> 34) & 1,
        'TFX': (val >> 35) & 3,
        'CBP': (val >> 37) & 0x3FFF,
        'CPSM': (val >> 51) & 0xF,
        'CSM': (val >> 55) & 1,
        'CSA': (val >> 56) & 0x1F,
        'CLD': (val >> 61) & 7,
    }


def vif_next_code(data, offset, end):
    """Advance past one VIF opcode."""
    if offset >= end or offset + 4 > len(data):
        return end
    cmd = data[offset + 3]
    if (cmd & 0x60) != 0x60:
        code = cmd & 0x7F
        if code == 0x20: return offset + 8
        elif code in (0x30, 0x31): return offset + 20
        elif code == 0x4A: return offset + (data[offset + 2] << 3) + 4
        elif code in (0x50, 0x51):
            imm = read_u16(data, offset)
            return offset + (imm << 4) + 4
        else: return offset + 4
    else:
        vn = (cmd >> 2) & 3
        vl = cmd & 3
        num = data[offset + 2] or 256
        dim = vn + 1
        blen = 32 >> vl
        size = (((blen * dim * num + 31) >> 5) << 2)
        return offset + 4 + size


def extract_gs_context(data, dma_offset, verbose=False):
    """Extract GS register writes from DMA chain VU1 input.

    Pattern: UNPACK V4_32 NUM=1 (GIF tag) followed by UNPACK V3_32 NUM=N
    (register writes as data_lo32, data_hi32, reg_addr triplets).
    """
    if dma_offset + 16 > len(data):
        return []

    qwc = read_u16(data, dma_offset)
    p_start = dma_offset + 16
    p_end = dma_offset + 16 + (qwc << 4)
    if p_end > len(data):
        p_end = len(data)

    results = []
    p = p_start

    while p < p_end and p + 4 <= len(data):
        vif = read_u32(data, p)
        cmd = (vif >> 24) & 0x7F
        num = (vif >> 16) & 0xFF

        # Look for UNPACK V4_32 NUM=1 (GIF tag)
        if cmd == 0x6C and num == 1:
            gif_off = p + 4
            if gif_off + 16 > len(data):
                p = vif_next_code(data, p, p_end)
                continue

            # Read GIF tag
            gif_lo = read_u64(data, gif_off)
            nloop = gif_lo & 0x7FFF
            flg = (gif_lo >> 58) & 3
            nreg = (gif_lo >> 60) & 0xF or 16

            # Check next VIF code for V3_32
            next_p = p + 4 + 16  # after UNPACK header + 1 quadword
            if next_p + 4 <= len(data):
                next_vif = read_u32(data, next_p)
                next_cmd = (next_vif >> 24) & 0x7F
                next_num = (next_vif >> 16) & 0xFF

                if next_cmd == 0x68 and next_num > 0:
                    # UNPACK V3_32: GS register writes
                    reg_data_start = next_p + 4
                    batch_regs = []

                    for i in range(next_num):
                        off = reg_data_start + i * 12
                        if off + 12 > len(data):
                            break
                        lo32 = read_u32(data, off)
                        hi32 = read_u32(data, off + 4)
                        reg = read_u32(data, off + 8)
                        data64 = (hi32 << 32) | lo32

                        reg_name = GS_REG.get(reg, f'REG_0x{reg:02X}')
                        batch_regs.append((reg, reg_name, data64))

                        if verbose:
                            print(f'      {reg_name:12s} = 0x{data64:016X}', end='')
                            if reg == 0x06:
                                t = decode_tex0(data64)
                                psm_name = PSM.get(t['PSM'], f"PSM_{t['PSM']}")
                                print(f'  -> TBP=0x{t["TBP0"]:04X} '
                                      f'{t["width"]}x{t["height"]} {psm_name} '
                                      f'TBW={t["TBW"]} CBP=0x{t["CBP"]:04X} CLD={t["CLD"]}')
                            else:
                                print()

                    results.append(batch_regs)

        p = vif_next_code(data, p, p_end)

    return results


def parse_file(filepath, verbose=False):
    """Parse GEOM file and extract GS context per leaf."""
    data = Path(filepath).read_bytes()
    if len(data) < 20:
        return None

    ds = read_u32(data, 0)
    root_off = read_u32(data, ds)

    # Walk CGeomNode tree
    stack = [ds + root_off]
    visited = set()
    leaves = []

    while stack:
        noff = stack.pop()
        if noff in visited or noff == 0xFFFFFFFF or noff + 80 > len(data):
            continue
        visited.add(noff)

        flags = read_u32(data, noff + 0x1C)
        child_dma = read_u32(data, noff + 0x20)
        sibling = read_u32(data, noff + 0x28)
        group_ck = read_u32(data, noff + 0x2C)
        checksum = read_u32(data, noff + 0x30)
        tex_ck = read_u32(data, noff + 0x44)
        is_leaf = bool(flags & 0x02)

        if is_leaf and child_dma != 0xFFFFFFFF:
            leaves.append({
                'offset': noff,
                'dma_offset': ds + child_dma,
                'checksum': checksum,
                'group_ck': group_ck,
                'tex_ck': tex_ck,
            })
        else:
            if child_dma != 0xFFFFFFFF:
                stack.append(ds + child_dma)
        if sibling != 0xFFFFFFFF:
            stack.append(ds + sibling)

    # Extract GS context from each leaf
    all_tex0 = []
    leaves_with_tex0 = 0
    tex0_per_leaf = {}

    for leaf in leaves:
        if verbose:
            print(f'  Leaf 0x{leaf["checksum"]:08X} (grp=0x{leaf["group_ck"]:08X}):')

        batches = extract_gs_context(data, leaf['dma_offset'], verbose=verbose)

        leaf_tex0 = set()
        for batch in batches:
            for reg, name, val in batch:
                if reg == 0x06:  # TEX0_1
                    leaf_tex0.add(val)
                    all_tex0.append(val)

        if leaf_tex0:
            leaves_with_tex0 += 1
            tex0_per_leaf[leaf['checksum']] = leaf_tex0

    return {
        'path': str(filepath),
        'leaves': len(leaves),
        'leaves_with_tex0': leaves_with_tex0,
        'all_tex0': all_tex0,
        'unique_tex0': set(all_tex0),
        'tex0_per_leaf': tex0_per_leaf,
    }


def main():
    args = sys.argv[1:]
    verbose = '-v' in args or '--verbose' in args
    summary = '--summary' in args
    batch = '--batch' in args
    args = [a for a in args if not a.startswith('-')]

    if not args:
        print('Usage: python ps2_geom_gscontext.py <file> [-v]')
        print('       python ps2_geom_gscontext.py --batch <dir> [--summary]')
        sys.exit(1)

    path = Path(args[0])

    if batch or path.is_dir():
        files = sorted(path.rglob('*.geom.ps2'))
        print(f'Scanning {len(files)} GEOM files...')

        total_leaves = 0
        total_with_tex0 = 0
        files_with_tex0 = 0
        all_unique_tex0 = set()
        all_dims = Counter()
        all_psm = Counter()
        all_tbp = Counter()

        for f in files:
            r = parse_file(f)
            if r is None:
                continue
            total_leaves += r['leaves']
            total_with_tex0 += r['leaves_with_tex0']
            if r['leaves_with_tex0'] > 0:
                files_with_tex0 += 1

            for val in r['unique_tex0']:
                all_unique_tex0.add(val)
                t = decode_tex0(val)
                all_dims[f'{t["width"]}x{t["height"]}'] += 1
                all_psm[PSM.get(t['PSM'], f'PSM_{t["PSM"]}')] += 1
                all_tbp[t['TBP0']] += 1

            if not summary:
                n_tex = len(r['unique_tex0'])
                print(f'  {f.name}: {r["leaves"]} leaves, '
                      f'{r["leaves_with_tex0"]}/{r["leaves"]} with TEX0, '
                      f'{n_tex} unique TEX0')

        print(f'\n=== Summary ===')
        print(f'Files: {len(files)}, {files_with_tex0} with TEX0')
        print(f'Leaves: {total_leaves}, {total_with_tex0} with TEX0 '
              f'({100*total_with_tex0/max(1,total_leaves):.1f}%)')
        print(f'Unique TEX0 values: {len(all_unique_tex0)}')
        print(f'Unique TBP values: {len(all_tbp)}')
        print(f'\nPixel formats:')
        for name, cnt in all_psm.most_common():
            print(f'  {name}: {cnt}')
        print(f'\nDimensions:')
        for dim, cnt in all_dims.most_common(20):
            print(f'  {dim}: {cnt}')
        if len(all_tbp) <= 100:
            print(f'\nTBP values (VRAM addresses):')
            for tbp, cnt in sorted(all_tbp.items()):
                print(f'  0x{tbp:04X} (byte offset 0x{tbp*256:X}): {cnt} unique TEX0 entries')

    else:
        r = parse_file(path, verbose=verbose)
        if r is None:
            print('Failed to parse')
            sys.exit(1)

        print(f'\n=== {Path(filepath).name if "filepath" in dir() else path.name} ===')
        print(f'Leaves: {r["leaves"]}, with TEX0: {r["leaves_with_tex0"]}')
        print(f'TEX0 refs: {len(r["all_tex0"])}, unique: {len(r["unique_tex0"])}')

        if r['unique_tex0']:
            print(f'\nUnique TEX0 values:')
            for val in sorted(r['unique_tex0']):
                t = decode_tex0(val)
                psm_name = PSM.get(t['PSM'], f'PSM_{t["PSM"]}')
                print(f'  0x{val:016X}: TBP=0x{t["TBP0"]:04X} '
                      f'{t["width"]}x{t["height"]} {psm_name} '
                      f'TBW={t["TBW"]} CBP=0x{t["CBP"]:04X} CLD={t["CLD"]}')


if __name__ == '__main__':
    main()
