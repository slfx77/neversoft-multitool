#!/usr/bin/env python3
"""Linearise SafeDisc's junk-jump obfuscation so an obfuscated function can be read.

SafeDisc pads its protection routines with a mechanical pattern that defeats a
linear disassembler but carries no information:

  * COMPLEMENTARY CONDITIONAL PAIRS to the same target -- `jg X` immediately
    followed by `jle X`. Whatever the flags say, control reaches X, so the pair
    is an unconditional `jmp X`. Placing them back to back also desynchronises a
    linear sweep, because the bytes after them are junk that is never executed.
  * `xchg reg, reg` and `nop` filler between real instructions.
  * Overlapping instruction boundaries: the junk is chosen so that decoding from
    the wrong offset produces plausible-looking garbage.

Because the pattern is deterministic, following control flow rather than
sweeping linearly recovers the real code. This walks from an entry point,
collapses each junk construct, and prints only instructions that actually
execute.

Why it matters here: AuthServ's media-authentication handler is obfuscated this
way, and it is the last thing standing between the emulator and a decrypted
THUG2.exe. Reading it answers whether the check can be satisfied without the
physical disc. See the STATUS block in safedisc_emu.py.

Usage:
    python tools/diagnostics/safedisc_deobfuscate.py <image.bin> <base> <entry>
    python tools/diagnostics/safedisc_deobfuscate.py \\
        TestOutput/safedisc_temp/~de36b4.tmp.runtime.bin 0x10300000 0x10316AE0

The image is a FLAT MEMORY DUMP (file offset == RVA), which is what
safedisc_emu.py's --dump-temp-files writes for each loaded module.

Requires: pip install capstone
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

try:
    import capstone
except ImportError:  # pragma: no cover
    print("missing dependency: pip install capstone", file=sys.stderr)
    raise SystemExit(2) from None

# Conditional jumps, paired with their complement. A pair to the SAME target is
# unconditional by construction.
COMPLEMENTS = {
    "jo": "jno", "jno": "jo", "jb": "jae", "jae": "jb", "je": "jne", "jne": "je",
    "jbe": "ja", "ja": "jbe", "js": "jns", "jns": "js", "jp": "jnp", "jnp": "jp",
    "jl": "jge", "jge": "jl", "jle": "jg", "jg": "jle",
}
UNCONDITIONAL_END = {"ret", "retf", "iret", "iretd", "jmp"}


def is_filler(insn) -> bool:
    """Instructions that exist only to pad."""
    if insn.mnemonic == "nop":
        return True
    if insn.mnemonic == "xchg":
        parts = [p.strip() for p in insn.op_str.split(",")]
        return len(parts) == 2 and parts[0] == parts[1]
    if insn.mnemonic == "mov":
        parts = [p.strip() for p in insn.op_str.split(",")]
        return len(parts) == 2 and parts[0] == parts[1]
    return False


def branch_target(insn) -> int | None:
    try:
        return int(insn.op_str, 16)
    except ValueError:
        return None


class Linearizer:
    def __init__(self, image: bytes, base: int):
        self.image = image
        self.base = base
        self.md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
        self.seen: set[int] = set()
        self.junk_pairs = 0
        self.filler = 0

    def decode_at(self, address: int):
        offset = address - self.base
        if not (0 <= offset < len(self.image)):
            return None
        block = self.image[offset : offset + 16]
        return next(iter(self.md.disasm(block, address)), None)

    def walk(self, entry: int, limit: int = 4000) -> list:
        """Follow control flow, collapsing junk. Conditional branches are
        reported and NOT followed -- this recovers the straight-line body, which
        is what a human needs to read; each unfollowed target is listed so it can
        be walked separately."""
        out: list = []
        pending = [entry]
        while pending and len(out) < limit:
            address = pending.pop(0)
            while address is not None and len(out) < limit:
                if address in self.seen:
                    break
                self.seen.add(address)
                insn = self.decode_at(address)
                if insn is None:
                    break

                if is_filler(insn):
                    self.filler += 1
                    address = insn.address + insn.size
                    continue

                # Complementary pair to one target == unconditional jump.
                if insn.mnemonic in COMPLEMENTS:
                    following = self.decode_at(insn.address + insn.size)
                    target = branch_target(insn)
                    if (following is not None
                            and following.mnemonic == COMPLEMENTS[insn.mnemonic]
                            and branch_target(following) == target
                            and target is not None):
                        self.junk_pairs += 1
                        address = target
                        continue

                out.append(insn)

                if insn.mnemonic in UNCONDITIONAL_END:
                    if insn.mnemonic == "jmp":
                        target = branch_target(insn)
                        if target is not None:
                            address = target
                            continue
                    break

                if insn.mnemonic in COMPLEMENTS:
                    target = branch_target(insn)
                    if target is not None and target not in self.seen:
                        pending.append(target)

                address = insn.address + insn.size
        return out


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("image", type=Path, help="Flat memory dump (offset == RVA)")
    ap.add_argument("base", type=lambda v: int(v, 0), help="Load address of the dump")
    ap.add_argument("entry", type=lambda v: int(v, 0), help="Function to linearise")
    ap.add_argument("--limit", type=int, default=400, help="Max instructions to print")
    ap.add_argument("--calls-only", action="store_true",
                    help="Print only call/API sites, which is usually enough to see "
                         "WHAT a check measures without reading every instruction")
    args = ap.parse_args()

    image = args.image.read_bytes()
    linearizer = Linearizer(image, args.base)
    body = linearizer.walk(args.entry, limit=args.limit)

    print(f"linearised {len(body)} real instructions from 0x{args.entry:08X}")
    print(f"  collapsed {linearizer.junk_pairs} complementary junk-jump pairs "
          f"and {linearizer.filler} filler instructions")
    print()
    for insn in body:
        if args.calls_only and insn.mnemonic not in ("call", "int", "ret"):
            continue
        print(f"  0x{insn.address:08X}  {insn.mnemonic:<7} {insn.op_str}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
