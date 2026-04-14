#!/usr/bin/env python3
"""
Diagnostic: Simulate PS2 GS VRAM allocation for TEX files.

Replicates the texture.cpp LoadTextureGroup() algorithm to compute TBP
(Texture Base Pointer) addresses for each texture. Used to map GEOM DMA
chain TEX0_1 register values back to texture checksums.

VRAM allocation algorithm (from THUG texture.cpp):
  1. VramBufferBase = 0x2BC0, groups alternate via XOR 0x1E20
  2. Per group: VramStart = VramBufferBase, VramEnd = VramStart + 0x0A20
  3. Textures allocated sequentially with 8KB (0x2000) page alignment
  4. CLUTs allocated from VramEnd backwards
  5. Cache optimization: odd-indexed small textures get +16 block offset

Usage:
  python ps2_vram_sim.py <tex_file> [--geom <geom_file>]
  python ps2_vram_sim.py --batch <tex_dir> --geom-dir <geom_dir>
"""

import struct
import sys
from pathlib import Path
from collections import Counter

# PS2 GS Pixel Storage Modes
PSMCT32 = 0x00
PSMCT24 = 0x01
PSMCT16 = 0x02
PSMT8   = 0x13
PSMT4   = 0x14

PSM_NAMES = {0: 'PSMCT32', 1: 'PSMCT24', 2: 'PSMCT16', 0x13: 'PSMT8', 0x14: 'PSMT4'}

# Page sizes per PSM (from texture.cpp lines 536-553)
PAGE_SIZES = {
    PSMCT32: (64, 32),
    PSMCT24: (64, 32),
    PSMCT16: (64, 64),
    PSMT8:   (128, 64),
    PSMT4:   (128, 128),
}

# Bits per pixel
BPP = {PSMCT32: 32, PSMCT24: 24, PSMCT16: 16, PSMT8: 8, PSMT4: 4}

VRAM_BUFFER_BASE_INIT = 0x2BC0
VRAM_GROUP_SIZE = 0x0A20
VRAM_TOGGLE = 0x1E20


def read_u32(data, off):
    return struct.unpack_from('<I', data, off)[0]


def align16(off):
    return (off + 15) & ~15


def parse_tex_metadata(filepath):
    """Parse TEX file header to get texture metadata in file order.
    Returns list of (checksum, TW, TH, PSM, CPSM, MXL) tuples per group."""
    data = Path(filepath).read_bytes()
    if len(data) < 12:
        return None

    off = 0
    version = read_u32(data, off); off += 4

    # Check for RenderWare TXD (magic 0x0016)
    if version == 0x0016:
        return None  # Not supported for VRAM sim

    num_groups = read_u32(data, off); off += 4

    if version >= 3:
        _total_tex = read_u32(data, off); off += 4

    groups = []
    for _g in range(num_groups):
        group_checksum = read_u32(data, off); off += 4

        if version >= 2:
            _group_flags = read_u32(data, off); off += 4

        if version >= 4:
            _group_priority = read_u32(data, off); off += 4

        num_textures = read_u32(data, off); off += 4

        textures = []
        for _t in range(num_textures):
            if version >= 5:
                _flags = read_u32(data, off); off += 4

            checksum = read_u32(data, off); off += 4
            tw = read_u32(data, off); off += 4

            if tw == 0xFFFFFFFF:
                # Skip entry (duplicate/reference)
                textures.append(None)
                continue

            th = read_u32(data, off); off += 4
            psm = read_u32(data, off); off += 4
            cpsm = PSMCT32  # default

            if psm in (PSMT8, PSMT4):
                cpsm = read_u32(data, off); off += 4

            mxl_raw = read_u32(data, off); off += 4

            # Check for duplicate (negative MXL / bit 31)
            if mxl_raw & 0x80000000:
                textures.append(None)
                continue

            mxl = mxl_raw & 0xFF  # actual mip level count

            width = 1 << tw
            height = 1 << th
            bpp = BPP.get(psm, 32)

            # Calculate sizes to skip past data
            off = align16(off)

            # CLUT data
            if psm == PSMT8:
                palette_size = 256
            elif psm == PSMT4:
                palette_size = 16
            else:
                palette_size = 0

            if palette_size > 0:
                clut_bpp = 32 if cpsm == PSMCT32 else 16
                clut_bytes = palette_size * clut_bpp // 8
                off += clut_bytes
                off = align16(off)

            # Pixel data: mip 0 + skip remaining mips
            for j in range(mxl + 1):
                mw = max(width >> j, 1)
                mh = max(height >> j, 1)
                tex_bytes = mw * mh * bpp // 8
                off += tex_bytes
                off = align16(off)

            textures.append({
                'checksum': checksum,
                'tw': tw,
                'th': th,
                'psm': psm,
                'cpsm': cpsm,
                'mxl': mxl,
                'width': width,
                'height': height,
            })

        groups.append({
            'checksum': group_checksum,
            'textures': textures,
        })

    return {'version': version, 'groups': groups}


