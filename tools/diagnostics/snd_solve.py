#!/usr/bin/env python3
"""Recover the THUG2 PC .snd state machine from chosen-plaintext captures.

Input is one or more (probe payload, captured decoded PCM) pairs produced by
`snd_probe_gen.py` + `snd_capture.js`. Because we choose the payload, the
decode is no longer a search: each probe isolates one part of the state machine
and the answer is read off directly.

    ramp-max  consecutive output deltas ARE quantise(step[i]) for i = 0,1,2,...
              until saturation -- a direct readout of the step table and the
              diff formula together.
    dither    +step/8 then -step/8 with index falling. A pure integrator holds
              its value; a leaky one decays, and the decay ratio IS the leak.
    sweep     each nibble held 16 samples -- per-nibble diff magnitude and the
              index delta it causes.

`--self-test` runs the whole chain against a synthetic engine with a KNOWN rule,
so the solver is verified before any real capture exists. That is the point of
this file: when a capture finally arrives, the analysis is already trusted.

Usage:
    python tools/diagnostics/snd_solve.py --self-test
    python tools/diagnostics/snd_solve.py --pair ramp-max=probe.snd,captured.raw
    python tools/diagnostics/snd_solve.py --pair dither=p.snd,c.raw --pair sweep=q.snd,d.raw

Captured PCM is raw signed 16-bit little-endian mono (what snd_capture.js
writes). Probe .snd files are read with their RIFF header stripped.
"""

from __future__ import annotations

import argparse
import math
import struct
import sys
from pathlib import Path

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


def read_snd_payload(path: Path) -> bytes:
    raw = path.read_bytes()
    if raw[0:4] != b"RIFF":
        return raw  # already a bare payload
    off = 12
    while off + 8 <= len(raw):
        cid = raw[off : off + 4]
        size = struct.unpack("<I", raw[off + 4 : off + 8])[0]
        if cid == b"data":
            return raw[off + 8 : off + 8 + min(size, len(raw) - off - 8)]
        off += 8 + size + (size & 1)
    raise ValueError(f"{path.name}: no data chunk")


def read_pcm(path: Path) -> list[int]:
    raw = path.read_bytes()
    if raw[0:4] == b"RIFF":  # tolerate a .wav capture
        off = 12
        while off + 8 <= len(raw):
            cid = raw[off : off + 4]
            size = struct.unpack("<I", raw[off + 4 : off + 8])[0]
            if cid == b"data":
                raw = raw[off + 8 : off + 8 + size]
                break
            off += 8 + size + (size & 1)
    count = len(raw) // 2
    return list(struct.unpack(f"<{count}h", raw[: count * 2]))


def nibbles(payload: bytes, high_first: bool = False) -> list[int]:
    out = []
    for byte in payload:
        out.extend((byte >> 4, byte & 0x0F) if high_first else (byte & 0x0F, byte >> 4))
    return out


# --- synthetic engine, for --self-test --------------------------------------


def synthetic_engine(payload: bytes, *, leak: float = 1.0, shift: int = 3) -> list[int]:
    """A deliberately NON-textbook IMA variant, standing in for the real engine.
    The solver must recover `leak` and the step table without being told them."""
    out: list[int] = []
    pred, idx = 0.0, 0
    for n in nibbles(payload):
        step = IMA_STEP[idx]
        diff = ((2 * (n & 7) + 1) * step) >> shift
        pred = pred * leak + (-diff if n & 8 else diff)
        pred = max(-32768.0, min(32767.0, pred))
        idx = max(0, min(88, idx + IMA_INDEX[n]))
        out.append(int(pred))
    return out


# --- analyses ---------------------------------------------------------------


def analyse_ramp(payload: bytes, pcm: list[int], sign: int) -> dict:
    """Max-magnitude nibbles: |delta[i]| is the quantised step at index i."""
    deltas = [pcm[i + 1] - pcm[i] for i in range(min(len(pcm), 200) - 1)]
    magnitudes = [abs(d) for d in deltas]

    # Walk the reference index progression the probe forces (+8 per sample,
    # clamped at 88) and compare our observed magnitude to (2*7+1)*step >> s.
    observed = []
    idx = 0
    for m in magnitudes[:40]:
        observed.append((idx, m, IMA_STEP[idx]))
        idx = min(88, idx + 8)

    shifts = {}
    for shift in range(1, 7):
        error = sum(abs(m - ((15 * s) >> shift)) for _, m, s in observed if m)
        shifts[shift] = error
    best_shift = min(shifts, key=shifts.get)

    return {
        "direction": "positive" if sign > 0 else "negative",
        "first_deltas": deltas[:12],
        "best_diff_shift": best_shift,
        "shift_errors": shifts,
        "saturates_at": next((i for i, m in enumerate(magnitudes) if m == 0), None),
    }


