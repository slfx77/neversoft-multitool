"""Framebuffer-state-aligned bisect harness for gsreplay vs PCSX2 RT dumps.

Background
==========
PCSX2's per-draw RT dumper bumps its draw counter (s_n) per primitive batch.
Our gsreplay's renderAudit.DrawsSeen counter bumps per triangle — so a single
PCSX2 strip-batch can correspond to dozens of our DrawsSeen ticks. Direct
index-by-index comparison is therefore meaningless.

This harness aligns the two by FRAMEBUFFER STATE TRANSITIONS instead. Both
renderers traverse the same .gs packet sequence, so they both encounter the
same sequence of (FBP, FBW, PSM) target transitions — just at different draw
indices. Use those transitions as anchors and compare the framebuffer RT
content captured at each transition point.

Inputs
======
Ours (from `--save-rt-on-state-transition`):
    <ours_dir>/NNNNN_rt_BBBBB_C_NN.png
    where:
        NNNNN = our DrawsSeen at the transition moment
        BBBBB = FBP (block address) in 5-digit hex
        C_NN  = PSM tag (C_32 / C_24 / C_16)

PCSX2 (from `capture_pcsx2_rt_dumps.ps1`):
    <pcsx2_dir>/NNNNN_fFFFFF_rt[01]_BBBBB_C_NN.png       (RGB view)
    <pcsx2_dir>/NNNNN_fFFFFF_rt[01]_BBBBB_C_NN_alpha.png (alpha view)
    <pcsx2_dir>/NNNNN_context.txt                        (GS state)
    where NNNNN = s_n at draw time, FFFFF = vsync frame index

Usage
=====
    python fb_state_bisect.py <ours_dir> <pcsx2_dir> [--threshold N] [--frame F]

The harness:
    1. Parses both directories' filenames into ordered transition lists keyed
       by (FBP, FBW, PSM).
    2. PCSX2 produces ONE RT per primitive-batch even when the target is
       unchanged from the previous batch — collapse consecutive PCSX2 dumps
       targeting the same (FBP, PSM) into a single "transition entry" at the
       LAST occurrence (matches what our state-transition-mode captures).
    3. Walks both transition lists in parallel, pairing by occurrence order
       within each (FBP, PSM) key. For each paired transition, compute the
       luma mean delta + a coarse RGB-histogram divergence.
    4. Reports the first transition whose mean delta exceeds the threshold
       (default 5 luma units). That's the bisect "hit".

Output
======
    bisect.csv         per-transition pairing with metrics
    first_divergent/   directory containing the three PNGs (ours, theirs, diff)
                       for the first divergent transition, if any
"""

from __future__ import annotations

import argparse
import os
import re
import sys
from collections import defaultdict
from dataclasses import dataclass

try:
    from PIL import Image
    import numpy as np
except ImportError as exc:
    print(f"ERROR: PIL + numpy required ({exc})", file=sys.stderr)
    sys.exit(2)


OURS_FILENAME = re.compile(r"^(\d+)_rt_([0-9A-Fa-f]{5})_C_(\d+S?)\.png$")
PCSX2_FILENAME = re.compile(
    r"^(\d+)_f(\d+)_rt(\d)_([0-9A-Fa-f]{5})_C_(\d+S?)(?P<alpha>_alpha)?\.png$"
)


@dataclass
class TransitionRow:
    side: str          # "ours" or "pcsx2"
    draw_index: int
    fbp_hex: str       # 5-char hex (lowercase) for stable matching
    psm_tag: str       # "32" / "24" / "16" / "16S"
    path: str
    alpha_path: str | None = None
    frame: int | None = None


def _collect_ours(d: str) -> list[TransitionRow]:
    rows: list[TransitionRow] = []
    for fn in sorted(os.listdir(d)):
        m = OURS_FILENAME.match(fn)
        if not m:
            continue
        rows.append(TransitionRow(
            side="ours",
            draw_index=int(m.group(1)),
            fbp_hex=m.group(2).lower(),
            psm_tag=m.group(3),
            path=os.path.join(d, fn),
        ))
    return rows


