#!/usr/bin/env python3
"""Survey Downhill Jam GBA's sentinel-terminated polygon blobs.

The Visual Impact engine stores raw, part-local model banks as grouped vertex
counts, grouped face counts, 8-byte vertices, and four-byte triangle records.
They only become a rider after applying a separate 13-part pose.  This research
helper finds the raw banks without relying on the one model observed in RAM.
"""
from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


SENTINEL = b"\x67\x45\x23\x01"


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


@dataclass(frozen=True)
class Candidate:
    header: int
    marker: int
    groups: int
    vertex_counts: tuple[int, ...]
    face_counts_offset: int
    face_counts: tuple[int, ...]
    vertices: int
    faces: int
    vertex_data: int
    face_data: int


def candidates_for(data: bytes, end: int, lookback: int = 0x1000) -> list[Candidate]:
    found: list[Candidate] = []
    lower = max(0, end - lookback)
    for header in range(lower, end - 8, 2):
        marker = u16(data, header)
        groups = u16(data, header + 2)
        if groups < 1 or groups > 32 or marker == 0:
            continue
        counts_end = header + 4 + groups * 2
        if counts_end > end:
            continue
        vertex_counts = tuple(u16(data, header + 4 + i * 2) for i in range(groups))
        if any(count == 0 or count > 4096 for count in vertex_counts):
            continue
        vertex_count = sum(vertex_counts)
        if vertex_count > 8192:
            continue

        for face_counts_offset in range((counts_end + 3) & ~3, min(counts_end + 0x100, end), 4):
            face_counts = tuple(u16(data, face_counts_offset + i * 2) for i in range(groups))
            if any(count == 0 or count > 8192 for count in face_counts):
                continue
            face_count = sum(face_counts)
            if face_count > 16384:
                continue
            face_data = end - face_count * 4
            vertex_data = face_data - vertex_count * 8
            if vertex_data < face_counts_offset + groups * 2 or vertex_data >= face_data:
                continue
            if vertex_data - header > 0x400:
                continue

            valid = True
            referenced: set[int] = set()
            for face in range(face_count):
                pos = face_data + face * 4
                indices = data[pos : pos + 3]
                if len(indices) != 3 or any(index >= vertex_count for index in indices):
                    valid = False
                    break
                referenced.update(indices)
            if not valid or not referenced or max(referenced) + 1 != vertex_count:
                continue

            found.append(Candidate(
                header, marker, groups, vertex_counts, face_counts_offset,
                face_counts, vertex_count, face_count, vertex_data, face_data))
    return found


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path)
    parser.add_argument("--offset", type=lambda value: int(value, 0))
    parser.add_argument("--limit", type=int, default=200)
    args = parser.parse_args()

    data = args.rom.read_bytes()
    ends = []
    start = 0
    while True:
        hit = data.find(SENTINEL, start)
        if hit < 0:
            break
        if args.offset is None or hit == args.offset:
            ends.append(hit)
        start = hit + len(SENTINEL)

    print(f"sentinels={len(ends)}")
    for end in ends[: args.limit]:
        found = candidates_for(data, end)
        if not found:
            print(f"0x{end:06X}: no candidate")
            continue
        # Prefer the tightest header-to-vertex-data prelude.
        found.sort(key=lambda item: (item.vertex_data - item.header, item.header))
        item = found[0]
        print(
            f"0x{end:06X}: header=0x{item.header:06X} marker={item.marker} "
            f"groups={item.groups} vertices={item.vertices}@0x{item.vertex_data:06X} "
            f"faces={item.faces}@0x{item.face_data:06X} "
            f"prelude=0x{item.vertex_data - item.header:X} candidates={len(found)}"
        )


if __name__ == "__main__":
    main()
