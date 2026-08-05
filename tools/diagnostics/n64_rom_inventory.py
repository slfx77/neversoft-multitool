#!/usr/bin/env python3
"""Inventory the N64 ROMs (THPS1/2/3 + Spider-Man, Edge of Reality ports).

First-look diagnostic for a platform the tool has never touched: parses the
.z64 header, harvests build-timestamp strings, maps entropy per 64 KB block
(code vs compressed vs raw-asset regions), scans for known compression magics
(MIO0/Yay0/Yaz0, zlib/gzip) AND the Neversoft-lineage signatures the PS1 corpus
taught us (0xABCD-version PREs, "04 00 02 00" PSX mesh headers incl. byte-
swapped variants, "_TRG" triggers, "pBAV" VABs, BMP headers, filename string
tables), and compares the four ROMs structurally.

Usage:
    python n64_rom_inventory.py <rom.z64> [more.z64 ...] [--blocks]
"""

from __future__ import annotations

import argparse
import collections
import math
import re
import struct
import sys
from pathlib import Path

Z64_MAGIC = bytes([0x80, 0x37, 0x12, 0x40])

# (label, pattern, alignment). Aligned scans cut false positives drastically.
SIGNATURES = [
    ('MIO0', b'MIO0', 4),
    ('Yay0', b'Yay0', 4),
    ('Yaz0', b'Yaz0', 4),
    ('gzip', b'\x1f\x8b\x08', 1),
    ('zlib-78DA', b'\x78\xda', 1),
    ('PRE-v2/v3 LE', b'\x02\x00\xcd\xab', 4),      # 0xABCD0002 little-endian
    ('PRE-v2/v3 BE', b'\xab\xcd\x00\x02', 4),
    ('PSX-mesh v3/v4/v6 LE', re.compile(rb'[\x03\x04\x06]\x00\x02\x00'), 4),
    ('PSX-mesh v3/v4/v6 BE', re.compile(rb'\x00[\x03\x04\x06]\x00\x02'), 4),
    ('_TRG', b'_TRG', 4),
    ('TRG_ (BE)', b'GRT_', 4),
    ('VAB pBAV', b'pBAV', 4),
    ('VAB (BE) VABp', b'VABp', 4),
    ('BMP "BM"+size', re.compile(rb'BM[\x00-\xff]{4}\x00\x00\x00\x00'), 4),
]

DATE_PATTERNS = [
    rb'(Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]* +\d{1,2},? +(19|20)\d\d',
    rb'(19|20)\d\d-\d\d-\d\d',
    rb'\d\d?/\d\d?/(19|20)?\d\d',
    rb'\d\d:\d\d:\d\d',
]


def entropy(block: bytes) -> float:
    counts = collections.Counter(block)
    total = len(block)
    return -sum(c / total * math.log2(c / total) for c in counts.values())


def inventory(path: Path, show_blocks: bool) -> dict:
    data = path.read_bytes()
    report: dict = {'file': path.name, 'size': len(data)}

    if data[:4] != Z64_MAGIC:
        report['error'] = f'not a .z64 (starts {data[:4].hex()})'
        return report

    name = data[0x20:0x34].decode('ascii', 'replace').strip()
    cart = data[0x3B:0x3F].decode('ascii', 'replace')
    crc1, crc2 = struct.unpack_from('>II', data, 0x10)
    report['internal_name'] = name
    report['cart_id'] = cart
    report['crc'] = f'{crc1:08X} {crc2:08X}'
    boot = struct.unpack_from('>I', data, 8)[0]
    report['entry_point'] = f'0x{boot:08X}'

    # Entropy map: 64 KB blocks classified low (<4, tables/zero), code (~6-7),
    # high (>7.6, compressed/random).
    block = 0x10000
    classes = []
    for offset in range(0, len(data) - block + 1, block):
        e = entropy(data[offset:offset + block])
        classes.append('z' if e < 1 else ('L' if e < 4.5 else ('c' if e < 7.2 else 'H')))
    report['entropy_map'] = ''.join(classes)
    report['entropy_summary'] = dict(collections.Counter(classes))

    # Signature scan.
    hits: dict[str, list[int]] = {}
    for label, pattern, align in SIGNATURES:
        offsets = []
        if isinstance(pattern, bytes):
            start = 0
            while (found := data.find(pattern, start)) != -1:
                if found % align == 0:
                    offsets.append(found)
                start = found + 1
                if len(offsets) >= 5000:
                    break
        else:
            for m in pattern.finditer(data):
                if m.start() % align == 0:
                    offsets.append(m.start())
                if len(offsets) >= 5000:
                    break
        if offsets:
            hits[label] = offsets
    report['signatures'] = {k: (len(v), [f'0x{o:X}' for o in v[:6]]) for k, v in hits.items()}

    # Date/timestamp strings.
    dates = {}
    for pat in DATE_PATTERNS:
        for m in re.finditer(pat, data):
            ctx = data[max(0, m.start() - 48):m.end() + 48]
            txt = re.sub(rb'[^\x20-\x7e]', b'.', ctx).decode()
            dates[txt] = m.start()
    report['date_strings'] = [f'0x{o:X}  {t}' for t, o in
                              sorted(dates.items(), key=lambda kv: kv[1])[:12]]

    # Filename-looking strings (dotted 8.3-ish tokens).
    files = collections.Counter()
    for m in re.finditer(rb'[A-Za-z0-9_\-]{2,12}\.[A-Za-z0-9]{2,4}\b', data):
        files[m.group().decode().lower()] += 1
    report['filename_strings'] = files.most_common(20)

    if show_blocks:
        print(report['entropy_map'])
    return report


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('roms', nargs='+')
    parser.add_argument('--blocks', action='store_true')
    args = parser.parse_args()

    for rom in args.roms:
        r = inventory(Path(rom), args.blocks)
        print(f"\n=== {r['file']} ({r['size'] // (1 << 20)} MB)")
        if 'error' in r:
            print('   ', r['error'])
            continue
        print(f"    name={r['internal_name']!r} cart={r['cart_id']} crc={r['crc']} "
              f"entry={r['entry_point']}")
        print(f"    entropy blocks: {r['entropy_summary']}   "
              f"(H=compressed-like, c=code-like, L=tables, z=empty)")
        for label, (count, sample) in sorted(r['signatures'].items(),
                                             key=lambda kv: -kv[1][0]):
            print(f"    sig {label:22} x{count:<6} {' '.join(sample)}")
        for line in r['date_strings']:
            print(f"    date {line}")
        if r['filename_strings']:
            print(f"    filenames: {r['filename_strings'][:12]}")
    return 0


if __name__ == '__main__':
    sys.exit(main())
