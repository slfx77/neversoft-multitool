#!/usr/bin/env python3
"""Scan a PS2 ELF for embedded VU1 microcode.

Two detection modes:
1. VIF MPG chains: cmd byte 0x4A at offset+3 with num*8 bytes of ucode payload.
   Chained MPGs (loadaddr advancing by num) are merged into one program.
2. Raw ucode heuristic: 8-byte aligned regions dense in valid-looking VU
   instruction pairs — upper word with typical opcode fields and lower words
   containing XGKICK/ISUBIU/etc. Used as fallback if the engine uploads ucode
   from a raw array (no VIF wrapper in the ELF image).

Reports candidate regions with file offset, length, and a few disassembly
signature stats (count of ITOF0/ITOF4/ITOF12/ITOF15 upper ops, XGKICK lowers)
— the fixed-point shift of position ITOF is the key THPG-vs-P8 discriminator.

Usage: python tools/diagnostics/thpg_elf_ucode_scan.py <game.elf> [--dump out_prefix]
"""
import struct
import sys

# VU upper instruction: bits[5:0]=0x3C..0x3F selects the special table where the
# opcode is bits[10:6]<<2 | bits[1:0]. ITOF/FTOI family (from PCSX2 VU tables):
#   ITOF0  = opcode 0x13C group: bits[10:0] pattern 0b100_1111_1100? We match via
#   the documented encodings instead: upper special1 (bits[5:0] in 0x3C..0x3F),
#   sub = ((op>>4)&0x7C) | (op&3):
#     ITOF0=0x10, ITOF4=0x11, ITOF12=0x12, ITOF15=0x13,
#     FTOI0=0x14, FTOI4=0x15, FTOI12=0x16, FTOI15=0x17
# XGKICK (lower): sub == 0x6C with same special-table derivation.


def upper_special_sub(op):
    if (op & 0x3C) != 0x3C:
        return None
    return ((op >> 4) & 0x7C) | (op & 3)


def classify_region(data, off, length):
    stats = {'ITOF0': 0, 'ITOF4': 0, 'ITOF12': 0, 'ITOF15': 0,
             'FTOI0': 0, 'FTOI4': 0, 'FTOI12': 0, 'FTOI15': 0, 'XGKICK': 0}
    names = {0x10: 'ITOF0', 0x11: 'ITOF4', 0x12: 'ITOF12', 0x13: 'ITOF15',
             0x14: 'FTOI0', 0x15: 'FTOI4', 0x16: 'FTOI12', 0x17: 'FTOI15'}
    for i in range(off, off + length - 7, 8):
        lower = struct.unpack_from('<I', data, i)[0]
        upper = struct.unpack_from('<I', data, i + 4)[0]
        sub_u = upper_special_sub(upper)
        if sub_u in names:
            stats[names[sub_u]] += 1
        sub_l = upper_special_sub(lower)
        if sub_l == 0x6C:
            stats['XGKICK'] += 1
    return stats


def find_mpg_chains(data):
    """Find VIF MPG command chains. Returns [(file_off, loadaddr, ucode_bytes)]."""
    chains = []
    i = 0
    n = len(data)
    while i + 4 <= n:
        if data[i + 3] == 0x4A:
            num = data[i + 2] or 256
            loadaddr = struct.unpack_from('<H', data, i)[0]
            size = num * 8
            if i + 4 + size <= n:
                # heuristic sanity: payload should not be all zeros
                payload = data[i + 4:i + 4 + size]
                if any(payload):
                    chains.append((i, loadaddr, payload))
                    i += 4 + size
                    continue
        i += 4
    # merge consecutive chains (loadaddr advancing by num)
    merged = []
    for off, addr, payload in chains:
        if merged:
            po, pa, pp = merged[-1]
            expected_off = po + 4 + len(pp)
            # allow NOP padding between MPG commands
            gap = data[expected_off:off]
            if addr * 8 == pa * 8 + len(pp) and off - expected_off <= 16 and all(
                    gap[k + 3] in (0, 0x10, 0x11, 0x13) for k in range(0, len(gap) - 3, 4)):
                merged[-1] = (po, pa, pp + payload)
                continue
        merged.append((off, addr, payload))
    return merged


def main():
    path = sys.argv[1]
    dump_prefix = None
    if '--dump' in sys.argv:
        dump_prefix = sys.argv[sys.argv.index('--dump') + 1]
    data = open(path, 'rb').read()
    chains = find_mpg_chains(data)
    real = []
    for off, addr, payload in chains:
        if len(payload) < 256:
            continue
        stats = classify_region(payload, 0, len(payload))
        # real ucode renders: has XGKICK
        if stats['XGKICK'] == 0:
            continue
        real.append((off, addr, payload, stats))
    print(f"{path}: {len(chains)} raw MPG hits, {len(real)} plausible programs")
    for off, addr, payload, stats in real:
        interesting = {k: v for k, v in stats.items() if v}
        print(f"  file_off=0x{off:06X} loadaddr=0x{addr:04X} size={len(payload)}B "
              f"({len(payload) // 8} instr) {interesting}")
        if dump_prefix:
            out = f"{dump_prefix}_0x{off:06X}.vubin"
            open(out, 'wb').write(payload)
            print(f"    -> {out}")


if __name__ == "__main__":
    main()
