# thaw_qb_probe.py — structural walker for THAW-generation sectioned QB files
# (.qb.ps2 / .qb.wpc little-endian "old" info encoding, .qb.ngc big-endian "new"
# GH-style encoding). Parses the 28-byte header, walks sections/structs/arrays,
# LZSS-decompresses script sections, and censuses the token bytes of decompressed
# script bodies against the THPS3-THUG2 raw token grammar (tokens.h, 0-68).
#
# Usage:
#   python thaw_qb_probe.py <file.qb.ps2|.qb.wpc|.qb.ngc> [-v]      # walk one file
#   python thaw_qb_probe.py --batch <dir> [pattern]                  # sweep a tree
#   python thaw_qb_probe.py --script-census <dir> [pattern]          # token census of all script bodies
#
# Reference: Sample/queen-bee/QueenBeeParser (Nanook) — PakFormat.cs type tables,
# QbItemBase.cs section/item layouts, Lzss.cs (4096-window LZSS).

import struct
import sys
from pathlib import Path

HEADER_SIG = bytes.fromhex("1c0802041004080c0c080204140204 0c10100c00".replace(" ", ""))

# QbItemType value tables (Queen-Bee PakFormat.cs). new = Wii/GH-era, old = THAW PS2/WPC/XBX.
TYPES = {
    "SectionInteger": (0x00200100, 0x00010400),
    "SectionFloat": (0x00200200, 0x00020400),
    "SectionString": (0x00200300, 0x00030400),
    "SectionStringW": (0x00200400, 0x00040400),
    "SectionFloatsX2": (0x00200500, 0x00050400),
    "SectionFloatsX3": (0x00200600, 0x00060400),
    "SectionScript": (0x00200700, 0x00070400),
    "SectionStruct": (0x00200A00, 0x000A0400),
    "SectionArray": (0x00200C00, 0x000C0400),
    "SectionQbKey": (0x00200D00, 0x000D0400),
    "SectionQbKeyString": (0x00201A00, 0x00041A00),
    "SectionStringPointer": (0x00201B00, 0x001A0400),
    "SectionQbKeyStringQs": (0x00201C00, 0x001C0400),
    "ArrayInteger": (0x00010100, 0x00010100),
    "ArrayFloat": (0x00010200, 0x00020100),
    "ArrayString": (0x00010300, 0x00030100),
    "ArrayStringW": (0x00010400, 0x00040100),
    "ArrayFloatsX2": (0x00010500, 0x00050100),
    "ArrayFloatsX3": (0x00010600, 0x00060100),
    "ArrayStruct": (0x00010A00, 0x000A0100),
    "ArrayArray": (0x00010C00, 0x000C0100),
    "ArrayQbKey": (0x00010D00, 0x000D0100),
    "ArrayQbKeyString": (0x00011A00, 0x001A0100),
    "ArrayStringPointer": (0x00011B00, 0x001B0100),
    "ArrayQbKeyStringQs": (0x00011C00, 0x001C0100),
    "StructItemInteger": (0x00810000, 0x00000300),
    "StructItemFloat": (0x00820000, 0x00000500),
    "StructItemString": (0x00830000, 0x00000700),
    "StructItemStringW": (0x00840000, 0x00000900),
    "StructItemFloatsX2": (0x00850000, 0x00000B00),
    "StructItemFloatsX3": (0x00860000, 0x00000D00),
    "StructItemStruct": (0x008A0000, 0x00001500),
    "StructItemArray": (0x008C0000, 0x00001900),
    "StructItemQbKey": (0x008D0000, 0x00001B00),
    "StructItemQbKeyString": (0x009A0000, 0x00003500),
    "StructItemQbKeyStringQs": (0x009C0000, 0x001A0400),
    "Floats": (0x00010000, 0x00000100),
    "StructHeader": (0x00000100, 0x00010000),
}

SECTIONS_COMPLEX = {"SectionString", "SectionStringW", "SectionArray", "SectionStruct",
                    "SectionScript", "SectionFloatsX2", "SectionFloatsX3"}
STRUCT_COMPLEX = {"StructItemArray", "StructItemFloatsX2", "StructItemFloatsX3",
                  "StructItemString", "StructItemStringW", "StructItemStruct"}
ARRAY_COMPLEX = {"ArrayArray", "ArrayString", "ArrayStringW", "ArrayStruct",
                 "ArrayFloatsX2", "ArrayFloatsX3"}

# THPS3-THUG2 token sizes (skiptoken.cpp): fixed sizes; -1 = variable
TOKEN_SIZES = {0: 1}
for t in list(range(1, 2)) + list(range(3, 22)) + [29, 32, 33, 34, 35, 36, 37, 38, 39, 40, 41, 42, 44, 45] + \
         list(range(48, 55)) + [56, 57, 58, 59, 60, 61, 62, 63, 66, 49]:
    TOKEN_SIZES[t] = 1
