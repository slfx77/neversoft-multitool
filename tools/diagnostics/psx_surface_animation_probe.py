#!/usr/bin/env python3
"""Summarize PS1 PSX mesh surface flags and tag-6/tag-7 animation data.

The Spider-Man/THPS2 PS1 renderer stores scrolling/wibbling UV data in tagged
chunk 6 and pulsing RGB palette keys in tagged chunk 7.  This probe correlates
those chunks with the on-disc object and mesh records without mutating them.

Usage::

    python tools/diagnostics/psx_surface_animation_probe.py path/to/chopper.psx
    python tools/diagnostics/psx_surface_animation_probe.py path/to/torch.psx --details
"""

from __future__ import annotations

import argparse
import collections
import struct
from dataclasses import dataclass
from pathlib import Path


TAG_STOP = 0xFFFFFFFF
TAG_RGBS = int.from_bytes(b"RGBs", "little")


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def i16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<h", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def i32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<i", data, offset)[0]


@dataclass(frozen=True)
class Face:
    offset: int
    flags: int
    length: int
    texture_index: int | None
    uvs: tuple[tuple[int, int], ...]


@dataclass(frozen=True)
class Mesh:
    index: int
    faces: tuple[Face, ...]


def read_mesh(data: bytes, version: int, mesh_index: int, offset: int) -> Mesh:
    if version != 4:
        raise ValueError("this diagnostic currently targets PS1 Spider-Man v4 assets")
    _, vertex_count, normal_count, face_count = struct.unpack_from("<HHHH", data, offset)
    cursor = offset + 8 + 16 + 4 + vertex_count * 8 + normal_count * 8
    faces: list[Face] = []
    for _ in range(face_count):
        face_offset = cursor
        flags, length = struct.unpack_from("<HH", data, cursor)
        cursor += 16
        texture_index = None
        uvs: tuple[tuple[int, int], ...] = ()
        if flags & 3:
            texture_index = u32(data, cursor)
            cursor += 4
            uvs = tuple(struct.unpack_from("<BB", data, cursor + slot * 2) for slot in range(4))
        faces.append(Face(face_offset, flags, length, texture_index, uvs))
        cursor = face_offset + length
    return Mesh(mesh_index, tuple(faces))


def parse_texture_catalog(data: bytes, cursor: int, mesh_count: int):
    cursor += 4 + mesh_count * 4  # stop tag + mesh-name table
    texture_hash_count = u32(data, cursor)
    cursor += 4
    hashes = struct.unpack_from(f"<{texture_hash_count}I", data, cursor)
    cursor += texture_hash_count * 4
    palette4_count = u32(data, cursor)
    cursor += 4 + palette4_count * (4 + 16 * 2)
    palette8_count = u32(data, cursor)
    cursor += 4 + palette8_count * (4 + 256 * 2)
    actual_count = u32(data, cursor)
    cursor += 4
    if actual_count == TAG_STOP:
        detail_count = u32(data, cursor)
        cursor += 4 + detail_count * 36
        cubemap_count = u32(data, cursor)
        cursor += 4 + cubemap_count * 36
        actual_count = u32(data, cursor)
        cursor += 4
    cursor += actual_count * 4
    catalog = {}
    for _ in range(actual_count):
        record_offset = cursor
        pal_size = u32(data, cursor + 4)
        texture_index = u32(data, cursor + 12)
        width, height = struct.unpack_from("<HH", data, cursor + 16)
        header_size = 28 if pal_size == 65536 else 20
        texture_start = cursor + header_size
        if texture_index < len(hashes):
            catalog[texture_index] = (hashes[texture_index], width, height, record_offset)
        if pal_size == 65536:
            size = u32(data, cursor + 24)
            cursor = texture_start + size
        elif pal_size == 16:
            padded_width = ((width + 3) & ~3) >> 1
            padding = 2 if height % 2 and padded_width % 4 else 0
            cursor = texture_start + padded_width * height + padding
        elif pal_size == 256:
            padded_width = (width + 1) & ~1
            padding = 2 if height % 2 and padded_width % 4 else 0
            cursor = texture_start + padded_width * height + padding
        else:
            raise ValueError(f"unknown palette size {pal_size}")
    return catalog


