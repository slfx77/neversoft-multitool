#!/usr/bin/env python3
"""texture_diagnostic.py -- Compare DDM texture names vs PSX texture hashes.

Parses a DDM/PSX file pair from the THPS2X Xbox build and shows:
  - DDM object names, material names, texture names
  - PSX mesh name hashes (with QBKey reverse lookup)
  - PSX texture name hashes (with QBKey reverse lookup)
  - Which DDM texture names match PSX texture hashes (spoiler: none so far)
  - Various name transformations attempted

Usage:
  python tools/texture_diagnostic.py [basename]
  python tools/texture_diagnostic.py bboombox
  python tools/texture_diagnostic.py --all          # run all DDM/PSX pairs
  python tools/texture_diagnostic.py --dump-ghidra  # try GHIDRA names against PSX hashes
"""

import struct
import sys
import os
import glob

# ---- QBKey hash (reflected CRC-32) ----

def _make_table():
    poly = 0xEDB88320
    table = []
    for i in range(256):
        crc = i
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ poly
            else:
                crc >>= 1
        table.append(crc)
    return table

_TABLE = _make_table()

def qbkey_hash(name: str) -> int:
    """QBKey hash: reflected CRC-32, poly 0xEDB88320, init 0xFFFFFFFF, no final XOR, lowercase."""
    crc = 0xFFFFFFFF
    for ch in name.lower():
        crc = (crc >> 8) ^ _TABLE[(crc ^ ord(ch)) & 0xFF]
    return crc & 0xFFFFFFFF


def qbkey_hash_case_sensitive(name: str) -> int:
    """QBKey without lowercasing — io_thps_scene crc32b_from_string for THPS1/2."""
    crc = 0xFFFFFFFF
    for ch in name:
        crc = (crc >> 8) ^ _TABLE[(crc ^ ord(ch)) & 0xFF]
    return crc & 0xFFFFFFFF


# ---- Forward CRC-32 (psxprev PIL/PSI format) ----

def _make_forward_table():
    poly = 0x04C11DB7
    table = []
    for i in range(256):
        crc = i << 24
        for _ in range(8):
            if crc & 0x80000000:
                crc = ((crc << 1) ^ poly) & 0xFFFFFFFF
            else:
                crc = (crc << 1) & 0xFFFFFFFF
        table.append(crc)
    return table

_FORWARD_TABLE = _make_forward_table()

def forward_crc32(name: str) -> int:
    """Forward CRC-32: poly 0x04C11DB7, init 0, no final XOR — psxprev PIL hash."""
    crc = 0
    for ch in name:
        index = ((crc >> 24) ^ ord(ch)) & 0xFF
        crc = ((crc << 8) ^ _FORWARD_TABLE[index]) & 0xFFFFFFFF
    return crc


def forward_crc32_lower(name: str) -> int:
    """Forward CRC-32 with lowercase normalization."""
    return forward_crc32(name.lower())


# All hash algorithms to test
HASH_ALGORITHMS = {
    'qbkey (reflected+lower)': qbkey_hash,
    'qbkey_casesens (reflected, no lower)': qbkey_hash_case_sensitive,
    'forward_crc32 (no lower)': forward_crc32,
    'forward_crc32_lower': forward_crc32_lower,
}


# ---- PSX file parser ----

def read_psx_hashes(filepath: str):
    """Parse a PSX file and return (mesh_hashes, texture_hashes, detail_names, cubemap_names, actual_tex_count)."""
    with open(filepath, "rb") as f:
        data = f.read()

    pos = 0
    magic = data[pos:pos+4]
    valid_magics = [b'\x04\x00\x02\x00', b'\x03\x00\x02\x00', b'\x06\x00\x02\x00']
    if magic not in valid_magics:
        raise ValueError(f"Invalid PSX magic: {magic.hex()}")
    pos += 4

    # Read model data with hashes (mirrors ReadModelDataWithHashes)
    ptr_meta = struct.unpack_from("<I", data, pos)[0]; pos += 4
    obj_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    pos += obj_count * 36  # skip objects (36 bytes each)
    mesh_count = struct.unpack_from("<I", data, pos)[0]; pos += 4

    # Seek to tagged chunks
    pos = ptr_meta
    chunk_count = 0
    while True:
        chunk_magic = data[pos:pos+4]; pos += 4
        if chunk_magic == b'\xff\xff\xff\xff':
            break
        unk_length = struct.unpack_from("<I", data, pos)[0]; pos += 4
        pos += unk_length
        chunk_count += 1
        if chunk_count > 16:
            raise ValueError("Cannot find texture data (>16 chunks)")

    # Read mesh name hashes
    mesh_hashes = []
    for _ in range(mesh_count):
        h = struct.unpack_from("<I", data, pos)[0]; pos += 4
        mesh_hashes.append(h)

    # Read texture name hashes (ReadTextureInfo)
    num_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4
    tex_hashes = []
    for _ in range(num_tex):
        h = struct.unpack_from("<I", data, pos)[0]; pos += 4
        tex_hashes.append(h)

    # Skip palettes: 4-bit then 8-bit
    for num_colors in (16, 256):
        pal_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        for _ in range(pal_count):
            pos += 4  # texId
            pos += num_colors * 2  # color data

    # Check for v6 extended header
    detail_names = []
    cubemap_names = []
    num_actual_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4
    if num_actual_tex == 0xFFFFFFFF:
        detail_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        for _ in range(detail_count):
            name_bytes = data[pos:pos+32]; pos += 32
            detail_names.append(name_bytes.split(b'\x00')[0].decode('ascii', errors='replace'))
            pos += 4  # flags
        cubemap_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        for _ in range(cubemap_count):
            name_bytes = data[pos:pos+32]; pos += 32
            cubemap_names.append(name_bytes.split(b'\x00')[0].decode('ascii', errors='replace'))
            pos += 4  # flags
        num_actual_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4

    return mesh_hashes, tex_hashes, detail_names, cubemap_names, num_actual_tex