def simulate_vram(tex_data):
    """Simulate VRAM allocation and return TBP→checksum mapping."""
    vram_buffer_base = VRAM_BUFFER_BASE_INIT
    mapping = {}  # (TBP, CBP) → checksum
    tbp_to_checksum = {}  # TBP → checksum (simpler)

    for group in tex_data['groups']:
        vram_start = vram_buffer_base
        vram_end = vram_buffer_base + VRAM_GROUP_SIZE
        vram_buffer_base ^= VRAM_TOGGLE

        next_tbp = vram_start
        last_cbp = vram_end
        tex_count = 0

        for tex in group['textures']:
            if tex is None:
                continue

            tw = tex['tw']
            th = tex['th']
            psm = tex['psm']
            cpsm = tex['cpsm']
            mxl = tex['mxl']
            bpp = BPP.get(psm, 32)

            # Adjusted bits per texel (24-bit treated as 32-bit in VRAM)
            adj_bpp = 32 if bpp == 24 else bpp

            # Get page size
            page_w, page_h = PAGE_SIZES.get(psm, (64, 32))

            # Calculate dimensions per mip level
            widths = []
            heights = []
            tbw_list = []
            adj_widths = []
            adj_heights = []
            num_vram_bytes = []

            for j in range(mxl + 1):
                w = max((1 << tw) >> j, 1) if tw >= 0 else 0
                h = max((1 << th) >> j, 1) if th >= 0 else 0
                tbw = (w + 63) >> 6
                if bpp < 16 and tbw < 2:
                    tbw = 2

                widths.append(w)
                heights.append(h)
                tbw_list.append(tbw)

                # Adjusted dimensions
                aw = w
                ah = h
                if aw < page_w and ah > page_h:
                    aw = page_w
                if aw > page_w and ah < page_h:
                    ah = page_h
                if (tbw << 6) > aw:
                    aw = tbw << 6

                adj_widths.append(aw)
                adj_heights.append(ah)
                num_vram_bytes.append(aw * ah * adj_bpp >> 3)

            # Calculate TBP per mip level
            tbp = [0] * (mxl + 1)
            tbp[0] = next_tbp
            for j in range(1, mxl + 1):
                tbp[j] = (((tbp[j-1] << 8) + num_vram_bytes[j-1] + 0x1FFF) & 0xFFFFE000) >> 8

            # Calculate CBP
            if bpp >= 16:
                cbp = last_cbp
            elif bpp == 4:
                cbp = last_cbp - 1
            else:  # 8-bit
                cbp = last_cbp - (4 if cpsm == PSMCT32 else 2)

            # Calculate next TBP
            next_tbp = (((tbp[mxl] << 8) + num_vram_bytes[mxl] + 0x1FFF) & 0xFFFFE000) >> 8

            # Bail if VRAM overpacked
            if next_tbp > cbp:
                break

            last_cbp = cbp

            # Cache optimization: odd-indexed small textures get +16
            for j in range(mxl + 1):
                if tex_count & 1:
                    if ((bpp in (32, 8)
                         and widths[j] <= (page_w >> 1)
                         and heights[j] <= page_h)
                        or
                        (bpp in (16, 4)
                         and widths[j] <= page_w
                         and heights[j] <= (page_h >> 1))):
                        tbp[j] += 16
                tex_count += 1

            # Store mapping
            mapping[(tbp[0], cbp)] = tex['checksum']
            tbp_to_checksum[tbp[0]] = tex['checksum']

            tex['computed_tbp'] = tbp[0]
            tex['computed_cbp'] = cbp

    return mapping, tbp_to_checksum, tex_data


