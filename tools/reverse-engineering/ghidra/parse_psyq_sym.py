#!/usr/bin/env python3
"""Parse PSY-Q MAIN.SYM binary debug symbol file and output address/name mappings.

PSY-Q SYM format (MND v1):
  - 8-byte header: "MND" + version(1) + target_unit(4)
  - Records: [address:4 LE][tag:1][variable body per tag]
  - Tag determines body layout (0x01-0x06=names, 0x80-0x8A=SLD, 0x8C-0x96=types, etc.)

Output: one "0xADDRESS NAME" per line for all named symbols.

Usage:
  python parse_psyq_sym.py MAIN.SYM --output symbols.txt

References:
  - lab313ru/ghidra_psx_ldr ImportPSX_SYM.java
  - mefistotelis/psx_mnd_sym (Go parser)
  - psx-spx: PsyQ .SYM Files (nocash specification)
"""

import argparse
import os
import struct
import sys


def read_pascal_string(data, pos):
    """Read a length-prefixed string. Returns (string, new_pos) or (None, new_pos) on failure."""
    if pos >= len(data):
        return None, pos
    length = data[pos]
    pos += 1
    if pos + length > len(data):
        return None, pos + length
    try:
        name = data[pos:pos + length].decode("ascii")
    except UnicodeDecodeError:
        name = None
    return name, pos + length


