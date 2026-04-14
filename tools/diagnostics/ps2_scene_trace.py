"""Trace PS2 scene file parsing following THUG source code (native PS2 format).

Key findings:
- THPS4 (mat=3, mesh=4): normals are 3 floats (12B) per vertex
- THUG  (mat=5, mesh=6): normals are packed uint32 (4B) per vertex
- THUG2 (mat=6, mesh=6): same as THUG, plus specular_power field per material

Usage:
    python ps2_scene_trace.py <file>              # verbose trace
    python ps2_scene_trace.py --batch <directory>  # batch validate all .ps2 files
"""
import struct
import sys
import os


def parse_file(data, verbose=True):
    """Parse a PS2 scene file and return (success, message)."""
    def p(*args, **kwargs):
        if verbose:
            print(*args, **kwargs)

    if len(data) < 16:
        return False, "File too small"

    off = 0

    # Version triple
    mat_ver = struct.unpack_from('<I', data, off)[0]; off += 4
    mesh_ver = struct.unpack_from('<I', data, off)[0]; off += 4
    vert_ver = struct.unpack_from('<I', data, off)[0]; off += 4
    p(f'Versions: mat={mat_ver}, mesh={mesh_ver}, vert={vert_ver}')

    if mat_ver not in (3, 5, 6) or mesh_ver not in (4, 6) or vert_ver != 1:
        return False, f"Unexpected version triple ({mat_ver},{mesh_ver},{vert_ver})"

    # Materials (per material.cpp LoadMaterials)
    num_mats = struct.unpack_from('<I', data, off)[0]; off += 4
    p(f'num_materials: {num_mats}')

    if num_mats > 1000:
        return False, f"Implausible num_materials={num_mats}"

    for mi in range(num_mats):
        if off + 4 > len(data):
            return False, f"EOF in material {mi}"

        p(f'\n--- Material {mi} at offset {off} (0x{off:X}) ---')
        checksum = struct.unpack_from('<I', data, off)[0]; off += 4
        p(f'  Checksum: 0x{checksum:08X}')

        flags = 0

        # mat_ver >= 5? flags read here
        if mat_ver >= 5:
            flags = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  Flags (ver5+): 0x{flags:08X}')

        # Aref
        if mat_ver >= 2:
            if mat_ver >= 3:
                aref = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'  Aref (4-byte padded): {aref & 0xFF}')
            else:
                aref = data[off]; off += 1
                p(f'  Aref (1-byte): {aref}')

        # Texture checksum
        if mat_ver >= 5:
            if flags & (1 << 11):  # MATFLAG_ANIMATED_TEX
                p(f'  Animated texture (not fully parsed)')
                # Skip animated texture data
                num_tex = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'  NumAnimTextures: {num_tex}')
                for _ in range(num_tex):
                    off += 4  # each texture checksum
                tex_checksum = 0  # no single tex
            else:
                tex_checksum = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'  TextureChecksum: 0x{tex_checksum:08X}')
        else:
            tex_checksum = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  TextureChecksum: 0x{tex_checksum:08X}')

        # Group checksum (mat_ver >= 3)
        if mat_ver >= 3:
            group_checksum = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  GroupChecksum: 0x{group_checksum:08X}')

        # Flags (mat_ver < 5)
        if mat_ver < 5:
            flags = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  Flags (ver<5): 0x{flags:08X}')

        # RegALPHA (u64)
        reg_alpha = struct.unpack_from('<Q', data, off)[0]; off += 8
        p(f'  RegALPHA: 0x{reg_alpha:016X}')

        # Clamp (mat_ver >= 2)
        if mat_ver >= 2:
            clamp_u = struct.unpack_from('<I', data, off)[0]; off += 4
            clamp_v = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  ClampU: {clamp_u}, ClampV: {clamp_v}')

        # Skip 36 bytes (material colours: ambient/diffuse/specular RGB + alpha)
        off += 36
        p(f'  (skip 36 bytes material colours)')

        # UV wibble (MATFLAG_UV_WIBBLE = 1<<0)
        if flags & 0x01:
            # THPS4 (mat_ver<=4): 32 bytes, THUG+ (mat_ver>=5): 40 bytes
            uv_wibble_size = 32 if mat_ver <= 4 else 40
            off += uv_wibble_size
            p(f'  (skip {uv_wibble_size} bytes UV wibble)')

        # VC wibble (MATFLAG_VC_WIBBLE = 1<<1)
        # Per sequence: num_keys(u32) + phase(i32) + num_keys × 20 bytes
        # Key struct: time(u32) + RGBA(4×f32) = 20 bytes
        # Note: THUG source material.cpp omits the phase read (bug), but files include it
        if flags & 0x02:
            if off + 4 > len(data):
                return False, f"EOF in VC wibble header"
            num_seqs = struct.unpack_from('<I', data, off)[0]; off += 4
            if num_seqs > 100:
                return False, f"Implausible VC wibble num_seqs={num_seqs}"
            for s in range(num_seqs):
                if off + 8 > len(data):
                    return False, f"EOF in VC wibble seq {s}"
                num_keys = struct.unpack_from('<I', data, off)[0]; off += 4
                if num_keys > 10000:
                    return False, f"Implausible VC wibble num_keys={num_keys}"
                off += 4  # phase (i32)
                off += 20 * num_keys
            p(f'  (skip VC wibble, {num_seqs} seqs)')

        # Mipmap
        if tex_checksum:
            mmag = struct.unpack_from('<I', data, off)[0]; off += 4
            mmin = struct.unpack_from('<I', data, off)[0]; off += 4
            k_float = struct.unpack_from('<f', data, off)[0]; off += 4
            p(f'  Mipmap: mmag={mmag}, mmin={mmin}, K={k_float}')
            if mat_ver <= 1:
                l_val = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'  L: {l_val}')
        else:
            skip = 12 + 4 * (mat_ver == 1)
            off += skip
            p(f'  (skip {skip} bytes mipmap, no texture)')

        # Reflection map scale (mat_ver >= 4)
        if mat_ver >= 4:
            ref_u = struct.unpack_from('<f', data, off)[0]; off += 4
            ref_v = struct.unpack_from('<f', data, off)[0]; off += 4
            p(f'  RefMapScale: U={ref_u}, V={ref_v}')

        # THUG2 (mat_ver >= 6): shader_id (was specular_power in cross-platform format)
        # Native PS2 format: always 4 bytes, no conditional color data
        if mat_ver >= 6:
            shader_id = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'  ShaderID: 0x{shader_id:08X}')

    p(f'\nAfter materials: offset={off} (0x{off:X})')

    # Mesh groups
    if off + 4 > len(data):
        return False, "EOF before NumGroups"
    num_groups = struct.unpack_from('<I', data, off)[0]; off += 4
    p(f'NumGroups: {num_groups}')

    if num_groups > 10000:
        return False, f"Implausible NumGroups={num_groups}"

    total_meshes = 0
    if mesh_ver >= 4:
        total_meshes = struct.unpack_from('<I', data, off)[0]; off += 4
        p(f'TotalNumMeshes: {total_meshes}')

    total_verts_parsed = 0

    # LoadMeshGroup per group
    for gi in range(num_groups):
        if off + 8 > len(data):
            return False, f"EOF in group {gi} header"

        p(f'\n--- Group {gi} at offset {off} (0x{off:X}) ---')
        grp_checksum = struct.unpack_from('<I', data, off)[0]; off += 4
        num_meshes_in_grp = struct.unpack_from('<I', data, off)[0]; off += 4
        p(f'  GroupChecksum: 0x{grp_checksum:08X}')
        p(f'  NumMeshes: {num_meshes_in_grp}')

        for mi2 in range(num_meshes_in_grp):
            if off + 4 > len(data):
                return False, f"EOF in mesh {mi2} of group {gi}"

            p(f'\n  --- Mesh {mi2} at offset {off} (0x{off:X}) ---')
            mesh_checksum = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'    Checksum: 0x{mesh_checksum:08X}')

            if mesh_ver >= 2:
                lod1 = struct.unpack_from('<I', data, off)[0]; off += 4
                lod2 = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'    LOD stuff: {lod1}, {lod2}')

                hier_data = struct.unpack_from('<I', data, off)[0]; off += 4
                num_child = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'    Hierarchy: data=0x{hier_data:X}, num_child={num_child}')
                if num_child > 100:
                    return False, f"Implausible num_child={num_child}"
                for c in range(num_child):
                    cd = struct.unpack_from('<I', data, off)[0]; off += 4
                    p(f'      child[{c}]: 0x{cd:X}')

                sphere = struct.unpack_from('<4f', data, off); off += 16
                p(f'    Sphere: ({sphere[0]:.4f}, {sphere[1]:.4f}, {sphere[2]:.4f}, r={sphere[3]:.4f})')

            # Material checksum
            mat_cs = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'    MaterialChecksum: 0x{mat_cs:08X}')

            # Mesh flags
            mesh_flags = struct.unpack_from('<I', data, off)[0]; off += 4

            MESHFLAG_TEXTURE = 1 << 0
            MESHFLAG_COLOURS = 1 << 1
            MESHFLAG_NORMALS = 1 << 2
            MESHFLAG_ST16    = 1 << 3
            MESHFLAG_SKINNED = 1 << 4

            flag_names = []
            if mesh_flags & MESHFLAG_TEXTURE: flag_names.append('TEXTURE')
            if mesh_flags & MESHFLAG_COLOURS: flag_names.append('COLOURS')
            if mesh_flags & MESHFLAG_NORMALS: flag_names.append('NORMALS')
            if mesh_flags & MESHFLAG_ST16:    flag_names.append('ST16')
            if mesh_flags & MESHFLAG_SKINNED: flag_names.append('SKINNED')
            p(f'    MeshFlags: 0x{mesh_flags:08X} ({"|".join(flag_names)})')

            # Bounding data
            objbox = struct.unpack_from('<3f', data, off); off += 12
            box = struct.unpack_from('<3f', data, off); off += 12
            mesh_sphere = struct.unpack_from('<4f', data, off); off += 16
            p(f'    ObjBox: ({objbox[0]:.4f}, {objbox[1]:.4f}, {objbox[2]:.4f})')
            p(f'    Box: ({box[0]:.4f}, {box[1]:.4f}, {box[2]:.4f})')
            p(f'    MeshSphere: ({mesh_sphere[0]:.4f}, {mesh_sphere[1]:.4f}, {mesh_sphere[2]:.4f}, r={mesh_sphere[3]:.4f})')

            # Pass (mesh_ver >= 5)
            if mesh_ver >= 5:
                pass_val = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'    Pass: {pass_val}')

            # MaterialName (mesh_ver >= 6)
            if mesh_ver >= 6:
                mat_name = struct.unpack_from('<I', data, off)[0]; off += 4
                p(f'    MaterialName: 0x{mat_name:08X}')

            # Vertices
            num_verts = struct.unpack_from('<I', data, off)[0]; off += 4
            p(f'    NumVertices: {num_verts}')

            if num_verts > 100000:
                return False, f"Implausible NumVertices={num_verts}"

            is_skinned = bool(mesh_flags & MESHFLAG_SKINNED)

            # Vertex size calculation
            # THPS4 (mesh_ver<=4): ALL attributes use full float precision
            #   Non-skinned: base=16 (XYZ f32x3 + u32 ADC), ST=8 (f32x2), colour=4, normal=12 (f32x3)
            #   Skinned: same base=16, ST=8, colour=4, normal=12, + skin=24 (weights 2xf32 + pad 4 + bones 2xu32 + pad 4)
            # THUG/THUG2 (mesh_ver>=6): non-skinned uses floats, skinned uses packed sint16
            #   Non-skinned: base=16, ST=8 (f32x2), colour=4, normal=4 (packed sint16x2)
            #   Skinned: base=8 (XYZ s16x3 + u16 ADC), ST=4 (s16x2), colour=4, normal=4, skin=8 (wt 4 + bone 4)

            if mesh_ver <= 4:  # THPS4 — full float precision for everything
                vert_size = 16  # position base (float XYZ + u32 ADC)
                st_offset = 0
                colour_offset = 0
                normal_offset = 0
                skin_offset = 0
                xyz_offset = 0

                if mesh_flags & MESHFLAG_TEXTURE:
                    vert_size += 8  # float x2
                    xyz_offset += 8; skin_offset += 8; normal_offset += 8; colour_offset += 8
                if mesh_flags & MESHFLAG_COLOURS:
                    vert_size += 4
                    xyz_offset += 4; skin_offset += 4; normal_offset += 4
                if mesh_flags & MESHFLAG_NORMALS:
                    vert_size += 12  # float x3
                    xyz_offset += 12; skin_offset += 12
                if is_skinned:
                    # weights(2xf32=8) + pad(4) + bones(2xu32=8) + pad(4) = 24
                    vert_size += 24
                    xyz_offset += 24
                pos_size = 16  # XYZ float x3 + u32 ADC
                p(f'    VertexSize: {vert_size} bytes (THPS4, {"skinned" if is_skinned else "non-skinned"})')
            else:  # THUG/THUG2 — skinned uses packed format
                if is_skinned:
                    vert_size = 8  # position base (sint16 XYZ + u16 ADC)
                    st_offset = colour_offset = normal_offset = skin_offset = xyz_offset = 0
                    if mesh_flags & MESHFLAG_TEXTURE:
                        vert_size += 4  # sint16 x2
                        xyz_offset += 4; skin_offset += 4; normal_offset += 4; colour_offset += 4
                    if mesh_flags & MESHFLAG_COLOURS:
                        vert_size += 4
                        xyz_offset += 4; skin_offset += 4; normal_offset += 4
                    if mesh_flags & MESHFLAG_NORMALS:
                        vert_size += 4  # packed sint16 x2
                        xyz_offset += 4; skin_offset += 4
                    vert_size += 8  # skin weights(4) + bones(4)
                    xyz_offset += 8
                    pos_size = 8  # sint16 XYZ + u16 ADC
                    p(f'    VertexSize: {vert_size} bytes (THUG skinned)')
                else:
                    vert_size = 16  # position base (float XYZ + u32 ADC)
                    st_offset = colour_offset = normal_offset = skin_offset = xyz_offset = 0
                    if mesh_flags & MESHFLAG_TEXTURE:
                        vert_size += 8  # float x2
                        xyz_offset += 8; normal_offset += 8; colour_offset += 8
                    if mesh_flags & MESHFLAG_COLOURS:
                        vert_size += 4
                        xyz_offset += 4; normal_offset += 4
                    if mesh_flags & MESHFLAG_NORMALS:
                        vert_size += 4  # packed sint16 x2
                        xyz_offset += 4
                    pos_size = 16  # float XYZ + u32 ADC
                    p(f'    VertexSize: {vert_size} bytes (THUG non-skinned)')

            total_vert_bytes = num_verts * vert_size
            remaining = len(data) - off

            if num_verts > 0 and total_vert_bytes <= remaining:
                # Print first few vertices
                for vi in range(min(3, num_verts)):
                    vdata = data[off + vi * vert_size : off + (vi + 1) * vert_size]

                    if pos_size == 16:  # float positions
                        x, y, z = struct.unpack_from('<3f', vdata, xyz_offset)
                        adc = struct.unpack_from('<I', vdata, xyz_offset + 12)[0]
                        parts = [f'pos=({x:.4f},{y:.4f},{z:.4f}) adc=0x{adc:04X}']
                    else:  # sint16 positions
                        sx, sy, sz = struct.unpack_from('<3h', vdata, xyz_offset)
                        adc = struct.unpack_from('<H', vdata, xyz_offset + 6)[0]
                        parts = [f'pos=({sx},{sy},{sz}) adc=0x{adc:04X}']

                    if mesh_flags & MESHFLAG_TEXTURE:
                        if pos_size == 16:  # float STs
                            s, t = struct.unpack_from('<2f', vdata, st_offset)
                            parts.append(f'uv=({s:.4f},{t:.4f})')
                        else:  # sint16 STs
                            su, sv = struct.unpack_from('<2h', vdata, st_offset)
                            parts.append(f'uv=({su},{sv})')

                    if mesh_flags & MESHFLAG_COLOURS:
                        r, g, b, a = struct.unpack_from('<4B', vdata, colour_offset)
                        parts.append(f'rgba=({r},{g},{b},{a})')

                    if mesh_flags & MESHFLAG_NORMALS:
                        if mesh_ver <= 4:  # THPS4: float normals
                            nx, ny, nz = struct.unpack_from('<3f', vdata, normal_offset)
                            parts.append(f'normal=({nx:.4f},{ny:.4f},{nz:.4f})')
                        else:
                            packed = struct.unpack_from('<I', vdata, normal_offset)[0]
                            parts.append(f'normal=0x{packed:08X}')

                    p(f'      vert[{vi}]: {" ".join(parts)}')

                off += total_vert_bytes
                total_verts_parsed += num_verts
                p(f'    After vertices: offset {off} (0x{off:X})')
            elif num_verts == 0:
                p(f'    (no vertices)')
            else:
                return False, f"Need {total_vert_bytes} bytes for vertices but only {remaining} remain"

    # num_hier
    if off + 4 <= len(data):
        num_hier = struct.unpack_from('<i', data, off)[0]; off += 4
        p(f'\nnum_hier: {num_hier}')
        if num_hier < -100 or num_hier > 10000:
            return False, f"Implausible num_hier={num_hier}"
    else:
        p(f'\n(no num_hier, EOF)')

    remaining = len(data) - off
    p(f'\nFinal offset: {off} (0x{off:X}), file size: {len(data)}')
    p(f'Remaining: {remaining} bytes')

    # After num_hier, remaining data may include:
    # - Shadow volume data (sShadowVolumeHeader + vertices + connectivity)
    # - Hierarchy array (80 bytes per entry when num_hier > 0, THPS4 only)
    # - THUG/THUG2 sentinel (0xDEAD1234) + 28 bytes padding = 32 bytes
    # - Nothing (THPS4 simple models)
    # We just accept any remaining data as valid.

    return True, f"OK: {num_mats} mats, {num_groups} groups, {total_verts_parsed} verts, {remaining} trailing"


