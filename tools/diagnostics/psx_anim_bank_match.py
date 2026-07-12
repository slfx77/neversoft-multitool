#!/usr/bin/env python3
"""Match animation clips between two PSX v2 (0x2C) anim banks byte-wise.

Used to test "character A shares animations with character B" claims (e.g.
spidey.psx vs daredevl.psx in Spider-Man PSX Final): slices each bank's
per-entry compressed stream (entry table: numStreams u32 + N x {poolOff u32,
frames u16, tween u16}, offsets relative to chunk data start) and reports
exact-hash matches plus frame-count/near-miss diagnostics.

Usage:
  python tools/diagnostics/psx_anim_bank_match.py <a.psx> <b.psx>
"""
import hashlib
import struct
import sys


def find_anim_chunk(data):
    meta_top = struct.unpack_from('<i', data, 4)[0]
    cursor, found = meta_top, None
    for _ in range(256):
        if cursor + 8 > len(data):
            break
        tag, size = struct.unpack_from('<II', data, cursor)
        if tag == 0xFFFFFFFF:
            break
        if cursor + 8 + size > len(data):
            break
        if tag in (0x2A, 0x2C):
            found = (tag, cursor + 8)
        cursor += 8 + size
    return found


def parse_bank(path):
    data = open(path, 'rb').read()
    tag, base = find_anim_chunk(data)
    n = struct.unpack_from('<I', data, base)[0]
    entries = []
    for i in range(n):
        off, frames, tween = struct.unpack_from('<IHH', data, base + 4 + i * 8)
        entries.append((off, frames, tween))
    # Stream extents: from each offset to the next-larger offset in the pool.
    offs = sorted({e[0] for e in entries})
    ends = {o: (offs[i + 1] if i + 1 < len(offs) else len(data) - base)
            for i, o in enumerate(offs)}
    out = []
    for off, frames, tween in entries:
        blob = data[base + off: base + ends[off]]
        out.append({'off': off, 'frames': frames, 'tween': tween,
                    'len': len(blob), 'sha': hashlib.sha1(blob).hexdigest()[:12],
                    'blob': blob})
    return tag, out


def main():
    a_path, b_path = sys.argv[1], sys.argv[2]
    tag_a, a = parse_bank(a_path)
    tag_b, b = parse_bank(b_path)
    print(f"A={a_path}: chunk=0x{tag_a:02X} entries={len(a)}")
    print(f"B={b_path}: chunk=0x{tag_b:02X} entries={len(b)}")

    by_sha = {}
    for i, e in enumerate(a):
        by_sha.setdefault(e['sha'], []).append(i)
    exact = prefix = miss = 0
    for j, e in enumerate(b):
        hit = by_sha.get(e['sha'])
        if hit:
            exact += 1
            print(f"  B[{j:>3}] == A[{hit[0]:>3}]  frames={e['frames']:>3} "
                  f"len={e['len']:>6}  EXACT")
            continue
        # prefix match: same leading bytes (stream extent may differ by padding)
        best, best_n = None, 0
        for i, ea in enumerate(a):
            n = 0
            for x, y in zip(e['blob'], ea['blob']):
                if x != y:
                    break
                n += 1
            if n > best_n:
                best, best_n = i, n
        frac = best_n / max(1, e['len'])
        if frac > 0.95:
            prefix += 1
            print(f"  B[{j:>3}] ~= A[{best:>3}]  frames={e['frames']:>3} "
                  f"prefix={best_n}/{e['len']} ({frac:.0%})")
        else:
            miss += 1
            print(f"  B[{j:>3}] NO MATCH  frames={e['frames']:>3} len={e['len']:>6} "
                  f"best A[{best}] prefix={best_n} ({frac:.0%})")
    print(f"\nexact={exact} prefix={prefix} miss={miss} of {len(b)}")


if __name__ == '__main__':
    main()
