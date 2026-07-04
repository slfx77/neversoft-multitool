"""Compare the VIF command stream of the SAME character skin across two games.

Project 8's gped_bam.skin.ps2 decodes correctly; Proving Ground's copy of the
same character (identical (1,9,9) header, identical file size) garbles. This
walks both VIF streams and prints them side-relevant so the structural
divergence (STCYCL layout, UNPACK formats, batch counts) is visible.

Usage:
    python thpg_vif_compare.py <p8_skin.ps2> <thpg_skin.ps2>

Reuses the opcode walker from thaw_ps2_vif_walk.py.
"""
import os
import struct
import sys

_HERE = os.path.dirname(__file__)
sys.path.insert(0, _HERE)
from thaw_ps2_vif_walk import vif_next  # noqa: E402

NAMES = {(0, 0): 'S_32', (1, 0): 'V2_32', (1, 1): 'V2_16', (2, 0): 'V3_32',
         (2, 1): 'V3_16', (2, 2): 'V3_8', (3, 0): 'V4_32', (3, 1): 'V4_16', (3, 2): 'V4_8'}
CMD = {0: 'NOP', 1: 'STCYCL', 2: 'OFFSET', 3: 'BASE', 4: 'ITOP', 5: 'STMOD', 6: 'MSKPATH3',
       7: 'MARK', 0x10: 'FLUSHE', 0x11: 'FLUSH', 0x13: 'FLUSHA', 0x14: 'MSCAL', 0x15: 'MSCALF',
       0x17: 'MSCNT', 0x20: 'STMASK', 0x30: 'STROW', 0x31: 'STCOL', 0x4A: 'MPG',
       0x50: 'DIRECT', 0x51: 'DIRECTHL'}


def tokens(path):
    """Return a list of compact opcode tokens for the VIF stream (relative offsets)."""
    with open(path, 'rb') as f:
        data = f.read()
    n_obj, _tm1, tm2, _dsize = struct.unpack_from('<4I', data, 0)
    entry_end = 32 + n_obj * 8 + tm2 * 64
    vif_start = None
    for i in range(entry_end, min(entry_end + 0x4000, len(data) - 3), 4):
        if (data[i + 3] & 0x7F) in (0x10, 0x11):
            vif_start = i
            break
    if vif_start is None:
        return [], [], data, None
    out = []
    offs = []
    pos, end, count = vif_start, len(data), 0
    while pos < end and pos + 4 <= len(data) and count < 20000:
        cmd = data[pos + 3]
        c = cmd & 0x7F
        rel = pos - vif_start
        if (cmd & 0x60) == 0x60:
            vn, vl = (cmd >> 2) & 3, cmd & 3
            num = data[pos + 2] or 256
            out.append(f"UNPACK {NAMES.get((vn, vl), f'vn{vn}vl{vl}')} num={num}")
        elif c == 0x01:
            out.append(f"STCYCL CL={data[pos]} WL={data[pos + 1]}")
        elif c in (0x50, 0x51):
            out.append(f"{CMD[c]} {struct.unpack_from('<H', data, pos)[0]}QW")
        elif c in CMD:
            out.append(CMD[c])
        else:
            out.append(f"UNK 0x{c:02X}")
            pos += 8
            count += 1
            continue
        offs.append(pos)
        nxt = vif_next(data, pos, end)
        if nxt <= pos:
            break
        pos = nxt
        count += 1
    return out, offs, data, vif_start


def main():
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    a_tok, a_off, _a, a_start = tokens(sys.argv[1])
    b_tok, b_off, _b, b_start = tokens(sys.argv[2])
    print(f"A (good) {os.path.basename(sys.argv[1])}: vif@0x{a_start:X}, {len(a_tok)} opcodes")
    print(f"B (bad)  {os.path.basename(sys.argv[2])}: vif@0x{b_start:X}, {len(b_tok)} opcodes")
    print("=" * 70)
    # aligned line-by-line diff
    n = max(len(a_tok), len(b_tok))
    first_div = None
    for i in range(n):
        a = a_tok[i] if i < len(a_tok) else "--"
        b = b_tok[i] if i < len(b_tok) else "--"
        mark = "" if a == b else "   <<< DIFF"
        if a != b and first_div is None:
            first_div = i
        # print a window around divergences + the head
        if i < 30 or (first_div is not None and abs(i - first_div) < 20):
            print(f"[{i:4}] {a:28} | {b:28}{mark}")
    print("=" * 70)
    print(f"First divergence at opcode index: {first_div}")
    if first_div is not None:
        ao = a_off[first_div] if first_div < len(a_off) else -1
        bo = b_off[first_div] if first_div < len(b_off) else -1
        print(f"  A byte offset: 0x{ao:X}   B byte offset: 0x{bo:X}")
        print(f"  prev opcode (A[{first_div-1}]): {a_tok[first_div-1]} @0x{a_off[first_div-1]:X}")
    # summary counts
    from collections import Counter

    def summarize(tok):
        c = Counter(t.split(' num=')[0] for t in tok)
        return dict(sorted(c.items()))

    print(f"A opcode histogram: {summarize(a_tok)}")
    print(f"B opcode histogram: {summarize(b_tok)}")


if __name__ == '__main__':
    main()
