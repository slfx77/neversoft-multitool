#!/usr/bin/env python3
"""List structurally valid GBA BIOS-LZ77 streams in a cartridge image.

The scanner is intentionally independent of the application decoder so it can
act as a reverse-engineering control.  By default it reports the greedy,
non-overlapping stream population and groups it by decompressed byte size.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from pathlib import Path


def decompress(data: bytes, offset: int) -> tuple[bytes, int] | None:
    if offset < 0 or offset + 4 > len(data) or data[offset] != 0x10:
        return None
    size = int.from_bytes(data[offset + 1:offset + 4], "little")
    if not 0 < size <= 16 * 1024 * 1024:
        return None
    source = offset + 4
    out = bytearray()
    while len(out) < size:
        if source >= len(data):
            return None
        flags = data[source]
        source += 1
        for bit in range(7, -1, -1):
            if len(out) >= size:
                break
            if flags & (1 << bit):
                if source + 2 > len(data):
                    return None
                first, second = data[source:source + 2]
                source += 2
                count = (first >> 4) + 3
                distance = ((first & 0x0F) << 8 | second) + 1
                if distance > len(out):
                    return None
                for _ in range(count):
                    if len(out) >= size:
                        break
                    out.append(out[-distance])
            else:
                if source >= len(data):
                    return None
                out.append(data[source])
                source += 1
    return bytes(out), source - offset


def scan(data: bytes, alignment: int) -> list[tuple[int, int, bytes]]:
    hits: list[tuple[int, int, bytes]] = []
    for offset in range(0, len(data) - 4, alignment):
        if data[offset] != 0x10:
            continue
        decoded = decompress(data, offset)
        if decoded is not None:
            payload, stored = decoded
            hits.append((offset, stored, payload))
    return hits


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("rom", type=Path)
    parser.add_argument("--alignment", type=int, default=4)
    parser.add_argument("--all", action="store_true", help="retain overlapping hits")
    parser.add_argument("--size", type=int, help="only list streams of this decoded size")
    parser.add_argument("--extract", type=lambda value: int(value, 0),
                        help="decompress the stream at this ROM offset")
    parser.add_argument("--output", type=Path, help="output path used with --extract")
    args = parser.parse_args()

    data = args.rom.read_bytes()
    if args.extract is not None:
        decoded = decompress(data, args.extract)
        if decoded is None:
            raise SystemExit(f"no valid BIOS-LZ77 stream at 0x{args.extract:X}")
        if args.output is None:
            raise SystemExit("--extract requires --output")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_bytes(decoded[0])
        print(f"wrote {len(decoded[0])} bytes to {args.output}")
        return

    hits = scan(data, args.alignment)
    if not args.all:
        non_overlapping: list[tuple[int, int, bytes]] = []
        end = -1
        for hit in hits:
            if hit[0] >= end:
                non_overlapping.append(hit)
                end = hit[0] + hit[1]
        hits = non_overlapping

    by_size: dict[int, list[int]] = defaultdict(list)
    for offset, _, payload in hits:
        by_size[len(payload)].append(offset)
    print(f"{args.rom.name}: {len(hits)} streams")
    for size, count in Counter({size: len(offsets) for size, offsets in by_size.items()}).most_common():
        offsets = by_size[size]
        preview = ", ".join(f"0x{x:06X}" for x in offsets[:8])
        suffix = " ..." if len(offsets) > 8 else ""
        if args.size is None or size == args.size:
            print(f"{size:8d} x {count:4d}: {preview}{suffix}")

    if args.size is not None:
        for offset, stored, payload in hits:
            if len(payload) == args.size:
                print(f"  0x{offset:06X}: stored={stored} decoded={len(payload)}")


if __name__ == "__main__":
    main()
