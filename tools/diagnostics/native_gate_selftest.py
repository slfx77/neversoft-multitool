#!/usr/bin/env python3
"""End-to-end synthetic smoke test for native_reference_sweep.py + native_gate.py.

Used when no real PCSX2 SW-native reference dumps exist locally (they get cleaned):
generates a fake capture tree with numpy and drives both scripts through their CLIs,
asserting every Phase-2 Step 5 behavior:

  capture 111111 (healthy):
    - 5 distinct full-size native frames + a runt file + an undersized image (filtered)
    - our FBP0 render = known affine tone (0.85*native + 5) of the MIDDLE frame,
      1px shifted, mild noise — under the CURRENT gsdump naming (*.fbp_buffers/...)
    - sibling hi-res "screenshot" = gamma-toned, letterboxed upscale of the same frame
      -> --anchor must select the middle frame with high confidence, recover
      slope ~0.85 / intercept ~5 / small MAE; default content-match must agree
  capture 222222 (outlier):
    - our render (LEGACY *.FBP-0_* naming) unrelated to every native frame
    - no sibling screenshot -> anchor mode falls back with a warning
    - natMAE > 25 -> outlier: true, excluded from aggregate + baseline
  gate:
    - --update-baseline seeds from the sweep (111111 only), then the gate PASSES
    - slope drift beyond +/-band FAILS; MAE worse than +tolerance FAILS
    - pending-capture baseline exits 0 with a notice

Usage: python tools/diagnostics/native_gate_selftest.py [--root TestOutput/native_gate_selftest]
Exits nonzero on any assertion failure.
"""
import json
import os
import shutil
import subprocess
import sys

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SWEEP = os.path.join(HERE, "native_reference_sweep.py")
GATE = os.path.join(HERE, "native_gate.py")
W, H = 640, 448
CHECKS = []


def check(name, ok, detail=""):
    CHECKS.append((name, bool(ok)))
    print(f"  [{'ok' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail else ""))


def save(path, arr):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8)).save(path)


def scene(t, rng):
    """Structured synthetic frame: phase-shifting gradients + blocks that move with t."""
    yy, xx = np.mgrid[0:H, 0:W].astype(float)
    img = np.zeros((H, W, 3))
    img[..., 0] = 60 + 45 * np.sin(xx / 37.0 + t * 1.7)
    img[..., 1] = 80 + 55 * np.sin(yy / 23.0 - t * 0.9)
    img[..., 2] = 70 + 50 * np.sin((xx + yy) / 41.0 + t)
    for k in range(6):
        x0 = int((37 * k + 97 * t) % (W - 80))
        y0 = int((53 * k + 71 * t) % (H - 60))
        img[y0:y0 + 60, x0:x0 + 80, k % 3] = 210
    img += rng.normal(0, 2.0, img.shape)
    return np.clip(img, 0, 255)


