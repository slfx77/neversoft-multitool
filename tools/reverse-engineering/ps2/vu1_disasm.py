"""
Full VU1 microcode disassembler for THAW PS2 executable (SLUS_212.95).
Decode tables ported from PCSX2 DebugTools/DisVUmicro.h.

Usage: python tools/reverse-engineering/ps2/vu1_disasm.py <PS2-ELF> [--range start-end]
       python tools/reverse-engineering/ps2/vu1_disasm.py <PS2-ELF> --all
       python tools/reverse-engineering/ps2/vu1_disasm.py <PS2-ELF> --xgkick
"""
import os, struct, sys

def read_u32(d, o): return struct.unpack_from('<I', d, o)[0]
def read_u16(d, o): return struct.unpack_from('<H', d, o)[0]

# ── Field extractors (match PCSX2 VU.h macros) ──

def _Ft_(inst): return (inst >> 16) & 0x1F
def _Fs_(inst): return (inst >> 11) & 0x1F
def _Fd_(inst): return (inst >> 6) & 0x1F
def _It_(inst): return _Ft_(inst) & 0xF
def _Is_(inst): return _Fs_(inst) & 0xF
def _Id_(inst): return _Fd_(inst) & 0xF

def _dest_(inst):
    bits = (inst >> 21) & 0xF
    s = ''
    if bits & 8: s += 'x'
    if bits & 4: s += 'y'
    if bits & 2: s += 'z'
    if bits & 1: s += 'w'
    return s

def _Fsf_(inst): return "xyzw"[(inst >> 21) & 3]
def _Ftf_(inst): return "xyzw"[(inst >> 23) & 3]

def _Imm11_(inst):
    v = inst & 0x7FF
    return v - 0x800 if v & 0x400 else v

def _Imm15_(inst):
    return ((inst >> 10) & 0x7800) | (inst & 0x7FF)

def _Imm5_(inst):
    v = (inst >> 6) & 0x1F
    return v - 0x20 if v & 0x10 else v

def _Imm24_(inst):
    return inst & 0xFFFFFF

def _Imm12_(inst):
    return inst & 0xFFF

def vf(i): return f"vf{i:02d}"
def vi(i): return f"vi{i & 0xF:02d}"

# ── Upper pipe decode (PCSX2 DisVUmicro.h upper tables) ──

