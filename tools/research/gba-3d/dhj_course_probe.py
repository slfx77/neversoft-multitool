#!/usr/bin/env python3
"""Find and summarize Downhill Jam GBA's self-relative course containers.

The course loader at ROM 0x080066EC retains each header's eight relative
section offsets in IWRAM.  This probe applies the same relationships and then
requires the chunk, mesh, centre, collision, object, edge and paired-texture
sections to close. It deliberately does not rely on retail offsets.
"""
from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


@dataclass(frozen=True)
class Course:
    base: int
    chunks: int
    vertices: int
    triangles: int
    objects: int
    texture_pages: int
    collision_lists: int
    left_points: int
    right_points: int
    vertex_offset: int
    triangle_offset: int
    centre_offset: int
    left_offset: int
    right_offset: int
    collision_offset: int
    object_offset: int


def find_courses(data: bytes) -> list[Course]:
    if len(data) < 0xB0 or data[0xAC:0xAF] != b"BXS":
        return []
    courses: list[Course] = []
    for base in range(0, len(data) - 0x20 + 1, 4):
        offsets = struct.unpack_from("<8I", data, base)
        vertex_rel, face_rel, centre_rel, objects, left_rel, right_rel, collision_rel, object_rel = offsets
        if not (0x20 < vertex_rel < face_rel < centre_rel < collision_rel
                < object_rel < left_rel < right_rel <= 0x800000):
            continue
        if not (0 < objects <= 4096):
            continue
        if (vertex_rel - 0x20) % 0x30:
            continue
        ends = [base + value for value in (vertex_rel, face_rel, centre_rel, left_rel,
                                            right_rel, collision_rel, object_rel)]
        if any(value < 0 or value > len(data) - 2 for value in ends):
            continue
        vertex, face, centre, left, right, collision, obj = ends
        if centre > len(data) - 4:
            continue
        chunks = u32(data, centre)
        record_count = (vertex_rel - 0x20) // 0x30
        if not (32 <= chunks <= 4096) or record_count != chunks + 1:
            continue

        vertex_bytes = face_rel - vertex_rel
        face_bytes = centre_rel - face_rel
        if vertex_bytes % 6 > 2 or face_bytes % 14 > 2:
            continue
        vertices = vertex_bytes // 6
        triangles = face_bytes // 14
        if not (3 <= vertices <= 0xFFFF and 0 < triangles <= 200_000):
            continue

        # Centre records are widened (Y,Z,X) triples. Object and first-edge
        # sections abut; the first edge ends exactly at the second.
        if centre + 4 + (chunks + 1) * 12 > collision:
            continue
        if obj + objects * 16 != left:
            continue
        left_points = u16(data, left)
        if left_points < 2 or left + 2 + left_points * 6 != right:
            continue
        right_value = u16(data, right)
        if 2 <= right_value <= 4096 and right + 2 + right_value * 6 <= len(data):
            right_points = right_value
        elif right_value == 0xCDCD:
            right_points = 0
        else:
            continue

        # Each 48-byte chunk names inclusive vertex bounds and a half-open
        # triangle range. Include the engine's look-ahead record.
        chunk_table = base + 0x20
        if chunk_table + record_count * 48 != vertex:
            continue
        records = [struct.unpack_from("<24H", data, chunk_table + i * 48)
                   for i in range(record_count)]
        highest_vertex = -1
        highest_face = -1
        valid = True
        for record in records:
            vertex_start, vertex_end, face_start, face_end = record[:4]
            empty_vertices = vertex_start == 0x7FFF and vertex_end == 0
            if ((not empty_vertices and (vertex_start > vertex_end or vertex_end >= vertices))
                    or face_start > face_end or face_end > triangles):
                valid = False
                break
            if not empty_vertices:
                highest_vertex = max(highest_vertex, vertex_end)
            highest_face = max(highest_face, face_end)
        if not valid or highest_vertex + 1 != vertices or highest_face != triangles:
            continue

        # Every authored triangle must address the bounded vertex bank.
        highest_texture = -1
        for i in range(triangles):
            a, b, c = struct.unpack_from("<3H", data, face + i * 14)
            if a >= vertices or b >= vertices or c >= vertices:
                valid = False
                break
            material = u16(data, face + i * 14 + 12)
            page = material & 0x3F
            if page != 0x3F:
                highest_texture = max(highest_texture, page)
        if not valid:
            continue

        # The indexed texture package must immediately precede the course and
        # have one unambiguous page count.
        texture_pages = 0
        for pages in range(max(1, highest_texture + 1), 33):
            texture_bytes = 0x208 + pages * 128 * 128
            texture = base - texture_bytes
            if texture < 0 or data[texture:texture + 8] != struct.pack("<4H", pages, 128, 0, 0x45):
                continue
            if any(pixel >= 240 for pixel in data[texture + 0x208:base]):
                continue
            if texture_pages:
                texture_pages = 0
                break
            texture_pages = pages
        if not texture_pages:
            continue

        # Packed chunk refs use low24 as a halfword offset into +18. The unique
        # lists must abut through the pool, with at most one alignment halfword.
        refs = sorted({u32(data, chunk_table + i * 48 + field) & 0xFFFFFF
                       for i in range(record_count)
                       for field in range(0x10, 0x20, 4)
                       if u32(data, chunk_table + i * 48 + field) != 0xFFFFFFFF})
        if not refs or refs[0] != 0:
            continue
        expected = collision
        for ref in refs:
            at = collision + ref * 2
            if at != expected or at > obj - 2:
                valid = False
                break
            count = u16(data, at)
            end = at + 2 + count * 8
            if count < 2 or end > obj:
                valid = False
                break
            expected = end
        if not valid or expected not in (obj, obj - 2):
            continue

        courses.append(Course(base, chunks, vertices, triangles, objects,
                              texture_pages, len(refs), left_points, right_points,
                              vertex, face, centre, left, right, collision, obj))
    return courses


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path)
    args = parser.parse_args()
    data = args.rom.read_bytes()
    courses = find_courses(data)
    print(f"courses={len(courses)}")
    for i, course in enumerate(courses):
        print(
            f"{i:02d} base=0x{course.base:06X} chunks={course.chunks} "
            f"vertices={course.vertices}@0x{course.vertex_offset:06X} "
            f"triangles={course.triangles}@0x{course.triangle_offset:06X} "
            f"pages={course.texture_pages} collision_lists={course.collision_lists} "
            f"objects={course.objects}@0x{course.object_offset:06X} "
            f"edges={course.left_points}/{course.right_points}"
            f"@0x{course.left_offset:06X}/0x{course.right_offset:06X} "
            f"collision=0x{course.collision_offset:06X}"
        )


if __name__ == "__main__":
    main()