# ---- DDM file parser ----

def read_ddm_names(filepath: str):
    """Parse a DDM file and return list of objects, each with (name, checksum, materials)
    where materials is list of (material_name, texture_name)."""
    with open(filepath, "rb") as f:
        data = f.read()

    pos = 0
    version = struct.unpack_from("<I", data, pos)[0]; pos += 4
    data_size = struct.unpack_from("<I", data, pos)[0]; pos += 4
    obj_count = struct.unpack_from("<I", data, pos)[0]; pos += 4

    # Object table
    obj_table = []
    for _ in range(obj_count):
        offset = struct.unpack_from("<I", data, pos)[0]; pos += 4
        size = struct.unpack_from("<I", data, pos)[0]; pos += 4
        obj_table.append((offset, size))

    objects = []
    for obj_offset, obj_size in obj_table:
        p = obj_offset
        # Object header: index(4) + checksum(4) + animSpeedX(4) + animSpeedY(4) + animRate(4) + animParams(4) + flags(4) = 28 bytes
        index = struct.unpack_from("<I", data, p)[0]; p += 4
        checksum = struct.unpack_from("<I", data, p)[0]; p += 4
        p += 20  # skip anim fields + flags

        # Object name: 64 bytes
        obj_name = data[p:p+64].split(b'\x00')[0].decode('ascii', errors='replace'); p += 64

        # Bounding box: 7 floats = 28 bytes
        p += 28

        mat_count = struct.unpack_from("<I", data, p)[0]; p += 4
        vert_count = struct.unpack_from("<I", data, p)[0]; p += 4
        idx_count = struct.unpack_from("<I", data, p)[0]; p += 4
        split_count = struct.unpack_from("<I", data, p)[0]; p += 4

        materials = []
        for _ in range(mat_count):
            mat_name = data[p:p+64].split(b'\x00')[0].decode('ascii', errors='replace'); p += 64
            tex_name = data[p:p+64].split(b'\x00')[0].decode('ascii', errors='replace'); p += 64
            # drawOrder(4) + diffuse(4) + emissive(4) + specular(4) + glossiness(4) + blendMode(4) = 24 bytes
            p += 24
            materials.append((mat_name, tex_name))

        objects.append({
            'name': obj_name,
            'checksum': checksum,
            'materials': materials,
        })

    return objects


# ---- Name transformation experiments ----

def generate_variants(name: str) -> dict:
    """Generate various transformations of a name and their QBKey hashes."""
    variants = {}
    base = name.lower()

    # As-is
    variants[base] = qbkey_hash(base)

    # Without extension
    stem, ext = os.path.splitext(base)
    if ext:
        variants[stem] = qbkey_hash(stem)

    # With different extensions
    for new_ext in ['.bmp', '.tga', '.png', '.tex', '.pvr', '.psx', '.img', '.tim']:
        v = stem + new_ext
        if v != base:
            variants[v] = qbkey_hash(v)

    # With path prefixes
    for prefix in ['textures/', 'tex/', 'gfx/', 'images/', 'skins/', 'levels/',
                   'textures\\', 'tex\\', 'gfx\\', 'models/', 'models\\']:
        variants[prefix + base] = qbkey_hash(prefix + base)
        if ext:
            variants[prefix + stem] = qbkey_hash(prefix + stem)

    # Without underscores
    if '_' in stem:
        no_us = stem.replace('_', '')
        variants[no_us] = qbkey_hash(no_us)

    return variants


# ---- Known name resolution (load from QbKeyNames.txt) ----

_KNOWN_NAMES = {}

def load_known_names():
    global _KNOWN_NAMES
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    names_file = os.path.join(repo_root, "src", "NeversoftMultitool", "Core", "QbKeyNames.txt")
    if not os.path.exists(names_file):
        print(f"QbKeyNames.txt not found at {names_file}")
        return
    with open(names_file, "r") as f:
        for line in f:
            line = line.strip()
            if '=' not in line:
                continue
            name, hashstr = line.split('=', 1)
            try:
                h = int(hashstr, 16)
                _KNOWN_NAMES[h] = name
            except ValueError:
                pass
    print(f"Loaded {len(_KNOWN_NAMES)} known name mappings from QbKeyNames.txt")


