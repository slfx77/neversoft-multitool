#!/usr/bin/env python3
"""gba_disasm.py - disassemble a GBA ROM region by bus address.

The GBA sibling of n64_disasm.py: ARM7TDMI (ARMv4T), LITTLE-endian, with the
cartridge mapped at 0x08000000. Vicarious Visions' Tony Hawk line is mostly
Thumb code, so both instruction sets are supported and --func understands the
Thumb push/pop frame convention.

Unlike MIPS, ARM builds 32-bit constants through PC-relative literal pools
(ldr rX, [pc, #imm]), so --find-word lists the literal-pool WORDS holding the
value plus every ldr site that references each one - the ARM analogue of the
N64 tool's lui/ori site listing.

Usage:
    gba_disasm.py <game> <hex_addr> <count> [--thumb]
    gba_disasm.py <game> --find-word 0x03007FFF     literal pools + ldr sites
    gba_disasm.py <game> --callers 0x080205C8       every BL site reaching it
    gba_disasm.py <game> --stores 0x02036B88 --window 0x30   pools for a struct
    gba_disasm.py <game> --func <hex_addr> [--thumb] enclosing function
    gba_disasm.py <game> --header                    decoded cartridge header

<game> is thps2 | thps3 | thps4 | thug | thug2 | sk8land | dhj, resolved
against Sample/Builds, or a path to a .gba image. Addresses may be given in
ROM space (0x08xxxxxx) or as plain file offsets.
"""
from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

from capstone import CS_ARCH_ARM, CS_MODE_ARM, CS_MODE_LITTLE_ENDIAN, CS_MODE_THUMB, Cs

REPO_ROOT = Path(__file__).resolve().parents[3]
SAMPLE_BUILDS = REPO_ROOT / "Sample" / "Builds"
ROM_BASE = 0x08000000

GAME_ROMS = {
    "thps2": ("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)",
              "Tony Hawk's Pro Skater 2 (USA, Europe).gba"),
    "thps3": ("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
              "Tony Hawk's Pro Skater 3 (USA, Europe).gba"),
    "thps4": ("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)",
              "Tony Hawk's Pro Skater 4 (USA, Europe).gba"),
    "thug": ("Tony Hawk's Underground (2003-10-27, GBA - Final)",
             "Tony Hawk's Underground (USA, Europe).gba"),
    "thug2": ("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)",
              "Tony Hawk's Underground 2 (USA, Europe).gba"),
    "sk8land": ("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)",
                "Tony Hawk's American Sk8land (USA).gba"),
    "dhj": ("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)",
            "Tony Hawk's Downhill Jam (USA).gba"),
}


def load(game: str) -> tuple[bytes, str]:
    requested = Path(game)
    if requested.is_file():
        return requested.read_bytes(), requested.stem
    if game not in GAME_ROMS:
        sys.exit(f"unknown game '{game}' (keys: {', '.join(GAME_ROMS)}, or a .gba path)")
    build, rom = GAME_ROMS[game]
    path = SAMPLE_BUILDS / build / rom
    if not path.is_file():
        sys.exit(f"missing {path}")
    return path.read_bytes(), game


def to_offset(data: bytes, addr: int) -> int:
    off = addr - ROM_BASE if addr >= ROM_BASE else addr
    if off < 0 or off >= len(data):
        sys.exit(f"{addr:#010x} outside the {len(data):#x}-byte ROM")
    return off


def disasm(data: bytes, addr: int, count: int, thumb: bool) -> None:
    off = to_offset(data, addr)
    width = 2 if thumb else 4
    md = Cs(CS_ARCH_ARM, (CS_MODE_THUMB if thumb else CS_MODE_ARM) | CS_MODE_LITTLE_ENDIAN)
    end = min(off + count * width, len(data))
    for insn in md.disasm(data[off:end], ROM_BASE + off):
        note = ""
        # resolve pc-relative literal loads inline; the pool word usually IS
        # the interesting datum (a table address or hardware register)
        if insn.mnemonic == "ldr" and "[pc" in insn.op_str:
            disp = 0
            if "#" in insn.op_str:
                try:
                    disp = int(insn.op_str.split("#")[1].rstrip("]"), 0)
                except ValueError:
                    disp = 0
            pc = (insn.address + 4) & ~3 if thumb else insn.address + 8
            pool = pc + disp
            pool_off = pool - ROM_BASE
            if 0 <= pool_off <= len(data) - 4:
                value = struct.unpack_from("<I", data, pool_off)[0]
                note = f"   ; [{pool:#010x}] = {value:#010x}"
        raw = data[insn.address - ROM_BASE:insn.address - ROM_BASE + insn.size]
        print(f"  {insn.address:#010x}  {raw.hex().upper():<8}  {insn.mnemonic} {insn.op_str}{note}")


