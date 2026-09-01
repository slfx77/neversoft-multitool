#!/usr/bin/env python3
"""Reconstruct a THPS3 GBA colour level surface from its 0x70 record.

This is an independent reverse-engineering probe for the production C# reader.
THPS3's table starts at ROM offset 0x0B1440.  Each record stores its visible
surface at +0x10: dimensions in 8-pixel units, an all-8bpp tile pool, a raw
2x2-metatile table, up to four word-RLE map planes, and a BGR555 palette.
"""
from __future__ import annotations

import argparse
import hashlib
import struct
from pathlib import Path

from PIL import Image


ROM_BASE = 0x08000000
TABLE = 0x0B1450
STRIDE = 0x70


def u16(data: bytes, offset: int) -> int:
    return struct.unpack_from("<H", data, offset)[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def pointer(data: bytes, offset: int) -> int:
    value = u32(data, offset)
    if not ROM_BASE <= value < ROM_BASE + len(data):
        raise ValueError(f"invalid ROM pointer 0x{value:08X} at 0x{offset:06X}")
    return value - ROM_BASE


def decode_plane(data: bytes, offset: int) -> tuple[int, int, list[int]]:
    width, height = struct.unpack_from("<HH", data, offset)
    stream = offset + 4 + height * 4
    result = [0] * (width * height)
    for y in range(height):
        cursor = stream + u32(data, offset + 4 + y * 4) * 2
        x = 0
        while x < width:
            command = u16(data, cursor)
            cursor += 2
            kind = command >> 14
            if kind < 2:
                result[y * width + x] = command
                x += 1
                continue
            count = command & 0x3FFF
            if count == 0 or x + count > width:
                raise ValueError(f"invalid row command 0x{command:04X}")
            if kind == 2:
                value = u16(data, cursor)
                cursor += 2
                result[y * width + x:y * width + x + count] = [value] * count
            else:
                result[y * width + x:y * width + x + count] = struct.unpack_from(
                    f"<{count}H", data, cursor
                )
                cursor += count * 2
            x += count
    return width, height, result


def palette_rgba(data: bytes, offset: int) -> list[tuple[int, int, int, int]]:
    palette = []
    for index in range(256):
        colour = u16(data, offset + index * 2)
        expand = lambda value: (value << 3) | (value >> 2)
        palette.append((
            expand(colour & 31),
            expand((colour >> 5) & 31),
            expand((colour >> 10) & 31),
            255,
        ))
    return palette


def render(data: bytes, level: int) -> Image.Image:
    record = TABLE + level * STRIDE
    tiles_w, tiles_h = struct.unpack_from("<HH", data, record)
    tile_count = u32(data, record + 0x04)
    tile_pool = pointer(data, record + 0x08)
    metatiles = pointer(data, record + 0x0C)
    planes = [
        decode_plane(data, pointer(data, record + field))
        for field in range(0x10, 0x20, 4)
        if u32(data, record + field) != 0
    ]
    palette = palette_rgba(data, pointer(data, record + 0x20))

    width, height = tiles_w * 8, tiles_h * 8
    image = Image.new("RGBA", (width, height), (18, 18, 22, 255))
    pixels = image.load()
    for map_w, map_h, refs in planes:
        for my in range(map_h):
            for mx in range(map_w):
                metatile = refs[my * map_w + mx]
                for quad in range(4):
                    tile_ref = u16(data, metatiles + metatile * 8 + quad * 2)
                    tile = tile_ref & 0x3FFF
                    if tile >= tile_count:
                        raise ValueError(
                            f"metatile {metatile} references tile {tile} >= {tile_count}"
                        )
                    flip_x = bool(tile_ref & 0x4000)
                    flip_y = bool(tile_ref & 0x8000)
                    dx = mx * 16 + (quad & 1) * 8
                    dy = my * 16 + (quad >> 1) * 8
                    for y in range(8):
                        py = dy + y
                        if py >= height:
                            continue
                        sy = 7 - y if flip_y else y
                        for x in range(8):
                            px = dx + x
                            if px >= width:
                                continue
                            sx = 7 - x if flip_x else x
                            colour = data[tile_pool + tile * 64 + sy * 8 + sx]
                            if colour:
                                pixels[px, py] = palette[colour]
    return image


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path)
    parser.add_argument("--level", type=int, default=0)
    parser.add_argument("--output", type=Path, default=Path("TestOutput/thps3-surface.png"))
    parser.add_argument("--sha256", action="store_true", help="print the raw RGBA pixel hash")
    args = parser.parse_args()
    data = args.rom.read_bytes()
    image = render(data, args.level)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    image.save(args.output)
    print(f"wrote {image.width}x{image.height} {args.output}")
    if args.sha256:
        print(hashlib.sha256(image.tobytes()).hexdigest().upper())


if __name__ == "__main__":
    main()
