#!/usr/bin/env python3
"""Crack hashed-HED entry names for THPS3/THPS4 PS1 (and any hashed CD.HED).

THPS1/THPS2 PS1 ship plaintext HEDs; the late PS1 ports (THPS3 by Shaba,
THPS4 by Vicarious Visions) hash each entry name with the Neversoft rotating
CRC-32 (`BinaryReaderExtensions.Crc32Neversoft` — NOT zlib CRC) and store
`{hash, offset, size}` triples. `WadArchive` resolves entries through
`HedDictionary`; anything unknown extracts as `{hash:X8}.dat`.

This tool recovers names by hashing candidates against the HED's hash set:
  1. every string harvested from the sibling PS1 EXE (SLUS_*), tokenized;
  2. `%s`-format-string expansions from the EXE (e.g. `%s.psx`, `%s.psh`);
  3. suffix-pattern expansion over every stem (level `_l/_o/_2/_t.trg`
     companions, `.sfx/.vab/.xa` audio, `.bmp` art, `.psh` skeletons);
  4. iterative re-derivation from cracked names until a fixed point.

Output is `name=0xHASH` lines ready to merge into
`Core/Formats/Archives/HedDictionaryPart*.cs`.

Usage:
    python harvest_hed_names.py <build-dir> [--exe SLUS_014.19] [-o out.txt]
    (build-dir must contain CD.HED and the EXE)
"""

from __future__ import annotations

import argparse
import re
import struct
import sys
from pathlib import Path

SUFFIXES = [
    '.psx', '.psh', '_l.psx', '_l.psh', '_o.psx', '_o.psh', '_2.psx',
    '_g.psx', '_g.psh',
    '_o2.psx', '_h.psx', '_fe.psx', '_t.trg', '.trg', '.sfx', '.vab',
    '.xa', '.bmp', '.dat', '.pre', '.col', '.qb', '.str', '.tim', '.raw',
    '.pal', '.fnt', '.mdl', '.txt',
]


def crc32_neversoft(name: str) -> int:
    """The engine's rotating CRC-32 (matches BinaryReaderExtensions.Crc32Neversoft)."""
    result = 0xFFFFFFFF
    for byte in name.encode('ascii', 'ignore'):
        mask = result ^ byte
        for _ in range(8):
            result = ((result << 1) | (result >> 31)) & 0xFFFFFFFF
            if mask & 1:
                result ^= 0xEDB88320
            mask >>= 1
    return result


def read_hed_hashes(path: Path) -> set[int]:
    data = path.read_bytes()
    hashes = set()
    for offset in range(0, len(data) - 11, 12):
        entry_hash, entry_offset, entry_size = struct.unpack_from('<III', data, offset)
        if entry_hash == 0 and entry_offset == 0 and entry_size == 0:
            break
        hashes.add(entry_hash)
    return hashes


def harvest_stems(exe: bytes) -> tuple[set[str], set[str]]:
    stems: set[str] = set()
    formats: set[str] = set()
    for match in re.findall(rb'[\x20-\x7e]{3,}', exe):
        text = match.decode()
        if '%s' in text and text.count('%s') == 1 and len(text) < 24:
            formats.add(text)
        for token in re.split(r'[^A-Za-z0-9_\-]+', text):
            if 2 <= len(token) <= 16:
                stems.add(token)
    return stems, formats


def crack(hed_hashes: set[int], stems: set[str], formats: set[str]) -> dict[int, str]:
    hits: dict[int, str] = {}

    def try_name(name: str) -> None:
        value = crc32_neversoft(name)
        if value in hed_hashes and value not in hits:
            hits[value] = name

    for stem in stems:
        for cased in {stem, stem.lower(), stem.upper()}:
            try_name(cased)
            for suffix in SUFFIXES:
                try_name(cased + suffix)
            for fmt in formats:
                try_name(fmt.replace('%s', cased))

    # Fixed-point iteration: cracked names spawn companion candidates.
    while True:
        derived = set()
        for name in hits.values():
            stem = name.rsplit('.', 1)[0]
            derived.update({stem, stem.removesuffix('_l'), stem.removesuffix('_o'),
                            stem.removesuffix('_t'), stem.removesuffix('_g'),
                            stem + '_l', stem + '_o', stem + '_g'})
        before = len(hits)
        for stem in derived:
            for cased in {stem, stem.lower()}:
                for suffix in SUFFIXES:
                    try_name(cased + suffix)
        if len(hits) == before:
            return hits


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('build_dir')
    parser.add_argument('--exe', help='EXE filename (default: first SLUS_*/SCUS_* found)')
    parser.add_argument('-o', '--output', help='output name=0xHASH list')
    args = parser.parse_args()

    build = Path(args.build_dir)
    hed_path = build / 'CD.HED'
    if not hed_path.exists():
        print(f'no CD.HED in {build}', file=sys.stderr)
        return 2

    if args.exe:
        exe_path = build / args.exe
    else:
        exe_path = next(iter(sorted(build.glob('SLUS_*')) + sorted(build.glob('SCUS_*'))), None)
    if exe_path is None or not exe_path.exists():
        print('no EXE found (pass --exe)', file=sys.stderr)
        return 2

    hed_hashes = read_hed_hashes(hed_path)
    stems, formats = harvest_stems(exe_path.read_bytes())
    hits = crack(hed_hashes, stems, formats)

    print(f'{build.name}: HED entries {len(hed_hashes)}, cracked {len(hits)} '
          f'({100.0 * len(hits) / max(1, len(hed_hashes)):.1f}%)')
    lines = [f'{name}=0x{value:08X}' for value, name in
             sorted(hits.items(), key=lambda kv: kv[1])]
    if args.output:
        Path(args.output).write_text('\n'.join(lines) + '\n', encoding='ascii')
        print(f'wrote {args.output}')
    else:
        for line in lines:
            print(line)
    return 0


if __name__ == '__main__':
    sys.exit(main())
