#!/usr/bin/env python3
"""
SafeDisc 3.20.22 obfuscated-string decryptor.

Reverse-engineered statically from the SecServ DLL that safedisc_emu.py drops as
`~df394b.tmp` (THUG2 / SafeDisc 3.20.22, preferred ImageBase 0x66700000).

The engine routine lives at RVA 0x1bf21 (VA 0x6671bf21):

    unsigned short DecryptString(char *dst /*[ebp+8]*/, const char *src /*[ebp+0xc]*/)

with the seed supplied by the tiny accessor at RVA 0x1bf03 (VA 0x6671bf03), which
returns the literal 0xC612DB4E (immediate stored at file offset 0x1a913).

Algorithm (byte stream, NUL-terminated, hard cap 0x80 bytes):

    key = 0xC612DB4E
    for i in 0..0x7f:
        plain[i] = cipher[i] ^ (key & 0xFF)         # helper at RVA 0x1bf82
        key = (0xA065432A - 0x22BC897F * key) & 0xFFFFFFFF   # RVA 0x1bf4a / 0x1bf54
        if plain[i] == 0: return SUCCESS
    return FAILURE          # no NUL inside 128 bytes

Note the recurrence constants match the published SafeDisc 3.x literature, but the
SEED IN THIS BUILD IS 0xC612DB4E -- the widely-quoted 0x522CFDD0 does not appear
anywhere in this image.

Verified: recovers 'drvmgt.dll', 'secdrv.sys', 'SecDrv04.VxD', '\\\\.\\NTICE',
'\\\\.\\SICE', 'IsDebuggerPresent', 'ZwQuerySystemInformation', etc.

Usage:
    python safedisc_string_decrypt.py <pe-file> [--seed 0xC612DB4E] [--min-len 8]
    python safedisc_string_decrypt.py <pe-file> --at 0xad240      # single RVA
"""

import argparse
import string
import sys

MASK = 0xFFFFFFFF
DEFAULT_SEED = 0xC612DB4E
ADD_CONST = 0xA065432A
MUL_CONST = 0x22BC897F
MAX_LEN = 0x80

PRINTABLE = set(bytes(string.printable[:-5], "ascii"))


def decrypt(buf, seed=DEFAULT_SEED, max_len=MAX_LEN):
    """Decrypt one NUL-terminated SafeDisc string. Returns bytes, or None if no
    NUL terminator appears within max_len (the engine treats that as failure)."""
    key = seed
    out = bytearray()
    for i in range(min(max_len, len(buf))):
        b = buf[i] ^ (key & 0xFF)
        key = (ADD_CONST - MUL_CONST * key) & MASK
        if b == 0:
            return bytes(out)
        out.append(b)
    return None


def keystream(n, seed=DEFAULT_SEED):
    """First n keystream bytes -- handy for encrypting or for spot checks."""
    key = seed
    ks = bytearray()
    for _ in range(n):
        ks.append(key & 0xFF)
        key = (ADD_CONST - MUL_CONST * key) & MASK
    return bytes(ks)


def _sections(pe):
    for s in pe.sections:
        yield s.Name.decode(errors="replace").rstrip("\0"), s


def main():
    ap = argparse.ArgumentParser(description="Decrypt SafeDisc 3.x obfuscated strings")
    ap.add_argument("pe")
    ap.add_argument("--seed", type=lambda v: int(v, 0), default=DEFAULT_SEED)
    ap.add_argument("--min-len", type=int, default=8)
    ap.add_argument("--at", type=lambda v: int(v, 0), default=None,
                    help="decrypt a single RVA instead of sweeping")
    ap.add_argument("--sections", default=".rdata,.data,.text,.txt2")
    args = ap.parse_args()

    try:
        import pefile
    except ImportError:
        print("pefile required: pip install pefile", file=sys.stderr)
        return 2

    pe = pefile.PE(args.pe)
    data = pe.__data__

    if args.at is not None:
        for _, s in _sections(pe):
            if s.VirtualAddress <= args.at < s.VirtualAddress + max(s.Misc_VirtualSize, s.SizeOfRawData):
                off = s.PointerToRawData + (args.at - s.VirtualAddress)
                r = decrypt(bytes(data[off:off + MAX_LEN]), args.seed)
                print(repr(r.decode("latin1")) if r else "<no NUL within 128 bytes>")
                return 0
        print("RVA not mapped", file=sys.stderr)
        return 1

    want = {x.strip() for x in args.sections.split(",")}
    found = []
    for name, s in _sections(pe):
        if name not in want:
            continue
        base = s.PointerToRawData
        raw = bytes(data[base:base + s.SizeOfRawData])
        for o in range(len(raw) - args.min_len):
            r = decrypt(raw[o:o + MAX_LEN], args.seed)
            if r and len(r) >= args.min_len and all(c in PRINTABLE for c in r):
                found.append((name, s.VirtualAddress + o, r))

    found.sort(key=lambda x: x[1])
    kept, last = [], -1
    for name, rva, r in found:
        if rva > last:
            kept.append((name, rva, r))
            last = rva + len(r)

    print(f"{len(kept)} strings recovered (seed 0x{args.seed:08X})")
    for name, rva, r in kept:
        print(f"  {name:<7} RVA {rva:08x}  {r.decode('latin1')!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