def try_resolve_known(h: int) -> str:
    return _KNOWN_NAMES.get(h)


# ---- Main logic ----

def find_builds_dir():
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    builds = os.path.join(repo_root, "Sample", "Builds")
    if os.path.isdir(builds):
        return builds
    return None


def get_thps2x_dir(builds_dir):
    pattern = os.path.join(builds_dir, "Tony Hawk*2X*Xbox*")
    matches = glob.glob(pattern)
    if matches:
        return matches[0]
    return os.path.join(builds_dir, "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)")


def index_files_by_stem(root: str, suffixes: tuple[str, ...]) -> dict[str, str]:
    """Index files under root recursively, keyed by lowercase basename-without-extension.
    Suffixes match case-insensitively. The new SampleGenerator emits builds as the actual
    game disc tree, so files are scattered rather than living in PSX/ or DDM/ folders."""
    suffixes_lower = tuple(s.lower() for s in suffixes)
    index = {}
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            fn_lower = fn.lower()
            if not fn_lower.endswith(suffixes_lower):
                continue
            stem = os.path.splitext(fn)[0].lower()
            index[stem] = os.path.join(dirpath, fn)
    return index


def list_files_by_extension(root: str, *suffixes: str) -> list[str]:
    """Return all files under root whose name ends with any of the given suffixes (case-insensitive)."""
    suffixes_lower = tuple(s.lower() for s in suffixes)
    out = []
    for dirpath, _, filenames in os.walk(root):
        for fn in filenames:
            if fn.lower().endswith(suffixes_lower):
                out.append(os.path.join(dirpath, fn))
    return sorted(out)


def analyze_pair(basename: str, thps2x_dir: str, all_psx_tex_hashes: set = None):
    """Analyze a DDM/PSX file pair."""
    ddm_index = index_files_by_stem(thps2x_dir, (".ddm",))
    psx_index = index_files_by_stem(thps2x_dir, (".psx",))

    ddm_path = ddm_index.get(basename.lower())
    psx_path = psx_index.get(basename.lower())

    has_ddm = ddm_path is not None
    has_psx = psx_path is not None

    print(f"\n{'='*70}")
    print(f"  {basename}")
    print(f"{'='*70}")
    print(f"  DDM: {'FOUND' if has_ddm else 'NOT FOUND'}")
    print(f"  PSX: {'FOUND' if has_psx else 'NOT FOUND'}")

    if not has_psx:
        print("  [SKIP] No PSX file found")
        return

    # Parse PSX
    mesh_hashes, tex_hashes, detail_names, cubemap_names, actual_tex_count = read_psx_hashes(psx_path)
    psx_tex_set = set(tex_hashes)
    psx_mesh_set = set(mesh_hashes)

    if all_psx_tex_hashes is not None:
        all_psx_tex_hashes.update(psx_tex_set)

    print(f"\n  PSX: {len(mesh_hashes)} mesh hashes, {len(tex_hashes)} texture hashes, {actual_tex_count} actual textures")
    if detail_names:
        print(f"  PSX v6 detail textures: {detail_names}")
    if cubemap_names:
        print(f"  PSX v6 cubemaps: {cubemap_names}")

    print(f"\n  --- Mesh name hashes ---")
    for i, h in enumerate(mesh_hashes):
        name = try_resolve_known(h)
        print(f"    [{i:3d}] 0x{h:08X}  {name or '???'}")

    print(f"\n  --- Texture name hashes ---")
    for i, h in enumerate(tex_hashes):
        name = try_resolve_known(h)
        print(f"    [{i:3d}] 0x{h:08X}  {name or '???'}")

    if not has_ddm:
        print("\n  [SKIP] No DDM file -- cannot compare texture names")
        return

    # Parse DDM
    objects = read_ddm_names(ddm_path)
    print(f"\n  DDM: {len(objects)} objects")

    all_ddm_tex_names = set()

    print(f"\n  --- DDM Objects ---")
    for obj in objects:
        checksum_match = "MESH-MATCH" if obj['checksum'] in psx_mesh_set else "no match"
        obj_hash = qbkey_hash(obj['name'].lower())
        obj_match = "MESH-MATCH" if obj_hash in psx_mesh_set else ""
        print(f"    {obj['name']}  checksum=0x{obj['checksum']:08X} ({checksum_match})")
        print(f"      qbkey('{obj['name'].lower()}') = 0x{obj_hash:08X}  {obj_match}")
        for mat_name, tex_name in obj['materials']:
            if tex_name:
                all_ddm_tex_names.add(tex_name)
                tex_hash = qbkey_hash(tex_name.lower())
                stem = os.path.splitext(tex_name)[0].lower() if '.' in tex_name else tex_name.lower()
                stem_hash = qbkey_hash(stem)
                full_match = "TEX-MATCH!" if tex_hash in psx_tex_set else ""
                stem_match = "TEX-MATCH!" if stem_hash in psx_tex_set else ""
                print(f"      mat={mat_name}  tex={tex_name}")
                print(f"        qbkey('{tex_name.lower()}') = 0x{tex_hash:08X}  {full_match}")
                print(f"        qbkey('{stem}')      = 0x{stem_hash:08X}  {stem_match}")
            else:
                print(f"      mat={mat_name}  tex=(none)")

    # Summary
    print(f"\n  --- Match Summary ---")
    match_count = 0
    for tex_name in sorted(all_ddm_tex_names):
        tex_hash = qbkey_hash(tex_name.lower())
        stem = os.path.splitext(tex_name)[0].lower() if '.' in tex_name else tex_name.lower()
        stem_hash = qbkey_hash(stem)
        if tex_hash in psx_tex_set or stem_hash in psx_tex_set:
            match_count += 1
            matched = "full" if tex_hash in psx_tex_set else "stem"
            print(f"    MATCH ({matched}): {tex_name}")

    print(f"\n  DDM texture names: {len(all_ddm_tex_names)}")
    print(f"  PSX texture hashes: {len(tex_hashes)} (unique: {len(psx_tex_set)})")
    print(f"  Matches: {match_count}")

    # Try extended transformations
    print(f"\n  --- Extended name experiments ---")
    ext_match_count = 0
    for tex_name in sorted(all_ddm_tex_names):
        variants = generate_variants(tex_name)
        for variant_name, variant_hash in variants.items():
            if variant_hash in psx_tex_set:
                print(f"    MATCH: '{variant_name}' -> 0x{variant_hash:08X}")
                ext_match_count += 1
    if ext_match_count == 0:
        print(f"    No matches from any variant transformations")


