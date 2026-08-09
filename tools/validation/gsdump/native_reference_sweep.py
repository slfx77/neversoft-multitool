#!/usr/bin/env python3
"""Native-reference re-baseline sweep (Phase-2 Step 5 input: SW-native metric gate).

For every <native-root>/pcsx2_native_<tag>/ (PCSX2 SW-native rt0_00000 dumps), grade our
FBP0 scene (from --our-dir) against the matching full-size complete native frame,
shift-aligned. Reports per-capture means, affine slope (toward 1.0 = tone parity), and
MAE, plus the embedded-reference 'compression' slope for contrast.

The point: the gsdump gate grades our render vs the EMBEDDED .gs screenshot (a
HW-display capture with a different tone, affine slope ~0.16). This grades vs the true
GS SW-native output (historical per-capture slope ~0.73-0.94).

Frame selection:
  default   content-match — pick the native frame with the lowest shift-aligned MAE vs
            our render (PCSX2 replays the .gs across many frames).
  --anchor  structural anchor — the paired hi-res PCSX2 screenshot saved next to the .gs
            dump (<snaps-dir>/*<tag>.png) is PRESENT-TIME but tone-biased; select the
            native frame with the highest gradient-magnitude NCC vs the letterbox-cropped
            downscaled screenshot, then SCORE against that frame's native rt0 dump.
            Tone-invariant, so frame-timing outliers (e.g. capture 143551's HUD) cannot
            drag selection toward whichever frame flatters our render. Falls back to
            content-match (with a low-confidence warning) when no sibling screenshot
            exists or the best NCC is below 0.25.

Outlier rejection (both modes): natMAE > 25, or the matched frame is the first/last
full-size dump of the batch (frame-timing suspicion; only applied when >= 3 frames
exist), flags the capture `outlier: true`. Outliers are excluded from the aggregate
stats and from native_gate.py baselines.

Our-render discovery per tag accepts both namings:
  legacy   <our-dir>/*<tag>*.FBP-0_*PSM-00_FBMSK-00000000.png  (+ TestOutput/w<tag>/)
  current  <our-dir>/*<tag>*.fbp_buffers/fbp_00000_*C_32*.png  (gsdump --dump-fbp-buffers)

Usage:
  python tools/validation/gsdump/native_reference_sweep.py [--our-dir TestOutput/sweep_fbp]
      [--native-root TestOutput] [--anchor] [--snaps-dir <dir>]
      [--json TestOutput/native_sweep.json]

  --our-dir     dir holding our FBP0 renders (default TestOutput/sweep_fbp)
  --native-root dir containing the pcsx2_native_<tag> capture dirs (default TestOutput)
  --anchor      enable screenshot-anchored frame selection
  --snaps-dir   PCSX2 snaps dir with the paired .gs/.png pairs
                (default PCSX2_SNAPS_DIR or ~/Documents/PCSX2/snaps)
  --json PATH   write machine-readable per-capture + aggregate results

Downstream: tools/validation/gsdump/native_gate.py consumes the --json output and gates
against tools/validation/gsdump/native_baseline.json.
"""
import glob
import json
import os
import sys
from datetime import date

import numpy as np
from PIL import Image

DEFAULT_SNAPS_DIR = os.environ.get(
    "PCSX2_SNAPS_DIR",
    os.path.join(os.path.expanduser("~"), "Documents", "PCSX2", "snaps"),
)
ANCHOR_LOW_CONFIDENCE = 0.25  # gradient-NCC below this -> fall back to content-match
OUTLIER_MAE = 25.0            # natMAE above this -> frame/content mismatch, flag outlier


def load(path):
    return np.asarray(Image.open(path).convert("RGB")).astype(float)


def find_one(patterns):
    for p in patterns:
        fs = glob.glob(p)
        if fs:
            return fs[0]
    return None


