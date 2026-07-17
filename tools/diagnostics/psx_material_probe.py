#!/usr/bin/env python3
"""Inspect PSX mesh face colours and primitive render flags.

This is a narrow diagnostic for comparing the raw face colour word against the
``RGBs`` colour table used by Neversoft's PS1 renderer.  It deliberately reads
the on-disc records rather than converter output, which makes it useful when a
material looks wrong after export.

The reported palette values are source vertex colours, not necessarily the
final in-game colour.  Spider-Man's renderer can additionally apply an
object-level ``ITEMFLAGS_RGB``/``mRGB`` tint at runtime; that scene state is not
stored in the mesh and is intentionally outside this probe.

Usage::

    python tools/diagnostics/psx_material_probe.py path/to/items.psx
    python tools/diagnostics/psx_material_probe.py path/to/level.psx --semi-only
    python tools/diagnostics/psx_material_probe.py path/to/items.psx --mesh 4
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


TAG_STOP = 0xFFFFFFFF
TAG_RGBS = int.from_bytes(b"RGBs", "little")


@dataclass(frozen=True)
class Face:
    index: int
    offset: int
    flags: int
    length: int
    indices: tuple[int, int, int, int]
    colour: tuple[int, int, int, int]
    normal_index: int
    texture_index: int | None


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path)
    parser.add_argument("--mesh", type=int, help="show only this mesh index")
    parser.add_argument(
        "--semi-only", action="store_true", help="show only bit-6 semi-transparent faces"
    )
    parser.add_argument(
        "--gouraud-only", action="store_true", help="show only bit-11 Gouraud faces"
    )
    parser.add_argument(
        "--vertices", action="store_true", help="also print the raw vertex coordinates"
    )
    return parser.parse_args()


def read_header(data: bytes):
    version, magic = struct.unpack_from("<HH", data, 0)
    if version not in (4, 6) or magic != 2:
        raise ValueError(f"not a supported PSX mesh (version={version}, magic={magic})")

    meta_top, object_count = struct.unpack_from("<II", data, 4)
    cursor = 12 + object_count * 36
    mesh_count = u32(data, cursor)
    cursor += 4
    mesh_offsets = struct.unpack_from(f"<{mesh_count}I", data, cursor)

    cursor = meta_top
    colour_table: list[tuple[int, int, int, int]] = []
    while u32(data, cursor) != TAG_STOP:
        tag, length = struct.unpack_from("<II", data, cursor)
        chunk = cursor + 8
        if tag == TAG_RGBS:
            colour_table = [
                struct.unpack_from("<BBBB", data, chunk + i)
                for i in range(0, min(length, 1024), 4)
            ]
        cursor = chunk + length
    cursor += 4

    mesh_names = struct.unpack_from(f"<{mesh_count}I", data, cursor)
    cursor += mesh_count * 4
    texture_count = u32(data, cursor)
    cursor += 4
    texture_ids = struct.unpack_from(f"<{texture_count}I", data, cursor)
    return version, mesh_offsets, mesh_names, texture_ids, colour_table


def parse_mesh(data: bytes, version: int, offset: int):
    cursor = offset
    _, vertex_count, normal_count, face_count = struct.unpack_from("<HHHH", data, cursor)
    cursor += 8
    cursor += 16  # radius + bounding box
    cursor += 4  # all Spider-Man v4/v6 meshes carry zMax + NextLOD
    vertices = [struct.unpack_from("<hhhH", data, cursor + i * 8) for i in range(vertex_count)]
    cursor += vertex_count * 8 + normal_count * 8

    faces: list[Face] = []
    for face_index in range(face_count):
        face_offset = cursor
        flags, length = struct.unpack_from("<HH", data, cursor)
        cursor += 4
        indices = struct.unpack_from("<BBBB", data, cursor)
        cursor += 4
        colour = struct.unpack_from("<BBBB", data, cursor)
        cursor += 4
        normal_index = u16(data, cursor)
        cursor += 4
        texture_index = None
        if flags & 3:
            texture_index = u32(data, cursor)
        faces.append(
            Face(
                face_index,
                face_offset,
                flags,
                length,
                indices,
                colour,
                normal_index,
                texture_index,
            )
        )
        cursor = face_offset + length
    return vertices, normal_count, faces


def palette_text(
    face: Face, palette: list[tuple[int, int, int, int]]
) -> str:
    if not (face.flags & 0x0800) or not palette:
        return ""
    count = 3 if face.flags & 0x0010 else 4
    resolved = [palette[i] if i < len(palette) else None for i in face.colour[:count]]
    return " palette=" + ",".join(str(c[:3] if c else None) for c in resolved)


def main() -> None:
    args = parse_args()
    data = args.input.read_bytes()
    version, mesh_offsets, mesh_names, texture_ids, palette = read_header(data)
    print(
        f"{args.input.name}: v{version} meshes={len(mesh_offsets)} "
        f"textures={len(texture_ids)} RGBs={len(palette)}"
    )
    print(
        "texture ids: "
        + ", ".join(f"{index}=0x{texture_id:08X}" for index, texture_id in enumerate(texture_ids))
    )
    if palette:
        unique = len({colour[:3] for colour in palette})
        print(f"RGBs unique={unique}; entries are raw PS1 RGB bytes")

    for mesh_index, offset in enumerate(mesh_offsets):
        if args.mesh is not None and mesh_index != args.mesh:
            continue
        vertices, normal_count, faces = parse_mesh(data, version, offset)
        xyz = [vertex[:3] for vertex in vertices]
        if xyz:
            bounds = (
                tuple(min(vertex[axis] for vertex in xyz) for axis in range(3)),
                tuple(max(vertex[axis] for vertex in xyz) for axis in range(3)),
            )
        else:
            bounds = ((), ())
        print(
            f"\nmesh {mesh_index}: name/id=0x{mesh_names[mesh_index]:08X} "
            f"vertices={len(vertices)} normals={normal_count} faces={len(faces)} "
            f"bbox={bounds[0]}..{bounds[1]}"
        )
        if args.vertices:
            for index, vertex in enumerate(vertices):
                print(f"  v{index:03} xyz={vertex[:3]} type=0x{vertex[3]:04X}")
        for face in faces:
            if args.semi_only and not face.flags & 0x0040:
                continue
            if args.gouraud_only and not face.flags & 0x0800:
                continue
            texture = "-"
            if face.texture_index is not None:
                texture = (
                    f"{face.texture_index}->0x{texture_ids[face.texture_index]:08X}"
                    if face.texture_index < len(texture_ids)
                    else str(face.texture_index)
                )
            print(
                f"  f{face.index:03} @0x{face.offset:X} flags=0x{face.flags:04X} "
                f"len={face.length} vi={face.indices} rgba/index={face.colour} "
                f"tex={texture}{palette_text(face, palette)}"
            )


if __name__ == "__main__":
    main()
