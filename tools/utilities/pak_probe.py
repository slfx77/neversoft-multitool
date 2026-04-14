#!/usr/bin/env python3
"""
PAK Archive Format Probe — THAW (Tony Hawk's American Wasteland) PS2

Investigates the .pak.ps2 / .pab.ps2 archive format used in THAW.
Previous investigation found a 32-byte entry structure with magic 0x745DCD45.

Usage:
    python pak_probe.py <pak_dir>                  # Scan all PAK files, report statistics
    python pak_probe.py <pak_dir> --dump <file>     # Deep hex analysis of a single file
    python pak_probe.py <pak_dir> --extract <file>  # Try extracting entries from a single file
"""

import argparse
import os
import struct
import sys
import zlib
from pathlib import Path

PAK_MAGIC = 0x745DCD45
ENTRY_SIZE = 32  # Hypothesized entry size


def scan_for_magic(data: bytes, magic: int = PAK_MAGIC) -> list[int]:
    """Find all offsets where the 4-byte LE magic appears."""
    magic_bytes = struct.pack("<I", magic)
    positions = []
    start = 0
    while True:
        pos = data.find(magic_bytes, start)
        if pos == -1:
            break
        positions.append(pos)
        start = pos + 1
    return positions


def parse_entry(data: bytes, offset: int) -> dict | None:
    """Parse a single 32-byte entry at the given offset."""
    if offset + ENTRY_SIZE > len(data):
        return None

    fields = struct.unpack_from("<IIII", data, offset)
    magic, data_offset, data_size, checksum = fields

    if magic != PAK_MAGIC:
        return None

    # Read the remaining 16 bytes for analysis
    reserved = data[offset + 16:offset + ENTRY_SIZE]

    return {
        "entry_offset": offset,
        "magic": magic,
        "data_offset": data_offset,
        "data_size": data_size,
        "checksum": checksum,
        "reserved": reserved,
        "reserved_nonzero": any(b != 0 for b in reserved),
    }


def check_zlib(data: bytes, offset: int, max_len: int = 65536) -> tuple[bool, int]:
    """Check if data at offset is zlib-compressed. Returns (is_zlib, decompressed_size)."""
    if offset + 2 > len(data):
        return False, 0

    header = data[offset:offset + 2]
    # Common zlib headers: 78 9C (default), 78 DA (best), 78 01 (no compression)
    if header[0] != 0x78 or header[1] not in (0x01, 0x5E, 0x9C, 0xDA):
        return False, 0

    try:
        end = min(offset + max_len, len(data))
        decompressed = zlib.decompress(data[offset:end])
        return True, len(decompressed)
    except zlib.error:
        return False, 0


def find_strings(data: bytes, offset: int, length: int, min_len: int = 6) -> list[str]:
    """Find ASCII string runs in a data region."""
    strings = []
    current = []
    for i in range(offset, min(offset + length, len(data))):
        b = data[i]
        if 0x20 <= b < 0x7F:
            current.append(chr(b))
        else:
            if len(current) >= min_len:
                strings.append("".join(current))
            current = []
    if len(current) >= min_len:
        strings.append("".join(current))
    return strings