for t in [2, 22, 23, 24, 25, 26, 46, 67, 68]:
    TOKEN_SIZES[t] = 5
TOKEN_SIZES[30] = 13  # vector
TOKEN_SIZES[31] = 9   # pair
# variable: 27/28 string, 43 checksum-name, 47/55/64/65 random


def lzss_decompress(data: bytes) -> bytes:
    N, F, THRESHOLD = 4096, 18, 2
    buf = bytearray(b"\x20" * (N - F)) + bytearray(F)
    r = N - F
    out = bytearray()
    flags = 0
    pos = 0
    while pos < len(data):
        flags >>= 1
        if (flags & 256) == 0:
            flags = data[pos] | 0xFF00
            pos += 1
        if flags & 1:
            if pos >= len(data):
                break
            c = data[pos]; pos += 1
            out.append(c)
            buf[r] = c
            r = (r + 1) & (N - 1)
        else:
            if pos + 1 >= len(data):
                break
            i = data[pos]; j = data[pos + 1]; pos += 2
            i |= (j & 0xF0) << 4
            j = (j & 0x0F) + THRESHOLD
            for k in range(j + 1):
                c = buf[(i + k) & (N - 1)]
                out.append(c)
                buf[r] = c
                r = (r + 1) & (N - 1)
    return bytes(out)