def find_word(data: bytes, want: int) -> None:
    """Literal-pool words equal to `want`, with the ldr sites that load each."""
    pools = [i for i in range(0, len(data) - 3, 4)
             if struct.unpack_from("<I", data, i)[0] == want]
    print(f"{len(pools)} aligned words hold {want:#010x}")
    for pool in pools:
        pool_addr = ROM_BASE + pool
        sites: list[tuple[int, str]] = []
        # Thumb: ldr rX, [pc, #imm8*4]; pc = align4(addr + 4)
        lo = max(0, pool - 4 - 255 * 4)
        for a in range(lo & ~1, pool, 2):
            half = struct.unpack_from("<H", data, a)[0]
            if (half & 0xF800) != 0x4800:
                continue
            if ((ROM_BASE + a + 4) & ~3) + (half & 0xFF) * 4 == pool_addr:
                sites.append((ROM_BASE + a, "thumb"))
        # ARM: ldr rX, [pc, #imm12]; pc = addr + 8
        lo = max(0, pool - 8 - 4095) & ~3
        for a in range(lo, pool, 4):
            word = struct.unpack_from("<I", data, a)[0]
            if (word & 0x0F7F0000) != 0x059F0000:  # ldr rX, [pc, #±imm]
                continue
            imm = word & 0xFFF
            if not (word & 0x00800000):
                imm = -imm
            if ROM_BASE + a + 8 + imm == pool_addr:
                sites.append((ROM_BASE + a, "arm"))
        tag = "" if sites else "   (no ldr site - likely data, not a literal pool)"
        print(f"  pool {pool_addr:#010x}{tag}")
        for site, mode in sites:
            print(f"    ldr @ {site:#010x} ({mode})")


def find_callers(data: bytes, target: int) -> None:
    """Every call site that reaches `target`, the ARM analogue of an xref list.

    Walking a consumer backwards is how these ROMs give up their formats: a
    packed asset's grammar is stated by the routine that reads it, and the way
    in is usually "who calls the decompressor / the uploader". Thumb BL is a
    HALFWORD PAIR, so the two halves must be read together - scanning for a
    single opcode finds nothing.
    """
    thumb: list[int] = []
    for a in range(0, len(data) - 3, 2):
        hi = struct.unpack_from("<H", data, a)[0]
        if (hi >> 11) != 0x1E:                     # F000-F7FF: BL high half
            continue
        lo = struct.unpack_from("<H", data, a + 2)[0]
        if (lo >> 11) not in (0x1F, 0x1D):         # F800 BL / E800 BLX
            continue
        off = hi & 0x7FF
        if off & 0x400:
            off -= 0x800
        dest = (ROM_BASE + a + 4) + (off << 12) + ((lo & 0x7FF) << 1)
        if dest == target:
            thumb.append(ROM_BASE + a)

    arm: list[int] = []
    for a in range(0, len(data) - 3, 4):
        word = struct.unpack_from("<I", data, a)[0]
        if (word & 0x0F000000) != 0x0B000000:      # BL (any condition)
            continue
        off = word & 0x00FFFFFF
        if off & 0x00800000:
            off -= 0x01000000
        if ROM_BASE + a + 8 + (off << 2) == target:
            arm.append(ROM_BASE + a)

    print(f"{len(thumb) + len(arm)} call sites reach {target:#010x}")
    for site in thumb:
        print(f"  bl @ {site:#010x} (thumb)")
    for site in arm:
        print(f"  bl @ {site:#010x} (arm)")


def find_stores(data: bytes, target: int, window: int = 0) -> None:
    """Literal-pool sites for an address, i.e. the code that can reach it.

    A RAM address is never an immediate; code loads it from a literal pool, so
    the pools holding it (and optionally nearby addresses, via `window`) are
    the candidate producers and consumers of whatever lives there.
    """
    for value in range(target, target + window + 1, 4):
        pools = [i for i in range(0, len(data) - 3, 4)
                 if struct.unpack_from("<I", data, i)[0] == value]
        if pools:
            print(f"== {value:#010x}")
            find_word(data, value)