def parse_file(data: bytes):
    version, magic = struct.unpack_from("<HH", data, 0)
    if version != 4 or magic != 2:
        raise ValueError(f"expected a Spider-Man PS1 v4 mesh, got version={version} magic={magic}")
    meta_top, object_count = struct.unpack_from("<II", data, 4)
    objects = []
    for object_index in range(object_count):
        offset = 12 + object_index * 36
        mesh_index = u16(data, offset + 0x16)
        objects.append((offset, mesh_index))
    cursor = 12 + object_count * 36
    mesh_count = u32(data, cursor)
    cursor += 4
    mesh_offsets = struct.unpack_from(f"<{mesh_count}I", data, cursor)
    meshes = tuple(read_mesh(data, version, i, offset) for i, offset in enumerate(mesh_offsets))

    chunks = []
    palette: tuple[tuple[int, int, int, int], ...] = ()
    cursor = meta_top
    while cursor + 4 <= len(data):
        tag = u32(data, cursor)
        if tag == TAG_STOP:
            break
        length = u32(data, cursor + 4)
        start = cursor + 8
        end = start + length
        if end > len(data):
            raise ValueError(f"chunk 0x{tag:08X} extends past EOF")
        chunks.append((tag, start, length))
        if tag == TAG_RGBS:
            palette = tuple(
                struct.unpack_from("<BBBB", data, start + offset)
                for offset in range(0, min(length, 1024), 4)
            )
        cursor = end
    texture_catalog = parse_texture_catalog(data, cursor, mesh_count)
    return version, objects, meshes, chunks, palette, texture_catalog


def tag_name(tag: int) -> str:
    if tag == 6:
        return "UV-wibble"
    if tag == 7:
        return "RGB-pulse"
    if tag == TAG_RGBS:
        return "RGBs"
    raw = tag.to_bytes(4, "little")
    if all(32 <= byte < 127 for byte in raw):
        return raw.decode("ascii")
    return f"0x{tag:X}"


def read_wibbles(data: bytes, start: int, length: int):
    cursor = start
    end = start + length
    items = []
    while cursor + 16 <= end:
        item_offset = u32(data, cursor)
        if item_offset == 0:
            break
        u_vel, v_vel = i16(data, cursor + 4), i16(data, cursor + 6)
        frequency = i32(data, cursor + 8)
        face_count = u16(data, cursor + 12)
        zero_u, zero_v = data[cursor + 14], data[cursor + 15]
        info_start = cursor + 16
        info_end = info_start + face_count * 16
        if info_end > end:
            raise ValueError("truncated UV-wibble item")
        base_uvs = []
        for face_index in range(face_count):
            face_start = info_start + face_index * 16
            base_uvs.append(tuple(
                struct.unpack_from("<BBBB", data, face_start + slot * 4)
                for slot in range(4)
            ))
        items.append((item_offset, u_vel, v_vel, frequency, zero_u, zero_v, tuple(base_uvs)))
        cursor = info_end
    return items


