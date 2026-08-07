#!/usr/bin/env python3
"""Fit a candidate codec to the THUG2 PC .snd format, scored against known plaintext.

THUG2 PC ships 788 `.snd` sound effects whose RIFF `fmt ` chunk claims 16-bit
mono PCM and is lying: `nAvgBytesPerSec` carries the DECODED byte count (4x the
on-disk data size), so the payload is 2 samples per byte -- a 4-bit codec. Read
as PCM it is white noise.

The oracle is that 350 of those basenames ALSO ship as Xbox `.pcm` on the same
Windows disc, in the fully-decoded Xbox ADPCM format (wFormatTag 0x0069). Those
are two encodes of the same source audio, so a correct `.snd` decode must
correlate strongly with the `.pcm` decode of the same name.

Scoring: median over pairs of the median windowed (1024-sample) normalised
cross-correlation. ACCEPTANCE is >= 0.97 over >= 100 pairs.

Two columns are reported and the difference between them is the main finding
so far:

  raw    correlation of the decoded waveforms. Textbook IMA scores 0.26-0.99
         depending on the file -- 0.99 on impulsive content (BailBodyPunch03),
         0.26 on low-level sustained content (AU_SANDSTEP01, BonkBush).
  deriv  correlation of the first differences, which cancels any accumulated
         integrator error. This sits at a UNIFORM 0.84-0.87 across the same
         files, including every one whose raw score is 0.26.

So the per-sample deltas already decode essentially correctly under textbook
IMA: the nibble order, the step table and the index table are right. What
diverges is the accumulated predictor, and the content-dependence of the raw
score is just how much that DC drift dominates a quiet signal. Whatever is
still missing is in the state/prediction rule, not the tables. A leaky
integrator confirms the direction (median 0.60 -> 0.65 at leak 0.98-0.995) but
does not solve it.

Ruled out so far: the .snd is NOT the .pcm bitstream with its 4-byte block
headers removed (1-8% byte agreement, i.e. chance -- they are independent
encodes); initial step index (no effect, the index adapts within a few
samples); nibble order; the shift-accumulate diff form; periodic state resets
at 16/32 bytes; Yamaha AICA. THAW.exe's IMA step and index tables have ZERO
xrefs in either .text section, so they are dead linked-in library data rather
than a live decoder -- THAW PC ships plain PCM .wav (1,148/1,148).

Usage:
    python tools/diagnostics/snd_codec_fit.py                 # score every model
    python tools/diagnostics/snd_codec_fit.py --model ima     # one model
    python tools/diagnostics/snd_codec_fit.py --pairs 50      # fewer pairs, faster
    python tools/diagnostics/snd_codec_fit.py --list          # list model names

Add a candidate by appending to MODELS: a name plus a function
(payload_bytes) -> list[int] of 16-bit samples.
"""

from __future__ import annotations

import argparse
import math
import os
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "utilities"))
try:
    from sample_paths import find_builds_root  # type: ignore
except Exception:  # pragma: no cover - the helper is optional
    find_builds_root = None

WINDOWS_BUILD = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)"
XBOX_BUILD = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)"

IMA_STEP = [
    7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
    50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
    253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
    1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
    3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
    11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
    32767,
]
IMA_INDEX = [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8]

# Yamaha AICA, already implemented in the repo (KatExtractor) -- included so the
# harness can re-check it cheaply rather than relying on memory that it failed.
AICA_DIFF = [1, 3, 5, 7, 9, 11, 13, 15, -1, -3, -5, -7, -9, -11, -13, -15]
AICA_SCALE = [0x0E6, 0x0E6, 0x0E6, 0x0E6, 0x133, 0x199, 0x200, 0x266]


def riff_data(path: Path) -> tuple[dict, bytes]:
    """Returns (fmt fields, data payload). Ignores the RIFF size field, which is
    wrong in every corpus file, and stops at `data` because ~130 .snd carry
    corrupt trailing chunks."""
    raw = path.read_bytes()
    if raw[0:4] != b"RIFF" or raw[8:12] != b"WAVE":
        raise ValueError(f"{path.name}: not RIFF/WAVE")

    fmt: dict = {}
    off = 12
    while off + 8 <= len(raw):
        cid = raw[off : off + 4]
        size = struct.unpack("<I", raw[off + 4 : off + 8])[0]
        payload = off + 8
        if cid == b"fmt ":
            tag, ch, rate, avg, align, bits = struct.unpack("<HHIIHH", raw[payload : payload + 16])
            fmt = dict(tag=tag, channels=ch, rate=rate, avg=avg, align=align, bits=bits)
        elif cid == b"data":
            return fmt, raw[payload : payload + min(size, len(raw) - payload)]
        off += 8 + size + (size & 1)
    raise ValueError(f"{path.name}: no data chunk")


