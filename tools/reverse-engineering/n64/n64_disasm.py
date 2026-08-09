#!/usr/bin/env python3
"""n64_disasm.py - disassemble a carved N64 boot.bin region by RAM address.

The N64 sibling of psx_disasm.py: MIPS R4300i, BIG-endian, and addressed
through boot.bin RAM bases recovered by the earlier boot-map analysis.
Annotates jal targets and lui/{ori,addiu} immediate pairs so display-list
constants and data tables are readable inline.

Usage:
    n64_disasm.py <game> <hex_ram_addr> <instruction_count>
    n64_disasm.py <game> --find-word 0xD9FDFFFF      list lui/ori build sites
    n64_disasm.py <game> --func <hex_ram_addr>       walk back to the prologue
                                                     and print the function

<game> is thps1 | thps2 | thps3 | spiderman, or a path to a boot.bin.
"""
from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

from capstone import CS_ARCH_MIPS, CS_MODE_BIG_ENDIAN, CS_MODE_MIPS32, Cs

REPO_ROOT = Path(__file__).resolve().parents[3]
CARVE_ROOTS = (
    REPO_ROOT / "TestOutput" / "n64carve",
    REPO_ROOT / "TestOutput" / "n64",
)
BOOT_BASES = {
    "thps1": 0x80016990,
    "thps2": 0x80016B20,
    "thps3": 0x80016B20,
    "spiderman": 0x80016AE0,
}

JR_RA = 0x03E00008


def load(game: str) -> tuple[bytes, int, str]:
    requested_path = Path(game)
    path = requested_path if requested_path.is_file() else None
    if path is None:
        for root in CARVE_ROOTS:
            candidate = root / game / "boot.bin"
            if candidate.is_file():
                path = candidate
                break
    if path is None:
        searched = ", ".join(str(root / game / "boot.bin") for root in CARVE_ROOTS)
        sys.exit(f"no boot.bin for '{game}' (searched: {searched})")
    key = game if game in BOOT_BASES else path.parent.name
    if key not in BOOT_BASES:
        sys.exit(f"unknown game '{game}'")
    return path.read_bytes(), BOOT_BASES[key], key


def disasm(data: bytes, base: int, start: int, count: int) -> None:
    off = start - base
    if off < 0 or off + count * 4 > len(data):
        sys.exit(f"{start:#010x} outside {base:#010x}..{base + len(data):#010x}")
    md = Cs(CS_ARCH_MIPS, CS_MODE_MIPS32 | CS_MODE_BIG_ENDIAN)
    hi: dict[str, int] = {}
    for insn in md.disasm(data[off:off + count * 4], start):
        note = ""
        text = f"{insn.mnemonic} {insn.op_str}"
        if insn.mnemonic == "lui":
            parts = insn.op_str.split(", ")
            if len(parts) == 2:
                try:
                    hi[parts[0]] = int(parts[1], 0) << 16
                except ValueError:
                    pass
        elif insn.mnemonic in ("ori", "addiu"):
            parts = insn.op_str.split(", ")
            if len(parts) == 3 and parts[1] in hi:
                try:
                    # capstone prints addiu displacements ALREADY signed; a
                    # second 16-bit sign extension here reports every negative
                    # displacement 64KB low.
                    low = int(parts[2], 0)
                except ValueError:
                    low = None
                if low is not None:
                    value = (hi[parts[1]] + low) & 0xFFFFFFFF
                    note = f"   ; = {value:#010x}"
                    # the destination now holds a full address, so chained
                    # arithmetic off it annotates correctly too
                    hi[parts[0]] = value
                else:
                    hi.pop(parts[0], None)
            else:
                hi.pop(parts[0].rstrip(","), None)
        elif insn.mnemonic in ("lw", "sw", "lb", "lbu", "lh", "lhu", "sb", "sh",
                               "lwc1", "swc1"):
            if "(" in insn.op_str:
                disp, rest = insn.op_str.rsplit(", ", 1)[-1].split("(")
                reg = rest.rstrip(")")
                if reg in hi:
                    try:
                        # capstone already prints the displacement SIGNED
                        # ("-0x5088"); re-applying a 16-bit sign extension here
                        # subtracts 0x10000 a second time and silently reports
                        # an address 64KB low.
                        note = f"   ; = {(hi[reg] + int(disp, 0)) & 0xFFFFFFFF:#010x}"
                    except ValueError:
                        pass
        elif insn.mnemonic in ("jal", "j"):
            note = "   ; ->"
        raw = struct.unpack_from(">I", data, insn.address - base)[0]
        print(f"  {insn.address:#010x}  {raw:08X}  {text}{note}")