def read_psx_texture_headers(filepath: str):
    """Parse PSX file and return texture headers (TexId, Width, Height, PalSize, PixelFormat)
    along with the 'texture name' values."""
    with open(filepath, "rb") as f:
        data = f.read()

    pos = 0
    magic = data[pos:pos+4]
    valid_magics = [b'\x04\x00\x02\x00', b'\x03\x00\x02\x00', b'\x06\x00\x02\x00']
    if magic not in valid_magics:
        raise ValueError(f"Invalid PSX magic: {magic.hex()}")
    pos += 4

    # Skip to texture info (mirrors SkipModelData + ReadTextureInfo)
    ptr_meta = struct.unpack_from("<I", data, pos)[0]; pos += 4
    obj_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
    pos += obj_count * 36
    mesh_count = struct.unpack_from("<I", data, pos)[0]; pos += 4

    pos = ptr_meta
    chunk_count = 0
    while True:
        chunk_magic = data[pos:pos+4]; pos += 4
        if chunk_magic == b'\xff\xff\xff\xff':
            break
        unk_length = struct.unpack_from("<I", data, pos)[0]; pos += 4
        pos += unk_length
        chunk_count += 1
        if chunk_count > 16:
            raise ValueError("Cannot find texture data")

    # Skip mesh hashes
    for _ in range(mesh_count):
        pos += 4

    # Read texture "name" values
    num_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4
    tex_names = []
    for _ in range(num_tex):
        h = struct.unpack_from("<I", data, pos)[0]; pos += 4
        tex_names.append(h)

    # Skip palettes
    for num_colors in (16, 256):
        pal_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        for _ in range(pal_count):
            pos += 4 + num_colors * 2

    # Handle v6 extended header
    num_actual_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4
    if num_actual_tex == 0xFFFFFFFF:
        detail_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        pos += detail_count * 36
        cubemap_count = struct.unpack_from("<I", data, pos)[0]; pos += 4
        pos += cubemap_count * 36
        num_actual_tex = struct.unpack_from("<I", data, pos)[0]; pos += 4

    # Skip unknown data (4 bytes per texture)
    for _ in range(num_actual_tex):
        pos += 4

    # Read texture headers
    headers = []
    for i in range(num_actual_tex):
        offset = pos
        unk = struct.unpack_from("<I", data, pos)[0]; pos += 4
        pal_size = struct.unpack_from("<I", data, pos)[0]; pos += 4
        tex_id = struct.unpack_from("<I", data, pos)[0]; pos += 4
        index = struct.unpack_from("<I", data, pos)[0]; pos += 4
        width = struct.unpack_from("<H", data, pos)[0]; pos += 2
        height = struct.unpack_from("<H", data, pos)[0]; pos += 2

        pixel_format = 0
        size = 0
        if pal_size == 65536:
            pixel_format = struct.unpack_from("<I", data, pos)[0]; pos += 4
            size = struct.unpack_from("<I", data, pos)[0]; pos += 4

        tex_data_offset = pos

        # Skip texture data
        if pal_size == 65536:
            pos = tex_data_offset + size
        elif pal_size == 16:
            pad_width = ((width + 0x3) & ~0x3) >> 1
            padding = 0
            if height % 2 != 0:
                padding = 2 if pad_width % 4 != 0 else 0
            pos = tex_data_offset + pad_width * height + padding
        elif pal_size == 256:
            pad_width = (width + 0x1) & ~0x1
            padding = 0
            if height % 2 != 0:
                padding = 2 if pad_width % 4 != 0 else 0
            pos = tex_data_offset + pad_width * height + padding

        name_val = tex_names[i] if i < len(tex_names) else None
        headers.append({
            'index': i,
            'tex_id': tex_id,
            'unk': unk,
            'pal_size': pal_size,
            'width': width,
            'height': height,
            'pixel_format': pixel_format,
            'size': size,
            'name_val': name_val,
            'header_index': index,
        })

    return tex_names, headers


