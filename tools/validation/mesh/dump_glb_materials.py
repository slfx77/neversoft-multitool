#!/usr/bin/env python3
"""Extract embedded PNGs + material metadata from a .glb so we can
diagnose per-texture alpha / blend-mode issues.

Usage:
    python dump_glb_materials.py <input.glb> <output_dir>

Produces:
    <out>/*.png               raw embedded images, named by material index
    <out>/_materials.csv      per-material summary (index, hash, alphaMode, uses, stats)
"""
import io
import json
import os
import struct
import sys
from collections import Counter, defaultdict

from PIL import Image


def load_glb(path):
    with open(path, 'rb') as f:
        magic, version, total = struct.unpack('<4sII', f.read(12))
        assert magic == b'glTF'
        json_len, json_type = struct.unpack('<II', f.read(8))
        assert json_type == 0x4E4F534A  # JSON
        json_bytes = f.read(json_len)
        bin_chunk_len, bin_type = struct.unpack('<II', f.read(8))
        assert bin_type == 0x004E4942  # BIN
        bin_data = f.read(bin_chunk_len)
    return json.loads(json_bytes.decode().rstrip('\x00')), bin_data


def image_bytes(doc, bin_data, image):
    if 'uri' in image and image['uri'].startswith('data:'):
        import base64
        _, _, payload = image['uri'].partition(',')
        return base64.b64decode(payload)
    if 'bufferView' in image:
        bv = doc['bufferViews'][image['bufferView']]
        off, length = bv.get('byteOffset', 0), bv['byteLength']
        return bin_data[off:off + length]
    return None


def alpha_stats(png_bytes):
    """Decode PNG via PIL and bucket alpha into a=0 / mid / a=255."""
    try:
        im = Image.open(io.BytesIO(png_bytes)).convert('RGBA')
    except Exception:
        return None
    pixels = list(im.getdata())
    total = len(pixels)
    a0 = 0
    a255 = 0
    a_lo = 0
    a_mid = 0
    for _, _, _, a in pixels:
        if a == 0:
            a0 += 1
        elif a == 255:
            a255 += 1
        elif a < 128:
            a_lo += 1
        else:
            a_mid += 1
    return {
        'w': im.width,
        'h': im.height,
        'a=0': a0,
        'a<128': a_lo,
        'a>=128': a_mid,
        'a=255': a255,
        'total': total,
    }


def main():
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    glb_path, out_dir = sys.argv[1], sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)

    doc, bin_data = load_glb(glb_path)
    materials = doc.get('materials', [])
    textures = doc.get('textures', [])
    images = doc.get('images', [])
    meshes = doc.get('meshes', [])

    mat_uses = Counter()
    mat_tri_count = defaultdict(int)
    for mesh in meshes:
        for prim in mesh.get('primitives', []):
            if 'material' in prim:
                m = prim['material']
                mat_uses[m] += 1
                idx_acc = prim.get('indices')
                if idx_acc is not None and idx_acc < len(doc.get('accessors', [])):
                    mat_tri_count[m] += doc['accessors'][idx_acc].get('count', 0) // 3

    import csv
    csv_path = os.path.join(out_dir, '_materials.csv')
    with open(csv_path, 'w', newline='') as f:
        w = csv.writer(f)
        w.writerow([
            'mat_idx', 'name', 'alphaMode', 'alphaCutoff',
            'doubleSided', 'uses', 'tris',
            'w', 'h', 'a=0', 'a<128', 'a>=128', 'a=255', 'png_path',
        ])

        for i, mat in enumerate(materials):
            name = mat.get('name', '')
            am = mat.get('alphaMode', 'OPAQUE')
            ac = mat.get('alphaCutoff', 0.5 if am == 'MASK' else '')
            ds = mat.get('doubleSided', False)
            pbr = mat.get('pbrMetallicRoughness', {})

            bct = pbr.get('baseColorTexture', {})
            tex_idx = bct.get('index')
            png_path = ''
            stats = None
            if tex_idx is not None and tex_idx < len(textures):
                src = textures[tex_idx].get('source')
                if src is not None and src < len(images):
                    img = images[src]
                    raw = image_bytes(doc, bin_data, img)
                    if raw is not None:
                        safe = name.replace('/', '_').replace('\\', '_') or f'mat{i}'
                        png_path = os.path.join(out_dir, f'{i:04d}_{safe}_{am}.png')
                        with open(png_path, 'wb') as of:
                            of.write(raw)
                        stats = alpha_stats(raw)

            row = [i, name, am, ac, ds, mat_uses[i], mat_tri_count[i]]
            if stats:
                row.extend([stats['w'], stats['h'], stats['a=0'], stats['a<128'], stats['a>=128'], stats['a=255']])
            else:
                row.extend(['', '', '', '', '', ''])
            row.append(png_path)
            w.writerow(row)

    print(f'Wrote {len(materials)} material rows + PNGs to {out_dir}')
    print(f'Summary CSV: {csv_path}')

    am_counts = Counter(m.get('alphaMode', 'OPAQUE') for m in materials)
    print(f'Alpha modes: {dict(am_counts)}')


if __name__ == '__main__':
    main()
