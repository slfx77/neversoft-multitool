#!/usr/bin/env python3
"""Make a partially recovered in-memory PE dump loader-readable.

SafeDisc restores the protected application's ordinary import descriptors before
jumping to the original entry point.  If emulation stops after the payload has
been decrypted but before that last transition, the descriptors and thunk
arrays may already be plaintext while the PE header still points at the
protection wrapper's imports.  This tool validates a caller-identified recovered
descriptor table and repoints the header without guessing any API names.

The input must be a memory-layout dump (section PointerToRawData equals its RVA),
as produced by ``safedisc_emu.py --dump``.  No protected branch is patched and
no key material is fabricated: every DLL, import name/ordinal, and thunk address
comes from the recovered image itself.  Producing a loadable PE does not prove
that every protected code/data region or import was restored.  In particular,
the known THUG2 dump is a useful diagnostic but is materially incomplete; use
``thug2_cd3_recover.py`` for the complete bundled same-build executable.
"""

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path

import pefile


IMPORT_DESCRIPTOR_SIZE = 20
IMAGE_DIRECTORY_ENTRY_IMPORT = 1
IMAGE_DIRECTORY_ENTRY_IAT = 12


@dataclass(frozen=True)
class RecoveredImport:
    descriptor_rva: int
    dll: str
    original_first_thunk: int
    first_thunk: int
    thunk_count: int


def integer(value: str) -> int:
    try:
        result = int(value, 0)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            f"expected a decimal or 0x-prefixed integer, got {value!r}"
        ) from exc
    if not 0 <= result <= 0xFFFFFFFF:
        raise argparse.ArgumentTypeError(f"value is outside the 32-bit range: {value!r}")
    return result


def rva_offset(pe: pefile.PE, rva: int, size: int = 1) -> int:
    """Translate an RVA and require the complete range to be file-backed."""
    if rva < 0 or size < 0 or rva + size > pe.OPTIONAL_HEADER.SizeOfImage:
        raise ValueError(
            f"RVA 0x{rva:X} range of 0x{size:X} bytes is outside "
            f"SizeOfImage 0x{pe.OPTIONAL_HEADER.SizeOfImage:X}"
        )
    if rva < pe.OPTIONAL_HEADER.SizeOfHeaders:
        if rva + size > pe.OPTIONAL_HEADER.SizeOfHeaders:
            raise ValueError(f"RVA 0x{rva:X} range crosses the PE headers")
        offset = rva
    else:
        section = next((candidate for candidate in pe.sections
                        if candidate.VirtualAddress <= rva
                        and rva + size <= candidate.VirtualAddress
                        + max(candidate.Misc_VirtualSize,
                              candidate.SizeOfRawData)), None)
        if section is None:
            raise ValueError(f"RVA 0x{rva:X} range is not mapped by a section")
        delta = rva - section.VirtualAddress
        if delta + size > section.SizeOfRawData:
            raise ValueError(f"RVA 0x{rva:X} range is not file-backed")
        offset = section.PointerToRawData + delta
    if offset < 0 or offset + size > len(pe.__data__):
        raise ValueError(f"RVA 0x{rva:X} range of 0x{size:X} bytes exceeds the file")
    return offset


def read_c_string(pe: pefile.PE, rva: int, limit: int = 260) -> str:
    raw = bytearray()
    for index in range(limit):
        offset = rva_offset(pe, rva + index)
        value = pe.__data__[offset]
        if value == 0:
            break
        raw.append(value)
    else:
        raise ValueError(f"unterminated string at RVA 0x{rva:X}")
    try:
        return raw.decode("ascii")
    except UnicodeDecodeError as exc:
        raise ValueError(f"non-ASCII string at RVA 0x{rva:X}") from exc


def validate_thunks(pe: pefile.PE, thunk_rva: int, max_thunks: int = 4096) -> int:
    """Validate one original thunk array and return its non-null length."""
    for index in range(max_thunks):
        offset = rva_offset(pe, thunk_rva + index * 4, 4)
        value = struct.unpack_from("<I", pe.__data__, offset)[0]
        if value == 0:
            return index
        if value & 0x80000000:
            if value & 0x7FFF0000:
                raise ValueError(
                    f"invalid ordinal thunk 0x{value:08X} at RVA "
                    f"0x{thunk_rva + index * 4:X}"
                )
            continue
        name_offset = rva_offset(pe, value, 3)
        # IMAGE_IMPORT_BY_NAME begins with a two-byte hint followed by ASCII.
        del name_offset
        name = read_c_string(pe, value + 2)
        if not name or any(ord(char) < 0x20 or ord(char) > 0x7E for char in name):
            raise ValueError(f"invalid import name at RVA 0x{value + 2:X}")
    raise ValueError(f"thunk array at RVA 0x{thunk_rva:X} has no terminator")