def build_fixture(root):
    if os.path.isdir(root):
        shutil.rmtree(root)
    rng = np.random.default_rng(1234)
    nat1 = os.path.join(root, "native", "pcsx2_native_111111")
    frames = [scene(t, rng) for t in range(5)]
    for i, fr in enumerate(frames):
        save(os.path.join(nat1, f"{(i + 1) * 100:05d}_f{(i + 1) * 10:05d}_rt0_00000_C_32.png"), fr)
    # runt + undersized dumps: must be filtered out, not break anything
    os.makedirs(nat1, exist_ok=True)
    with open(os.path.join(nat1, "00600_f00060_rt0_00000_C_32.png"), "wb") as f:
        f.write(b"\x89PNG runt")
    save(os.path.join(nat1, "00700_f00070_rt0_00000_C_32.png"), scene(9, rng)[:64, :64])

    target = frames[2]  # middle frame — neither first nor last
    ours = 0.85 * np.roll(target, (1, 1), axis=(0, 1)) + 5 + rng.normal(0, 1.0, target.shape)
    save(os.path.join(root, "ours", "dump_111111.fbp_buffers", "fbp_00000_fbw10_C_32_640x448.png"),
         ours)
    # embedded biased ref (exercises embSlope): heavy tone compression of the target
    save(os.path.join(root, "ours", "dump_111111.pcsx2.png"), 0.16 * target + 120)
    # sibling hi-res screenshot: gamma-toned target, upscaled + letterboxed to 1920x1080
    shot = 255.0 * (target / 255.0) ** 0.55
    up = Image.fromarray(np.clip(shot, 0, 255).astype(np.uint8)).resize((1792, 1008),
                                                                        Image.BILINEAR)
    canvas = np.zeros((1080, 1920, 3))
    canvas[36:36 + 1008, 64:64 + 1792] = np.asarray(up).astype(float)
    save(os.path.join(root, "snaps", "Fake Game_SLUS-00000_20260101111111.png"), canvas)

    # capture 222222: our render unrelated to every native frame -> MAE outlier
    nat2 = os.path.join(root, "native", "pcsx2_native_222222")
    for i in range(3):
        save(os.path.join(nat2, f"{(i + 1) * 100:05d}_f{(i + 1) * 10:05d}_rt0_00000_C_32.png"),
             scene(20 + i, rng))
    save(os.path.join(root, "ours", "dump_222222.FBP-0_FBW10_PSM-00_FBMSK-00000000.png"),
         scene(40, rng))


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr


def run_sweep(root, json_name, anchor):
    cmd = [sys.executable, SWEEP,
           "--our-dir", os.path.join(root, "ours"),
           "--native-root", os.path.join(root, "native"),
           "--snaps-dir", os.path.join(root, "snaps"),
           "--json", os.path.join(root, json_name)]
    if anchor:
        cmd.append("--anchor")
    rc, out = run(cmd)
    print(out)
    check(f"sweep ({'anchor' if anchor else 'default'}) exits 0", rc == 0, f"rc={rc}")
    with open(os.path.join(root, json_name), encoding="utf-8") as f:
        return json.load(f)