def analyze_file(filepath: str, verbose: bool = False) -> dict:
    """Analyze a single PAK file and return statistics."""
    data = Path(filepath).read_bytes()
    file_size = len(data)

    # Find all magic occurrences
    magic_positions = scan_for_magic(data)

    # Determine type: check if file starts with zeros
    first_nonzero = 0
    for i in range(min(file_size, 0x20000)):
        if data[i] != 0:
            first_nonzero = i
            break

    is_type_a = first_nonzero > 256  # Large zero-padded header

    # Parse entries
    entries = []
    for pos in magic_positions:
        entry = parse_entry(data, pos)
        if entry:
            entries.append(entry)

    # Check if entries are contiguous (consecutive offsets = 32 bytes apart)
    contiguous_entries = 0
    if len(magic_positions) >= 2:
        for i in range(1, len(magic_positions)):
            if magic_positions[i] - magic_positions[i-1] == ENTRY_SIZE:
                contiguous_entries += 1

    # Check zlib on data blocks pointed to by entries
    zlib_count = 0
    valid_offset_count = 0
    for entry in entries:
        if 0 < entry["data_offset"] < file_size:
            valid_offset_count += 1
            is_zlib, _ = check_zlib(data, entry["data_offset"])
            if is_zlib:
                zlib_count += 1

    # Check for strings in the file
    all_strings = find_strings(data, 0, file_size, min_len=8)
    path_strings = [s for s in all_strings if "\\" in s or "/" in s or ".ps2" in s.lower() or ".qb" in s.lower()]

    result = {
        "filepath": filepath,
        "filename": os.path.basename(filepath),
        "file_size": file_size,
        "magic_count": len(magic_positions),
        "first_magic_offset": magic_positions[0] if magic_positions else -1,
        "is_type_a": is_type_a,
        "first_nonzero": first_nonzero,
        "entry_count": len(entries),
        "contiguous_entries": contiguous_entries,
        "valid_offsets": valid_offset_count,
        "zlib_count": zlib_count,
        "path_strings": path_strings[:10],  # First 10
        "has_reserved_data": any(e["reserved_nonzero"] for e in entries),
    }

    if verbose and entries:
        result["entries"] = entries[:20]  # First 20 entries for verbose output

        # Analyze data sizes vs offsets for overlap detection
        if len(entries) >= 2:
            overlaps = 0
            gaps = 0
            for i in range(1, len(entries)):
                prev_end = entries[i-1]["data_offset"] + entries[i-1]["data_size"]
                curr_start = entries[i]["data_offset"]
                if prev_end > curr_start and entries[i-1]["data_offset"] < file_size:
                    overlaps += 1
                elif curr_start > prev_end:
                    gaps += 1
            result["overlaps"] = overlaps
            result["gaps"] = gaps

    return result


def dump_file(filepath: str):
    """Deep hex analysis of a single PAK file."""
    data = Path(filepath).read_bytes()
    file_size = len(data)
    filename = os.path.basename(filepath)

    print(f"=== Deep Analysis: {filename} ({file_size:,} bytes) ===\n")

    # Find magic positions
    magic_positions = scan_for_magic(data)
    print(f"Magic (0x{PAK_MAGIC:08X}) found at {len(magic_positions)} positions")

    if not magic_positions:
        print("No PAK magic found in file!")
        # Hex dump first 128 bytes
        print("\nFirst 128 bytes:")
        hex_dump(data, 0, 128)
        return

    # First magic position
    first_pos = magic_positions[0]
    print(f"First magic at offset 0x{first_pos:X} ({first_pos})")

    # Check for zero header
    if first_pos > 0:
        zero_run = 0
        for i in range(first_pos):
            if data[i] == 0:
                zero_run += 1
        print(f"Bytes before first magic: {first_pos}, zeros: {zero_run} ({100*zero_run/first_pos:.1f}%)")

    # Check if entries are contiguous
    if len(magic_positions) >= 2:
        spacings = [magic_positions[i+1] - magic_positions[i] for i in range(min(10, len(magic_positions)-1))]
        print(f"Entry spacings (first 10): {spacings}")
        all_32 = all(s == ENTRY_SIZE for s in spacings)
        print(f"All entries 32 bytes apart: {all_32}")

    # Parse and display entries
    print(f"\n--- Entries (showing first 20 of {len(magic_positions)}) ---")
    print(f"{'#':>4} {'EntryOfs':>10} {'DataOfs':>10} {'DataSize':>10} {'Checksum':>10} {'Zlib':>5} {'Reserved':>20}")
    print("-" * 80)

    for i, pos in enumerate(magic_positions[:20]):
        entry = parse_entry(data, pos)
        if not entry:
            continue

        # Check zlib
        is_zlib = False
        if 0 < entry["data_offset"] < file_size:
            is_zlib, _ = check_zlib(data, entry["data_offset"])

        reserved_hex = entry["reserved"].hex()[:20]
        print(f"{i:4d} 0x{pos:08X} 0x{entry['data_offset']:08X} {entry['data_size']:10d} 0x{entry['checksum']:08X} {'YES' if is_zlib else 'no':>5} {reserved_hex}")

    # Show data at first entry's data offset
    if magic_positions and len(magic_positions) > 0:
        entry = parse_entry(data, magic_positions[0])
        if entry and 0 < entry["data_offset"] < file_size:
            print(f"\n--- Data at first entry's offset (0x{entry['data_offset']:X}) ---")
            hex_dump(data, entry["data_offset"], 64)

    # Find embedded strings
    path_strings = find_strings(data, 0, file_size, min_len=8)
    path_like = [s for s in path_strings if "\\" in s or "/" in s or ".ps2" in s.lower() or ".qb" in s.lower()]
    if path_like:
        print(f"\n--- Embedded path strings ({len(path_like)} found) ---")
        for s in path_like[:20]:
            print(f"  {s}")

    # Show non-path strings too (first 20)
    other_strings = [s for s in path_strings if s not in path_like][:20]
    if other_strings:
        print(f"\n--- Other strings ({len(other_strings)} shown) ---")
        for s in other_strings:
            print(f"  {s}")