THUMB_PUSH_LR = (0xB500, 0xFF00)   # push {..., lr}
THUMB_POP_PC = (0xBD00, 0xFF00)    # pop {..., pc}
ARM_PUSH_LR = (0xE92D4000, 0xFFFF4000)  # stmfd sp!, {..., lr}
ARM_BX_LR = (0xE12FFF1E, 0xFFFFFFFF)


def func_bounds(data: bytes, addr: int, thumb: bool, limit: int = 2000) -> tuple[int, int]:
    off = to_offset(data, addr)
    if thumb:
        start = off & ~1
        for back in range(0, limit * 2, 2):
            probe = start - back
            if probe < 0:
                break
            half = struct.unpack_from("<H", data, probe)[0]
            if (half & THUMB_PUSH_LR[1]) == THUMB_PUSH_LR[0]:
                start = probe
                break
        else:
            start = max(0, start - limit * 2)
        end = start
        for step in range(0, limit * 2, 2):
            probe = start + step
            if probe + 2 > len(data):
                break
            half = struct.unpack_from("<H", data, probe)[0]
            if (half & THUMB_POP_PC[1]) == THUMB_POP_PC[0]:
                end = probe + 2
                break
        else:
            end = start + limit * 2
        return ROM_BASE + start, ROM_BASE + end
    start = off & ~3
    for back in range(0, limit * 4, 4):
        probe = start - back
        if probe < 0:
            break
        word = struct.unpack_from("<I", data, probe)[0]
        if (word & ARM_PUSH_LR[1]) == ARM_PUSH_LR[0]:
            start = probe
            break
    else:
        start = max(0, start - limit * 4)
    end = start
    for step in range(0, limit * 4, 4):
        probe = start + step
        if probe + 4 > len(data):
            break
        word = struct.unpack_from("<I", data, probe)[0]
        if word == ARM_BX_LR[0]:
            end = probe + 4
            break
    else:
        end = start + limit * 4
    return ROM_BASE + start, ROM_BASE + end


def show_header(data: bytes, key: str) -> None:
    title = data[0xA0:0xAC].rstrip(b"\0").decode("ascii", "replace")
    code = data[0xAC:0xB0].decode("ascii", "replace")
    maker = data[0xB0:0xB2].decode("ascii", "replace")
    version = data[0xBC]
    entry = struct.unpack_from("<I", data, 0)[0]
    # decode the entry branch: ARM b <target>
    target = ROM_BASE + 8 + ((entry & 0x00FFFFFF) << 2) if (entry >> 24) == 0xEA else None
    print(f"{key}: '{title}' code={code} maker={maker} v{version} size={len(data):#x}")
    if target is not None:
        print(f"  entry b {target:#010x}")


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("game", help="game key (see GAME_ROMS) or path to a .gba image")
    ap.add_argument("addr", nargs="?")
    ap.add_argument("count", nargs="?", type=int, default=40)
    ap.add_argument("-t", "--thumb", action="store_true", help="decode as Thumb")
    ap.add_argument("--find-word", help="list literal pools holding this value + ldr sites")
    ap.add_argument("--func", help="disassemble the whole enclosing function")
    ap.add_argument("--header", action="store_true", help="decode the cartridge header")
    ap.add_argument("--callers", help="list every BL site that reaches this address")
    ap.add_argument("--stores", help="literal-pool sites for a RAM/ROM address")
    ap.add_argument("--window", type=int, default=0,
                    help="with --stores, also scan this many bytes past the address")
    args = ap.parse_args()

    data, key = load(args.game)

    if args.header:
        show_header(data, key)
        return
    if args.find_word:
        find_word(data, int(args.find_word, 16))
        return
    if args.callers:
        find_callers(data, int(args.callers, 16))
        return
    if args.stores:
        find_stores(data, int(args.stores, 16), args.window)
        return
    if args.func:
        start, end = func_bounds(data, int(args.func, 16), args.thumb)
        width = 2 if args.thumb else 4
        print(f"{key}: function {start:#010x}..{end:#010x} "
              f"({(end - start) // width} {'thumb' if args.thumb else 'arm'} slots)")
        disasm(data, start, (end - start) // width, args.thumb)
        return
    if not args.addr:
        ap.error("give an address, --find-word, --func, or --header")
    disasm(data, int(args.addr, 16), args.count, args.thumb)


if __name__ == "__main__":
    main()