def find_our_fbp0(our_dir, tag):
    """Locate our FBP0 render for a capture tag (legacy naming first, then the
    current gsdump --dump-fbp-buffers naming)."""
    return find_one([
        f"{our_dir}/*{tag}*.FBP-0_*PSM-00_FBMSK-00000000.png",
        f"TestOutput/w{tag}/*{tag}*.FBP-0_*PSM-00_FBMSK-00000000.png",
        f"{our_dir}/*{tag}*.fbp_buffers/fbp_00000_*C_32*.png",
        f"{our_dir}/*{tag}*/fbp_00000_*C_32*.png",
        f"TestOutput/w{tag}/*.fbp_buffers/fbp_00000_*C_32*.png",
    ])


def find_embedded(our_dir, tag):
    return find_one([f"{our_dir}/*{tag}*.pcsx2.png", f"TestOutput/w{tag}/*{tag}*.pcsx2.png"])


def full_frames(natdir):
    """All complete, full-size (>=620x440) native rt0_00000 dumps for a capture."""
    out = []
    for f in sorted(glob.glob(natdir + "/*_rt0_00000_C_32.png")):
        if os.path.getsize(f) < 1000:
            continue
        try:
            w, h = Image.open(f).size
        except Exception:
            continue
        if w >= 620 and h >= 440:
            out.append(f)
    return out


def best_shift(a, b, pad=2):
    h = min(a.shape[0], b.shape[0])
    w = min(a.shape[1], b.shape[1])
    a, b = a[:h, :w], b[:h, :w]
    best = None
    for dy in range(-pad, pad + 1):
        for dx in range(-pad, pad + 1):
            ax0, ay0 = max(0, dx), max(0, dy)
            bx0, by0 = max(0, -dx), max(0, -dy)
            ww, hh = w - abs(dx), h - abs(dy)
            aa = a[ay0:ay0 + hh, ax0:ax0 + ww]
            bb = b[by0:by0 + hh, bx0:bx0 + ww]
            m = np.abs(aa - bb).mean()
            if best is None or m < best[0]:
                best = (m, aa, bb)
    return best


def best_matching_frame(our, frames):
    """PCSX2 replays the .gs across many frames; pick the native frame whose content
    best matches our final FBP0 (lowest shift-aligned MAE) — the corresponding frame."""
    best = None
    for f in frames:
        m, aa, bb = best_shift(our, load(f))
        if best is None or m < best[0]:
            best = (m, aa, bb, f)
    return best


# ---------------------------------------------------------------------------
# --anchor mode: tone-invariant structural frame selection (numpy only)
# ---------------------------------------------------------------------------

def to_gray(img):
    return img.mean(axis=2) if img.ndim == 3 else img


def autocrop_borders(g, thresh=8.0):
    """Strip near-black letterbox/pillarbox borders from a grayscale screenshot."""
    rows = np.where(g.max(axis=1) > thresh)[0]
    cols = np.where(g.max(axis=0) > thresh)[0]
    if rows.size == 0 or cols.size == 0:
        return g
    return g[rows[0]:rows[-1] + 1, cols[0]:cols[-1] + 1]


def resize_gray(g, w, h):
    im = Image.fromarray(np.clip(g, 0, 255).astype(np.uint8), "L")
    return np.asarray(im.resize((w, h), Image.BILINEAR)).astype(float)


def gradient_magnitude(g):
    gy, gx = np.gradient(g)
    return np.hypot(gx, gy)


def ncc(a, b):
    a = a - a.mean()
    b = b - b.mean()
    d = np.sqrt((a * a).sum() * (b * b).sum())
    return float((a * b).sum() / d) if d > 0 else 0.0


def structural_similarity(anchor_gray_cropped, frame_gray):
    """Gradient-magnitude NCC between the (resized) anchor screenshot and a native
    frame. Gradients kill the tone offset; normalization kills the tone scale, so a
    tone-biased HW screenshot still scores its own frame highest."""
    a = resize_gray(anchor_gray_cropped, frame_gray.shape[1], frame_gray.shape[0])
    return ncc(gradient_magnitude(a), gradient_magnitude(frame_gray))