def decode_upper(inst):
    """Full VU upper instruction decode using PCSX2 tables."""
    dest = _dest_(inst)
    dsuf = f".{dest}" if dest else ""
    ft = _Ft_(inst); fs = _Fs_(inst); fd = _Fd_(inst)
    bc = "xyzw"[inst & 3]  # broadcast field for *x/*y/*z/*w variants
    op = inst & 0x3F

    # Check for NOP first
    if op == 0x3F and fd == 11:
        return "nop"

    # Main upper table [0x00..0x2F] — 3-operand VF instructions
    main_ops = {
        0x00: f"add{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # ADDx
        0x01: f"add{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # ADDy
        0x02: f"add{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # ADDz
        0x03: f"add{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # ADDw
        0x04: f"sub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # SUBx
        0x05: f"sub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # SUBy
        0x06: f"sub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # SUBz
        0x07: f"sub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # SUBw
        0x08: f"madd{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MADDx
        0x09: f"madd{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MADDy
        0x0A: f"madd{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MADDz
        0x0B: f"madd{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MADDw
        0x0C: f"msub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MSUBx
        0x0D: f"msub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MSUBy
        0x0E: f"msub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MSUBz
        0x0F: f"msub{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MSUBw
        0x10: f"max{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MAXx
        0x11: f"max{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MAXy
        0x12: f"max{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MAXz
        0x13: f"max{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MAXw
        0x14: f"mini{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MINIx
        0x15: f"mini{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MINIy
        0x16: f"mini{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MINIz
        0x17: f"mini{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",   # MINIw
        0x18: f"mul{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MULx
        0x19: f"mul{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MULy
        0x1A: f"mul{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MULz
        0x1B: f"mul{dsuf}.{bc} {vf(fd)},{vf(fs)},{vf(ft)}",    # MULw
        0x1C: f"mulq{dsuf} {vf(fd)},{vf(fs)},Q",               # MULq
        0x1D: f"maxi{dsuf} {vf(fd)},{vf(fs)},I",               # MAXi
        0x1E: f"muli{dsuf} {vf(fd)},{vf(fs)},I",               # MULi
        0x1F: f"minii{dsuf} {vf(fd)},{vf(fs)},I",              # MINIi
        0x20: f"addq{dsuf} {vf(fd)},{vf(fs)},Q",               # ADDq
        0x21: f"maddq{dsuf} {vf(fd)},{vf(fs)},Q",              # MADDq
        0x22: f"addi{dsuf} {vf(fd)},{vf(fs)},I",               # ADDi
        0x23: f"maddi{dsuf} {vf(fd)},{vf(fs)},I",              # MADDi
        0x24: f"subq{dsuf} {vf(fd)},{vf(fs)},Q",               # SUBq
        0x25: f"msubq{dsuf} {vf(fd)},{vf(fs)},Q",              # MSUBq
        0x26: f"subi{dsuf} {vf(fd)},{vf(fs)},I",               # SUBi
        0x27: f"msubi{dsuf} {vf(fd)},{vf(fs)},I",              # MSUBi
        0x28: f"add{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",         # ADD
        0x29: f"madd{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",        # MADD
        0x2A: f"mul{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",         # MUL
        0x2B: f"max{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",         # MAX
        0x2C: f"sub{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",         # SUB
        0x2D: f"msub{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",        # MSUB
        0x2E: f"opmsub{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",      # OPMSUB
        0x2F: f"mini{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",        # MINI
    }

    if op in main_ops:
        return main_ops[op]

    # FD sub-tables [0x3C..0x3F]
    if op == 0x3C:  # FD_00
        fd_ops = {
            0: f"addax{dsuf} ACC,{vf(fs)},{vf(ft)}",     # ADDAx
            1: f"subx{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",  # SUBx (duplicate?)
            2: f"maddax{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MADDAx
            3: f"msubax{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MSUBAx
            4: f"itof0{dsuf} {vf(ft)},{vf(fs)}",         # ITOF0
            5: f"ftoi0{dsuf} {vf(ft)},{vf(fs)}",         # FTOI0
            6: f"mulax{dsuf} ACC,{vf(fs)},{vf(ft)}",     # MULAx
            7: f"mulaq{dsuf} ACC,{vf(fs)},Q",            # MULAq
            8: f"addaq{dsuf} ACC,{vf(fs)},Q",            # ADDAq
            9: f"subaq{dsuf} ACC,{vf(fs)},Q",            # SUBAq
            10: f"adda{dsuf} ACC,{vf(fs)},{vf(ft)}",     # ADDA
            11: f"suba{dsuf} ACC,{vf(fs)},{vf(ft)}",     # SUBA
        }
        return fd_ops.get(fd)

    if op == 0x3D:  # FD_01
        fd_ops = {
            0: f"adday{dsuf} ACC,{vf(fs)},{vf(ft)}",     # ADDAy
            1: f"suby{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",  # SUBy
            2: f"madday{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MADDAy
            3: f"msubay{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MSUBAy
            4: f"itof4{dsuf} {vf(ft)},{vf(fs)}",         # ITOF4
            5: f"ftoi4{dsuf} {vf(ft)},{vf(fs)}",         # FTOI4
            6: f"mulay{dsuf} ACC,{vf(fs)},{vf(ft)}",     # MULAy
            7: f"abs{dsuf} {vf(ft)},{vf(fs)}",           # ABS
            8: f"maddaq{dsuf} ACC,{vf(fs)},Q",           # MADDAq
            9: f"msubaq{dsuf} ACC,{vf(fs)},Q",           # MSUBAq
            10: f"madda{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MADDA
            11: f"msuba{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MSUBA
        }
        return fd_ops.get(fd)

    if op == 0x3E:  # FD_10
        fd_ops = {
            0: f"addaz{dsuf} ACC,{vf(fs)},{vf(ft)}",     # ADDAz
            1: f"subz{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",  # SUBz
            2: f"maddaz{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MADDAz
            3: f"msubaz{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MSUBAz
            4: f"itof12{dsuf} {vf(ft)},{vf(fs)}",        # ITOF12
            5: f"ftoi12{dsuf} {vf(ft)},{vf(fs)}",        # FTOI12
            6: f"mulaz{dsuf} ACC,{vf(fs)},{vf(ft)}",     # MULAz
            7: f"mulai{dsuf} ACC,{vf(fs)},I",            # MULAi
            8: f"addai{dsuf} ACC,{vf(fs)},I",            # ADDAi
            9: f"subai{dsuf} ACC,{vf(fs)},I",            # SUBAi
            10: f"mula{dsuf} ACC,{vf(fs)},{vf(ft)}",     # MULA
            11: f"opmula{dsuf} ACC,{vf(fs)},{vf(ft)}",   # OPMULA
        }
        return fd_ops.get(fd)

    if op == 0x3F:  # FD_11
        fd_ops = {
            0: f"addaw{dsuf} ACC,{vf(fs)},{vf(ft)}",     # ADDAw
            1: f"subw{dsuf} {vf(fd)},{vf(fs)},{vf(ft)}",  # SUBw
            2: f"maddaw{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MADDAw
            3: f"msubaw{dsuf} ACC,{vf(fs)},{vf(ft)}",    # MSUBAw
            4: f"itof15{dsuf} {vf(ft)},{vf(fs)}",        # ITOF15
            5: f"ftoi15{dsuf} {vf(ft)},{vf(fs)}",        # FTOI15
            6: f"mulaw{dsuf} ACC,{vf(fs)},{vf(ft)}",     # MULAw
            7: f"clip{dsuf} {vf(fs)},{vf(ft)}",          # CLIP
            8: f"maddai{dsuf} ACC,{vf(fs)},I",           # MADDAi
            9: f"msubai{dsuf} ACC,{vf(fs)},I",           # MSUBAi
            11: "nop",                                     # NOP
        }
        return fd_ops.get(fd)

    return None