def analyze_hash_distribution(thps2x_dir: str):
    """Analyze the distribution of PSX texture 'hashes' to determine if they're really hashes."""
    all_tex_vals = []
    all_mesh_hashes = []
    per_file = {}

    for fp in list_files_by_extension(thps2x_dir, ".psx"):
        try:
            tex_names, headers = read_psx_texture_headers(fp)
            mh, th, _, _, _ = read_psx_hashes(fp)
            all_tex_vals.extend(th)
            all_mesh_hashes.extend(mh)
            per_file[os.path.basename(fp)] = (th, headers)
        except Exception:
            pass

    print(f"\n{'='*70}")
    print(f"  TEXTURE 'HASH' DISTRIBUTION ANALYSIS")
    print(f"{'='*70}")
    print(f"\n  Total texture values: {len(all_tex_vals)} ({len(set(all_tex_vals))} unique)")
    print(f"  Total mesh hashes: {len(all_mesh_hashes)} ({len(set(all_mesh_hashes))} unique)")

    # Analyze top-byte distribution
    print(f"\n  --- Top byte distribution (texture values) ---")
    top_bytes = {}
    for v in all_tex_vals:
        tb = (v >> 24) & 0xFF
        top_bytes[tb] = top_bytes.get(tb, 0) + 1
    for tb in sorted(top_bytes.keys()):
        pct = 100.0 * top_bytes[tb] / len(all_tex_vals)
        print(f"    0x{tb:02X}: {top_bytes[tb]:5d} ({pct:.1f}%)")

    print(f"\n  --- Top 16-bit distribution (texture values) ---")
    top_words = {}
    for v in all_tex_vals:
        tw = (v >> 16) & 0xFFFF
        top_words[tw] = top_words.get(tw, 0) + 1
    for tw in sorted(top_words.keys(), key=lambda k: -top_words[k])[:20]:
        pct = 100.0 * top_words[tw] / len(all_tex_vals)
        print(f"    0x{tw:04X}: {top_words[tw]:5d} ({pct:.1f}%)")

    # Compare with mesh hashes (should be uniform for real CRC-32)
    print(f"\n  --- Top byte distribution (mesh hashes, for comparison) ---")
    mesh_top_bytes = {}
    for v in all_mesh_hashes:
        tb = (v >> 24) & 0xFF
        mesh_top_bytes[tb] = mesh_top_bytes.get(tb, 0) + 1
    for tb in sorted(mesh_top_bytes.keys())[:20]:
        pct = 100.0 * mesh_top_bytes[tb] / len(all_mesh_hashes)
        print(f"    0x{tb:02X}: {mesh_top_bytes[tb]:5d} ({pct:.1f}%)")
    if len(mesh_top_bytes) > 20:
        print(f"    ... ({len(mesh_top_bytes)} distinct top bytes total)")

    # Check if texture values == TexId from headers
    print(f"\n  --- Texture value vs TexId comparison (first 10 files) ---")
    count = 0
    for fname, (tex_vals, headers) in sorted(per_file.items()):
        if count >= 10:
            break
        count += 1
        print(f"\n    {fname}: {len(tex_vals)} tex vals, {len(headers)} headers")
        for i, hdr in enumerate(headers):
            name_val = hdr['name_val']
            tex_id = hdr['tex_id']
            match = "==" if name_val == tex_id else "!="
            print(f"      [{i}] name_val=0x{name_val:08X}  tex_id=0x{tex_id:08X}  {match}  "
                  f"{hdr['width']}x{hdr['height']} pal={hdr['pal_size']}")

    # Check if values could be floats
    print(f"\n  --- Interpreting as IEEE 754 float (sample) ---")
    for v in sorted(set(all_tex_vals))[:10]:
        fbytes = struct.pack("<I", v)
        fval = struct.unpack("<f", fbytes)[0]
        print(f"    0x{v:08X} = {fval:.6f}")


def is_likely_float_not_hash(val: int) -> bool:
    """Check if a uint32 value looks like an IEEE 754 float rather than a CRC-32 hash.
    The 0x46A0xxxx cluster (floats ~20480) appears in placeholder 16x16 PVR textures."""
    top16 = (val >> 16) & 0xFFFF
    return top16 in (0x469F, 0x46A0, 0x46A1)


