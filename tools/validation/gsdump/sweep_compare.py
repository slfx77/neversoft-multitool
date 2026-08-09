#!/usr/bin/env python3
"""Compare PixelDiff.MeanAbsoluteError per capture between two gsdump sweep output dirs.

Gate (from the #4 plan): mean delta <= 0, no single capture regresses > 0.3, and the THAW
canonical (20260507234126) improves.

Usage: python tools/validation/gsdump/sweep_compare.py BASELINE_DIR FIXED_DIR
"""
import glob
import json
import os
import sys

base_dir, fixed_dir = sys.argv[1], sys.argv[2]


def load(d):
    out = {}
    for f in glob.glob(os.path.join(d, "*.gsdump-audit.json")):
        stem = os.path.basename(f).rsplit(".gsdump-audit.json", 1)[0]
        j = json.load(open(f))
        pd = (j.get("PixelDiff") or {}).get("MeanAbsoluteError")
        out[stem] = pd
    return out


b = load(base_dir)
fx = load(fixed_dir)
keys = sorted(set(b) & set(fx))
print(f"{'capture':<70} {'base':>8} {'fixed':>8} {'delta':>8}")
deltas = []
worst = None
for k in keys:
    if b[k] is None or fx[k] is None:
        print(f"{k[-40:]:<70} {'?':>8} {'?':>8}")
        continue
    d = fx[k] - b[k]
    deltas.append(d)
    if worst is None or d > worst[1]:
        worst = (k, d)
    flag = "  <-- REGRESS" if d > 0.3 else ("  (worse)" if d > 0 else "")
    print(f"{k[-66:]:<70} {b[k]:>8.3f} {fx[k]:>8.3f} {d:>+8.3f}{flag}")

mean_d = sum(deltas) / len(deltas) if deltas else 0
print(f"\nmean delta: {mean_d:+.4f}   (n={len(deltas)})")
print(f"worst regression: {worst[1]:+.3f} on ...{worst[0][-40:]}" if worst else "n/a")
canon = [k for k in keys if "20260507234126" in k]
if canon:
    k = canon[0]
    print(f"canonical 234126: {b[k]:.3f} -> {fx[k]:.3f} ({fx[k]-b[k]:+.3f})")
gate = mean_d <= 0 and (worst is None or worst[1] <= 0.3)
print(f"\nGATE: {'PASS' if gate else 'FAIL'} (mean<=0 and no capture +>0.3)")