# ── Lower pipe decode (PCSX2 DisVUmicro.h lower tables) ──

def decode_lower(inst):
    """Full VU lower instruction decode using PCSX2 tables."""
    opcode = (inst >> 25) & 0x7F
    ft = _Ft_(inst); fs = _Fs_(inst); fd = _Fd_(inst)
    it = _It_(inst); is_r = _Is_(inst); id_r = _Id_(inst)
    dest = _dest_(inst)
    dsuf = f".{dest}" if dest else ""
    fsf = _Fsf_(inst); ftf = _Ftf_(inst)
    imm11 = _Imm11_(inst)
    imm15 = _Imm15_(inst)
    imm5 = _Imm5_(inst)

    # Check for NOP patterns
    if inst in (0x8000033C, 0x800002FF) or (inst & 0xFFFF07FF) == 0x000002FF:
        return "nop"

    # Main lower table (opcode = inst >> 25)
    main_table = {
        0x00: f"lq{dsuf} {vf(ft)},{imm11}({vi(is_r)})",
        0x01: f"sq{dsuf} {vf(fs)},{imm11}({vi(it)})",
        0x04: f"ilw{dsuf} {vi(it)},{imm11}({vi(is_r)})",
        0x05: f"isw{dsuf} {vi(it)},{imm11}({vi(is_r)})",
        0x08: f"iaddiu {vi(it)},{vi(is_r)},{imm15}",
        0x09: f"isubiu {vi(it)},{vi(is_r)},{imm15}",
        0x10: f"fceq {vi(it)},{_Imm24_(inst)}",
        0x11: f"fcset {_Imm24_(inst)}",
        0x12: f"fcand vi01,{_Imm24_(inst)}",
        0x13: f"fcor vi01,{_Imm24_(inst)}",
        0x14: f"fseq {vi(it)},{_Imm12_(inst)}",
        0x15: f"fsset {_Imm12_(inst)}",
        0x16: f"fsand {vi(it)},{_Imm12_(inst)}",
        0x17: f"fsor {vi(it)},{_Imm12_(inst)}",
        0x18: f"fmeq {vi(it)},{vi(is_r)}",
        0x1A: f"fmand {vi(it)},{vi(is_r)}",
        0x1B: f"fmor {vi(it)},{vi(is_r)}",
        0x1C: f"fcget {vi(it)}",
        0x20: f"b {imm11:+d}",
        0x21: f"bal {vi(it)},{imm11:+d}",
        0x24: f"ibeq {vi(it)},{vi(is_r)},{imm11:+d}",
        0x25: f"ibne {vi(it)},{vi(is_r)},{imm11:+d}",
        0x28: f"ibltz {vi(is_r)},{imm11:+d}",
        0x29: f"ibgtz {vi(is_r)},{imm11:+d}",
        0x2A: f"iblez {vi(is_r)},{imm11:+d}",
        0x2B: f"ibgez {vi(is_r)},{imm11:+d}",
    }

    if opcode in main_table:
        return main_table[opcode]

    # JR/JALR (opcode 0x22, 0x23)
    if opcode == 0x22:
        return f"jr {vi(is_r)}"
    if opcode == 0x23:
        return f"jalr {vi(it)},{vi(is_r)}"

    # Lower OP (opcode 0x40)
    if opcode != 0x40:
        return None

    lower_sub = inst & 0x3F
    simple = {
        0x30: f"iadd {vi(id_r)},{vi(is_r)},{vi(it)}",
        0x31: f"isub {vi(id_r)},{vi(is_r)},{vi(it)}",
        0x32: f"iaddi {vi(it)},{vi(is_r)},{imm5}",
        0x34: f"iand {vi(id_r)},{vi(is_r)},{vi(it)}",
        0x35: f"ior {vi(id_r)},{vi(is_r)},{vi(it)}",
    }
    if lower_sub in simple:
        return simple[lower_sub]

    # T3 sub-tables
    tertiary = fd
    if lower_sub == 0x3C:
        t = {
            12: f"move{dsuf} {vf(ft)},{vf(fs)}" if (fs or ft) else "nop",
            13: f"lqi{dsuf} {vf(ft)},({vi(is_r)}++)",
            14: f"div {vf(fs)}.{fsf},{vf(ft)}.{ftf}",
            15: f"mtir {vi(it)},{vf(fs)}.{fsf}",
            16: f"rnext {vf(ft)}",
            25: f"mfp{dsuf} {vf(ft)},P",
            26: f"xtop {vi(it)}",
            27: f"xgkick {vi(is_r)}",
            28: f"esadd {vf(fs)}",
            29: f"eatanxy {vf(fs)}",
            30: f"esqrt {vf(fs)}.{fsf}",
            31: f"esin {vf(fs)}.{fsf}",
        }
        return t.get(tertiary)

    if lower_sub == 0x3D:
        t = {
            12: f"mr32{dsuf} {vf(ft)},{vf(fs)}",
            13: f"sqi{dsuf} {vf(fs)},({vi(it)}++)",
            14: f"sqrt {vf(ft)}.{ftf}",
            15: f"mfir{dsuf} {vf(ft)},{vi(is_r)}",
            16: f"rget {vf(ft)}",
            26: f"xitop {vi(it)}",
            28: f"ersadd {vf(fs)}",
            29: f"eatanxz {vf(fs)}",
            30: f"ersqrt {vf(fs)}.{fsf}",
            31: f"eatan {vf(fs)}.{fsf}",
        }
        return t.get(tertiary)

    if lower_sub == 0x3E:
        t = {
            13: f"lqd{dsuf} {vf(ft)},({vi(is_r)}--)",
            14: f"rsqrt {vf(fs)}.{fsf},{vf(ft)}.{ftf}",
            15: f"ilwr{dsuf} {vi(it)},({vi(is_r)})",
            16: f"rinit {vf(fs)}.{fsf}",
            28: f"eleng {vf(fs)}",
            29: f"esum {vf(fs)}",
            30: f"ercpr {vf(fs)}.{fsf}",
            31: f"eexp {vf(fs)}.{fsf}",
        }
        return t.get(tertiary)

    if lower_sub == 0x3F:
        t = {
            13: f"sqd{dsuf} {vf(fs)},({vi(it)}--)",
            14: f"waitq",
            15: f"iswr{dsuf} {vi(it)},({vi(is_r)})",
            16: f"rxor {vf(fs)}.{fsf}",
            28: f"erleng {vf(fs)}",
            30: f"waitp",
        }
        return t.get(tertiary)

    return None