def find_word(data: bytes, base: int, want: int) -> list[int]:
    """lui/ori (or lui alone when the low half is zero) building `want`."""
    sites = []
    hi_want = (want >> 16) & 0xFFFF
    lo_want = want & 0xFFFF
    for i in range(0, len(data) - 4, 4):
        word = struct.unpack_from(">I", data, i)[0]
        if (word >> 26) != 0x0F or (word & 0xFFFF) != hi_want:
            continue
        reg = (word >> 16) & 0x1F
        if lo_want == 0:
            sites.append(base + i)
            continue
        for k in range(1, 6):
            off = i + k * 4
            if off + 4 > len(data):
                break
            follow = struct.unpack_from(">I", data, off)[0]
            if ((follow >> 21) & 0x1F) != reg:
                continue
            if (follow >> 26) == 0x0D and (follow & 0xFFFF) == lo_want:
                sites.append(base + i)
            break
    return sites


def find_seq(data: bytes, base: int, pattern: list[tuple[int, int]]) -> list[int]:
    """Find an instruction sequence given as (value, mask) pairs.

    Masking lets a signature survive per-ROM differences in branch targets and
    register allocation, which is what makes cross-build matching possible
    without symbols.
    """
    hits = []
    span = len(pattern)
    for i in range(0, len(data) - 4 * span, 4):
        for k, (want, mask) in enumerate(pattern):
            word = struct.unpack_from(">I", data, i + 4 * k)[0]
            if (word & mask) != (want & mask):
                break
        else:
            hits.append(base + i)
    return hits


def parse_seq(text: str) -> list[tuple[int, int]]:
    """'32820400/FFE0FFFF,........' -> [(value, mask), ...]; dots are wildcards."""
    out = []
    for token in text.split(","):
        token = token.strip()
        if "/" in token:
            value, mask = token.split("/")
            out.append((int(value, 16), int(mask, 16)))
        elif set(token) == {"."}:
            out.append((0, 0))
        else:
            digits = "".join("0" if c == "." else c for c in token)
            mask = "".join("0" if c == "." else "F" for c in token)
            out.append((int(digits, 16), int(mask, 16)))
    return out


def func_start(data: bytes, base: int, addr: int, limit: int = 600) -> int:
    off = addr - base
    for back in range(0, limit * 4, 4):
        probe = off - back
        if probe < 8:
            return base
        if struct.unpack_from(">I", data, probe - 8)[0] == JR_RA:
            return base + probe
    return addr - limit * 4


def func_end(data: bytes, base: int, start: int, limit: int = 900) -> int:
    off = start - base
    for step in range(0, limit * 4, 4):
        probe = off + step
        if probe + 8 > len(data):
            break
        if struct.unpack_from(">I", data, probe)[0] == JR_RA:
            return base + probe + 8
    return start + limit * 4


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument(
        "game",
        help=("game key (searched under TestOutput/n64carve and TestOutput/n64) "
              "or path to boot.bin"),
    )
    ap.add_argument("addr", nargs="?")
    ap.add_argument("count", nargs="?", type=int, default=40)
    ap.add_argument("--find-word")
    ap.add_argument("--find-seq",
                    help="masked instruction signature, e.g. '3.820400,........,3C0.0002'")
    ap.add_argument("--func", help="disassemble the whole enclosing function")
    args = ap.parse_args()

    data, base, key = load(args.game)

    if args.find_word:
        want = int(args.find_word, 16)
        sites = find_word(data, base, want)
        print(f"{key}: {len(sites)} lui/ori sites building {want:#010x}")
        for site in sites:
            print(f"  {site:#010x}   (function starts {func_start(data, base, site):#010x})")
        return

    if args.find_seq:
        pattern = parse_seq(args.find_seq)
        hits = find_seq(data, base, pattern)
        print(f"{key}: {len(hits)} matches for a {len(pattern)}-instruction signature")
        for hit in hits:
            print(f"  {hit:#010x}   (function starts {func_start(data, base, hit):#010x})")
            disasm(data, base, hit, len(pattern))
        return

    if args.func:
        addr = int(args.func, 16)
        start = func_start(data, base, addr)
        end = func_end(data, base, start)
        print(f"{key}: function {start:#010x}..{end:#010x} "
              f"({(end - start) // 4} instructions), marker at {addr:#010x}")
        disasm(data, base, start, (end - start) // 4)
        return

    if not args.addr:
        ap.error("give an address, --find-word, or --func")
    disasm(data, base, int(args.addr, 16), args.count)


if __name__ == "__main__":
    main()