def main():
    args = sys.argv[1:]
    root = args[args.index("--root") + 1] if "--root" in args else \
        os.path.join("TestOutput", "native_gate_selftest")
    print(f"fixture root: {root}")
    build_fixture(root)

    # --- sweep, anchor mode -------------------------------------------------
    data = run_sweep(root, "sweep_anchor.json", anchor=True)
    caps = {c["tag"]: c for c in data["captures"]}
    c1, c2 = caps.get("111111"), caps.get("222222")
    check("111111 present with metrics", c1 and c1.get("slope") is not None)
    if c1 and c1.get("slope") is not None:
        check("anchor selected the middle frame", c1["matchedFrame"].startswith("00300_"),
              c1["matchedFrame"])
        check("frameSelection == anchor", c1.get("frameSelection") == "anchor")
        check("anchorConfidence high", (c1.get("anchorConfidence") or 0) > 0.3,
              f"{c1.get('anchorConfidence')}")
        check("slope ~0.85 recovered", 0.80 <= c1["slope"] <= 0.90, f"{c1['slope']}")
        check("intercept ~5 recovered", 1.0 <= c1["intercept"] <= 9.0, f"{c1['intercept']}")
        # MAE is tone-INCLUSIVE by design (the 0.85x+5 tone alone contributes ~6),
        # so a healthy capture just needs to sit far below the 25 outlier bound.
        check("MAE well below outlier bound", c1["mae"] < 12.0, f"{c1['mae']}")
        check("111111 not outlier", not c1["outlier"])
        check("embSlope reported (biased ref)", c1.get("embSlope") is not None,
              f"{c1.get('embSlope')}")
    check("222222 flagged outlier (MAE > 25)", c2 and c2.get("outlier"),
          f"mae={c2 and c2.get('mae')}, reason={c2 and c2.get('outlierReason')}")
    check("222222 fell back to content-match (no screenshot)",
          c2 and c2.get("frameSelection") == "content" and c2.get("anchorConfidence") is None)
    agg = data["aggregate"]
    check("aggregate excludes the outlier", agg["included"] == 1 and agg["outliers"] == 1,
          f"{agg}")
    check("aggregate meanSlope == 111111 slope",
          c1 and agg["meanSlope"] is not None and abs(agg["meanSlope"] - c1["slope"]) < 1e-6)

    # --- sweep, default (content-match) path preserved ----------------------
    data_d = run_sweep(root, "sweep_default.json", anchor=False)
    d1 = {c["tag"]: c for c in data_d["captures"]}.get("111111")
    check("default content-match agrees on the frame",
          d1 and d1["matchedFrame"].startswith("00300_"), d1 and d1["matchedFrame"])
    check("default mode reports no anchorConfidence",
          d1 and d1.get("anchorConfidence") is None)

    # --- gate ---------------------------------------------------------------
    sweep_json = os.path.join(root, "sweep_anchor.json")
    baseline = os.path.join(root, "baseline.json")

    rc, out = run([sys.executable, GATE, "--sweep", sweep_json,
                   "--baseline", os.path.join(HERE, "native_baseline.json")])
    print(out)
    check("pending-capture committed baseline -> exit 0 with notice",
          rc == 0 and "nothing to gate" in out)

    rc, out = run([sys.executable, GATE, "--sweep", sweep_json, "--baseline", baseline,
                   "--update-baseline"])
    print(out)
    check("--update-baseline exits 0", rc == 0)
    with open(baseline, encoding="utf-8") as f:
        base = json.load(f)
    check("baseline active with only 111111 (outlier dropped)",
          base["status"] == "active" and list(base["captures"]) == ["111111"])

    rc, out = run([sys.executable, GATE, "--sweep", sweep_json, "--baseline", baseline])
    print(out)
    check("gate PASSES against its own baseline", rc == 0 and "GATE PASSED" in out)

    # tamper: slope drift beyond band
    with open(sweep_json, encoding="utf-8") as f:
        tampered = json.load(f)
    for c in tampered["captures"]:
        if c["tag"] == "111111":
            c["slope"] = round(c["slope"] - 0.05, 4)
    bad = os.path.join(root, "sweep_slope_regressed.json")
    with open(bad, "w", encoding="utf-8") as f:
        json.dump(tampered, f)
    rc, out = run([sys.executable, GATE, "--sweep", bad, "--baseline", baseline])
    print(out)
    check("slope regression (-0.05) FAILS the gate", rc == 1 and "FAIL (slope)" in out)

    # tamper: MAE worse than tolerance
    with open(sweep_json, encoding="utf-8") as f:
        tampered = json.load(f)
    for c in tampered["captures"]:
        if c["tag"] == "111111":
            c["mae"] = round(c["mae"] + 1.0, 3)
    bad = os.path.join(root, "sweep_mae_regressed.json")
    with open(bad, "w", encoding="utf-8") as f:
        json.dump(tampered, f)
    rc, out = run([sys.executable, GATE, "--sweep", bad, "--baseline", baseline])
    print(out)
    check("MAE regression (+1.0) FAILS the gate", rc == 1 and "FAIL (MAE)" in out)

    # missing capture = coverage loss
    with open(sweep_json, encoding="utf-8") as f:
        tampered = json.load(f)
    tampered["captures"] = [c for c in tampered["captures"] if c["tag"] != "111111"]
    bad = os.path.join(root, "sweep_missing_capture.json")
    with open(bad, "w", encoding="utf-8") as f:
        json.dump(tampered, f)
    rc, out = run([sys.executable, GATE, "--sweep", bad, "--baseline", baseline])
    print(out)
    check("missing baseline capture FAILS the gate", rc == 1 and "missing from sweep" in out)

    failed = [n for n, ok in CHECKS if not ok]
    print(f"\n{len(CHECKS) - len(failed)}/{len(CHECKS)} checks passed")
    if failed:
        print("FAILED: " + "; ".join(failed))
        return 1
    print("SELFTEST PASSED")
    return 0


if __name__ == "__main__":
    sys.exit(main())