class QbWalker:
    def __init__(self, data: bytes, verbose=False):
        self.data = data
        self.verbose = verbose
        self.scripts = []  # (key, decompressed_bytes)
        self.section_counts = {}
        if data[8:28] != HEADER_SIG:
            raise ValueError("no 28-byte sectioned-QB header signature")
        size_le = struct.unpack_from("<I", data, 4)[0]
        size_be = struct.unpack_from(">I", data, 4)[0]
        if 28 <= size_le <= len(data):
            self.be, self.file_size = False, size_le
        elif 28 <= size_be <= len(data):
            self.be, self.file_size = True, size_be
        else:
            raise ValueError(f"implausible fileSize (le={size_le:#x} be={size_be:#x} len={len(data):#x})")
        self.fmt = ">I" if self.be else "<I"
        # info-encoding mode: try the first section value in both columns
        first = self.u32(28)
        self.new_mode = None
        for name, (new_v, old_v) in TYPES.items():
            if not name.startswith("Section"):
                continue
            if first == new_v:
                self.new_mode = True
            elif first == old_v:
                self.new_mode = False
        if self.new_mode is None:
            raise ValueError(f"first section value {first:#010x} not a known section type")
        self.col = 0 if self.new_mode else 1
        self.by_value = {}
        for name, vals in TYPES.items():
            self.by_value.setdefault(vals[self.col], name)

    def u32(self, off):
        return struct.unpack_from(self.fmt, self.data, off)[0]

    def f32(self, off):
        return struct.unpack_from(">f" if self.be else "<f", self.data, off)[0]

    def type_at(self, off):
        v = self.u32(off)
        return self.by_value.get(v), v

    def log(self, depth, msg):
        if self.verbose:
            print("  " * depth + msg)

    def walk(self):
        pos = 28
        while pos < self.file_size:
            t, v = self.type_at(pos)
            if t is None or not t.startswith("Section"):
                raise ValueError(f"@{pos:#x}: unknown section value {v:#010x}")
            self.section_counts[t] = self.section_counts.get(t, 0) + 1
            key = self.u32(pos + 4)
            self.log(0, f"@{pos:#x} {t} key={key:08x}")
            if t in SECTIONS_COMPLEX:
                ptr = self.u32(pos + 8 + 4)
                if ptr != pos + 20:
                    raise ValueError(f"@{pos:#x}: section pointer {ptr:#x} != {pos + 20:#x}")
                pos = self.walk_complex(t.replace("Section", ""), ptr, key, 1)
            else:
                pos = pos + 20  # info, key, fileId, value, reserved
        return pos

    def walk_complex(self, kind, pos, key, depth):
        if kind == "Script":
            unk = self.u32(pos)
            decomp_size = self.u32(pos + 4)
            comp_size = self.u32(pos + 8)
            blob = self.data[pos + 12: pos + 12 + comp_size]
            body = lzss_decompress(blob) if comp_size < decomp_size else bytes(blob)
            if len(body) != decomp_size:
                raise ValueError(f"@{pos:#x}: script decompressed {len(body)} != {decomp_size}")
            self.scripts.append((key, body))
            self.log(depth, f"script unk={unk:08x} decomp={decomp_size} comp={comp_size}")
            end = pos + 12 + comp_size
            return (end + 3) & ~3
        if kind in ("Struct", "ItemStruct", "Header"):
            return self.walk_struct(pos, depth, kind != "Header")
        if kind == "Array" or kind == "ItemArray":
            return self.walk_array(pos, depth)
        if kind in ("String", "StringW", "ItemString", "ItemStringW"):
            return self.walk_string_to_align(pos, depth)
        if kind in ("FloatsX2", "FloatsX3", "ItemFloatsX2", "ItemFloatsX3"):
            n = 3 if kind.endswith("X3") else 2
            t, v = self.type_at(pos)
            if t != "Floats":
                raise ValueError(f"@{pos:#x}: expected Floats marker, got {v:#010x}")
            vals = [self.f32(pos + 4 + 4 * i) for i in range(n)]
            self.log(depth, f"floats {vals}")
            return pos + 4 + 4 * n
        raise ValueError(f"unhandled complex kind {kind}")

    def walk_struct(self, pos, depth, has_marker):
        if has_marker:
            t, v = self.type_at(pos)
            if t != "StructHeader":
                raise ValueError(f"@{pos:#x}: expected StructHeader, got {v:#010x}")
            pos += 4
        ptr = self.u32(pos)
        pos += 4
        end = pos
        if ptr != 0 and ptr != pos:
            raise ValueError(f"@{pos - 4:#x}: struct first-item ptr {ptr:#x} != {pos:#x}")
        while ptr != 0:
            t, v = self.type_at(ptr)
            if t is None:
                raise ValueError(f"@{ptr:#x}: unknown struct item value {v:#010x}")
            key = self.u32(ptr + 4)
            if t in STRUCT_COMPLEX or t in ARRAY_COMPLEX:
                dptr = self.u32(ptr + 8)
                nxt = self.u32(ptr + 12)
                if dptr != ptr + 16:
                    raise ValueError(f"@{ptr:#x}: struct item data ptr {dptr:#x} != {ptr + 16:#x}")
                self.log(depth, f"@{ptr:#x} {t} key={key:08x}")
                # normalise: ArrayXxx encoded directly under struct behaves like StructItemXxx
                if t.startswith("Array"):
                    kind = "ItemArray" if t == "ArrayArray" else \
                        ("ItemStruct" if t == "ArrayStruct" else "Item" + t[5:])
                else:
                    kind = t.replace("StructItem", "Item")
                end = self.walk_complex(kind, ptr + 16, key, depth + 1)
            else:
                val = self.u32(ptr + 8)
                nxt = self.u32(ptr + 12)
                self.log(depth, f"@{ptr:#x} {t} key={key:08x} val={val:#010x}")
                end = ptr + 16
            ptr = nxt
        return end

    def walk_array(self, pos, depth):
        t, v = self.type_at(pos)
        if t is None or not t.startswith(("Array", "Floats", "StructHeader")):
            raise ValueError(f"@{pos:#x}: unknown array element type {v:#010x}")
        count = self.u32(pos + 4)
        self.log(depth, f"@{pos:#x} array<{t}> n={count}")
        simple = t in ("ArrayInteger", "ArrayFloat", "ArrayQbKey", "ArrayQbKeyString",
                       "ArrayStringPointer", "ArrayQbKeyStringQs")
        if simple:
            if count == 0:
                return pos + 8  # type + count only
            if count == 1:
                return pos + 12  # type + count + inline value
            return pos + 12 + 4 * count  # type + count + ptr + values
        # complex arrays
        ptr_pos = pos + 8
        first_ptr = self.u32(ptr_pos)
        if count == 0:
            return pos + 12
        if count == 1:
            elem_pos = first_ptr
            if elem_pos != ptr_pos + 4:
                raise ValueError(f"@{pos:#x}: array elem ptr {elem_pos:#x} != {ptr_pos + 4:#x}")
            ptrs = [elem_pos]
        else:
            list_pos = first_ptr
            if list_pos != ptr_pos + 4:
                raise ValueError(f"@{pos:#x}: array ptr-list {list_pos:#x} != {ptr_pos + 4:#x}")
            ptrs = [self.u32(list_pos + 4 * i) for i in range(count)]
        end = ptrs[0] if count else pos + 12
        for p in ptrs:
            if t == "ArrayStruct":
                end = self.walk_struct(p, depth + 1, True)
            elif t == "ArrayArray":
                end = self.walk_array(p, depth + 1)
            elif t in ("ArrayString", "ArrayStringW"):
                end = self.walk_string_to_align(p, depth + 1, terminate_aligned=(p == ptrs[-1]))
            elif t in ("ArrayFloatsX2", "ArrayFloatsX3"):
                n = 3 if t.endswith("X3") else 2
                end = p + 4 + 4 * n
        return end

    def walk_string_to_align(self, pos, depth, terminate_aligned=True):
        nul = self.data.index(b"\x00", pos)
        s = self.data[pos:nul]
        self.log(depth, f'@{pos:#x} str "{s[:60].decode("latin1")}"')
        end = nul + 1
        return (end + 3) & ~3 if terminate_aligned else end


