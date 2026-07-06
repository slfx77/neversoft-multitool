#!/usr/bin/env python3
"""Minimal PS2 VU1 microcode disassembler (tables ported from PCSX2
DebugTools/DisVUmicro.h). Enough coverage for Neversoft render microprograms.

Usage: python tools/diagnostics/vu_disasm.py <prog.vubin> [start_instr] [end_instr]

Output columns: addr | upper-instruction | lower-instruction
The I bit (bit 31 of upper) makes the lower word a 32-bit float immediate (LOI).
E bit marks end-of-program (executes one more pair).
"""
import struct
import sys

DEST = ['', 'w', 'z', 'zw', 'y', 'yw', 'yz', 'yzw',
        'x', 'xw', 'xz', 'xzw', 'xy', 'xyw', 'xyz', 'xyzw']

UPPER_MAIN = {
    0x00: 'ADDx', 0x01: 'ADDy', 0x02: 'ADDz', 0x03: 'ADDw',
    0x04: 'SUBx', 0x05: 'SUBy', 0x06: 'SUBz', 0x07: 'SUBw',
    0x08: 'MADDx', 0x09: 'MADDy', 0x0A: 'MADDz', 0x0B: 'MADDw',
    0x0C: 'MSUBx', 0x0D: 'MSUBy', 0x0E: 'MSUBz', 0x0F: 'MSUBw',
    0x10: 'MAXx', 0x11: 'MAXy', 0x12: 'MAXz', 0x13: 'MAXw',
    0x14: 'MINIx', 0x15: 'MINIy', 0x16: 'MINIz', 0x17: 'MINIw',
    0x18: 'MULx', 0x19: 'MULy', 0x1A: 'MULz', 0x1B: 'MULw',
    0x1C: 'MULq', 0x1D: 'MAXi', 0x1E: 'MULi', 0x1F: 'MINIi',
    0x20: 'ADDq', 0x21: 'MADDq', 0x22: 'ADDi', 0x23: 'MADDi',
    0x24: 'SUBq', 0x25: 'MSUBq', 0x26: 'SUBi', 0x27: 'MSUBi',
    0x28: 'ADD', 0x29: 'MADD', 0x2A: 'MUL', 0x2B: 'MAX',
    0x2C: 'SUB', 0x2D: 'MSUB', 0x2E: 'OPMSUB', 0x2F: 'MINI',
}

UPPER_FD = {
    0: ['ADDAx', 'ADDAy', 'ADDAz', 'ADDAw'],
    1: ['SUBAx', 'SUBAy', 'SUBAz', 'SUBAw'],
    2: ['MADDAx', 'MADDAy', 'MADDAz', 'MADDAw'],
    3: ['MSUBAx', 'MSUBAy', 'MSUBAz', 'MSUBAw'],
    4: ['ITOF0', 'ITOF4', 'ITOF12', 'ITOF15'],
    5: ['FTOI0', 'FTOI4', 'FTOI12', 'FTOI15'],
    6: ['MULAx', 'MULAy', 'MULAz', 'MULAw'],
    7: ['MULAq', 'ABS', 'MULAi', 'CLIP'],
    8: ['ADDAq', 'MADDAq', 'ADDAi', 'MADDAi'],
    9: ['SUBAq', 'MSUBAq', 'SUBAi', 'MSUBAi'],
    10: ['ADDA', 'MADDA', 'MULA', None],
    11: ['SUBA', 'MSUBA', 'OPMULA', 'NOP'],
}

LOWER_T3 = {
    0: {12: 'MOVE', 13: 'LQI', 14: 'DIV', 15: 'MTIR', 16: 'RNEXT',
        25: 'MFP', 26: 'XTOP', 27: 'XGKICK', 28: 'ESADD', 29: 'EATANxy', 30: 'ESQRT', 31: 'ESIN'},
    1: {12: 'MR32', 13: 'SQI', 14: 'SQRT', 15: 'MFIR', 16: 'RGET',
        26: 'XITOP', 28: 'ERSADD', 29: 'EATANxz', 30: 'ERSQRT', 31: 'EATAN'},
    2: {13: 'LQD', 14: 'RSQRT', 15: 'ILWR', 16: 'RINIT',
        28: 'ELENG', 29: 'ESUM', 30: 'ERCPR', 31: 'EEXP'},
    3: {13: 'SQD', 14: 'WAITQ', 15: 'ISWR', 16: 'RXOR', 28: 'ERLENG', 30: 'WAITP'},
}