def extract_geom_tex0(filepath):
    """Extract TEX0_1 register values from GEOM DMA chains."""
    data = Path(filepath).read_bytes()
    if len(data) < 20:
        return {}

    ds = read_u32(data, 0)
    root_off = read_u32(data, ds)

    # Walk CGeomNode tree
    stack = [ds + root_off]
    visited = set()
    tex0_values = {}  # leaf_checksum → set of (TBP, CBP) tuples

    while stack:
        noff = stack.pop()
        if noff in visited or noff == 0xFFFFFFFF or noff + 80 > len(data):
            continue
        visited.add(noff)

        flags = read_u32(data, noff + 0x1C)
        child_dma = read_u32(data, noff + 0x20)
        sibling = read_u32(data, noff + 0x28)
        checksum = read_u32(data, noff + 0x30)
        is_leaf = bool(flags & 0x02)

        if is_leaf and child_dma != 0xFFFFFFFF:
            dma_off = ds + child_dma
            tex0_vals = _scan_gs_context(data, dma_off)
            if tex0_vals:
                tex0_values[checksum] = tex0_vals
        else:
            if child_dma != 0xFFFFFFFF:
                stack.append(ds + child_dma)
        if sibling != 0xFFFFFFFF:
            stack.append(ds + sibling)

    return tex0_values


def _vif_next(data, offset, end):
    if offset >= end or offset + 4 > len(data):
        return end
    cmd = data[offset + 3]
    if (cmd & 0x60) != 0x60:
        code = cmd & 0x7F
        if code == 0x20: return offset + 8
        elif code in (0x30, 0x31): return offset + 20
        elif code == 0x4A: return offset + (data[offset + 2] << 3) + 4
        elif code in (0x50, 0x51):
            imm = struct.unpack_from('<H', data, offset)[0]
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


def _scan_gs_context(data, dma_offset):
    """Extract (TBP, CBP) from GS context in DMA chain."""
    if dma_offset + 16 > len(data):
        return set()

    qwc = struct.unpack_from('<H', data, dma_offset)[0]
    p_start = dma_offset + 16
    p_end = dma_offset + 16 + (qwc << 4)
    if p_end > len(data):
        p_end = len(data)

    results = set()
    p = p_start

    while p < p_end and p + 4 <= len(data):
        vif = read_u32(data, p)
        cmd = (vif >> 24) & 0x7F
        num = (vif >> 16) & 0xFF

        if cmd == 0x6C and num == 1:
            next_p = p + 4 + 16
            if next_p + 4 <= len(data):
                next_vif = read_u32(data, next_p)
                next_cmd = (next_vif >> 24) & 0x7F
                next_num = (next_vif >> 16) & 0xFF

                if next_cmd == 0x68 and next_num > 0:
                    reg_start = next_p + 4
                    for i in range(next_num):
                        off = reg_start + i * 12
                        if off + 12 > len(data):
                            break
                        lo32 = read_u32(data, off)
                        hi32 = read_u32(data, off + 4)
                        reg = read_u32(data, off + 8)
                        if reg == 0x06:  # TEX0_1
                            data64 = (hi32 << 32) | lo32
                            tbp = data64 & 0x3FFF
                            cbp = (data64 >> 37) & 0x3FFF
                            results.add((tbp, cbp))

        p = _vif_next(data, p, p_end)

    return results


