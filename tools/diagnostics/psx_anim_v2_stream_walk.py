#!/usr/bin/env python3
"""Hand-walk a PSX v2 (0x2C) compressed anim entry stream-by-stream.

Python port of PsxAnimDecompressor (itself a byte-certified port of the THPS2
proto DECOMP.cpp DecompressStream). Decodes every bone's 6 channels for one
anim entry and prints per-stream structure (header seg/mode, bytes consumed)
plus decoded samples for a chosen bone — used to distinguish on-disc pose
snaps from stream mis-slicing when a clip shows single-frame pops.

Usage:
  python tools/diagnostics/psx_anim_v2_stream_walk.py <file.psx> --anim 89 \
      [--bones 18] [--show-bone 0]
"""
import argparse
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from psx_anim_bank_match import parse_bank  # noqa: E402


def read_s16(b, i):
    v = b[i] | (b[i + 1] << 8)
    return v - 0x10000 if v & 0x8000 else v


def read_signed_bits(b, byte_idx, bit_off, width):
    window = (b[byte_idx] << 16) | (b[byte_idx + 1] << 8) | b[byte_idx + 2]
    shifted = ((window << (bit_off + 8)) & 0xFFFFFFFF) >> (32 - width)
    if shifted & (1 << (width - 1)):
        shifted |= -1 << width
    else:
        shifted &= (1 << width) - 1
    nxt = bit_off + width
    return shifted, byte_idx + (nxt >> 3), nxt & 7


def s16(v):
    v &= 0xFFFF
    return v - 0x10000 if v & 0x8000 else v


def decompress(b, start, n_frames):
    """Returns (samples, bytes_consumed, header_desc)."""
    i = start
    header = b[i]
    i += 1
    num_seg = (header >> 4) + 1
    mode = header & 0xF
    if num_seg >= 2:
        seg_len = (n_frames - 1) // num_seg
        remainder = n_frames - (seg_len * num_seg + 1)
    else:
        seg_len = n_frames - 1
        remainder = 0
    out = []
    desc = f"seg={num_seg} mode={mode:>2} segLen={seg_len} rem={remainder}"

    if mode == 15:
        return [0] * n_frames, i - start, desc
    if mode == 14:
        v = read_s16(b, i)
        return [v] * n_frames, i + 2 - start, desc
    if mode == 0:
        prev = read_s16(b, i)
        i += 2
        out.append(prev)
        for _ in range(seg_len):
            end = read_s16(b, i)
            i += 2
            delta = end - prev
            for _ in range(num_seg - 1):
                prev = s16(prev + s16(int(delta / num_seg)))
                out.append(prev)
            prev = end
            out.append(prev)
        if remainder > 0:
            end = read_s16(b, i)
            i += 2
            delta = end - prev
            for _ in range(remainder - 1):
                prev = s16(prev + s16(int(delta / remainder)))
                out.append(prev)
            out.append(end)
        return out, i - start, desc

    # modes 1..13: bit-packed deltas. Engine contract: the segment endpoint is
    # start + delta computed BEFORE the interp writes (matches the fixed
    # PsxAnimDecompressor, DECOMP.cpp 0x80023B38).
    width = mode + 1
    prev = read_s16(b, i)
    i += 2
    out.append(prev)
    bit_off = 0
    for _ in range(seg_len):
        delta, i, bit_off = read_signed_bits(b, i, bit_off, width)
        step = s16(int(delta / num_seg))
        end = s16(prev + delta)
        for _ in range(num_seg - 1):
            prev += step
            out.append(s16(prev))
        prev = end
        out.append(prev)
    if remainder > 0:
        delta, i, bit_off = read_signed_bits(b, i, bit_off, width)
        step = s16(int(delta / remainder))
        end = s16(prev + delta)
        for _ in range(remainder - 1):
            prev += step
            out.append(s16(prev))
        out.append(end)
    if bit_off != 0:
        i += 1
    return out, i - start, desc


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('psx')
    ap.add_argument('--anim', type=int, required=True)
    ap.add_argument('--bones', type=int, default=None,
                    help='bone count (default: probe from consumed size)')
    ap.add_argument('--show-bone', type=int, default=0)
    args = ap.parse_args()

    tag, entries = parse_bank(args.psx)
    e = entries[args.anim]
    blob, frames = e['blob'], e['frames']
    print(f"anim {args.anim}: frames={frames} tween={e['tween']} len={e['len']}")

    names = ['Rx', 'Ry', 'Rz', 'Tx', 'Ty', 'Tz']
    pos = 0
    bone = 0
    while pos < len(blob) - 4 and (args.bones is None or bone < args.bones):
        for c in range(6):
            samples, used, desc = decompress(blob, pos, frames)
            mark = ''
            if bone == args.show_bone:
                big = max(abs(samples[k + 1] - samples[k])
                          for k in range(len(samples) - 1)) if len(samples) > 1 else 0
                mark = f"  maxStep={big}"
                print(f"  bone{bone:>2} {names[c]}: @{pos:>5} {desc} used={used}{mark}")
                print(f"          {samples}")
            pos += used
        bone += 1
    print(f"total consumed={pos} of {len(blob)} (bones walked: {bone})")


if __name__ == '__main__':
    main()