LOWER_MAIN = {0x00: 'LQ', 0x01: 'SQ', 0x04: 'ILW', 0x05: 'ISW',
              0x08: 'IADDIU', 0x09: 'ISUBIU',
              0x10: 'FCEQ', 0x11: 'FCSET', 0x12: 'FCAND', 0x13: 'FCOR',
              0x14: 'FSEQ', 0x15: 'FSSET', 0x16: 'FSAND', 0x17: 'FSOR',
              0x18: 'FMEQ', 0x1A: 'FMAND', 0x1B: 'FMOR', 0x1C: 'FCGET',
              0x20: 'B', 0x21: 'BAL', 0x24: 'JR', 0x25: 'JALR',
              0x28: 'IBEQ', 0x29: 'IBNE', 0x2C: 'IBLTZ', 0x2D: 'IBGTZ',
              0x2E: 'IBLEZ', 0x2F: 'IBGEZ'}


def sext11(v):
    return v - 2048 if v & 0x400 else v


def dis_upper(code):
    dest = DEST[(code >> 21) & 0xF]
    ft = (code >> 16) & 0x1F
    fs = (code >> 11) & 0x1F
    fd = (code >> 6) & 0x1F
    op = code & 0x3F
    if op < 0x30:
        name = UPPER_MAIN.get(op, f'U?{op:02X}')
        bc = 'xyzw'[op & 3] if op < 0x18 and (op & ~3) in (0, 4, 8, 0xC, 0x10, 0x14) or op in range(0x18, 0x1C) else None
        if op in (0x1C, 0x20, 0x21, 0x24, 0x25):
            return f"{name}.{dest} VF{fd:02d}, VF{fs:02d}, Q"
        if op in (0x1D, 0x1E, 0x1F, 0x22, 0x23, 0x26, 0x27):
            return f"{name}.{dest} VF{fd:02d}, VF{fs:02d}, I"
        if bc:
            return f"{name}.{dest} VF{fd:02d}, VF{fs:02d}, VF{ft:02d}{bc}"
        return f"{name}.{dest} VF{fd:02d}, VF{fs:02d}, VF{ft:02d}"
    if op >= 0x3C:
        row = UPPER_FD.get(fd)
        name = row[op & 3] if row else None
        if name is None:
            return f"U?fd{fd}:{op & 3}"
        if name.startswith(('ITOF', 'FTOI', 'ABS', 'MR32')):
            return f"{name}.{dest} VF{ft:02d}, VF{fs:02d}"
        if name == 'CLIP':
            return f"CLIPw.xyz VF{fs:02d}, VF{ft:02d}w"
        if name == 'NOP':
            return 'NOP'
        if name.endswith(('x', 'y', 'z', 'w')) and not name.endswith('A'):
            return f"{name}.{dest} ACC, VF{fs:02d}, VF{ft:02d}{name[-1]}"
        if name.endswith(('q', 'i')):
            return f"{name}.{dest} ACC, VF{fs:02d}, {name[-1].upper()}"
        return f"{name}.{dest} ACC, VF{fs:02d}, VF{ft:02d}"
    return f"U?{op:02X}"


