#!/usr/bin/env python3
"""Dump per-batch preamble VIF commands in THAW-family .skin.ps2 files.

Each interleaved vertex batch (STCYCL(3,1) + V3_16 pos + V3_8 nrm + V4_16 uv/adc)
is preceded by "setup" VIF commands — small UNPACKs (V4_8/V3_8/V2_8) believed to
be bone-slot remap tables (VU matrix cache addresses, stride-7 = 7 qwords per
bone: 4x4 position matrix + 3x3 normal-ish rows). This tool walks the window
between consecutive batches, decodes every VIF code + payload, and prints the
table contents so the slot->global-bone mapping can be reverse engineered.

Also diffs P8 vs THPG preamble bytes when two files are given (same character).

Usage:
  python tools/diagnostics/thpg_preamble_dump.py <file.skin.ps2> [file2.skin.ps2] [--batches 0,1,2]
"""
import struct
import sys

CMD = {0: 'NOP', 1: 'STCYCL', 2: 'OFFSET', 3: 'BASE', 4: 'ITOP', 5: 'STMOD',
       6: 'MSKPATH3', 7: 'MARK', 0x10: 'FLUSHE', 0x11: 'FLUSH', 0x13: 'FLUSHA',
       0x14: 'MSCAL', 0x15: 'MSCALF', 0x17: 'MSCNT', 0x20: 'STMASK',
       0x30: 'STROW', 0x31: 'STCOL', 0x4A: 'MPG', 0x50: 'DIRECT', 0x51: 'DIRECTHL'}

UNPACK_FMT = {0x60: ('S_32', 4, 1), 0x61: ('S_16', 2, 1), 0x62: ('S_8', 1, 1),
              0x64: ('V2_32', 8, 2), 0x65: ('V2_16', 4, 2), 0x66: ('V2_8', 2, 2),
              0x68: ('V3_32', 12, 3), 0x69: ('V3_16', 6, 3), 0x6A: ('V3_8', 3, 3),
              0x6C: ('V4_32', 16, 4), 0x6D: ('V4_16', 8, 4), 0x6E: ('V4_8', 4, 4),
              0x6F: ('V4_5', 2, 4)}


def enumerate_batches(data):
    batches = []
    i = 0
    end = len(data) - 8
    while i < end:
        if data[i] == 3 and data[i + 1] == 1 and data[i + 3] == 0x01:
            j = i + 4
            if j + 4 <= len(data) and (data[j + 3] & 0x6F) == 0x69:
                n = data[j + 2] or 256
                pos_size = ((6 * n + 3) >> 2) << 2
                k = j + 4 + pos_size
                if k + 4 <= len(data) and (data[k + 3] & 0x6F) == 0x6A and (data[k + 2] or 256) == n:
                    nrm_size = ((3 * n + 3) >> 2) << 2
                    m = k + 4 + nrm_size
                    if m + 4 <= len(data) and (data[m + 3] & 0x6F) == 0x6D and (data[m + 2] or 256) == n:
                        uv_end = m + 4 + 8 * n
                        batches.append((n, i, uv_end))
                        i = uv_end
                        continue
        i += 4
    return batches


def walk_window(data, start, end):
    """Decode VIF codes in [start, end); returns list of (off, desc, payload_words)."""
    out = []
    i = (start + 3) & ~3
    while i + 4 <= end:
        imm = struct.unpack_from('<H', data, i)[0]
        num = data[i + 2]
        cmd = data[i + 3]
        base = cmd & 0x7F
        if base in UNPACK_FMT or (base & 0x60) == 0x60:
            name, size, comps = UNPACK_FMT.get(base & 0x6F, ('U?', 4, 4))
            n = num or 256
            total = ((size * n + 3) >> 2) << 2
            addr = imm & 0x3FF
            flg = 'FLG|' if imm & 0x8000 else ''
            usn = 'USN|' if imm & 0x4000 else ''
            payload = data[i + 4:i + 4 + total]
            out.append((i, f"UNPACK {name} num={n} addr=0x{addr:03X} {flg}{usn}", payload, base & 0x6F, n))
            i += 4 + total
        elif base == 0x4A:  # MPG
            n = (num or 256) * 8
            out.append((i, f"MPG num={num} loadaddr=0x{imm:04X} ({n}B ucode)", data[i + 4:i + 4 + n], None, 0))
            i += 4 + n
        elif base in (0x50, 0x51):
            n = (imm or 65536) * 16
            out.append((i, f"{CMD[base]} qwc={imm}", b'', None, 0))
            i += 4 + n
        else:
            name = CMD.get(base, f'0x{base:02X}?')
            out.append((i, f"{name} imm=0x{imm:04X} num={num}", b'', None, 0))
            i += 4
            if base == 0x30:
                i += 16
            elif base == 0x31:
                i += 16
            elif base == 0x20:
                i += 4
    return out


def fmt_payload(payload, fmt, n):
    if fmt == 0x6E:  # V4_8
        vals = [tuple(payload[k * 4 + c] for c in range(4)) for k in range(min(n, len(payload) // 4))]
        return ' '.join(f"({a},{b},{c},{d})" for a, b, c, d in vals)
    if fmt == 0x6A:  # V3_8
        vals = [tuple(payload[k * 3 + c] for c in range(3)) for k in range(min(n, len(payload) // 3))]
        return ' '.join(f"({a},{b},{c})" for a, b, c in vals)
    if fmt == 0x66:  # V2_8
        vals = [tuple(payload[k * 2 + c] for c in range(2)) for k in range(min(n, len(payload) // 2))]
        return ' '.join(f"({a},{b})" for a, b in vals)
    if fmt == 0x6D:  # V4_16
        m = min(n, len(payload) // 8)
        vals = [struct.unpack_from('<4h', payload, k * 8) for k in range(m)]
        return ' '.join(str(v) for v in vals)
    if fmt == 0x6C:  # V4_32
        m = min(n, len(payload) // 16)
        vals = [struct.unpack_from('<4f', payload, k * 16) for k in range(m)]
        return ' '.join(f"({a:.3f},{b:.3f},{c:.3f},{d:.3f})" for a, b, c, d in vals)
    return payload[:64].hex()


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    sel = None
    for a in sys.argv[1:]:
        if a.startswith('--batches'):
            sel = {int(x) for x in a.split('=', 1)[1].split(',')} if '=' in a else None
    files = [open(p, 'rb').read() for p in args]
    all_batches = [enumerate_batches(d) for d in files]
    print(f"batches: {[len(b) for b in all_batches]}")

    d = files[0]
    batches = all_batches[0]
    prev_end = 0
    for bi, (n, st, uv_end) in enumerate(batches):
        if sel is not None and bi not in sel:
            prev_end = uv_end
            continue
        print(f"\n=== batch {bi} (n={n}) preamble [0x{prev_end:05X}..0x{st:05X}) len={st - prev_end} ===")
        for off, desc, payload, fmt, num in walk_window(d, prev_end, st):
            line = f"  0x{off:05X}: {desc}"
            if payload and fmt is not None:
                line += "\n           " + fmt_payload(payload, fmt, num)
            print(line)
        if len(files) > 1:
            d2 = files[1]
            b2 = all_batches[1]
            if bi < len(b2):
                _, st2, _ = b2[bi]
                prev2 = b2[bi - 1][2] if bi > 0 else 0
                same = d[prev_end:st] == d2[prev2:st2]
                print(f"  [P8 vs THPG preamble bytes: {'IDENTICAL' if same else 'DIFFER'}]")
        prev_end = uv_end


if __name__ == "__main__":
    main()