def analyze_real_vs_placeholder(thps2x_dir: str):
    """Separate real texture hashes from placeholder float values and analyze each group."""
    real_hashes = set()
    placeholder_vals = set()
    real_per_file = {}
    placeholder_per_file = {}

    for fp in list_files_by_extension(thps2x_dir, ".psx"):
        f = os.path.basename(fp)
        try:
            tex_names, headers = read_psx_texture_headers(fp)
        except Exception:
            continue

        file_real = []
        file_placeholder = []
        for i, hdr in enumerate(headers):
            nv = hdr['name_val']
            if nv is None:
                continue
            # Classify: 16x16 placeholder PVR with float-like value = placeholder
            is_placeholder = (hdr['pal_size'] == 65536 and hdr['width'] == 16 and hdr['height'] == 16
                              and hdr['tex_id'] == 0)
            is_float = is_likely_float_not_hash(nv)

            if is_placeholder or is_float:
                placeholder_vals.add(nv)
                file_placeholder.append((nv, hdr))
            else:
                real_hashes.add(nv)
                file_real.append((nv, hdr))

        if file_real:
            real_per_file[f] = file_real
        if file_placeholder:
            placeholder_per_file[f] = file_placeholder

    print(f"\n{'='*70}")
    print(f"  REAL vs PLACEHOLDER TEXTURE VALUE ANALYSIS")
    print(f"{'='*70}")
    print(f"\n  Real hash values: {len(real_hashes)} unique")
    print(f"  Placeholder float values: {len(placeholder_vals)} unique")
    print(f"  Files with real hashes: {len(real_per_file)}")
    print(f"  Files with only placeholders: {len(placeholder_per_file) - len(real_per_file)}")

    # Check real hashes against known names
    print(f"\n  --- Real hashes with known name resolution ---")
    resolved = 0
    for h in sorted(real_hashes):
        name = try_resolve_known(h)
        if name:
            resolved += 1
            print(f"    0x{h:08X} = '{name}'")
    print(f"\n  Resolved: {resolved}/{len(real_hashes)} ({100.0*resolved/max(len(real_hashes),1):.1f}%)")

    # Show files with real hashes
    print(f"\n  --- Files with real (non-placeholder) texture hashes ---")
    for fname, entries in sorted(real_per_file.items()):
        real_count = len(entries)
        resolved_count = sum(1 for nv, _ in entries if try_resolve_known(nv))
        print(f"    {fname}: {real_count} real hashes ({resolved_count} resolved)")
        for nv, hdr in entries:
            name = try_resolve_known(nv)
            pal_desc = f"pal={hdr['pal_size']}" if hdr['pal_size'] != 65536 else f"pvr 0x{hdr['pixel_format']:X}"
            print(f"      0x{nv:08X}  {hdr['width']:3d}x{hdr['height']:<3d}  {pal_desc}  {name or '???'}")

    # Now try GHIDRA names against ONLY the real hashes
    script_dir = os.path.dirname(os.path.abspath(__file__))
    ghidra_combined = os.path.join(script_dir, "ghidra", "output", "ghidra_names_combined.txt")
    if os.path.exists(ghidra_combined):
        with open(ghidra_combined) as f:
            ghidra_names = [line.strip() for line in f if line.strip()]

        matches = []
        for name in ghidra_names:
            h = qbkey_hash(name)
            if h in real_hashes:
                matches.append((name, h))
            stem = os.path.splitext(name)[0] if '.' in name else None
            if stem and stem != name:
                sh = qbkey_hash(stem)
                if sh in real_hashes:
                    matches.append((stem, sh))

        print(f"\n  --- GHIDRA names vs real hashes ---")
        print(f"  GHIDRA candidates: {len(ghidra_names)}")
        print(f"  Matches: {len(set(matches))}")
        for name, h in sorted(set(matches)):
            print(f"    0x{h:08X} = '{name}'")

    return real_hashes


def collect_real_texture_hashes(thps2x_dir: str) -> set:
    """Collect all real (non-placeholder) texture hashes from all PSX files."""
    real_hashes = set()
    for fp in list_files_by_extension(thps2x_dir, ".psx"):
        try:
            tex_names, headers = read_psx_texture_headers(fp)
        except Exception:
            continue
        for hdr in headers:
            nv = hdr['name_val']
            if nv is None:
                continue
            is_placeholder = (hdr['pal_size'] == 65536 and hdr['width'] == 16 and hdr['height'] == 16
                              and hdr['tex_id'] == 0)
            if is_placeholder or is_likely_float_not_hash(nv):
                continue
            real_hashes.add(nv)
    return real_hashes


def collect_all_texture_hashes(thps2x_dir: str) -> set:
    """Collect ALL texture hashes (including placeholders) from all PSX files."""
    all_hashes = set()
    for fp in list_files_by_extension(thps2x_dir, ".psx"):
        try:
            _, th, _, _, _ = read_psx_hashes(fp)
            all_hashes.update(th)
        except Exception:
            continue
    return all_hashes