# ── LOI (Load Immediate to I register) ──
# LOI is encoded as the lower instruction of the NEXT word pair,
# where the upper instruction of the current word has the I bit set (bit 31).
# The immediate value is the entire 32-bit lower word.

def decode_loi(lower_word):
    """If the previous upper had I-bit, this lower word is an immediate float."""
    import struct as st
    f = st.unpack('<f', st.pack('<I', lower_word))[0]
    return f"loi I,{f} (0x{lower_word:08X})"


# ── ELF parser ──

def parse_elf_sections(data):
    e_shoff = read_u32(data, 0x20)
    e_shentsize = read_u16(data, 0x2E)
    e_shnum = read_u16(data, 0x30)
    e_shstrndx = read_u16(data, 0x32)
    strtab_off = e_shoff + e_shstrndx * e_shentsize
    strtab_sh_offset = read_u32(data, strtab_off + 16)
    sections = []
    for i in range(e_shnum):
        off = e_shoff + i * e_shentsize
        sh_name_idx = read_u32(data, off)
        sh_type = read_u32(data, off + 4)
        sh_addr = read_u32(data, off + 12)
        sh_offset = read_u32(data, off + 16)
        sh_size = read_u32(data, off + 20)
        name_end = data.index(b'\x00', strtab_sh_offset + sh_name_idx)
        name = data[strtab_sh_offset + sh_name_idx : name_end].decode('ascii', errors='replace')
        sections.append((name, sh_type, sh_addr, sh_offset, sh_size))
    return sections