def analyse_dither(pcm: list[int]) -> dict:
    """Alternating +/- with a falling index. A pure integrator holds its level;
    a leaky one decays geometrically, and the ratio is the leak."""
    # Take the local mean over each +/- pair to remove the oscillation.
    means = [(pcm[i] + pcm[i + 1]) / 2 for i in range(0, min(len(pcm), 4000) - 1, 2)]
    means = [m for m in means]
    if len(means) < 8:
        return {"leak": None, "note": "capture too short"}

    # Fit a geometric decay across the region where the signal is still large
    # enough to measure.
    ratios = []
    for i in range(len(means) - 1):
        if abs(means[i]) > 8:
            ratios.append(means[i + 1] / means[i])
    if not ratios:
        return {"leak": 1.0, "note": "signal never departed zero; pure integrator or silent"}

    ratios.sort()
    median_ratio = ratios[len(ratios) // 2]

    # Consecutive pair-means are TWO samples apart, so the measured ratio is
    # leak^2. Reporting it as-is would understate the exponent by a factor of
    # two (a true 0.990 reads as 0.9801) -- caught by --self-test.
    per_sample = math.sqrt(median_ratio) if median_ratio > 0 else 0.0

    return {
        "leak": round(per_sample, 6),
        "leak_per_pair": round(median_ratio, 6),
        "verdict": "PURE INTEGRATOR" if per_sample > 0.9995 else f"LEAKY (~{per_sample:.5f}/sample)",
        "samples_used": len(ratios),
        "first_means": [round(m, 1) for m in means[:8]],
    }


def analyse_sweep(payload: bytes, pcm: list[int]) -> dict:
    """Each nibble value held for a run: report the mean |delta| per value, which
    exposes the diff quantiser's shape across magnitudes."""
    ns = nibbles(payload)
    per_value: dict[int, list[int]] = {}
    for i in range(min(len(ns), len(pcm)) - 1):
        per_value.setdefault(ns[i], []).append(pcm[i + 1] - pcm[i])

    summary = {}
    for value in sorted(per_value):
        deltas = per_value[value]
        summary[value] = {
            "count": len(deltas),
            "mean_delta": round(sum(deltas) / len(deltas), 2),
            "sign": "negative" if value & 8 else "positive",
        }
    return summary


ANALYSES = {
    "ramp-max": lambda p, c: analyse_ramp(p, c, +1),
    "ramp-min": lambda p, c: analyse_ramp(p, c, -1),
    "dither": lambda p, c: analyse_dither(c),
    "settle": lambda p, c: analyse_dither(c),
    "zero": lambda p, c: analyse_ramp(p, c, +1),
    "sweep": analyse_sweep,
}


def self_test() -> int:
    """Prove the solver recovers a known rule before any real capture exists."""
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from snd_probe_gen import PROBES  # noqa: PLC0415

    print("SELF-TEST: synthetic engine with leak=0.990, diff shift=3\n")
    failures = 0

    payload = PROBES["ramp-max"][1](2048)
    pcm = synthetic_engine(payload, leak=0.990, shift=3)
    ramp = analyse_ramp(payload, pcm, +1)
    print(f"  ramp-max -> recovered diff shift = {ramp['best_diff_shift']} (truth 3)")
    if ramp["best_diff_shift"] != 3:
        failures += 1
        print("    FAIL: wrong shift")

    payload = PROBES["settle"][1](4096)
    pcm = synthetic_engine(payload, leak=0.990, shift=3)
    dither = analyse_dither(pcm)
    leak = dither.get("leak")
    print(f"  settle   -> recovered leak = {leak} (truth 0.990), verdict: {dither.get('verdict')}")
    if leak is None or abs(leak - 0.990) > 0.002:
        failures += 1
        print("    FAIL: leak not recovered within 0.002")

    # A pure integrator must be reported as such, not as a small leak.
    pcm = synthetic_engine(payload, leak=1.0, shift=3)
    pure = analyse_dither(pcm)
    print(f"  settle   -> pure integrator control: {pure.get('verdict')}")
    if pure.get("leak") is not None and pure["leak"] < 0.9995:
        failures += 1
        print("    FAIL: pure integrator misreported as leaky")

    print()
    print("SELF-TEST PASSED" if failures == 0 else f"SELF-TEST FAILED ({failures})")
    return 1 if failures else 0


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--self-test", action="store_true", help="Validate the solver on synthetic data")
    ap.add_argument(
        "--pair",
        action="append",
        default=[],
        metavar="NAME=PROBE.snd,CAPTURED.raw",
        help="A probe/capture pair (repeatable)",
    )
    args = ap.parse_args()

    if args.self_test:
        return self_test()

    if not args.pair:
        ap.print_help()
        return 2

    for spec in args.pair:
        name, _, paths = spec.partition("=")
        probe_path, _, capture_path = paths.partition(",")
        if not probe_path or not capture_path:
            print(f"bad --pair: {spec}", file=sys.stderr)
            return 2

        payload = read_snd_payload(Path(probe_path))
        pcm = read_pcm(Path(capture_path))
        print(f"=== {name}: {len(payload)} payload bytes -> {len(pcm)} samples")
        ratio = len(pcm) / len(payload) if payload else 0
        print(f"    samples per payload byte: {ratio:.3f} (expect 2.000 for a 4-bit codec)")

        analysis = ANALYSES.get(name)
        if analysis is None:
            print(f"    (no analysis registered for probe '{name}')")
            continue
        for key, value in analysis(payload, pcm).items():
            print(f"    {key}: {value}")
        print()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