def collect_candidate_names(thps2x_dir: str) -> set:
    """Collect all candidate names from DDM files, GHIDRA output, and archive filenames."""
    candidates = set()
    script_dir = os.path.dirname(os.path.abspath(__file__))
    repo_root = os.path.dirname(script_dir)
    builds_dir = os.path.join(repo_root, "Sample", "Builds")

    # 1. DDM texture names (plaintext)
    for ddm_path in list_files_by_extension(thps2x_dir, ".ddm"):
        try:
            objects = read_ddm_names(ddm_path)
            for obj in objects:
                candidates.add(obj['name'])
                for mat_name, tex_name in obj['materials']:
                    if tex_name and tex_name.lower() != 'no_texture_map':
                        candidates.add(tex_name)
                    if mat_name:
                        candidates.add(mat_name)
        except Exception:
            pass
    ddm_count = len(candidates)

    # 2. GHIDRA names
    ghidra_combined = os.path.join(script_dir, "ghidra", "output", "ghidra_names_combined.txt")
    if os.path.exists(ghidra_combined):
        with open(ghidra_combined) as f:
            for line in f:
                line = line.strip()
                if line:
                    candidates.add(line)
    ghidra_count = len(candidates) - ddm_count

    # 3. Archive filenames (scan all builds)
    archive_count_before = len(candidates)
    if os.path.isdir(builds_dir):
        for build in os.listdir(builds_dir):
            build_path = os.path.join(builds_dir, build)
            if not os.path.isdir(build_path):
                continue
            for subdir in os.listdir(build_path):
                subdir_path = os.path.join(build_path, subdir)
                if not os.path.isdir(subdir_path):
                    continue
                for fname in os.listdir(subdir_path):
                    candidates.add(fname)
                    stem = os.path.splitext(fname)[0]
                    if stem != fname:
                        candidates.add(stem)
    archive_count = len(candidates) - archive_count_before

    print(f"  Candidates: {ddm_count} DDM + {ghidra_count} GHIDRA + {archive_count} archive = {len(candidates)} total")
    return candidates


def test_algorithms(thps2x_dir: str):
    """Test all 4 hash algorithms against real PSX texture hashes using all available candidates."""
    print(f"\n{'='*70}")
    print(f"  ALTERNATIVE HASH ALGORITHM TEST")
    print(f"{'='*70}")

    # Collect targets
    real_hashes = collect_real_texture_hashes(thps2x_dir)
    all_hashes = collect_all_texture_hashes(thps2x_dir)
    print(f"\n  Real texture hashes (non-placeholder): {len(real_hashes)}")
    print(f"  All texture hashes (including placeholders): {len(all_hashes)}")

    # Collect candidates
    candidates = collect_candidate_names(thps2x_dir)

    # Also generate stems for all candidates
    expanded = set()
    for name in candidates:
        expanded.add(name)
        stem = os.path.splitext(name)[0]
        if stem != name:
            expanded.add(stem)
    print(f"  Expanded candidates (with stems): {len(expanded)}")

    # Test each algorithm against REAL hashes
    print(f"\n  {'='*60}")
    print(f"  Testing against {len(real_hashes)} REAL texture hashes")
    print(f"  {'='*60}")

    for algo_name, algo_fn in HASH_ALGORITHMS.items():
        matches = []
        for name in expanded:
            h = algo_fn(name)
            if h in real_hashes:
                matches.append((name, h))
        print(f"\n  [{algo_name}]: {len(matches)} matches")
        for name, h in sorted(set(matches))[:30]:
            print(f"    0x{h:08X} = '{name}'")
        if len(matches) > 30:
            print(f"    ... and {len(set(matches)) - 30} more")

    # Also test against ALL hashes (including placeholders)
    print(f"\n  {'='*60}")
    print(f"  Testing against {len(all_hashes)} ALL texture hashes (incl. placeholders)")
    print(f"  {'='*60}")

    for algo_name, algo_fn in HASH_ALGORITHMS.items():
        matches = []
        for name in expanded:
            h = algo_fn(name)
            if h in all_hashes:
                matches.append((name, h))
        print(f"\n  [{algo_name}]: {len(matches)} matches")
        for name, h in sorted(set(matches))[:30]:
            print(f"    0x{h:08X} = '{name}'")
        if len(matches) > 30:
            print(f"    ... and {len(set(matches)) - 30} more")

    # Sanity check: verify QBKey works on known mesh hashes
    print(f"\n  {'='*60}")
    print(f"  Sanity check: algorithms vs known MESH hashes")
    print(f"  {'='*60}")

    psx_dir = os.path.join(thps2x_dir, "PSX")
    all_mesh_hashes = set()
    for f in sorted(os.listdir(psx_dir)):
        if not f.lower().endswith('.psx'):
            continue
        try:
            mh, _, _, _, _ = read_psx_hashes(os.path.join(psx_dir, f))
            all_mesh_hashes.update(mh)
        except Exception:
            continue

    print(f"  Total unique mesh hashes: {len(all_mesh_hashes)}")
    for algo_name, algo_fn in HASH_ALGORITHMS.items():
        matches = []
        for name in expanded:
            h = algo_fn(name)
            if h in all_mesh_hashes:
                matches.append((name, h))
        print(f"  [{algo_name}]: {len(set(matches))} mesh matches")


