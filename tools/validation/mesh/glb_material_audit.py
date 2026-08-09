"""
glb_material_audit.py — print material/primitive/texture coverage for a GLB.

Usage:
    python tools/validation/mesh/glb_material_audit.py <path/to/file.glb>

Reads the JSON chunk of a GLB and prints, per material, whether the BaseColor
channel has an image attached (and what the baseColor factor is). Then per
mesh primitive, which material it uses and whether it carries TEXCOORD_0.

Useful for diagnosing "white poly" / missing-texture issues where a material
is created without a BaseColor texture image, falling back to the (typically
white) baseColorFactor in viewers.
"""
import json
import struct
import sys
from pathlib import Path


def read_glb(path: Path):
    with path.open('rb') as f:
        magic = f.read(4)
        if magic != b'glTF':
            raise ValueError(f"Not a GLB file (magic={magic!r})")
        version, length = struct.unpack('<II', f.read(8))
        chunk_len, chunk_type = struct.unpack('<II', f.read(8))
        if chunk_type != 0x4E4F534A:  # 'JSON'
            raise ValueError(f"First chunk is not JSON (type=0x{chunk_type:08X})")
        json_data = json.loads(f.read(chunk_len).rstrip(b'\x00').decode('utf-8'))
        # Read BIN chunk
        bin_data = b''
        if f.tell() < length:
            bin_chunk_len, bin_chunk_type = struct.unpack('<II', f.read(8))
            bin_data = f.read(bin_chunk_len)
        return json_data, bin_data


def read_glb_json(path: Path) -> dict:
    return read_glb(path)[0]


def get_image_bytes(data: dict, bin_data: bytes, image_idx: int):
    """Return (mime_type, image_bytes) for a glTF image."""
    img = data['images'][image_idx]
    if 'bufferView' in img:
        bv = data['bufferViews'][img['bufferView']]
        offset = bv.get('byteOffset', 0)
        length = bv['byteLength']
        return img.get('mimeType', 'image/png'), bin_data[offset:offset + length]
    return None, None


def analyze_image(image_bytes: bytes):
    """Return (width, height, mean_rgb) of a PNG image without pulling PIL."""
    if image_bytes[:8] != b'\x89PNG\r\n\x1a\n':
        return None
    # IHDR chunk starts after 8-byte signature
    ihdr_len = struct.unpack('>I', image_bytes[8:12])[0]
    width, height = struct.unpack('>II', image_bytes[16:24])
    return width, height, None  # mean_rgb not computed (would need zlib + filter parsing)


