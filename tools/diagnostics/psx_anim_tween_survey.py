"""Survey tween-interval values across every PSX anim table under Sample/Builds/.

Per the byte-perfect decomp (RunAnim PERFECT, M3dUtils_InterpolateVectors
PERFECT — thps2-psx-proto docs/anim_format_for_converter.md §2/§4), each anim
entry is 8 bytes: (u32 dataOffset, u16 frameCount, u16 tweenInterval). When
tweenInterval > 1 the v1 (0x2A) payload stores ONLY keyframes every
tweenInterval frames; playback lerps between them via M3dUtils_InBetween /
InterpolateVectors with a truncating 1.12 factor. A converter that reads
frameCount full-frame records from such files over-reads into garbage.

This script reports the tween-value distribution per build and lists every
file/entry with tween > 1 so the converter knows whether keyframe expansion
matters for the shipped corpus.

Usage: python tools/diagnostics/psx_anim_tween_survey.py
"""
from __future__ import annotations

import struct
from collections import Counter, defaultdict
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
SAMPLE_BUILDS = REPO_ROOT / "Sample" / "Builds"

HIER_V1 = 0x2A
HIER_V2 = 0x2C
CHUNK_TERMINATOR = 0xFFFFFFFF

MAX_FRAMES = 4096
MAX_STREAMS = 4096


def walk_chunks(data: bytes) -> list[tuple[int, int, int]]:
    """Yield (tag, size, data_offset) triples along the tagged chunk chain."""
    if len(data) < 8:
        return []
    meta_top = struct.unpack_from("<I", data, 4)[0]
    if meta_top + 8 > len(data) or meta_top < 8:
        return []

    chunks: list[tuple[int, int, int]] = []
    cursor = meta_top
    while cursor + 8 <= len(data):
        tag, size = struct.unpack_from("<II", data, cursor)
        if tag == CHUNK_TERMINATOR:
            break
        data_off = cursor + 8
        if size > len(data) - data_off:
            break
        chunks.append((tag, size, data_off))
        cursor = data_off + size
    return chunks


def parse_entries(data: bytes, base: int, size: int) -> list[tuple[int, int, int]]:
    """Parse (dataOffset, frameCount, tween) entries; mirrors PsxAnimFile.cs."""
    if size < 12:
        return []
    count = struct.unpack_from("<I", data, base)[0]
    if not 1 <= count <= MAX_STREAMS:
        return []
    first_data = 4 + count * 8
    entries = []
    for i in range(count):
        off = base + 4 + i * 8
        if off + 8 > base + size:
            break
        pool_off, frames, tween = struct.unpack_from("<IHH", data, off)
        if not (first_data <= pool_off < size and 1 <= frames <= MAX_FRAMES):
            continue
        entries.append((pool_off, frames, tween))
    return entries


def main() -> None:
    tween_by_build: dict[str, Counter] = defaultdict(Counter)
    hits: list[tuple[str, str, int, int, int, int]] = []
    files_scanned = 0

    for path in sorted(SAMPLE_BUILDS.rglob("*.psx")) + sorted(SAMPLE_BUILDS.rglob("*.PSX")):
        try:
            data = path.read_bytes()
        except OSError:
            continue
        anim = None
        for tag, size, off in walk_chunks(data):
            if tag in (HIER_V1, HIER_V2):
                anim = (tag, size, off)  # last matching chunk wins
        if anim is None:
            continue
        files_scanned += 1
        tag, size, off = anim
        build = path.relative_to(SAMPLE_BUILDS).parts[0]
        entries = parse_entries(data, off, size)
        for idx, (_, frames, tween) in enumerate(entries):
            tween_by_build[build][(tag, tween)] += 1
            if tween > 1:
                hits.append((build, path.name, tag, idx, frames, tween))

    print(f"Files with anim chunks parsed: {files_scanned}\n")
    print("| Build | Tag | Tween | Entries |")
    print("|---|---|---|---|")
    for build in sorted(tween_by_build):
        for (tag, tween), n in sorted(tween_by_build[build].items()):
            print(f"| {build} | 0x{tag:02X} | {tween} | {n} |")

    print(f"\nEntries with tween > 1: {len(hits)}")
    for build, name, tag, idx, frames, tween in hits[:40]:
        print(f"  {build} / {name} tag=0x{tag:02X} anim={idx} frames={frames} tween={tween}")
    if len(hits) > 40:
        print(f"  ... and {len(hits) - 40} more")


if __name__ == "__main__":
    main()