def anchor_select(anchor_path, frames):
    """Pick the native frame structurally closest to the sibling screenshot.
    Returns (frame_path, confidence)."""
    ag = autocrop_borders(to_gray(load(anchor_path)))
    best_f, best_s = None, -2.0
    for f in frames:
        s = structural_similarity(ag, to_gray(load(f)))
        if s > best_s:
            best_f, best_s = f, s
    return best_f, best_s


# ---------------------------------------------------------------------------


def evaluate_capture(natdir, tag, our_dir, anchor_mode, snaps_dir):
    rec = {"tag": tag, "slope": None, "intercept": None, "mae": None,
           "matchedFrame": None, "anchorConfidence": None, "outlier": False,
           "outlierReason": None, "frameSelection": None, "embSlope": None}
    fbp0f = find_our_fbp0(our_dir, tag)
    if not fbp0f:
        rec["error"] = "no our FBP0 render"
        return rec
    rec["ourFbp0"] = fbp0f.replace("\\", "/")
    frames = full_frames(natdir)
    if not frames:
        rec["error"] = "no full-size native frame"
        return rec
    our = load(fbp0f)

    natf = None
    rec["frameSelection"] = "content"
    if anchor_mode:
        anchorf = find_one([os.path.join(snaps_dir, f"*{tag}.png")])
        if anchorf is None:
            print(f"  [{tag}] WARN: no sibling screenshot in {snaps_dir}; "
                  f"falling back to content-match (low confidence)")
        else:
            natf, conf = anchor_select(anchorf, frames)
            rec["anchorConfidence"] = round(conf, 4)
            rec["frameSelection"] = "anchor"
            if conf < ANCHOR_LOW_CONFIDENCE:
                print(f"  [{tag}] WARN: anchor confidence {conf:.3f} < "
                      f"{ANCHOR_LOW_CONFIDENCE}; falling back to content-match")
                natf = None
                rec["frameSelection"] = "content-fallback"

    if natf is not None:
        m, aa, bb = best_shift(our, load(natf))
    else:
        m, aa, bb, natf = best_matching_frame(our, frames)

    sl, ic = np.polyfit(bb.mean(axis=2).ravel(), aa.mean(axis=2).ravel(), 1)
    rec["slope"] = round(float(sl), 4)
    rec["intercept"] = round(float(ic), 2)
    rec["mae"] = round(float(m), 3)
    rec["matchedFrame"] = os.path.basename(natf)

    embf = find_embedded(our_dir, tag)
    if embf:
        emb = load(embf)
        h = min(our.shape[0], emb.shape[0])
        w = min(our.shape[1], emb.shape[1])
        embSl, _ = np.polyfit(emb[:h, :w].mean(axis=2).ravel(),
                              our[:h, :w].mean(axis=2).ravel(), 1)
        rec["embSlope"] = round(float(embSl), 4)

    # hard outlier rejection
    reasons = []
    if m > OUTLIER_MAE:
        reasons.append(f"natMAE {m:.1f} > {OUTLIER_MAE:g}")
    if len(frames) >= 3:
        idx = frames.index(natf)
        if idx == 0 or idx == len(frames) - 1:
            reasons.append(f"matched frame is {'first' if idx == 0 else 'last'} of batch")
    if reasons:
        rec["outlier"] = True
        rec["outlierReason"] = "; ".join(reasons)

    rec["ourMean"] = [int(v) for v in aa.mean(axis=(0, 1)).round(0)]
    rec["natMean"] = [int(v) for v in bb.mean(axis=(0, 1)).round(0)]
    return rec