def disassemble(code, vu_base=0, start_addr=0, end_addr=None, label_prefix=""):
    """Disassemble VU code, returning list of (addr, upper_str, lower_str, flags)."""
    if end_addr is None:
        end_addr = vu_base + len(code) // 8

    # First pass: collect branch targets for labels
    branch_targets = set()
    for i in range(0, len(code) - 7, 8):
        addr = vu_base + i // 8
        if addr < start_addr or addr >= end_addr:
            continue
        lower = read_u32(code, i)
        low_dec = decode_lower(lower)
        if low_dec and any(low_dec.startswith(b) for b in ('b ', 'bal ', 'ibeq ', 'ibne ', 'ibltz ', 'ibgtz ', 'iblez ', 'ibgez ')):
            # Extract branch offset (last token after comma or space)
            parts = low_dec.split(',')
            try:
                offset_str = parts[-1].strip()
                if offset_str.startswith('+') or offset_str.startswith('-') or offset_str.lstrip('-').isdigit():
                    target = addr + 1 + int(offset_str)  # VU branches: PC+1+offset (delay slot)
                    branch_targets.add(target)
            except ValueError:
                pass

    # Second pass: disassemble
    lines = []
    prev_upper_i_bit = False
    for i in range(0, len(code) - 7, 8):
        addr = vu_base + i // 8
        if addr < start_addr or addr >= end_addr:
            prev_upper_i_bit = (read_u32(code, i + 4) >> 31) & 1
            continue

        lower = read_u32(code, i)
        upper = read_u32(code, i + 4)

        e_bit = (upper >> 30) & 1  # E bit = end of program
        i_bit = (upper >> 31) & 1  # I bit = next lower is LOI immediate
        d_bit = (upper >> 27) & 1  # D bit = debug breakpoint
        t_bit = (upper >> 26) & 1  # T bit = debug breakpoint

        # Decode
        if prev_upper_i_bit:
            low_str = decode_loi(lower)
        else:
            low_str = decode_lower(lower) or f"??? L 0x{lower:08X}"

        up_str = decode_upper(upper) or f"??? U 0x{upper:08X}"

        flags = ''
        if e_bit: flags += 'E'
        if i_bit: flags += 'I'
        if d_bit: flags += 'D'

        label = f"L{addr}:" if addr in branch_targets else ""

        lines.append((addr, upper, lower, up_str, low_str, flags, label))
        prev_upper_i_bit = i_bit

    return lines


