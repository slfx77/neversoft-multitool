#!/usr/bin/env python3
"""SW-native reference metric gate for the GS software replay (Phase-2 Step 5).

Reads a sweep JSON produced by `native_reference_sweep.py --json` and grades each
capture against the committed baseline tools/validation/gsdump/native_baseline.json.

The gate protects against REGRESSION from baseline, NOT absolute parity: current
per-capture tone parity vs SW-native is ~0.73-0.94 slope, so the check is
  |slope - baseline.slope| <= slope band (default 0.03)   — not an absolute 0.97-1.03
  mae <= baseline.mae + tolerance (default 0.5)
Captures the sweep flagged `outlier: true` are SKIPPED (frame-timing flake, excluded
from baselines too). Captures present in the baseline but absent from the sweep FAIL
(coverage lost). New captures not in the baseline are reported but not gated.

Exit codes: 0 = pass (or baseline still pending-capture), 1 = regression, 2 = usage/IO.

Usage:
  python tools/validation/gsdump/native_gate.py --sweep TestOutput/native_sweep.json
      [--baseline tools/validation/gsdump/native_baseline.json]
      [--slope-band 0.03] [--mae-tolerance 0.5]

  # (re)write the committed baseline from a sweep JSON (outliers excluded):
  python tools/validation/gsdump/native_gate.py --sweep TestOutput/native_sweep.json --update-baseline
"""
import json
import os
import sys
from datetime import date

DEFAULT_BASELINE = os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                "native_baseline.json")


def load_json(path, what):
    if not os.path.isfile(path):
        raise SystemExit(f"error: {what} not found: {path}")
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def sweep_captures(sweep):
    """tag -> record, for records that produced metrics."""
    return {c["tag"]: c for c in sweep.get("captures", []) if c.get("slope") is not None}


def update_baseline(sweep_path, baseline_path, slope_band, mae_tol):
    sweep = load_json(sweep_path, "sweep JSON")
    caps = sweep_captures(sweep)
    kept = {t: c for t, c in caps.items() if not c.get("outlier")}
    dropped = sorted(set(caps) - set(kept))
    baseline = {
        "status": "active",
        "generated": date.today().isoformat(),
        "sourceSweep": sweep_path.replace("\\", "/"),
        "anchorMode": sweep.get("anchorMode", False),
        "slopeBand": slope_band,
        "maeTolerance": mae_tol,
        "captures": {
            t: {
                "slope": c["slope"],
                "intercept": c.get("intercept"),
                "mae": c["mae"],
                "matchedFrame": c.get("matchedFrame"),
                "anchorConfidence": c.get("anchorConfidence"),
            }
            for t, c in sorted(kept.items())
        },
        "aggregate": sweep.get("aggregate"),
    }
    with open(baseline_path, "w", encoding="utf-8") as f:
        json.dump(baseline, f, indent=2)
        f.write("\n")
    print(f"baseline updated: {baseline_path} ({len(kept)} capture(s)"
          + (f", {len(dropped)} outlier(s) dropped: {', '.join(dropped)}" if dropped else "")
          + ")")
    return 0


def run_gate(sweep_path, baseline_path, slope_band, mae_tol):
    sweep = load_json(sweep_path, "sweep JSON")
    baseline = load_json(baseline_path, "baseline")
    if baseline.get("status") != "active" or not baseline.get("captures"):
        print(f"baseline status is '{baseline.get('status')}' with "
              f"{len(baseline.get('captures') or {})} capture(s) — nothing to gate yet.")
        print("After a fresh SW-native capture + sweep, seed it with:")
        print(f"  python tools/validation/gsdump/native_gate.py --sweep {sweep_path} --update-baseline")
        return 0

    # CLI overrides win; otherwise use the tolerances recorded in the baseline.
    band = slope_band if slope_band is not None else baseline.get("slopeBand", 0.03)
    tol = mae_tol if mae_tol is not None else baseline.get("maeTolerance", 0.5)
    caps = sweep_captures(sweep)
    print(f"gate: slope band +/-{band:g} around baseline, MAE tolerance +{tol:g}  "
          f"(baseline {baseline.get('generated')}, {len(baseline['captures'])} capture(s))")
    hdr = (f"{'tag':>7} {'baseSlope':>9} {'slope':>7} {'dSlope':>7} "
           f"{'baseMAE':>7} {'mae':>7} {'dMAE':>6}  status")
    print(hdr)
    print("-" * len(hdr))
    failures = 0
    for tag, base in sorted(baseline["captures"].items()):
        cur = caps.get(tag)
        if cur is None:
            print(f"{tag:>7} {base['slope']:>9.3f} {'-':>7} {'-':>7} "
                  f"{base['mae']:>7.2f} {'-':>7} {'-':>6}  FAIL (missing from sweep)")
            failures += 1
            continue
        if cur.get("outlier"):
            print(f"{tag:>7} {base['slope']:>9.3f} {cur['slope']:>7.3f} {'-':>7} "
                  f"{base['mae']:>7.2f} {cur['mae']:>7.2f} {'-':>6}  "
                  f"SKIP (outlier: {cur.get('outlierReason')})")
            continue
        d_slope = cur["slope"] - base["slope"]
        d_mae = cur["mae"] - base["mae"]
        problems = []
        if abs(d_slope) > band:
            problems.append("slope")
        if d_mae > tol:
            problems.append("MAE")
        status = "PASS" if not problems else f"FAIL ({'+'.join(problems)})"
        if problems:
            failures += 1
        print(f"{tag:>7} {base['slope']:>9.3f} {cur['slope']:>7.3f} {d_slope:>+7.3f} "
              f"{base['mae']:>7.2f} {cur['mae']:>7.2f} {d_mae:>+6.2f}  {status}")
    for tag in sorted(set(caps) - set(baseline["captures"])):
        c = caps[tag]
        note = " (outlier)" if c.get("outlier") else ""
        print(f"{tag:>7} {'-':>9} {c['slope']:>7.3f} {'-':>7} "
              f"{'-':>7} {c['mae']:>7.2f} {'-':>6}  NEW (not gated){note}")
    print("-" * len(hdr))
    if failures:
        print(f"GATE FAILED: {failures} regression(s) vs baseline")
        return 1
    print("GATE PASSED")
    return 0


def main():
    args = sys.argv[1:]
    if "--sweep" not in args:
        print(__doc__)
        return 2

    def opt(name, default=None, cast=str):
        return cast(args[args.index(name) + 1]) if name in args else default

    sweep_path = opt("--sweep")
    baseline_path = opt("--baseline", DEFAULT_BASELINE)
    slope_band = opt("--slope-band", None, float)
    mae_tol = opt("--mae-tolerance", None, float)
    if "--update-baseline" in args:
        return update_baseline(sweep_path, baseline_path,
                               slope_band if slope_band is not None else 0.03,
                               mae_tol if mae_tol is not None else 0.5)
    return run_gate(sweep_path, baseline_path, slope_band, mae_tol)


if __name__ == "__main__":
    sys.exit(main())