def _collect_pcsx2(d: str, frame_filter: int | None) -> list[TransitionRow]:
    raw: list[tuple[str, int, int, str, str, bool]] = []
    for fn in sorted(os.listdir(d)):
        m = PCSX2_FILENAME.match(fn)
        if not m:
            continue
        draw, frame, rt_idx, fbp, psm = (
            int(m.group(1)),
            int(m.group(2)),
            int(m.group(3)),
            m.group(4).lower(),
            m.group(5),
        )
        is_alpha = m.group("alpha") is not None
        if rt_idx != 0:
            # rt1 is the second circuit; we only correlate against the primary RT.
            continue
        if frame_filter is not None and frame != frame_filter:
            continue
        raw.append((fn, draw, frame, fbp, psm, is_alpha))

    # Pair color + alpha PNGs by base draw/frame/fbp/psm.
    by_key: dict[tuple[int, int, str, str], dict[str, str]] = defaultdict(dict)
    for fn, draw, frame, fbp, psm, is_alpha in raw:
        key = (draw, frame, fbp, psm)
        by_key[key]["alpha" if is_alpha else "color"] = os.path.join(d, fn)

    # Collapse consecutive entries that target the same (fbp, psm) into a single
    # "transition" entry keyed at the LAST occurrence — matches what our
    # state-transition mode emits.
    rows: list[TransitionRow] = []
    prev_key: tuple[str, str] | None = None
    pending: TransitionRow | None = None
    for key in sorted(by_key.keys()):  # sort by (draw, frame, fbp, psm)
        draw, frame, fbp, psm = key
        state_key = (fbp, psm)
        if state_key != prev_key and pending is not None:
            rows.append(pending)
        files = by_key[key]
        pending = TransitionRow(
            side="pcsx2",
            draw_index=draw,
            fbp_hex=fbp,
            psm_tag=psm,
            path=files.get("color", ""),
            alpha_path=files.get("alpha"),
            frame=frame,
        )
        prev_key = state_key
    if pending is not None:
        rows.append(pending)
    return rows


def _key(row: TransitionRow) -> tuple[str, str]:
    return (row.fbp_hex, row.psm_tag)


def _pair_transitions(
    ours: list[TransitionRow], pcsx2: list[TransitionRow]
) -> list[tuple[TransitionRow, TransitionRow]]:
    """Pair by occurrence order within each (fbp, psm) bucket.

    Both renderers emit transitions in chronological order across the dump.
    Within a single (fbp, psm) bucket, the Nth ours-entry pairs with the
    Nth pcsx2-entry. If one side has fewer entries, the surplus is dropped
    (and reported separately by the caller).
    """
    ours_by_key: dict[tuple[str, str], list[TransitionRow]] = defaultdict(list)
    pcsx2_by_key: dict[tuple[str, str], list[TransitionRow]] = defaultdict(list)
    for row in ours:
        ours_by_key[_key(row)].append(row)
    for row in pcsx2:
        pcsx2_by_key[_key(row)].append(row)

    pairs: list[tuple[TransitionRow, TransitionRow]] = []
    for state_key in sorted(set(ours_by_key) | set(pcsx2_by_key)):
        o_rows = ours_by_key.get(state_key, [])
        p_rows = pcsx2_by_key.get(state_key, [])
        for o, p in zip(o_rows, p_rows):
            pairs.append((o, p))
    # Order the pairs by our draw index so the user reads them in render order.
    pairs.sort(key=lambda op: op[0].draw_index)
    return pairs


def _open_rgba(path: str) -> np.ndarray | None:
    if not path or not os.path.exists(path):
        return None
    try:
        img = Image.open(path).convert("RGBA")
        return np.array(img)
    except (OSError, ValueError):
        return None


def _crop_to_shared(a: np.ndarray, b: np.ndarray) -> tuple[np.ndarray, np.ndarray]:
    h = min(a.shape[0], b.shape[0])
    w = min(a.shape[1], b.shape[1])
    return a[:h, :w], b[:h, :w]


@dataclass
class PairMetric:
    pair_index: int
    fbp_hex: str
    psm_tag: str
    ours_draw: int
    pcsx2_draw: int
    luma_delta: float       # signed: ours - pcsx2
    abs_luma_delta: float
    rms_rgb_delta: float
    width: int
    height: int


def _compare(o: np.ndarray, p: np.ndarray) -> tuple[float, float, float, int, int]:
    o3, p3 = _crop_to_shared(o[..., :3].astype(np.float32), p[..., :3].astype(np.float32))
    luma_ours = o3.mean()
    luma_pcsx2 = p3.mean()
    diff = o3 - p3
    rms = float(np.sqrt((diff * diff).mean()))
    return float(luma_ours - luma_pcsx2), abs(float(luma_ours - luma_pcsx2)), rms, o3.shape[1], o3.shape[0]