def main():
    if len(sys.argv) < 2:
        print(__doc__, file=sys.stderr)
        sys.exit(2)

    path = Path(sys.argv[1])
    if not path.is_file():
        print(f"File not found: {path}", file=sys.stderr)
        sys.exit(1)

    data, bin_data = read_glb(path)
    materials = data.get('materials', [])
    meshes = data.get('meshes', [])
    textures = data.get('textures', [])
    images = data.get('images', [])
    samplers = data.get('samplers', [])
    accessors = data.get('accessors', [])
    buffer_views = data.get('bufferViews', [])

    print(f"\n=== {path.name} ===")
    print(f"materials: {len(materials)}, textures: {len(textures)}, images: {len(images)}, meshes: {len(meshes)}, samplers: {len(samplers)}")

    # Analyze unique image dimensions
    print("\n--- Images (size + bytes) ---")
    for i, img in enumerate(images):
        mime, img_bytes = get_image_bytes(data, bin_data, i)
        size_info = analyze_image(img_bytes) if img_bytes else None
        if size_info:
            w, h, _ = size_info
            print(f"  image[{i}] mime={mime} bytes={len(img_bytes):>6} dims={w}x{h}")
        else:
            print(f"  image[{i}] mime={mime} bytes={len(img_bytes) if img_bytes else 0}")

    # Analyze samplers
    print("\n--- Samplers ---")
    for i, s in enumerate(samplers):
        mag = s.get('magFilter', '?')
        minf = s.get('minFilter', '?')
        wrap_s = s.get('wrapS', 10497)  # default REPEAT
        wrap_t = s.get('wrapT', 10497)
        wrap_names = {33071: 'CLAMP', 33648: 'MIRROR', 10497: 'REPEAT'}
        print(f"  sampler[{i}] wrapS={wrap_names.get(wrap_s, wrap_s)} wrapT={wrap_names.get(wrap_t, wrap_t)}")

    print("\n--- Materials ---")
    print(f"{'idx':>3}  {'has_img':<7}  {'alpha':<6}  {'2sided':<6}  {'baseColor':<28}  name")
    materials_with_image = 0
    materials_without_image = 0
    for i, mat in enumerate(materials):
        pbr = mat.get('pbrMetallicRoughness', {})
        bc = pbr.get('baseColorFactor', [1, 1, 1, 1])
        bc_str = f"({bc[0]:.2f},{bc[1]:.2f},{bc[2]:.2f},{bc[3]:.2f})"
        has_img = 'baseColorTexture' in pbr
        alpha = mat.get('alphaMode', 'OPAQUE')
        two = 'yes' if mat.get('doubleSided') else 'no'
        name = mat.get('name', f'<{i}>')
        if has_img:
            materials_with_image += 1
        else:
            materials_without_image += 1
        flag = 'YES' if has_img else '---'
        print(f"{i:>3}  {flag:<7}  {alpha:<6}  {two:<6}  {bc_str:<28}  {name}")

    def read_uv_range(uv_acc_idx):
        """Return (umin, umax, vmin, vmax) of a TEXCOORD_0 accessor."""
        acc = accessors[uv_acc_idx]
        bv = buffer_views[acc['bufferView']]
        offset = bv.get('byteOffset', 0) + acc.get('byteOffset', 0)
        count = acc['count']
        # Vec2 of float = 8 bytes per vertex
        umin = vmin = float('inf')
        umax = vmax = float('-inf')
        for vi in range(count):
            u, v = struct.unpack_from('<2f', bin_data, offset + vi * 8)
            if u < umin: umin = u
            if u > umax: umax = u
            if v < vmin: vmin = v
            if v > vmax: vmax = v
        return umin, umax, vmin, vmax

    print("\n--- Mesh primitives ---")
    print(f"{'mesh':>4}  {'prim':>4}  {'mat':>4}  {'verts':>6}  {'U range':<14}  {'V range':<14}  material")
    prims_with_uv_no_tex = 0
    prims_no_uv = 0
    prims_total = 0
    prims_uv_oor = 0  # UV out of [0,1]
    for mi, mesh in enumerate(meshes):
        for pi, prim in enumerate(mesh.get('primitives', [])):
            prims_total += 1
            mat_idx = prim.get('material', -1)
            mat_name = (
                materials[mat_idx].get('name', f'<{mat_idx}>')
                if 0 <= mat_idx < len(materials) else '(none)'
            )
            attrs = prim.get('attributes', {})
            has_uv = 'TEXCOORD_0' in attrs
            pos_acc = attrs.get('POSITION')
            if pos_acc is not None:
                verts = data['accessors'][pos_acc]['count']
            else:
                verts = 0
            mat_has_img = (0 <= mat_idx < len(materials)
                           and 'baseColorTexture' in materials[mat_idx].get('pbrMetallicRoughness', {}))
            if has_uv and not mat_has_img:
                prims_with_uv_no_tex += 1
            if not has_uv:
                prims_no_uv += 1

            u_str = v_str = '(no uv)'
            if has_uv:
                umin, umax, vmin, vmax = read_uv_range(attrs['TEXCOORD_0'])
                u_str = f"[{umin:+.2f},{umax:+.2f}]"
                v_str = f"[{vmin:+.2f},{vmax:+.2f}]"
                if umin < -0.001 or umax > 1.001 or vmin < -0.001 or vmax > 1.001:
                    prims_uv_oor += 1
                    u_str += '!'
            print(f"{mi:>4}  {pi:>4}  {mat_idx:>4}  {verts:>6}  {u_str:<14}  {v_str:<14}  {mat_name}")

    print("\n--- Summary ---")
    print(f"  materials WITH baseColorTexture:    {materials_with_image}")
    print(f"  materials WITHOUT baseColorTexture: {materials_without_image}")
    print(f"  primitives total:                   {prims_total}")
    print(f"  primitives with UV but no tex:      {prims_with_uv_no_tex}")
    print(f"  primitives without UV:              {prims_no_uv}")
    print(f"  primitives with UV out of [0,1]:    {prims_uv_oor}  (CLAMP samplers would clip these)")


if __name__ == '__main__':
    main()