def dump_ghidra_vs_psx(thps2x_dir: str):
    """Load all GHIDRA-extracted names and check against all PSX texture hashes."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    ghidra_combined = os.path.join(script_dir, "ghidra", "output", "ghidra_names_combined.txt")

    if not os.path.exists(ghidra_combined):
        print(f"GHIDRA combined output not found: {ghidra_combined}")
        return

    # Collect all PSX texture hashes
    psx_dir = os.path.join(thps2x_dir, "PSX")
    all_tex_hashes = set()
    all_mesh_hashes = set()
    for f in sorted(os.listdir(psx_dir)):
        fp = os.path.join(psx_dir, f)
        try:
            mh, th, _, _, _ = read_psx_hashes(fp)
            all_tex_hashes.update(th)
            all_mesh_hashes.update(mh)
        except Exception as e:
            pass  # skip non-PSX files silently

    print(f"\nTotal unique PSX texture hashes: {len(all_tex_hashes)}")
    print(f"Total unique PSX mesh hashes: {len(all_mesh_hashes)}")

    # Load GHIDRA names
    with open(ghidra_combined) as f:
        ghidra_names = [line.strip() for line in f if line.strip()]

    print(f"GHIDRA candidate names: {len(ghidra_names)}")

    tex_matches = []
    mesh_matches = []
    for name in ghidra_names:
        h = qbkey_hash(name)
        if h in all_tex_hashes:
            tex_matches.append((name, h))
        if h in all_mesh_hashes:
            mesh_matches.append((name, h))

        # Also try stem
        stem = os.path.splitext(name)[0] if '.' in name else None
        if stem and stem != name:
            sh = qbkey_hash(stem)
            if sh in all_tex_hashes:
                tex_matches.append((stem, sh))
            if sh in all_mesh_hashes:
                mesh_matches.append((stem, sh))

    print(f"\nGHIDRA -> PSX texture hash matches: {len(tex_matches)}")
    for name, h in sorted(set(tex_matches)):
        print(f"  0x{h:08X} = '{name}'")

    print(f"\nGHIDRA -> PSX mesh hash matches: {len(mesh_matches)}")
    for name, h in sorted(set(mesh_matches))[:30]:
        print(f"  0x{h:08X} = '{name}'")
    if len(mesh_matches) > 30:
        print(f"  ... and {len(set(mesh_matches)) - 30} more")


def main():
    builds_dir = find_builds_dir()
    if not builds_dir:
        print("ERROR: Cannot find Sample/Builds directory")
        sys.exit(1)

    thps2x_dir = get_thps2x_dir(builds_dir)
    if not os.path.isdir(thps2x_dir):
        print(f"ERROR: THPS2X directory not found: {thps2x_dir}")
        sys.exit(1)

    print(f"THPS2X dir: {thps2x_dir}")
    load_known_names()

    args = sys.argv[1:]
    if not args:
        args = ["bboombox"]

    if args[0] == "--all":
        ddm_dir = os.path.join(thps2x_dir, "DDM")
        psx_dir = os.path.join(thps2x_dir, "PSX")
        ddm_bases = {os.path.splitext(f)[0].lower() for f in os.listdir(ddm_dir) if f.lower().endswith('.ddm')}
        psx_bases = set()
        for f in os.listdir(psx_dir):
            if f.lower().endswith('.psx'):
                psx_bases.add(os.path.splitext(f)[0].lower())

        pairs = sorted(ddm_bases & psx_bases)
        print(f"\nFound {len(pairs)} DDM/PSX pairs (out of {len(ddm_bases)} DDM, {len(psx_bases)} PSX)")

        all_psx_tex_hashes = set()
        for basename in pairs:
            analyze_pair(basename, thps2x_dir, all_psx_tex_hashes)

        print(f"\n{'='*70}")
        print(f"  OVERALL: {len(all_psx_tex_hashes)} unique texture hashes across all pairs")
        print(f"{'='*70}")

    elif args[0] == "--analyze-hashes":
        analyze_hash_distribution(thps2x_dir)

    elif args[0] == "--real-hashes":
        analyze_real_vs_placeholder(thps2x_dir)

    elif args[0] == "--test-algorithms":
        test_algorithms(thps2x_dir)

    elif args[0] == "--dump-ghidra":
        dump_ghidra_vs_psx(thps2x_dir)

    else:
        for basename in args:
            analyze_pair(basename, thps2x_dir)


if __name__ == "__main__":
    main()