def decode_xbox_pcm(payload: bytes) -> list[int]:
    """Reference decode. Bit-exact with ffmpeg's adpcm_ima_xbox: per 36-byte
    block the header predictor is sample 0, then 63 nibbles; the 64th is
    padding."""
    out: list[int] = []
    for base in range(0, len(payload) - 35, 36):
        block = payload[base : base + 36]
        pred = struct.unpack("<h", block[0:2])[0]
        idx = min(88, max(0, block[2]))
        out.append(pred)
        nibbles = []
        for byte in block[4:36]:
            nibbles.append(byte & 0x0F)
            nibbles.append(byte >> 4)
        for n in nibbles[:63]:
            step = IMA_STEP[idx]
            diff = ((2 * (n & 7) + 1) * step) >> 3
            pred = max(-32768, min(32767, pred - diff if n & 8 else pred + diff))
            idx = max(0, min(88, idx + IMA_INDEX[n]))
            out.append(pred)
    return out


# --- candidate models -------------------------------------------------------
# Each takes the raw .snd payload and returns 16-bit samples, one per nibble.


def _ima(payload: bytes, *, high_first=False, resync=0, mul_form=True, skip=0) -> list[int]:
    out: list[int] = []
    pred, idx = 0, 0
    for i, byte in enumerate(payload[skip:]):
        if resync and i % resync == 0:
            pred, idx = 0, 0
        pair = (byte >> 4, byte & 0x0F) if high_first else (byte & 0x0F, byte >> 4)
        for n in pair:
            step = IMA_STEP[idx]
            if mul_form:
                diff = ((2 * (n & 7) + 1) * step) >> 3
            else:
                diff = step >> 3
                if n & 1:
                    diff += step >> 2
                if n & 2:
                    diff += step >> 1
                if n & 4:
                    diff += step
            pred = max(-32768, min(32767, pred - diff if n & 8 else pred + diff))
            idx = max(0, min(88, idx + IMA_INDEX[n]))
            out.append(pred)
    return out


def _aica(payload: bytes, *, high_first=False) -> list[int]:
    out: list[int] = []
    hist, step = 0, 127
    for byte in payload:
        pair = (byte >> 4, byte & 0x0F) if high_first else (byte & 0x0F, byte >> 4)
        for n in pair:
            diff = step * AICA_DIFF[n & 7] // 8
            diff = min(diff, 0x7FFF)
            if n & 8:
                diff = -diff
            hist = max(-32768, min(32767, hist + diff))
            step = max(0x7F, min(0x6000, (step * AICA_SCALE[n & 7]) >> 8))
            out.append(hist)
    return out


def _ima_leaky(payload: bytes, *, leak: float) -> list[int]:
    """Textbook IMA with a leaky integrator. The derivative already matches, so
    the open question is the prediction rule; a leak is the cheapest probe of
    that and does move the raw score, without closing the gap."""
    out: list[int] = []
    pred, idx = 0.0, 0
    for byte in payload:
        for n in (byte & 0x0F, byte >> 4):
            step = IMA_STEP[idx]
            diff = ((2 * (n & 7) + 1) * step) >> 3
            pred = pred * leak + (-diff if n & 8 else diff)
            pred = max(-32768.0, min(32767.0, pred))
            idx = max(0, min(88, idx + IMA_INDEX[n]))
            out.append(int(pred))
    return out


MODELS: dict[str, callable] = {
    "ima": lambda p: _ima(p),
    "ima-high-first": lambda p: _ima(p, high_first=True),
    "ima-shift-form": lambda p: _ima(p, mul_form=False),
    "ima-resync-16": lambda p: _ima(p, resync=16),
    "ima-resync-32": lambda p: _ima(p, resync=32),
    "ima-skip-2": lambda p: _ima(p, skip=2),
    "ima-leak-0.995": lambda p: _ima_leaky(p, leak=0.995),
    "ima-leak-0.98": lambda p: _ima_leaky(p, leak=0.98),
    "aica": lambda p: _aica(p),
    "aica-high-first": lambda p: _aica(p, high_first=True),
}