def main():
    args = sys.argv[1:]
    verbose = '-v' in args or '--verbose' in args
    batch = '--batch' in args

    geom_path = None
    geom_dir = None
    for i, a in enumerate(args):
        if a == '--geom' and i + 1 < len(args):
            geom_path = args[i + 1]
        elif a == '--geom-dir' and i + 1 < len(args):
            geom_dir = args[i + 1]

    clean_args = [a for a in args if not a.startswith('-')]
    # Also remove values after --geom and --geom-dir
    i = 0
    positional = []
    skip_next = False
    for a in args:
        if skip_next:
            skip_next = False
            continue
        if a in ('--geom', '--geom-dir'):
            skip_next = True
            continue
        if not a.startswith('-'):
            positional.append(a)

    if not positional:
        print('Usage: python ps2_vram_sim.py <tex_file> [--geom <geom_file>] [-v]')
        print('       python ps2_vram_sim.py --batch <tex_dir> --geom-dir <geom_dir>')
        sys.exit(1)

    path = Path(positional[0])

    if batch or path.is_dir():
        tex_dir = path
        tex_files = sorted(tex_dir.rglob('*.tex.ps2'))
        print(f'Found {len(tex_files)} TEX files')

        if geom_dir:
            geom_path_obj = Path(geom_dir)
            total_leaves = 0
            matched_leaves = 0
            unmatched_tbp = Counter()
            files_tested = 0

            for tex_file in tex_files:
                stem = tex_file.stem
                if stem.endswith('.tex'):
                    stem = stem[:-4]

                geom_file = geom_path_obj / f'{stem}.geom.ps2'
                if not geom_file.exists():
                    continue

                tex_data = parse_tex_metadata(tex_file)
                if tex_data is None:
                    continue

                mapping, tbp_map, _ = simulate_vram(tex_data)
                geom_tex0 = extract_geom_tex0(geom_file)

                file_leaves = 0
                file_matched = 0
                for leaf_ck, tex0_set in geom_tex0.items():
                    for tbp, cbp in tex0_set:
                        file_leaves += 1
                        total_leaves += 1
                        if (tbp, cbp) in mapping:
                            file_matched += 1
                            matched_leaves += 1
                        elif tbp in tbp_map:
                            # TBP matches but CBP doesn't
                            file_matched += 1
                            matched_leaves += 1
                        else:
                            unmatched_tbp[tbp] += 1

                files_tested += 1
                pct = 100 * file_matched / max(1, file_leaves)
                if verbose or pct < 90:
                    print(f'  {stem}: {file_matched}/{file_leaves} matched ({pct:.0f}%)')

            pct = 100 * matched_leaves / max(1, total_leaves)
            print(f'\n=== Summary ===')
            print(f'Files tested: {files_tested}')
            print(f'Matched: {matched_leaves}/{total_leaves} ({pct:.1f}%)')
            if unmatched_tbp:
                print(f'Unmatched TBP values: {len(unmatched_tbp)} unique')
                for tbp, cnt in unmatched_tbp.most_common(20):
                    print(f'  TBP=0x{tbp:04X}: {cnt}')
        else:
            for tf in tex_files:
                td = parse_tex_metadata(tf)
                if td is None:
                    continue
                _, tbp_map, _ = simulate_vram(td)
                print(f'  {tf.name}: {len(tbp_map)} textures, '
                      f'TBP range 0x{min(tbp_map)if tbp_map else 0:04X}-0x{max(tbp_map)if tbp_map else 0:04X}')

    else:
        tex_data = parse_tex_metadata(path)
        if tex_data is None:
            print('Failed to parse TEX file')
            sys.exit(1)

        mapping, tbp_map, tex_data = simulate_vram(tex_data)

        print(f'TEX file: {path.name} (version {tex_data["version"]})')
        print(f'Groups: {len(tex_data["groups"])}')

        total_tex = 0
        for group in tex_data['groups']:
            valid = [t for t in group['textures'] if t is not None]
            total_tex += len(valid)
            if verbose:
                print(f'\n  Group 0x{group["checksum"]:08X}: {len(valid)} textures')
                for tex in valid:
                    if 'computed_tbp' in tex:
                        psm_name = PSM_NAMES.get(tex['psm'], f'PSM_{tex["psm"]}')
                        print(f'    0x{tex["checksum"]:08X}: TBP=0x{tex["computed_tbp"]:04X} '
                              f'CBP=0x{tex["computed_cbp"]:04X} '
                              f'{tex["width"]}x{tex["height"]} {psm_name} MXL={tex["mxl"]}')

        print(f'Total textures: {total_tex}')
        print(f'Unique TBP values: {len(tbp_map)}')

        if geom_path:
            print(f'\n--- Matching against {geom_path} ---')
            geom_tex0 = extract_geom_tex0(geom_path)
            total = 0
            matched = 0
            matched_tbp_only = 0
            for leaf_ck, tex0_set in geom_tex0.items():
                for tbp, cbp in tex0_set:
                    total += 1
                    if (tbp, cbp) in mapping:
                        matched += 1
                    elif tbp in tbp_map:
                        matched_tbp_only += 1

            print(f'GEOM leaves with TEX0: {len(geom_tex0)}')
            print(f'Total TEX0 refs: {total}')
            print(f'Matched (TBP+CBP): {matched} ({100*matched/max(1,total):.1f}%)')
            print(f'Matched (TBP only): {matched+matched_tbp_only} ({100*(matched+matched_tbp_only)/max(1,total):.1f}%)')

            # Show unmatched
            unmatched = []
            for leaf_ck, tex0_set in geom_tex0.items():
                for tbp, cbp in tex0_set:
                    if (tbp, cbp) not in mapping and tbp not in tbp_map:
                        unmatched.append((tbp, cbp))
            if unmatched:
                print(f'\nUnmatched ({len(unmatched)}):')
                for tbp, cbp in sorted(set(unmatched))[:20]:
                    print(f'  TBP=0x{tbp:04X} CBP=0x{cbp:04X}')


if __name__ == '__main__':
    main()