def dis_lower(code, pc):
    op = code >> 25
    it = (code >> 16) & 0x1F
    isr = (code >> 11) & 0x1F
    idr = (code >> 6) & 0x1F
    dest = DEST[(code >> 21) & 0xF]
    if op == 0x40:
        sub = code & 0x3F
        if sub == 0x30:
            return f"IADD VI{idr:02d}, VI{isr:02d}, VI{it:02d}"
        if sub == 0x31:
            return f"ISUB VI{idr:02d}, VI{isr:02d}, VI{it:02d}"
        if sub == 0x32:
            imm5 = idr - 32 if idr & 0x10 else idr
            return f"IADDI VI{it:02d}, VI{isr:02d}, {imm5}"
        if sub == 0x34:
            return f"IAND VI{idr:02d}, VI{isr:02d}, VI{it:02d}"
        if sub == 0x35:
            return f"IOR VI{idr:02d}, VI{isr:02d}, VI{it:02d}"
        if sub >= 0x3C:
            name = LOWER_T3.get(sub & 3, {}).get(idr)
            if name is None:
                return f"L?T3fd{idr}"
            if name == 'DIV':
                fsf = (code >> 21) & 3
                ftf = (code >> 23) & 3
                return f"DIV Q, VF{isr:02d}{'xyzw'[fsf]}, VF{it:02d}{'xyzw'[ftf]}"
            if name in ('SQRT', 'RSQRT'):
                ftf = (code >> 23) & 3
                return f"{name} Q, VF{it:02d}{'xyzw'[ftf]}"
            if name == 'MTIR':
                fsf = (code >> 21) & 3
                return f"MTIR VI{it:02d}, VF{isr:02d}{'xyzw'[fsf]}"
            if name == 'MFIR':
                return f"MFIR.{dest} VF{it:02d}, VI{isr:02d}"
            if name in ('LQI', 'LQD'):
                return f"{name}.{dest} VF{it:02d}, (VI{isr:02d}{'++' if name == 'LQI' else '--'})"
            if name in ('SQI', 'SQD'):
                return f"{name}.{dest} VF{isr:02d}, (VI{it:02d}{'++' if name == 'SQI' else '--'})"
            if name in ('MOVE', 'MR32'):
                return f"{name}.{dest} VF{it:02d}, VF{isr:02d}"
            if name == 'XGKICK':
                return f"XGKICK VI{isr:02d}"
            if name == 'XTOP':
                return f"XTOP VI{it:02d}"
            if name == 'XITOP':
                return f"XITOP VI{it:02d}"
            if name == 'ILWR':
                return f"ILWR.{dest} VI{it:02d}, (VI{isr:02d})"
            if name == 'ISWR':
                return f"ISWR.{dest} VI{it:02d}, (VI{isr:02d})"
            if name == 'MFP':
                return f"MFP.{dest} VF{it:02d}, P"
            return name
        return f"L?40:{sub:02X}"
    name = LOWER_MAIN.get(op)
    if name is None:
        return f"L?{op:02X}"
    if name in ('LQ', 'SQ'):
        imm = sext11(code & 0x7FF)
        if name == 'LQ':
            return f"LQ.{dest} VF{it:02d}, {imm}(VI{isr:02d})"
        return f"SQ.{dest} VF{isr:02d}, {imm}(VI{it:02d})"
    if name in ('ILW', 'ISW'):
        imm = sext11(code & 0x7FF)
        return f"{name}.{dest} VI{it:02d}, {imm}(VI{isr:02d})"
    if name in ('IADDIU', 'ISUBIU'):
        imm15 = ((code >> 10) & 0x7800) | (code & 0x7FF)
        return f"{name} VI{it:02d}, VI{isr:02d}, 0x{imm15:X}"
    if name in ('FCSET', 'FCAND', 'FCOR'):
        return f"{name} VI01, 0x{code & 0xFFFFFF:X}"
    if name in ('FSEQ', 'FSSET', 'FSAND', 'FSOR', 'FMEQ', 'FMAND', 'FMOR', 'FCEQ', 'FCGET'):
        return f"{name} VI{it:02d}, ..."
    if name in ('B', 'BAL'):
        imm = sext11(code & 0x7FF)
        tgt = pc + 1 + imm
        return f"{name} {'VI' + format(it, '02d') + ', ' if name == 'BAL' else ''}-> 0x{tgt:03X}"
    if name in ('JR', 'JALR'):
        return f"{name} VI{isr:02d}"
    if name in ('IBEQ', 'IBNE'):
        imm = sext11(code & 0x7FF)
        return f"{name} VI{it:02d}, VI{isr:02d} -> 0x{pc + 1 + imm:03X}"
    if name in ('IBLTZ', 'IBGTZ', 'IBLEZ', 'IBGEZ'):
        imm = sext11(code & 0x7FF)
        return f"{name} VI{isr:02d} -> 0x{pc + 1 + imm:03X}"
    return name


def main():
    data = open(sys.argv[1], 'rb').read()
    start = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0
    end = int(sys.argv[3], 0) if len(sys.argv) > 3 else len(data) // 8
    for pc in range(start, min(end, len(data) // 8)):
        lower, upper = struct.unpack_from('<II', data, pc * 8)
        flags = ''
        for bit, ch in ((31, 'I'), (30, 'E'), (29, 'M'), (28, 'D'), (27, 'T')):
            if upper & (1 << bit):
                flags += ch
        u = dis_upper(upper & 0x07FFFFFF)
        if 'I' in flags:
            f = struct.unpack('<f', struct.pack('<I', lower))[0]
            l = f"LOI {f!r} (0x{lower:08X})"
        else:
            l = dis_lower(lower, pc)
        print(f"0x{pc:03X}: {u:<44s} | {l}{('  [' + flags + ']') if flags else ''}")


if __name__ == "__main__":
    main()