# --- scoring ----------------------------------------------------------------


def ncc(a: list[int], b: list[int]) -> float:
    n = min(len(a), len(b))
    if n < 8:
        return 0.0
    sa = sum(a[:n]) / n
    sb = sum(b[:n]) / n
    num = da = db = 0.0
    for i in range(n):
        x, y = a[i] - sa, b[i] - sb
        num += x * y
        da += x * x
        db += y * y
    return num / math.sqrt(da * db) if da > 0 and db > 0 else 0.0


def diff(x: list[int]) -> list[int]:
    """First difference. Cancels accumulated integrator error, so correlating
    diffs isolates 'are the nibble decisions right?' from 'does the predictor
    track?'."""
    return [x[i + 1] - x[i] for i in range(len(x) - 1)]


def windowed_median_ncc(a: list[int], b: list[int], window: int = 1024) -> float:
    n = min(len(a), len(b))
    scores = [
        ncc(a[i : i + window], b[i : i + window])
        for i in range(0, n - window, window)
    ]
    if not scores:
        return ncc(a, b)
    scores.sort()
    return scores[len(scores) // 2]


def find_pairs(builds: Path, limit: int) -> list[tuple[Path, Path]]:
    snds: dict[str, Path] = {}
    for p in (builds / WINDOWS_BUILD).rglob("*.snd"):
        snds.setdefault(p.stem.lower(), p)
    pcms: dict[str, Path] = {}
    for p in (builds / XBOX_BUILD).rglob("*.pcm"):
        pcms.setdefault(p.stem.lower(), p)
    shared = sorted(set(snds) & set(pcms))
    return [(snds[k], pcms[k]) for k in shared[:limit]]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--builds", type=Path, help="Path to Sample/Builds")
    ap.add_argument("--model", action="append", help="Model name (repeatable); default all")
    ap.add_argument("--pairs", type=int, default=120, help="How many known-plaintext pairs to score")
    ap.add_argument("--list", action="store_true", help="List model names and exit")
    args = ap.parse_args()

    if args.list:
        for name in MODELS:
            print(name)
        return 0

    builds = args.builds
    if builds is None and find_builds_root is not None:
        builds = Path(find_builds_root())
    if builds is None:
        builds = Path("Sample/Builds")
    if not builds.is_dir():
        print(f"Sample/Builds not found at {builds}", file=sys.stderr)
        return 2

    pairs = find_pairs(builds, args.pairs)
    if not pairs:
        print("No .snd/.pcm name pairs found", file=sys.stderr)
        return 2
    print(f"scoring {len(pairs)} known-plaintext pairs (acceptance: median >= 0.97 over >= 100 pairs)\n")

    decoded_refs = []
    payloads = []
    for snd_path, pcm_path in pairs:
        _, snd_payload = riff_data(snd_path)
        _, pcm_payload = riff_data(pcm_path)
        payloads.append(snd_payload)
        decoded_refs.append(decode_xbox_pcm(pcm_payload))

    names = args.model or list(MODELS)
    print(f"{'model':<20} {'raw':>8} {'best':>8} {'worst':>8} {'deriv':>8}")
    print("-" * 57)
    results = []
    for name in names:
        model = MODELS.get(name)
        if model is None:
            print(f"{name:<20} (unknown model)")
            continue

        scores, derivs = [], []
        for payload, ref in zip(payloads, decoded_refs):
            got = model(payload)
            scores.append(windowed_median_ncc(got, ref))
            derivs.append(windowed_median_ncc(diff(got), diff(ref)))

        scores.sort()
        derivs.sort()
        median = scores[len(scores) // 2]
        results.append((median, name))
        print(
            f"{name:<20} {median:8.4f} {scores[-1]:8.4f} {scores[0]:8.4f} "
            f"{derivs[len(derivs) // 2]:8.4f}"
        )

    if results:
        best_median, best_name = max(results)
        print()
        verdict = "ACCEPTED" if best_median >= 0.97 and len(pairs) >= 100 else "rejected"
        print(f"best: {best_name} at {best_median:.4f} -> {verdict}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
