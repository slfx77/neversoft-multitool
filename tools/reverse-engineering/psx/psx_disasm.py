#!/usr/bin/env python3
"""psx_disasm.py — disassemble a PSX (MIPS R3000, little-endian) executable
region by virtual address, annotating jal targets and lui/ori immediate pairs.

Reused for reverse-engineering Spider-Man PSX functions whose C is still a stub
in spidey-decomp (e.g. the CPowerUp ctor / DoPhysics ground-snap). The load
address and .text file offset match the shipped SLUS/PSX.EXE header.

Usage: python psx_disasm.py <exe> <hex_vaddr> <count> [--find-jal-to 0xADDR]
"""
import sys
from pathlib import Path
from capstone import Cs, CS_ARCH_MIPS, CS_MODE_MIPS32, CS_MODE_LITTLE_ENDIAN

LOAD_ADDR = 0x80010000
TEXT_FILE_OFF = 0x800


def vaddr_to_off(vaddr):
    return TEXT_FILE_OFF + (vaddr - LOAD_ADDR)


def main():
    exe = Path(sys.argv[1])
    start = int(sys.argv[2], 16)
    count = int(sys.argv[3])
    find_jal = None
    if "--find-jal-to" in sys.argv:
        find_jal = int(sys.argv[sys.argv.index("--find-jal-to") + 1], 16)

    data = exe.read_bytes()
    off = vaddr_to_off(start)
    code = data[off:off + count * 4]

    md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 | CS_MODE_LITTLE_ENDIAN)
    md.detail = False

    hi = {}  # reg -> lui hi value, to annotate lui/ori pairs as absolute addresses
    for insn in md.disasm(code, start):
        note = ""
        m = insn.mnemonic
        opstr = insn.op_str
        if m == "jal" and find_jal is not None:
            tgt = int(opstr, 16) if opstr.startswith("0x") else None
            if tgt == find_jal:
                note = "   <<< TARGET"
        # lui/ori tracking for address/immediate reconstruction
        if m == "lui":
            parts = opstr.replace(",", "").split()
            if len(parts) == 2:
                hi[parts[0]] = int(parts[1], 16)
        elif m in ("ori", "addiu") and "," in opstr:
            parts = [p.strip() for p in opstr.split(",")]
            if len(parts) == 3 and parts[1] in hi:
                try:
                    lo = int(parts[2], 16) if parts[2].startswith("0x") else int(parts[2])
                    val = (hi[parts[1]] << 16) + (lo if m == "ori" else _s16(lo))
                    note = f"   ; {parts[0]} = 0x{val & 0xFFFFFFFF:08X}"
                except ValueError:
                    pass
        print(f"  0x{insn.address:08X}:  {m:<8} {opstr}{note}")


def _s16(v):
    return v - 0x10000 if v & 0x8000 else v


if __name__ == "__main__":
    main()