def print_disassembly(lines, show_hex=True):
    for addr, upper, lower, up_str, low_str, flags, label in lines:
        if label:
            print(f"  {label}")
        flag_str = f" [{flags}]" if flags else ""
        if show_hex:
            print(f"  {addr:5d}: {upper:08X} {lower:08X}  {up_str:40s} | {low_str}{flag_str}")
        else:
            print(f"  {addr:5d}: {up_str:40s} | {low_str}{flag_str}")


def find_xgkick_regions(lines, context=30):
    """Find XGKICK instructions and return surrounding regions."""
    regions = []
    xgkick_addrs = []
    for addr, upper, lower, up_str, low_str, flags, label in lines:
        if 'xgkick' in low_str:
            xgkick_addrs.append(addr)

    for xg_addr in xgkick_addrs:
        start = xg_addr - context
        end = xg_addr + context
        region = [(a, u, l, us, ls, f, lb) for a, u, l, us, ls, f, lb in lines if start <= a <= end]
        regions.append((xg_addr, region))

    return regions


def main():
    if len(sys.argv) < 2:
        print(f"Usage: {sys.argv[0]} <PS2-ELF> [--all|--xgkick|--range START-END]")
        return

    data = open(sys.argv[1], 'rb').read()
    sections = parse_elf_sections(data)

    # Find .vutext
    vutext = None
    vutext_base = 0
    for name, sh_type, sh_addr, sh_offset, sh_size in sections:
        if name == '.vutext':
            vutext = data[sh_offset : sh_offset + sh_size]
            vutext_base = 0
            print(f".vutext: offset=0x{sh_offset:08X} size={sh_size} ({sh_size//8} instructions)")
            break

    # Find overlays
    overlays = []
    for name, sh_type, sh_addr, sh_offset, sh_size in sections:
        if '.DVP.overlay' in name:
            parts = name.split('.')
            vu_addr_str = parts[4] if len(parts) > 4 else '0x0'
            vu_base = int(vu_addr_str, 16) if vu_addr_str.startswith('0x') else 0
            overlay_id = parts[5] if len(parts) > 5 else '?'
            odata = data[sh_offset : sh_offset + sh_size]
            overlays.append((name, vu_base, overlay_id, odata))

    if not vutext:
        print("ERROR: .vutext section not found")
        return

    mode = '--all' if '--all' in sys.argv else ('--xgkick' if '--xgkick' in sys.argv else '--range')

    # Parse range
    start_addr = 0
    end_addr = len(vutext) // 8
    for arg in sys.argv:
        if arg.startswith('--range'):
            pass
        elif '-' in arg and arg[0].isdigit():
            parts = arg.split('-')
            start_addr = int(parts[0])
            end_addr = int(parts[1])

    if mode == '--all':
        print(f"\n{'='*80}")
        print(f"Full .vutext disassembly ({len(vutext)//8} instructions)")
        print(f"{'='*80}")
        lines = disassemble(vutext, vutext_base, 0, len(vutext) // 8)
        print_disassembly(lines)

        for name, vu_base, overlay_id, odata in overlays:
            print(f"\n{'='*80}")
            print(f"Overlay: {name} (base={vu_base}, {len(odata)//8} instrs)")
            print(f"{'='*80}")
            lines = disassemble(odata, vu_base, vu_base, vu_base + len(odata) // 8)
            print_disassembly(lines)

    elif mode == '--xgkick':
        print(f"\n{'='*80}")
        print(f"XGKICK regions in .vutext")
        print(f"{'='*80}")
        lines = disassemble(vutext, vutext_base, 0, len(vutext) // 8)
        regions = find_xgkick_regions(lines, context=40)
        for xg_addr, region in regions:
            print(f"\n--- XGKICK at addr {xg_addr} ---")
            print_disassembly(region)

    else:
        lines = disassemble(vutext, vutext_base, start_addr, end_addr)
        print_disassembly(lines)


if __name__ == '__main__':
    main()