def main():
    args = sys.argv[1:]
    our_dir = "TestOutput/sweep_fbp"
    native_root = "TestOutput"
    snaps_dir = DEFAULT_SNAPS_DIR
    json_path = None
    anchor_mode = "--anchor" in args
    if "--our-dir" in args:
        our_dir = args[args.index("--our-dir") + 1]
    if "--native-root" in args:
        native_root = args[args.index("--native-root") + 1]
    if "--snaps-dir" in args:
        snaps_dir = args[args.index("--snaps-dir") + 1]
    if "--json" in args:
        json_path = args[args.index("--json") + 1]

    print(f"our FBP0 dir: {our_dir}  |  native root: {native_root}  |  "
          f"frame selection: {'anchor' if anchor_mode else 'content-match'}")
    print(f"{'tag':>7} {'ourMean':>14} {'natMean':>14} {'natSlope':>8} {'natMAE':>7} "
          f"{'embSlope':>8} {'batch':>6} {'anchor':>7} {'outl':>4}")
    records = []
    for natdir in sorted(glob.glob(os.path.join(native_root, "pcsx2_native_*"))):
        tag = os.path.basename(natdir).split("pcsx2_native_")[-1]
        if tag.endswith("b"):
            continue  # skip scratch re-capture dirs
        rec = evaluate_capture(natdir, tag, our_dir, anchor_mode, snaps_dir)
        records.append(rec)
        if rec.get("error"):
            print(f"{tag:>7}  ({rec['error']})")
            continue
        batch = rec["matchedFrame"].split("_")[0]
        conf = f"{rec['anchorConfidence']:.3f}" if rec["anchorConfidence"] is not None else "-"
        emb = f"{rec['embSlope']:.3f}" if rec["embSlope"] is not None else "nan"
        print(f"{tag:>7} {str(tuple(rec['ourMean'])):>14} {str(tuple(rec['natMean'])):>14} "
              f"{rec['slope']:>8.2f} {rec['mae']:>7.2f} {emb:>8} {batch:>6} {conf:>7} "
              f"{'YES' if rec['outlier'] else '':>4}")
        if rec["outlier"]:
            print(f"  [{tag}] OUTLIER ({rec['outlierReason']}) — excluded from aggregate")

    good = [r for r in records if r.get("slope") is not None and not r["outlier"]]
    outliers = [r for r in records if r["outlier"]]
    aggregate = {
        "captures": len([r for r in records if r.get("slope") is not None]),
        "included": len(good),
        "outliers": len(outliers),
        "meanSlope": round(float(np.mean([r["slope"] for r in good])), 4) if good else None,
        "meanMae": round(float(np.mean([r["mae"] for r in good])), 3) if good else None,
        "meanEmbSlope": round(float(np.mean([r["embSlope"] for r in good
                                             if r["embSlope"] is not None])), 4)
        if any(r["embSlope"] is not None for r in good) else None,
    }
    if good:
        print(f"\n{aggregate['included']}/{aggregate['captures']} captures "
              f"({aggregate['outliers']} outlier(s) excluded) | "
              f"mean natSlope={aggregate['meanSlope']:.3f} (1.0=tone parity) | "
              f"mean natMAE={aggregate['meanMae']:.2f} | "
              f"mean embSlope={aggregate['meanEmbSlope'] if aggregate['meanEmbSlope'] is not None else float('nan')} "
              f"(biased-ref 'compression')")
    elif not records:
        print(f"\nno pcsx2_native_* capture dirs under {native_root} — "
              f"run tools/validation/gsdump/capture_all_native.ps1 first")

    if json_path:
        payload = {
            "generated": date.today().isoformat(),
            "ourDir": our_dir,
            "nativeRoot": native_root,
            "anchorMode": anchor_mode,
            "captures": records,
            "aggregate": aggregate,
        }
        parent = os.path.dirname(json_path)
        if parent:
            os.makedirs(parent, exist_ok=True)
        with open(json_path, "w", encoding="utf-8") as f:
            json.dump(payload, f, indent=2)
        print(f"wrote {json_path}")


if __name__ == "__main__":
    main()
