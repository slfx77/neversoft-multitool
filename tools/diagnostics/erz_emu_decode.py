#!/usr/bin/env python3
"""Decode ERZ blocks by EXECUTING the ROM's own decompressor (MIPS interpreter).

Edge of Reality's "ERZ" compression (THPS1/2/3 + Spider-Man N64) has no public
RE. Instead of hand-deriving the bitstream, this runs the boot-segment
decompressor itself in a minimal MIPS-BE interpreter: the .z64 is loaded at its
boot address (ROM+0x1000 → 0x80000400), the version-2 core (RAM 0x80000CF8 in
THPS2; located per-ROM via its "ERZ" magic-check signature) is called with
a0=block, a1=output, and the emulated stores are the decoded bytes. Slow but
bit-exact by construction — the outputs are the golden fixtures for a native
C# ErzDecoder.

Block header (BE): "ERZ" u8 version | u16 0x0001 | u16 0 | u32 decompressedSize
| u32 compressedSize | bitstream (v2 core reads it from block+18; the sizes sit
at +4 and +8 as the CODE reads them - the emulator is the authority).

Usage:
    python erz_emu_decode.py <rom.z64> <block-rom-offset-hex> [-o out.bin]
    python erz_emu_decode.py <rom.z64> --table <table-offset-hex> [-o dir]
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

RAM_BASE = 0x80000000
RAM_SIZE = 8 << 20
BOOT_RAM = 0x80000400
BOOT_ROM = 0x1000


class Cpu:
    def __init__(self, ram: bytearray):
        self.ram = ram
        self.r = [0] * 32
        self.pc = 0

    # --- memory (BE) ---
    def _idx(self, addr: int) -> int:
        return (addr - RAM_BASE) & 0xFFFFFFFF

    def lbu(self, a):
        return self.ram[self._idx(a)]

    def lb(self, a):
        v = self.ram[self._idx(a)]
        return v - 256 if v >= 128 else v

    def lhu(self, a):
        i = self._idx(a)
        return (self.ram[i] << 8) | self.ram[i + 1]

    def lh(self, a):
        v = self.lhu(a)
        return v - 65536 if v >= 32768 else v

    def lw(self, a):
        i = self._idx(a)
        return struct.unpack_from('>I', self.ram, i)[0]

    def sb(self, a, v):
        self.ram[self._idx(a)] = v & 0xFF

    def sh(self, a, v):
        i = self._idx(a)
        struct.pack_into('>H', self.ram, i, v & 0xFFFF)

    def sw(self, a, v):
        i = self._idx(a)
        struct.pack_into('>I', self.ram, i, v & 0xFFFFFFFF)

    # --- execution ---
    def run(self, entry: int, until_return: bool = True, max_steps: int = 200_000_000):
        self.pc = entry
        self.r[31] = 0xDEAD0000  # sentinel return
        steps = 0
        while steps < max_steps:
            steps += 1
            if self.pc == 0xDEAD0000:
                return steps
            self.step()
        raise RuntimeError('step limit hit')

    def step(self):
        pc = self.pc
        word = self.lw(pc)
        next_pc = pc + 4
        branch_to = None

        op = word >> 26
        rs = (word >> 21) & 31
        rt = (word >> 16) & 31
        rd = (word >> 11) & 31
        sh = (word >> 6) & 31
        fn = word & 63
        imm = word & 0xFFFF
        simm = imm - 65536 if imm >= 32768 else imm
        r = self.r

        def w(reg, val):
            if reg:
                r[reg] = val & 0xFFFFFFFF

        if op == 0:  # SPECIAL
            if fn == 0x00: w(rd, r[rt] << sh)                     # sll
            elif fn == 0x02: w(rd, (r[rt] & 0xFFFFFFFF) >> sh)    # srl
            elif fn == 0x03:                                       # sra
                v = r[rt]
                v = v - (1 << 32) if v & 0x80000000 else v
                w(rd, v >> sh)
            elif fn == 0x04: w(rd, r[rt] << (r[rs] & 31))          # sllv
            elif fn == 0x06: w(rd, (r[rt] & 0xFFFFFFFF) >> (r[rs] & 31))  # srlv
            elif fn == 0x07:                                       # srav
                v = r[rt]
                v = v - (1 << 32) if v & 0x80000000 else v
                w(rd, v >> (r[rs] & 31))
            elif fn == 0x08: branch_to = r[rs]                     # jr
            elif fn == 0x09:                                       # jalr
                w(rd or 31, pc + 8)
                branch_to = r[rs]
            elif fn in (0x20, 0x21): w(rd, r[rs] + r[rt])          # add/addu
            elif fn in (0x22, 0x23): w(rd, r[rs] - r[rt])          # sub/subu
            elif fn == 0x24: w(rd, r[rs] & r[rt])                  # and
            elif fn == 0x25: w(rd, r[rs] | r[rt])                  # or
            elif fn == 0x26: w(rd, r[rs] ^ r[rt])                  # xor
            elif fn == 0x27: w(rd, ~(r[rs] | r[rt]))               # nor
            elif fn == 0x2A:                                       # slt
                a = r[rs] - (1 << 32) if r[rs] & 0x80000000 else r[rs]
                b = r[rt] - (1 << 32) if r[rt] & 0x80000000 else r[rt]
                w(rd, 1 if a < b else 0)
            elif fn == 0x2B: w(rd, 1 if (r[rs] & 0xFFFFFFFF) < (r[rt] & 0xFFFFFFFF) else 0)
            else:
                raise NotImplementedError(f'special fn=0x{fn:X} at 0x{pc:08X}')
        elif op == 0x01:  # REGIMM
            if rt == 0x00:                                         # bltz
                if r[rs] & 0x80000000:
                    branch_to = pc + 4 + (simm << 2)
            elif rt == 0x01:                                       # bgez
                if not (r[rs] & 0x80000000):
                    branch_to = pc + 4 + (simm << 2)
            else:
                raise NotImplementedError(f'regimm rt=0x{rt:X} at 0x{pc:08X}')
        elif op == 0x02: branch_to = (pc & 0xF0000000) | ((word & 0x3FFFFFF) << 2)   # j
        elif op == 0x03:                                           # jal
            w(31, pc + 8)
            branch_to = (pc & 0xF0000000) | ((word & 0x3FFFFFF) << 2)
        elif op == 0x04:                                           # beq
            if r[rs] == r[rt]:
                branch_to = pc + 4 + (simm << 2)
        elif op == 0x05:                                           # bne
            if r[rs] != r[rt]:
                branch_to = pc + 4 + (simm << 2)
        elif op == 0x06:                                           # blez
            v = r[rs]
            if v == 0 or v & 0x80000000:
                branch_to = pc + 4 + (simm << 2)
        elif op == 0x07:                                           # bgtz
            v = r[rs]
            if v != 0 and not (v & 0x80000000):
                branch_to = pc + 4 + (simm << 2)
        elif op in (0x08, 0x09): w(rt, r[rs] + simm)               # addi/addiu
        elif op == 0x0A:                                           # slti
            a = r[rs] - (1 << 32) if r[rs] & 0x80000000 else r[rs]
            w(rt, 1 if a < simm else 0)
        elif op == 0x0B: w(rt, 1 if (r[rs] & 0xFFFFFFFF) < (simm & 0xFFFFFFFF) else 0)  # sltiu
        elif op == 0x0C: w(rt, r[rs] & imm)                        # andi
        elif op == 0x0D: w(rt, r[rs] | imm)                        # ori
        elif op == 0x0E: w(rt, r[rs] ^ imm)                        # xori
        elif op == 0x0F: w(rt, imm << 16)                          # lui
        elif op == 0x20: w(rt, self.lb(r[rs] + simm) & 0xFFFFFFFF)  # lb
        elif op == 0x21: w(rt, self.lh(r[rs] + simm) & 0xFFFFFFFF)  # lh
        elif op == 0x23: w(rt, self.lw(r[rs] + simm))              # lw
        elif op == 0x24: w(rt, self.lbu(r[rs] + simm))             # lbu
        elif op == 0x25: w(rt, self.lhu(r[rs] + simm))             # lhu
        elif op == 0x28: self.sb(r[rs] + simm, r[rt])              # sb
        elif op == 0x29: self.sh(r[rs] + simm, r[rt])              # sh
        elif op == 0x2B: self.sw(r[rs] + simm, r[rt])              # sw
        else:
            raise NotImplementedError(f'op=0x{op:X} at 0x{pc:08X}')

        if branch_to is not None:
            # execute delay slot then jump
            self.pc = pc + 4
            saved = branch_to
            self.step_delay()
            self.pc = saved
        else:
            self.pc = next_pc

    def step_delay(self):
        # one instruction, no branch expected in a delay slot for this code
        saved_pc = self.pc
        self.step_no_branch(saved_pc)

    def step_no_branch(self, pc):
        # run exactly the instruction at pc, ignoring any branch it would take
        # (the ERZ code never branches in delay slots)
        self.pc = pc
        word = self.lw(pc)
        op = word >> 26
        if op in (0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x01) or (op == 0 and (word & 63) in (8, 9)):
            raise NotImplementedError(f'branch in delay slot at 0x{pc:08X}')
        self.step()


def find_cores(ram: bytearray, cpu: Cpu) -> tuple[int, int]:
    """Both decompressor entries from the dispatch signature.

    The magic-check dispatch (lui 0x4552 / ori 0x5A00) branches on the
    version byte and jal's the v1 routine first, the v2 thunk second. The v1
    target is a self-contained function; the v2 thunk register-saves then
    jalr's the raw core named by a lui/addiu $t4 pair.
    """
    for addr in range(BOOT_RAM, BOOT_RAM + 0x2000, 4):
        if cpu.lw(addr) == 0x3C024552 and cpu.lw(addr + 4) == 0x34425A00:
            targets = []
            for scan in range(addr, addr + 0x100, 4):
                word = cpu.lw(scan)
                if word >> 26 == 3:  # jal
                    targets.append((addr & 0xF0000000) | ((word & 0x3FFFFFF) << 2))
                    if len(targets) == 2:
                        break
            if len(targets) != 2:
                continue
            v1 = targets[0]
            thunk = targets[1]
            v2 = thunk
            for scan3 in range(thunk, thunk + 0x80, 4):
                w3 = cpu.lw(scan3)
                if w3 >> 16 == 0x3C0C:  # lui $t4
                    hi = (w3 & 0xFFFF) << 16
                    lo = cpu.lw(scan3 + 4) & 0xFFFF
                    v2 = hi + (lo - 65536 if lo >= 32768 else lo)
                    break
            return v1, v2
    raise RuntimeError('dispatch signature not found')


def find_v2_core(ram: bytearray, cpu: Cpu) -> int:
    """The v2 core entry: the thunk saves regs then jalr's a lui/addiu target.

    Located by the dispatch signature (lui 0x4552 / ori 0x5A00 / version
    compare / jal), whose second jal target is the reg-saving thunk; inside it
    the lui/addiu pair names the core. For THPS2 this resolves to 0x80000CF8.
    """
    for addr in range(BOOT_RAM, BOOT_RAM + 0x2000, 4):
        if cpu.lw(addr) == 0x3C024552 and cpu.lw(addr + 4) == 0x34425A00:
            # v2 branch: 'beq v1, 2' path jal target
            for scan in range(addr, addr + 0x100, 4):
                word = cpu.lw(scan)
                if word >> 26 == 3:  # jal
                    target = (addr & 0xF0000000) | ((word & 0x3FFFFFF) << 2)
                    # first jal is v1; keep scanning for the second
                    for scan2 in range(scan + 4, addr + 0x100, 4):
                        word2 = cpu.lw(scan2)
                        if word2 >> 26 == 3:
                            thunk = (addr & 0xF0000000) | ((word2 & 0x3FFFFFF) << 2)
                            # inside the thunk: lui $t4, hi / addiu $t4, lo
                            for scan3 in range(thunk, thunk + 0x80, 4):
                                w3 = cpu.lw(scan3)
                                if w3 >> 16 == 0x3C0C:  # lui $t4
                                    hi = (w3 & 0xFFFF) << 16
                                    w4 = cpu.lw(scan3 + 4)
                                    lo = w4 & 0xFFFF
                                    return hi + (lo - 65536 if lo >= 32768 else lo)
                            return thunk
                    return target
    raise RuntimeError('v2 dispatch signature not found')


def decode_block(rom: bytes, offset: int) -> bytes:
    if rom[offset:offset + 3] != b'ERZ':
        raise ValueError(f'no ERZ magic at 0x{offset:X}')
    version = rom[offset + 3]
    decomp_size = struct.unpack_from('>I', rom, offset + 4)[0]
    comp_size = struct.unpack_from('>I', rom, offset + 8)[0]
    if decomp_size <= 0 or decomp_size > 1 << 24:
        raise ValueError(f'implausible decompressed size 0x{decomp_size:X}')

    ram = bytearray(RAM_SIZE)
    boot_len = min(len(rom) - BOOT_ROM, 1 << 20)
    ram[BOOT_RAM - RAM_BASE:BOOT_RAM - RAM_BASE + boot_len] = rom[BOOT_ROM:BOOT_ROM + boot_len]
    cpu = Cpu(ram)

    src = 0x80400000
    dst = 0x80600000
    block = rom[offset:offset + 12 + comp_size + 64]
    ram[src - RAM_BASE:src - RAM_BASE + len(block)] = block

    v1_core, v2_core = find_cores(ram, cpu)
    core = v2_core if version == 2 else v1_core

    cpu.r[4] = src            # a0 = block
    cpu.r[5] = dst            # a1 = output
    cpu.r[6] = comp_size      # a2 (the dispatch passes both sizes)
    cpu.r[7] = decomp_size    # a3
    cpu.r[29] = 0x807F0000    # sp
    cpu.run(core)

    return bytes(ram[dst - RAM_BASE:dst - RAM_BASE + decomp_size])


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('rom')
    parser.add_argument('offset', help='ERZ block offset in ROM (hex)')
    parser.add_argument('-o', '--output')
    args = parser.parse_args()

    rom = Path(args.rom).read_bytes()
    offset = int(args.offset, 16)
    out = decode_block(rom, offset)
    print(f'decoded {len(out)} bytes; head: {out[:24].hex(" ")}')
    printable = bytes(c if 32 <= c < 127 else 46 for c in out[:96]).decode()
    print(f'as text: {printable}')
    if args.output:
        Path(args.output).write_bytes(out)
        print(f'wrote {args.output}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