def census_tokens(body: bytes):
    """Walk a decompressed script body with THPS3-THUG2 token sizes; return
    (clean, tokens_seen, fail_offset)."""
    pos = 0
    seen = {}
    while pos < len(body):
        t = body[pos]
        seen[t] = seen.get(t, 0) + 1
        if t in TOKEN_SIZES:
            pos += TOKEN_SIZES[t]
        elif t in (27, 28):  # string: u32 len + data (endianness matters!)
            le = struct.unpack_from("<I", body, pos + 1)[0] if pos + 5 <= len(body) else 1 << 30
            be = struct.unpack_from(">I", body, pos + 1)[0] if pos + 5 <= len(body) else 1 << 30
            ln = min(le, be)
            if ln > 100000:
                return False, seen, pos
            pos += 5 + ln
        elif t == 43:  # checksum name
            nul = body.find(b"\x00", pos + 5)
            if nul < 0:
                return False, seen, pos
            pos = nul + 1
        elif t in (47, 55, 64, 65):  # random: u32 count + 2n + 4n
            le = struct.unpack_from("<I", body, pos + 1)[0] if pos + 5 <= len(body) else 1 << 30
            be = struct.unpack_from(">I", body, pos + 1)[0] if pos + 5 <= len(body) else 1 << 30
            n = min(le, be)
            if n > 10000:
                return False, seen, pos
            pos += 5 + 6 * n
        else:
            return False, seen, pos
    return True, seen, None


def main():
    args = sys.argv[1:]
    if not args:
        print(__doc__)
        return
    if args[0] == "--batch" or args[0] == "--script-census":
        mode = args[0]
        root = Path(args[1])
        pattern = args[2] if len(args) > 2 else "*.qb.*"
        files = sorted(root.rglob(pattern))
        ok = bad = nosig = 0
        errors = {}
        all_tokens = {}
        script_ok = script_bad = 0
        bad_examples = []
        for f in files:
            if not f.is_file():
                continue
            data = f.read_bytes()
            if len(data) < 28 or data[8:28] != HEADER_SIG:
                nosig += 1
                continue
            try:
                w = QbWalker(data)
                w.walk()
                ok += 1
                if mode == "--script-census":
                    for key, body in w.scripts:
                        clean, seen, fail = census_tokens(body)
                        if clean:
                            script_ok += 1
                            for t, n in seen.items():
                                all_tokens[t] = all_tokens.get(t, 0) + n
                        else:
                            script_bad += 1
                            if len(bad_examples) < 8:
                                bad_examples.append((f.name, key, fail, body[max(0, fail-8):fail+8].hex()))
            except Exception as e:
                bad += 1
                msg = str(e)[:90]
                errors[msg] = errors.get(msg, 0) + 1
                if len(errors) <= 6 and errors[msg] == 1:
                    print(f"FAIL {f.name}: {msg}")
        print(f"\nsig-files parsed OK: {ok}, failed: {bad}, no-sig skipped: {nosig}")
        for msg, n in sorted(errors.items(), key=lambda kv: -kv[1])[:12]:
            print(f"  {n:5d}x {msg}")
        if mode == "--script-census":
            print(f"scripts token-clean: {script_ok}, failed: {script_bad}")
            print("token histogram:", {f"{t:#04x}": n for t, n in sorted(all_tokens.items())})
            for ex in bad_examples:
                print("  bad:", ex)
        return

    verbose = "-v" in args
    f = Path(args[0])
    data = f.read_bytes()
    w = QbWalker(data, verbose=True)
    end = w.walk()
    print(f"\nendian={'BE' if w.be else 'LE'} mode={'new' if w.new_mode else 'old'} "
          f"fileSize={w.file_size:#x} end={end:#x} sections={w.section_counts}")
    for key, body in w.scripts:
        clean, seen, fail = census_tokens(body)
        print(f"script {key:08x}: {len(body)} bytes, token-clean={clean}"
              + (f" fail@{fail} ctx={body[max(0,fail-8):fail+8].hex()}" if not clean else ""))
        if verbose:
            print("  tokens:", {f"{t:#04x}": n for t, n in sorted(seen.items())})


if __name__ == "__main__":
    main()