def parse_psyq_sym(sym_path, output_path):
    """Parse a PSY-Q SYM file using tag-based dispatch and write address/name pairs."""
    with open(sym_path, "rb") as f:
        data = f.read()

    # Verify header: starts with "MND"
    if data[:3] != b"MND":
        print(f"ERROR: Not a PSY-Q SYM file (magic: {data[:3]})")
        sys.exit(1)

    version = data[3]
    target_unit = struct.unpack_from("<I", data, 4)[0]
    print(f"SYM file: {sym_path}")
    print(f"Size: {len(data):,} bytes")
    print(f"Version: {version}, Target unit: {target_unit}")

    # Skip 8-byte header
    pos = 8
    symbols = []  # (address, name) pairs
    tag_counts = {}
    records = 0
    errors = 0

    while pos + 5 <= len(data):
        addr = struct.unpack_from("<I", data, pos)[0]
        tag = data[pos + 4]
        pos += 5
        records += 1
        tag_counts[tag] = tag_counts.get(tag, 0) + 1

        try:
            if tag <= 0x06 and tag != 0x00:
                # Tags 0x01-0x06: Named symbol — [name_len:1][name:N]
                name, pos = read_pascal_string(data, pos)
                if name and addr != 0:
                    symbols.append((addr, name))

            elif tag == 0x00:
                # Null/end marker — no body
                pass

            elif tag == 0x08:
                # MX-info — [mx_info:1]
                pos += 1

            elif tag == 0x80:
                # IncSLD — no body (increment line by 1)
                pass

            elif tag == 0x82:
                # IncSLDByte — [inc:1]
                pos += 1

            elif tag == 0x84:
                # IncSLDWord — [inc:2 LE]
                pos += 2

            elif tag == 0x86:
                # SetSLD — [line:4 LE]
                pos += 4

            elif tag == 0x88:
                # SetSLD2 — [line:4 LE][path_len:1][path:N]
                pos += 4
                _, pos = read_pascal_string(data, pos)

            elif tag == 0x8A:
                # EndSLD — no body
                pass

            elif tag == 0x8C:
                # FuncStart — [fp:2][fsize:4][retreg:2][mask:4][maskoffs:4][line:4]
                #              + [file_len:1][file:N] + [name_len:1][name:N]
                if pos + 20 > len(data):
                    break
                pos += 20  # 2+4+2+4+4+4 = 20 fixed bytes
                _, pos = read_pascal_string(data, pos)  # source file
                name, pos = read_pascal_string(data, pos)  # function name
                if name and addr != 0:
                    symbols.append((addr, name))

            elif tag == 0x8E:
                # FuncEnd — [line:4]
                pos += 4

            elif tag == 0x90:
                # BlockStart — [line:4]
                pos += 4

            elif tag == 0x92:
                # BlockEnd — [line:4]
                pos += 4

            elif tag == 0x94:
                # Def — [class:2 LE][type:2 LE][size:4 LE][name_len:1][name:N]
                if pos + 8 > len(data):
                    break
                cls = struct.unpack_from("<H", data, pos)[0]
                pos += 8  # 2+2+4 = 8 fixed bytes
                name, pos = read_pascal_string(data, pos)
                # EXT(2) and STAT(3) are global symbols worth labeling
                if name and addr != 0 and cls in (0x0002, 0x0003):
                    symbols.append((addr, name))

            elif tag == 0x96:
                # Def2 — [class:2 LE][type:2 LE][size:4 LE][dims_count:2 LE]
                #         + [dims:N*4 LE] + [tag_len:1][tag:N] + [name_len:1][name:N]
                if pos + 10 > len(data):
                    break
                cls = struct.unpack_from("<H", data, pos)[0]
                dims_count = struct.unpack_from("<H", data, pos + 8)[0]
                pos += 10  # 2+2+4+2 = 10 fixed bytes
                pos += dims_count * 4  # dimension values
                _, pos = read_pascal_string(data, pos)  # tag string
                name, pos = read_pascal_string(data, pos)  # name
                if name and addr != 0 and cls in (0x0002, 0x0003):
                    symbols.append((addr, name))

            elif tag == 0x98:
                # Overlay — [length:4 LE][id:4 LE]
                pos += 8

            elif tag == 0x9A:
                # SetOverlay — no body
                pass

            elif tag == 0x9C:
                # FuncStart2 (extended) — [fp:2][fsize:4][retreg:2][mask:4][maskoffs:4]
                #   [fmask:4][fmaskoffs:4][line:4] + [file_len:1][file:N] + [name_len:1][name:N]
                if pos + 28 > len(data):
                    break
                pos += 28  # 2+4+2+4+4+4+4+4 = 28 fixed bytes
                _, pos = read_pascal_string(data, pos)  # source file
                name, pos = read_pascal_string(data, pos)  # function name
                if name and addr != 0:
                    symbols.append((addr, name))

            elif tag == 0x9E:
                # MangledName — [mangled_len:1][mangled:N][demangled_len:1][demangled:N]
                _, pos = read_pascal_string(data, pos)
                _, pos = read_pascal_string(data, pos)

            else:
                # Unknown tag — try treating as pascal string (tags 0x07-0x7F)
                if tag <= 0x7F:
                    _, pos = read_pascal_string(data, pos)
                else:
                    errors += 1
                    if errors <= 5:
                        print(f"  WARNING: Unknown tag 0x{tag:02X} at offset {pos - 5}")
                    break

        except (struct.error, IndexError):
            errors += 1
            if errors <= 5:
                print(f"  ERROR: Failed to parse tag 0x{tag:02X} at offset {pos - 5}")
            break

    # Deduplicate: keep first occurrence of each (addr, name) pair
    seen = set()
    unique_symbols = []
    for addr, name in symbols:
        key = (addr, name)
        if key not in seen:
            seen.add(key)
            unique_symbols.append((addr, name))

    # Print tag statistics
    print(f"\nRecord statistics:")
    print(f"  Total records: {records:,}")
    tag_names = {
        0x00: "null", 0x01: "name1", 0x02: "name2", 0x05: "name5", 0x06: "name6",
        0x08: "mx-info", 0x80: "IncSLD", 0x82: "IncSLDByte", 0x84: "IncSLDWord",
        0x86: "SetSLD", 0x88: "SetSLD2", 0x8A: "EndSLD",
        0x8C: "FuncStart", 0x8E: "FuncEnd", 0x90: "BlockStart", 0x92: "BlockEnd",
        0x94: "Def", 0x96: "Def2", 0x98: "Overlay", 0x9A: "SetOverlay",
        0x9C: "FuncStart2", 0x9E: "MangledName",
    }
    for tag in sorted(tag_counts.keys()):
        label = tag_names.get(tag, f"unknown")
        print(f"    0x{tag:02X} ({label}): {tag_counts[tag]:,}")

    print(f"\nParsed: {len(unique_symbols):,} unique named symbols")
    if errors > 0:
        print(f"  Errors: {errors}")

    # Write output
    output_path = os.path.abspath(output_path)
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        for addr, name in unique_symbols:
            f.write(f"0x{addr:08X} {name}\n")

    print(f"Output: {output_path}")


def main():
    parser = argparse.ArgumentParser(
        description="Parse a PSY-Q MND .SYM file into Ghidra-compatible address/name pairs."
    )
    parser.add_argument("sym_path", help="Input PSY-Q .SYM file")
    parser.add_argument(
        "-o",
        "--output",
        help="Output text file (default: <input>.symbols.txt)",
    )
    args = parser.parse_args()

    sym_path = os.path.abspath(args.sym_path)
    output_path = args.output or os.path.splitext(sym_path)[0] + ".symbols.txt"

    if not os.path.isfile(sym_path):
        parser.error(f"SYM file not found: {sym_path}")

    parse_psyq_sym(sym_path, output_path)


if __name__ == "__main__":
    main()