def read_pulses(data: bytes, start: int, length: int):
    cursor = start
    end = start + length
    entries = []
    while cursor + 4 <= end:
        colour_index, key_count, key_index, time_acc = struct.unpack_from("<BBBB", data, cursor)
        keys_end = cursor + 4 + key_count * 4
        if key_count == 0 or keys_end > end:
            raise ValueError("invalid RGB-pulse entry")
        keys = tuple(
            struct.unpack_from("<BBBB", data, cursor + 4 + key * 4)
            for key in range(key_count)
        )
        entries.append((colour_index, key_index, time_acc, keys))
        cursor = keys_end
    return entries


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--details", action="store_true")
    args = parser.parse_args()

    data = args.input.read_bytes()
    version, objects, meshes, chunks, palette, texture_catalog = parse_file(data)
    flags = collections.Counter(face.flags for mesh in meshes for face in mesh.faces)
    faces = [face for mesh in meshes for face in mesh.faces]
    print(
        f"{args.input.name}: v{version} objects={len(objects)} meshes={len(meshes)} "
        f"faces={len(faces)} chunks="
        + ", ".join(f"{tag_name(tag)}:{length}" for tag, _, length in chunks)
    )
    print(
        f"  textured={sum(bool(face.flags & 3) for face in faces)} "
        f"gouraud={sum(bool(face.flags & 0x800) for face in faces)} "
        f"semi={sum(bool(face.flags & 0x40) for face in faces)} "
        f"animated-bit5={sum(bool(face.flags & 0x20) for face in faces)}"
    )
    print("  common flags: " + ", ".join(
        f"0x{flag:04X}={count}" for flag, count in flags.most_common(12)
    ))
    edge_samples = 0
    outside_samples = 0
    total_samples = 0
    for face in faces:
        if face.texture_index is None or face.texture_index not in texture_catalog:
            continue
        _, width, height, _ = texture_catalog[face.texture_index]
        for u, v in face.uvs:
            total_samples += 1
            edge_samples += int(u in (0, width - 1) or v in (0, height - 1))
            outside_samples += int(u >= width or v >= height)
    print(
        f"  UV samples={total_samples} edge={edge_samples} outside-cropped-texture={outside_samples} "
        f"textures={len(texture_catalog)}"
    )
    used_textures = collections.Counter()
    animated_textures = collections.Counter()
    unresolved_animated_faces = 0
    for face in faces:
        if face.texture_index is not None and face.texture_index in texture_catalog:
            texture_hash = texture_catalog[face.texture_index][0]
            used_textures[texture_hash] += 1
        if not face.flags & 0x20:
            continue
        if face.texture_index is None or face.texture_index not in texture_catalog:
            unresolved_animated_faces += 1
            continue
        texture_hash = texture_catalog[face.texture_index][0]
        animated_textures[texture_hash] += 1
    if animated_textures or unresolved_animated_faces:
        print(
            "  animated face textures: "
            + ", ".join(
                f"0x{texture_hash:08X}={count}"
                for texture_hash, count in animated_textures.most_common()
            )
            + (f", unresolved={unresolved_animated_faces}" if unresolved_animated_faces else "")
        )
    if args.details:
        print("  texture catalog:")
        for texture_index, (texture_hash, width, height, record_offset) in sorted(texture_catalog.items()):
            print(
                f"    index={texture_index:02} hash=0x{texture_hash:08X} "
                f"size={width}x{height} record=0x{record_offset:08X} "
                f"faces={used_textures[texture_hash]} animated-faces={animated_textures[texture_hash]}"
            )
        animated_face_rows = []
        for mesh in meshes:
            for face_index, face in enumerate(mesh.faces):
                if not face.flags & 0x20:
                    continue
                texture_hash = None
                if face.texture_index is not None and face.texture_index in texture_catalog:
                    texture_hash = texture_catalog[face.texture_index][0]
                animated_face_rows.append(
                    f"mesh={mesh.index:02} face={face_index:03} flags=0x{face.flags:04X} "
                    f"texture=" + (f"0x{texture_hash:08X}" if texture_hash is not None else "unresolved")
                    + (" uv=" + ",".join(f"({u},{v})" for u, v in face.uvs) if face.uvs else "")
                )
        if animated_face_rows:
            print("  animated faces:")
            for row in animated_face_rows:
                print(f"    {row}")

    object_by_offset = {offset: (index, mesh_index) for index, (offset, mesh_index) in enumerate(objects)}
    for tag, start, length in chunks:
        if tag == 6:
            items = read_wibbles(data, start, length)
            print(f"  UV-wibble items={len(items)} faces={sum(len(item[6]) for item in items)}")
            for item_offset, u_vel, v_vel, frequency, zero_u, zero_v, base_uvs in items:
                target = object_by_offset.get(item_offset)
                target_text = "unresolved"
                mismatch = ""
                if target:
                    object_index, mesh_index = target
                    target_text = f"object={object_index} mesh={mesh_index}"
                    if mesh_index < len(meshes):
                        target_faces = meshes[mesh_index].faces[: len(base_uvs)]
                        bit5 = sum(bool(face.flags & 0x20) for face in target_faces)
                        mismatch = f" target-bit5={bit5}/{len(target_faces)}"
                print(
                    f"    item@0x{item_offset:X} {target_text} faces={len(base_uvs)} "
                    f"vel=({u_vel},{v_vel}) freq={frequency} zero=({zero_u},{zero_v}){mismatch}"
                )
                if args.details:
                    for index, uv_set in enumerate(base_uvs):
                        print(f"      f{index:03}: " + ", ".join(
                            f"uv=({u},{v}) ampPhase=({ua:02X},{va:02X})"
                            for u, v, ua, va in uv_set
                        ))
        elif tag == 7:
            pulses = read_pulses(data, start, length)
            print(f"  RGB-pulse entries={len(pulses)}")
            for colour_index, key_index, time_acc, keys in pulses:
                first = keys[0]
                original = palette[colour_index][:3] if colour_index < len(palette) else None
                print(
                    f"    colour={colour_index} keys={len(keys)} state=({key_index},{time_acc}) "
                    f"original={original} first=rgb({first[0]},{first[1]},{first[2]})/{first[3]}"
                )
                if args.details:
                    print("      " + ", ".join(
                        f"rgb({r},{g},{b})/{interval}" for r, g, b, interval in keys
                    ))


if __name__ == "__main__":
    main()