def batch_validate(directory):
    """Validate all .ps2 scene files in a directory."""
    extensions = ('.mdl.ps2', '.skin.ps2', '.iskin.ps2')
    ok_count = 0
    fail_count = 0
    skip_count = 0
    failures = []

    for root, dirs, files in os.walk(directory):
        for fn in sorted(files):
            if not any(fn.lower().endswith(ext) for ext in extensions):
                continue
            path = os.path.join(root, fn)
            try:
                data = open(path, 'rb').read()
                success, msg = parse_file(data, verbose=False)
                if success:
                    ok_count += 1
                else:
                    fail_count += 1
                    failures.append((fn, msg))
            except Exception as e:
                fail_count += 1
                failures.append((fn, str(e)))

    total = ok_count + fail_count
    print(f'\nResults: {ok_count}/{total} OK, {fail_count} failed')
    if failures:
        print('\nFailures:')
        for fn, msg in failures[:20]:
            print(f'  {fn}: {msg}')
        if len(failures) > 20:
            print(f'  ... and {len(failures) - 20} more')
    return fail_count == 0


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print("Usage: python ps2_scene_trace.py <file>")
        print("       python ps2_scene_trace.py --batch <directory>")
        sys.exit(1)

    if sys.argv[1] == '--batch':
        directory = sys.argv[2] if len(sys.argv) > 2 else '.'
        success = batch_validate(directory)
        sys.exit(0 if success else 1)
    else:
        data = open(sys.argv[1], 'rb').read()
        success, msg = parse_file(data, verbose=True)
        print(f'\n{"SUCCESS" if success else "FAILED"}: {msg}')
        sys.exit(0 if success else 1)
