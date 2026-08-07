#!/usr/bin/env python3
"""Generate CHOSEN-PLAINTEXT .snd probe files for the THUG2 PC codec.

The `.snd` codec is IMA-family but its predictor rule is unknown (see
`snd_codec_fit.py`). Correlating against the paired Xbox `.pcm` can only ever be
approximate, because those are two independent lossy encodes of the same audio.

This sidesteps that. Rather than analysing shipped audio, it writes `.snd` files
whose payload we CHOOSE, so that the engine's decoded output reveals the state
machine directly:

  ramp-max   0x77 repeated. Nibble 7 is maximum positive magnitude and drives
             the step index +8 per sample, so it walks the step table from the
             bottom to saturation in ~11 samples. Consecutive output deltas are
             a direct readout of `quantise(step[i])` -- i.e. the step table AND
             the diff formula, straight off the wire.
  ramp-min   0xFF. Same, negated (nibble 15 = sign bit + magnitude 7).
  dither     0x80 repeated: nibble 0 then nibble 8, i.e. +step/8 then -step/8
             with index -1 each time. A pure integrator returns to its starting
             value and stays; a LEAKY one decays toward zero. This measures the
             leak coefficient on its own, which is exactly the term the fit
             harness says is wrong.
  settle     0x77 x 64 then 0x80 forever. Drives the predictor far from zero,
             then holds. The decay curve from the plateau is the leak with a
             large signal-to-noise ratio.
  sweep      Every nibble value 0..15 held for 16 samples, in order. Isolates
             the per-nibble diff magnitude and the per-nibble index delta.
  zero       0x00 repeated (nibble 0 = +step/8). The positive mirror of dither's
             first half; catches an asymmetric clamp.

Usage:
    python tools/diagnostics/snd_probe_gen.py -o probes/
    python tools/diagnostics/snd_probe_gen.py -o probes/ --seconds 0.5
    python tools/diagnostics/snd_probe_gen.py --list

Then replace a shipped `.snd` with one of these (keep a backup), trigger that
sound in-game, and capture the decoded buffer with `snd_capture.js`. Feed the
pair to `snd_solve.py`.

Headers are byte-shaped like the real corpus: 16-byte `fmt ` (NOT the 20-byte
Xbox-ADPCM one), wFormatTag 1, mono, 16-bit, nBlockAlign 2, and
nAvgBytesPerSec = 4 x dataSize, which is how the format actually stores its
decoded byte count. If the engine validates anything, it validates that.
"""

from __future__ import annotations

import argparse
import struct
import sys
from pathlib import Path

DEFAULT_RATE = 44100

# name -> (description, payload builder taking a byte count)
PROBES: dict[str, tuple[str, callable]] = {
    "ramp-max": (
        "0x77 repeated - max positive magnitude, index +8; reads out the step table",
        lambda n: bytes([0x77]) * n,
    ),
    "ramp-min": (
        "0xFF repeated - max negative magnitude, index +8; negated mirror of ramp-max",
        lambda n: bytes([0xFF]) * n,
    ),
    "dither": (
        "0x80 repeated - +step/8 then -step/8, index -1 each; isolates the LEAK",
        lambda n: bytes([0x80]) * n,
    ),
    "settle": (
        "0x77 x64 then 0x80 - drive far from zero, then watch the decay",
        lambda n: (bytes([0x77]) * min(64, n)) + bytes([0x80]) * max(0, n - 64),
    ),
    "sweep": (
        "each nibble value 0..15 held 16 samples - per-nibble diff and index delta",
        lambda n: bytes(
            ((v << 4) | v) for i in range(n) for v in [((i // 8) % 16)]
        )[:n],
    ),
    "zero": (
        "0x00 repeated - nibble 0 only; positive mirror of dither, catches asymmetric clamp",
        lambda n: bytes([0x00]) * n,
    ),
}


def build_snd(payload: bytes, rate: int = DEFAULT_RATE) -> bytes:
    """Wraps a raw nibble payload in a corpus-shaped .snd header."""
    data_size = len(payload)

    # The format's tell: nAvgBytesPerSec is the DECODED byte count, not a rate.
    # 2 samples per byte x 2 bytes per sample = 4 x dataSize, less 2 when the
    # sample count is odd (matches 788/788 shipped files).
    avg_bytes = 4 * data_size if data_size % 2 == 0 else 4 * data_size - 2

    fmt_chunk = struct.pack(
        "<HHIIHH",
        1,  # wFormatTag - the shipped files all claim plain PCM and lie
        1,  # channels
        rate,
        avg_bytes,
        2,  # nBlockAlign
        16,  # wBitsPerSample
    )

    body = b"fmt " + struct.pack("<I", len(fmt_chunk)) + fmt_chunk
    body += b"data" + struct.pack("<I", data_size) + payload
    if data_size % 2:
        body += b"\x00"  # RIFF word pad

    # Shipped files declare the DECODED size here too, not the real one.
    return b"RIFF" + struct.pack("<I", avg_bytes + 36) + b"WAVE" + body


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("-o", "--output", type=Path, help="Directory to write probe .snd files into")
    ap.add_argument("--seconds", type=float, default=0.25, help="Probe length in decoded seconds")
    ap.add_argument("--rate", type=int, default=DEFAULT_RATE, help="Declared sample rate")
    ap.add_argument("--probe", action="append", help="Probe name (repeatable); default all")
    ap.add_argument("--list", action="store_true", help="List probe names and exit")
    args = ap.parse_args()

    if args.list:
        for name, (description, _) in PROBES.items():
            print(f"{name:<10} {description}")
        return 0

    if args.output is None:
        print("--output is required (or use --list)", file=sys.stderr)
        return 2

    # One sample per nibble, two nibbles per byte.
    payload_bytes = max(16, int(args.seconds * args.rate) // 2)

    args.output.mkdir(parents=True, exist_ok=True)
    for name in args.probe or list(PROBES):
        entry = PROBES.get(name)
        if entry is None:
            print(f"unknown probe: {name}", file=sys.stderr)
            return 2
        description, build = entry
        payload = build(payload_bytes)
        path = args.output / f"probe_{name}.snd"
        path.write_bytes(build_snd(payload, args.rate))
        print(f"{path}  ({len(payload)} payload bytes = {len(payload) * 2} samples)  {description}")

    print()
    print("Next: back up a shipped .snd, copy a probe over it, trigger that sound")
    print("in-game, capture with snd_capture.js, then run snd_solve.py.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