def _save_first_divergent(
    pair_index: int,
    o_row: TransitionRow,
    p_row: TransitionRow,
    o_img: np.ndarray,
    p_img: np.ndarray,
    out_dir: str,
) -> None:
    os.makedirs(out_dir, exist_ok=True)
    stem = f"pair{pair_index:04d}_fbp{o_row.fbp_hex}_psm{o_row.psm_tag}"
    Image.fromarray(o_img).save(os.path.join(out_dir, f"{stem}_ours.png"))
    Image.fromarray(p_img).save(os.path.join(out_dir, f"{stem}_pcsx2.png"))
    o3, p3 = _crop_to_shared(o_img[..., :3].astype(np.int16), p_img[..., :3].astype(np.int16))
    diff = np.abs(o3 - p3).clip(0, 255).astype(np.uint8)
    Image.fromarray(diff).save(os.path.join(out_dir, f"{stem}_diff.png"))


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("ours_dir", help="Output directory from --save-rt-on-state-transition")
    ap.add_argument("pcsx2_dir", help="PCSX2 RT dump directory produced by capture_pcsx2_rt_dumps.ps1")
    ap.add_argument("--threshold", type=float, default=5.0,
                    help="Luma mean delta (|ours - pcsx2|) threshold for 'divergent'. Default 5.")
    ap.add_argument("--frame", type=int, default=None,
                    help="Filter PCSX2 captures to one specific frame index (default: all).")
    ap.add_argument("--csv", default="bisect.csv",
                    help="CSV output filename inside ours_dir (default: bisect.csv).")
    ap.add_argument("--first-divergent-dir", default="first_divergent",
                    help="Directory inside ours_dir for the first divergent transition's PNGs.")
    args = ap.parse_args()

    if not os.path.isdir(args.ours_dir):
        print(f"ERROR: ours dir not found: {args.ours_dir}", file=sys.stderr)
        return 1
    if not os.path.isdir(args.pcsx2_dir):
        print(f"ERROR: pcsx2 dir not found: {args.pcsx2_dir}", file=sys.stderr)
        return 1

    ours = _collect_ours(args.ours_dir)
    pcsx2 = _collect_pcsx2(args.pcsx2_dir, args.frame)
    print(f"ours transitions  : {len(ours)} (across {len({_key(r) for r in ours})} unique fbp/psm)")
    print(f"pcsx2 transitions : {len(pcsx2)} (across {len({_key(r) for r in pcsx2})} unique fbp/psm)")

    pairs = _pair_transitions(ours, pcsx2)
    if not pairs:
        print("No pairs to compare. Check that both directories captured against the same .gs.")
        return 1

    csv_path = os.path.join(args.ours_dir, args.csv)
    metrics: list[PairMetric] = []
    first_divergent_idx: int | None = None
    first_divergent_state: tuple[np.ndarray, np.ndarray, TransitionRow, TransitionRow] | None = None

    for idx, (o_row, p_row) in enumerate(pairs):
        o_img = _open_rgba(o_row.path)
        p_img = _open_rgba(p_row.path)
        if o_img is None or p_img is None:
            continue
        luma_delta, abs_delta, rms, w, h = _compare(o_img, p_img)
        metrics.append(PairMetric(
            pair_index=idx,
            fbp_hex=o_row.fbp_hex,
            psm_tag=o_row.psm_tag,
            ours_draw=o_row.draw_index,
            pcsx2_draw=p_row.draw_index,
            luma_delta=luma_delta,
            abs_luma_delta=abs_delta,
            rms_rgb_delta=rms,
            width=w,
            height=h,
        ))
        if abs_delta > args.threshold and first_divergent_idx is None:
            first_divergent_idx = idx
            first_divergent_state = (o_img, p_img, o_row, p_row)

    with open(csv_path, "w", encoding="utf-8") as fp:
        fp.write("pair,fbp,psm,ours_draw,pcsx2_draw,luma_delta,abs_luma_delta,rms_rgb_delta,w,h\n")
        for m in metrics:
            fp.write(
                f"{m.pair_index},{m.fbp_hex},{m.psm_tag},{m.ours_draw},{m.pcsx2_draw},"
                f"{m.luma_delta:.3f},{m.abs_luma_delta:.3f},{m.rms_rgb_delta:.3f},{m.width},{m.height}\n"
            )
    print(f"wrote {csv_path}  ({len(metrics)} pair rows)")

    if first_divergent_idx is None:
        print(f"no transition exceeded threshold |luma delta| > {args.threshold}.")
        return 0

    o_img, p_img, o_row, p_row = first_divergent_state  # type: ignore[misc]
    out_dir = os.path.join(args.ours_dir, args.first_divergent_dir)
    _save_first_divergent(first_divergent_idx, o_row, p_row, o_img, p_img, out_dir)
    m = metrics[first_divergent_idx]
    print()
    print(f"FIRST DIVERGENT TRANSITION:")
    print(f"  pair#         : {m.pair_index}")
    print(f"  fbp / psm     : 0x{m.fbp_hex} / PSMCT{m.psm_tag}")
    print(f"  ours draw     : {m.ours_draw}")
    print(f"  pcsx2 draw    : {m.pcsx2_draw}")
    print(f"  luma delta    : {m.luma_delta:+.2f} ({'ours brighter' if m.luma_delta > 0 else 'ours darker'})")
    print(f"  rms rgb delta : {m.rms_rgb_delta:.2f}")
    print(f"  PNGs written  : {out_dir}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