def extract_file(filepath: str, output_dir: str = None):
    """Try extracting entries from a PAK file."""
    data = Path(filepath).read_bytes()
    file_size = len(data)
    filename = os.path.basename(filepath)

    if output_dir is None:
        output_dir = os.path.splitext(filepath)[0] + "_extracted"

    magic_positions = scan_for_magic(data)
    if not magic_positions:
        print(f"No PAK magic found in {filename}")
        return

    os.makedirs(output_dir, exist_ok=True)

    print(f"Extracting {len(magic_positions)} entries from {filename}...")

    extracted = 0
    for i, pos in enumerate(magic_positions):
        entry = parse_entry(data, pos)
        if not entry:
            continue

        data_offset = entry["data_offset"]
        data_size = entry["data_size"]
        checksum = entry["checksum"]

        if data_offset >= file_size or data_offset == 0:
            print(f"  Entry {i}: invalid offset 0x{data_offset:X}")
            continue

        # Extract raw data
        end = min(data_offset + data_size, file_size)
        raw_data = data[data_offset:end]

        # Try zlib decompression
        is_zlib, _ = check_zlib(data, data_offset, data_size)
        if is_zlib:
            try:
                raw_data = zlib.decompress(data[data_offset:end])
            except zlib.error:
                pass  # Keep raw data

        # Determine filename
        # Check if data contains a filename string
        embedded_strings = find_strings(raw_data, 0, min(256, len(raw_data)), min_len=4)
        name_candidates = [s for s in embedded_strings if ".ps2" in s.lower() or ".qb" in s.lower()]

        if name_candidates:
            out_name = name_candidates[0].replace("\\", "/").split("/")[-1]
        else:
            # Try QbKey-style name
            ext = detect_file_type(raw_data)
            out_name = f"{checksum:08X}{ext}"

        out_path = os.path.join(output_dir, out_name)
        Path(out_path).write_bytes(raw_data)
        extracted += 1

        if i < 10 or (i % 50 == 0):
            zlib_tag = " [zlib]" if is_zlib else ""
            print(f"  Entry {i}: {out_name} ({len(raw_data):,} bytes){zlib_tag}")

    print(f"\nExtracted {extracted}/{len(magic_positions)} entries to {output_dir}")


def detect_file_type(data: bytes) -> str:
    """Detect file type from first bytes."""
    if len(data) < 4:
        return ".dat"

    # QB file: starts with token bytes 0-68
    if data[0] <= 68:
        return ".qb"

    # Check for common signatures
    if data[:4] == b"VAGp":
        return ".vag"

    magic32 = struct.unpack_from("<I", data, 0)[0]
    if magic32 == 0x0016:
        return ".txd"

    return ".dat"


def hex_dump(data: bytes, offset: int, length: int):
    """Print a hex dump."""
    for i in range(0, length, 16):
        addr = offset + i
        if addr >= len(data):
            break
        end = min(addr + 16, len(data))
        hex_part = " ".join(f"{data[j]:02X}" for j in range(addr, end))
        ascii_part = "".join(
            chr(data[j]) if 0x20 <= data[j] < 0x7F else "."
            for j in range(addr, end)
        )
        print(f"  {addr:08X}: {hex_part:<48s} {ascii_part}")


