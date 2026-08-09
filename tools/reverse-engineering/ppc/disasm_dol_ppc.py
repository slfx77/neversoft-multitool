#!/usr/bin/env python3
"""
Disassemble PowerPC code directly from a GameCube/Wii DOL image.

Usage:
    python tools/reverse-engineering/ppc/disasm_dol_ppc.py main.dol 0x8029A878 --count 80
    python tools/reverse-engineering/ppc/disasm_dol_ppc.py main.dol --find-disp 0xA0 --start 0x80299000 --end 0x8029E000
    python tools/reverse-engineering/ppc/disasm_dol_ppc.py main.dol --find-imm 0x8029D038
"""

from __future__ import annotations

import argparse
import bisect
import pathlib
import struct
import sys
from dataclasses import dataclass

from capstone import CS_ARCH_PPC, CS_MODE_32, CS_MODE_BIG_ENDIAN, Cs, CsError
from capstone.ppc import PPC_OP_IMM, PPC_OP_MEM


TEXT_SECTION_COUNT = 7
DATA_SECTION_COUNT = 11
TOTAL_SECTION_COUNT = TEXT_SECTION_COUNT + DATA_SECTION_COUNT


@dataclass(frozen=True)
class DolSection:
    kind: str
    index: int
    file_offset: int
    address: int
    size: int

    @property
    def end_address(self) -> int:
        return self.address + self.size

    @property
    def end_file_offset(self) -> int:
        return self.file_offset + self.size


class DolImage:
    def __init__(self, path: pathlib.Path):
        self.path = path
        self.data = path.read_bytes()
        self.sections = self._parse_sections()
        self._section_starts = [section.address for section in self.sections]

    def _parse_u32(self, offset: int) -> int:
        return struct.unpack_from(">I", self.data, offset)[0]

    def _parse_sections(self) -> list[DolSection]:
        offsets = [self._parse_u32(i * 4) for i in range(TOTAL_SECTION_COUNT)]
        addresses = [self._parse_u32(0x48 + (i * 4)) for i in range(TOTAL_SECTION_COUNT)]
        sizes = [self._parse_u32(0x90 + (i * 4)) for i in range(TOTAL_SECTION_COUNT)]

        sections: list[DolSection] = []
        for i in range(TOTAL_SECTION_COUNT):
            size = sizes[i]
            if size == 0:
                continue

            kind = "text" if i < TEXT_SECTION_COUNT else "data"
            kind_index = i if kind == "text" else i - TEXT_SECTION_COUNT
            sections.append(DolSection(kind, kind_index, offsets[i], addresses[i], size))

        sections.sort(key=lambda section: section.address)
        return sections

    def section_for_address(self, address: int) -> DolSection:
        index = bisect.bisect_right(self._section_starts, address) - 1
        if index < 0:
            raise ValueError(f"Address 0x{address:08X} is not inside the DOL image")

        section = self.sections[index]
        if address >= section.end_address:
            raise ValueError(f"Address 0x{address:08X} is not inside the DOL image")

        return section

    def file_offset_for_address(self, address: int) -> int:
        section = self.section_for_address(address)
        return section.file_offset + (address - section.address)

    def read(self, address: int, size: int) -> bytes:
        file_offset = self.file_offset_for_address(address)
        section = self.section_for_address(address)
        max_size = section.end_file_offset - file_offset
        if size > max_size:
            raise ValueError(
                f"Read 0x{size:X} at 0x{address:08X} crosses section boundary "
                f"({section.kind}{section.index} ends at 0x{section.end_address:08X})"
            )

        return self.data[file_offset : file_offset + size]

    def iter_code(self, start: int | None = None, end: int | None = None):
        md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
        md.detail = True
        md.skipdata = True

        for section in self.sections:
            if section.kind != "text":
                continue

            section_start = section.address if start is None else max(section.address, start)
            section_end = section.end_address if end is None else min(section.end_address, end)
            if section_start >= section_end:
                continue

            file_offset = section.file_offset + (section_start - section.address)
            code = self.data[file_offset : file_offset + (section_end - section_start)]
            yield from md.disasm(code, section_start)

    def iter_code_aligned(self, start: int | None = None, end: int | None = None):
        md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
        md.detail = True

        for section in self.sections:
            if section.kind != "text":
                continue

            section_start = section.address if start is None else max(section.address, start)
            section_end = section.end_address if end is None else min(section.end_address, end)
            if section_start >= section_end:
                continue

            aligned_start = section_start & ~0x3
            if aligned_start < section.address:
                aligned_start = section.address

            address = aligned_start
            while address + 4 <= section_end:
                try:
                    instruction_bytes = self.read(address, 4)
                except ValueError:
                    break

                instructions = list(md.disasm(instruction_bytes, address, count=1))
                if instructions:
                    yield instructions[0]

                address += 4