def recover_imports(pe: pefile.PE, table_rva: int) -> tuple[list[RecoveredImport], int]:
    """Validate a null-terminated IMAGE_IMPORT_DESCRIPTOR array."""
    imports: list[RecoveredImport] = []
    for index in range(64):
        descriptor_rva = table_rva + index * IMPORT_DESCRIPTOR_SIZE
        offset = rva_offset(pe, descriptor_rva, IMPORT_DESCRIPTOR_SIZE)
        original, _timestamp, _forwarders, name_rva, first = struct.unpack_from(
            "<5I", pe.__data__, offset
        )
        if not any((original, _timestamp, _forwarders, name_rva, first)):
            if not imports:
                raise ValueError(f"import table at RVA 0x{table_rva:X} is empty")
            return imports, descriptor_rva + IMPORT_DESCRIPTOR_SIZE - table_rva
        if not original or not name_rva or not first:
            raise ValueError(
                f"incomplete import descriptor {index} at RVA 0x{descriptor_rva:X}"
            )
        dll = read_c_string(pe, name_rva)
        if not dll.lower().endswith(".dll"):
            raise ValueError(
                f"descriptor {index} name is not a DLL at RVA 0x{name_rva:X}: {dll!r}"
            )
        thunk_count = validate_thunks(pe, original)
        rva_offset(pe, first, (thunk_count + 1) * 4)
        imports.append(RecoveredImport(
            descriptor_rva, dll, original, first, thunk_count
        ))
    raise ValueError(f"import table at RVA 0x{table_rva:X} has no null descriptor")


def patch_dump(data: bytearray, entry_rva: int, import_rva: int) -> tuple[bytes, list[RecoveredImport]]:
    pe = pefile.PE(data=bytes(data), fast_load=False)
    image_size = pe.OPTIONAL_HEADER.SizeOfImage
    if entry_rva >= image_size:
        raise ValueError(
            f"entry RVA 0x{entry_rva:X} is outside SizeOfImage 0x{image_size:X}"
        )
    rva_offset(pe, entry_rva)

    imports, directory_size = recover_imports(pe, import_rva)
    populated = [item for item in imports if item.thunk_count]
    if not populated:
        raise ValueError("recovered descriptor table contains no import thunks")

    # Bound timestamps are not portable to the user's installed DLL versions.
    # Force normal name/ordinal resolution while preserving the original thunk
    # and forwarder fields recovered from the decrypted image.
    for item in imports:
        descriptor_offset = rva_offset(pe, item.descriptor_rva, IMPORT_DESCRIPTOR_SIZE)
        struct.pack_into("<I", data, descriptor_offset + 4, 0)

    entry_offset = pe.OPTIONAL_HEADER.get_field_absolute_offset("AddressOfEntryPoint")
    struct.pack_into("<I", data, entry_offset, entry_rva)

    import_directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[IMAGE_DIRECTORY_ENTRY_IMPORT]
    struct.pack_into("<II", data, import_directory.get_file_offset(),
                     import_rva, directory_size)

    iat_lo = min(item.first_thunk for item in populated)
    iat_hi = max(item.first_thunk + (item.thunk_count + 1) * 4 for item in populated)
    iat_directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[IMAGE_DIRECTORY_ENTRY_IAT]
    struct.pack_into("<II", data, iat_directory.get_file_offset(), iat_lo, iat_hi - iat_lo)

    # A stale checksum is less honest than an explicitly absent checksum, and
    # executable images do not require one outside drivers/boot components.
    checksum_offset = pe.OPTIONAL_HEADER.get_field_absolute_offset("CheckSum")
    struct.pack_into("<I", data, checksum_offset, 0)
    return bytes(data), imports


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("input", type=Path, help="partially recovered memory-layout PE dump")
    parser.add_argument("output", type=Path, help="loader-readable diagnostic PE path")
    parser.add_argument("--entry-rva", type=integer, required=True,
                        help="recovered original entry-point RVA")
    parser.add_argument("--import-rva", type=integer, required=True,
                        help="RVA of the recovered IMAGE_IMPORT_DESCRIPTOR table")
    args = parser.parse_args()

    if not args.input.is_file():
        parser.error(f"input does not exist: {args.input}")
    if args.output.resolve() == args.input.resolve():
        parser.error("refusing to overwrite the source dump; choose a distinct output")

    try:
        finalized, imports = patch_dump(
            bytearray(args.input.read_bytes()), args.entry_rva, args.import_rva
        )
    except (OSError, ValueError, pefile.PEFormatError) as exc:
        parser.error(str(exc))

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_bytes(finalized)
    total_thunks = sum(item.thunk_count for item in imports)
    populated = sum(bool(item.thunk_count) for item in imports)
    print(f"wrote {args.output} ({len(finalized):,} bytes)")
    print(f"entry RVA 0x{args.entry_rva:X}; {len(imports)} descriptors "
          f"({populated} populated), {total_thunks} validated import thunks")
    for item in imports:
        if item.thunk_count:
            print(f"    {item.dll:<16} {item.thunk_count:>3} imports  "
                  f"IAT RVA 0x{item.first_thunk:X}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