def batch_scan(pak_dir: str):
    """Scan all PAK files and report statistics."""
    pak_files = sorted(
        [os.path.join(pak_dir, f) for f in os.listdir(pak_dir)
         if f.lower().endswith((".pak.ps2", ".pab.ps2"))],
        key=lambda f: os.path.basename(f).lower()
    )

    if not pak_files:
        print(f"No .pak.ps2 or .pab.ps2 files found in {pak_dir}")
        return

    print(f"Scanning {len(pak_files)} PAK files in {pak_dir}...\n")

    stats = {
        "total": len(pak_files),
        "with_magic": 0,
        "no_magic": 0,
        "type_a": 0,
        "type_b": 0,
        "total_entries": 0,
        "total_zlib": 0,
        "total_valid_offsets": 0,
        "with_paths": 0,
        "with_reserved": 0,
        "errors": 0,
    }

    no_magic_files = []
    type_a_files = []
    files_with_paths = []

    for filepath in pak_files:
        try:
            result = analyze_file(filepath)

            if result["magic_count"] == 0:
                stats["no_magic"] += 1
                no_magic_files.append(result["filename"])
            else:
                stats["with_magic"] += 1
                if result["is_type_a"]:
                    stats["type_a"] += 1
                    type_a_files.append(result["filename"])
                else:
                    stats["type_b"] += 1

            stats["total_entries"] += result["entry_count"]
            stats["total_zlib"] += result["zlib_count"]
            stats["total_valid_offsets"] += result["valid_offsets"]

            if result["path_strings"]:
                stats["with_paths"] += 1
                files_with_paths.append((result["filename"], result["path_strings"][:3]))

            if result["has_reserved_data"]:
                stats["with_reserved"] += 1

        except Exception as e:
            stats["errors"] += 1
            print(f"  ERROR: {os.path.basename(filepath)}: {e}")

    # Print summary
    print("=" * 70)
    print("PAK FORMAT ANALYSIS SUMMARY")
    print("=" * 70)
    print(f"Total files:           {stats['total']}")
    print(f"With magic 0x745DCD45: {stats['with_magic']}")
    print(f"No magic found:        {stats['no_magic']}")
    print(f"Type A (zero header):  {stats['type_a']}")
    print(f"Type B (immediate):    {stats['type_b']}")
    print(f"Total entries:         {stats['total_entries']}")
    print(f"Valid data offsets:     {stats['total_valid_offsets']}")
    print(f"Zlib compressed:       {stats['total_zlib']}")
    print(f"With embedded paths:   {stats['with_paths']}")
    print(f"With reserved data:    {stats['with_reserved']}")
    print(f"Parse errors:          {stats['errors']}")

    if no_magic_files:
        print(f"\n--- Files without PAK magic ({len(no_magic_files)}) ---")
        for f in no_magic_files[:20]:
            print(f"  {f}")
        if len(no_magic_files) > 20:
            print(f"  ... and {len(no_magic_files) - 20} more")

    if type_a_files:
        print(f"\n--- Type A files (zero-padded header) ({len(type_a_files)}) ---")
        for f in type_a_files:
            print(f"  {f}")

    if files_with_paths:
        print(f"\n--- Files with embedded path strings ({len(files_with_paths)}) ---")
        for fname, paths in files_with_paths[:15]:
            print(f"  {fname}:")
            for p in paths:
                print(f"    {p}")


def main():
    parser = argparse.ArgumentParser(description="THAW PAK Archive Format Probe")
    parser.add_argument("pak_dir", help="Directory containing .pak.ps2 files")
    parser.add_argument("--dump", help="Deep hex analysis of a specific file")
    parser.add_argument("--extract", help="Try extracting entries from a specific file")
    parser.add_argument("--output", "-o", help="Output directory for extraction")

    args = parser.parse_args()

    if args.dump:
        dump_path = os.path.join(args.pak_dir, args.dump) if not os.path.isabs(args.dump) else args.dump
        dump_file(dump_path)
    elif args.extract:
        extract_path = os.path.join(args.pak_dir, args.extract) if not os.path.isabs(args.extract) else args.extract
        extract_file(extract_path, args.output)
    else:
        batch_scan(args.pak_dir)


if __name__ == "__main__":
    main()