def parse_int(value: str) -> int:
    return int(value, 0)


def format_instruction(instruction) -> str:
    return (
        f"0x{instruction.address:08X}: "
        f"{instruction.mnemonic:<10} {instruction.op_str}"
    ).rstrip()


def disassemble(dol: DolImage, address: int, count: int) -> int:
    section = dol.section_for_address(address)
    if section.kind != "text":
        raise ValueError(f"Address 0x{address:08X} is not in a text section")

    md = Cs(CS_ARCH_PPC, CS_MODE_32 | CS_MODE_BIG_ENDIAN)
    md.detail = True
    md.skipdata = True
    max_bytes = min(section.end_address - address, count * 4)
    code = dol.read(address, max_bytes)
    for instruction in md.disasm(code, address):
        print(format_instruction(instruction))
    return 0


def iter_search_code(dol: DolImage, start: int | None, end: int | None, aligned: bool):
    if aligned:
        yield from dol.iter_code_aligned(start, end)
        return

    yield from dol.iter_code(start, end)


def iter_instruction_operands(instruction):
    try:
        yield from instruction.operands
    except CsError:
        return


def find_disp(
    dol: DolImage,
    displacement: int,
    start: int | None,
    end: int | None,
    aligned: bool,
) -> int:
    hit_count = 0
    for instruction in iter_search_code(dol, start, end, aligned):
        for operand in iter_instruction_operands(instruction):
            if operand.type == PPC_OP_MEM and operand.mem.disp == displacement:
                print(format_instruction(instruction))
                hit_count += 1
                break

    print(f"\nHits: {hit_count}")
    return 0


def find_imm(
    dol: DolImage,
    immediate: int,
    start: int | None,
    end: int | None,
    aligned: bool,
) -> int:
    hit_count = 0
    for instruction in iter_search_code(dol, start, end, aligned):
        for operand in iter_instruction_operands(instruction):
            if operand.type == PPC_OP_IMM and operand.imm == immediate:
                print(format_instruction(instruction))
                hit_count += 1
                break

    print(f"\nHits: {hit_count}")
    return 0


def print_sections(dol: DolImage) -> int:
    for section in dol.sections:
        print(
            f"{section.kind}{section.index}: "
            f"file=0x{section.file_offset:06X} "
            f"addr=0x{section.address:08X} "
            f"size=0x{section.size:06X}"
        )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser()
    parser.add_argument("dol", type=pathlib.Path)
    parser.add_argument("address", nargs="?", type=parse_int, help="virtual address to disassemble")
    parser.add_argument("--count", type=int, default=64, help="instructions to disassemble from address")
    parser.add_argument("--start", type=parse_int, help="optional start address for searches")
    parser.add_argument("--end", type=parse_int, help="optional end address for searches")
    parser.add_argument("--find-disp", type=parse_int, help="find load/store operands using this displacement")
    parser.add_argument("--find-imm", type=parse_int, help="find immediates equal to this value")
    parser.add_argument(
        "--aligned-scan",
        action="store_true",
        help="scan text sections as fixed-width 4-byte PPC instructions instead of stopping at invalid regions",
    )
    parser.add_argument("--sections", action="store_true", help="print DOL sections")
    return parser


def main(argv: list[str] | None = None) -> int:
    parser = build_parser()
    args = parser.parse_args(argv)
    dol = DolImage(args.dol)

    if args.sections:
        return print_sections(dol)

    if args.find_disp is not None:
        return find_disp(dol, args.find_disp, args.start, args.end, args.aligned_scan)

    if args.find_imm is not None:
        return find_imm(dol, args.find_imm, args.start, args.end, args.aligned_scan)

    if args.address is None:
        parser.error("address is required unless --sections, --find-disp, or --find-imm is used")

    return disassemble(dol, args.address, args.count)


if __name__ == "__main__":
    sys.exit(main())
