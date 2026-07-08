#!/usr/bin/env python3
"""Locate THPS2-proto engine functions in other Neversoft PS1 executables.

Uses the symboled THPS2 PSX prototype (SLUS_900.86 + symbols.txt from the
matching-decomp project) as the reference: extracts the first N instruction
words of each target function, masks relocation-sensitive fields (lui/ori/
addiu address halves, $gp-relative load/store offsets, j/jal targets), and
slides the masked pattern over every word-aligned position of each target
executable's text segment. Same-source functions compiled by the same GCC
2.9x-psx toolchain match near-100%; genuine per-game code changes show up
as depressed scores.

Usage:
  python tools/diagnostics/psx_func_signature_scan.py [--window 64] [--json out.json]

Output: per-target markdown tables of function -> (candidate vaddr, score,
runner-up score) printed to stdout. Scores >= 0.95 are near-certain
counterparts; 0.80-0.95 usually the same routine with source drift; below
0.70 treat as not found.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path

import numpy as np

REPO = Path(__file__).resolve().parents[2]
BUILDS = REPO / "Sample" / "Builds"
SYMBOLS = REPO / "tools" / "ghidra" / "thps2-trg-proto" / "output" / "symbols.txt"
REFERENCE_EXE = (
    BUILDS / "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)" / "SLUS_900.86"
)

# Animation / character-mesh-selection function set. Names must match
# symbols.txt (PSY-Q mangled). Grouped by what they answer per-game.
TARGET_FUNCTIONS = [
    # v2 compressed anim codec + pose assembly
    "DecompressStream__FPUcPsii",
    "Decomp_GetAnimTransform__FP6CSuper",
    # playback state machine (entry table indexing, loop mode, speed)
    "RunAnim__6CSuperiiii",
    "CycleAnim__6CSuperiSc",
    "UpdateFrame__6CSuper",
    # part/pose remap (hawk-arm relevant)
    "SetAnimOrder__6CSuperUcUc",
    "CalculateAnimOrder__6CSuperPPciT1i",
    "PatchAnimationOrder__8MySkaterP6CSuper",
    "RenderSuperItem__FP4Item",
    # spool-side anim/model handling
    "Spool_FindAnim__FPc",
    "PreProcessAnimPacket__FPUiT0",
    "Spool_StripAnim__Fi",
    "Spool_StripAnimFile__FPc",
    "Spool_StripModel__FiPs",
    "Spool_GetReducedSkater__FPcT0",
    "Spool_GetModel__FUii",
    "Spool_LoadPSH__FPcPPcPii",
    # v1 tween + math helpers
    "M3dUtils_InterpolateVectors__FiiPUiP4Itemii",
    "M3dUtils_InBetween__FP6CSuper",
    "M3dUtils_AnglesFromMatrix__FP7SVECTORP6MATRIX",
    "M3d_BuildTransform__FP6CSuper",
    # loader (draw-enable XOR, NextLOD/zMax packing, ppModels population)
    "M3dInit_ParsePSX__Fi",
    # stitch re-encode
    "InsertStitch__8MySkaterP7SVECTORN31",
]

# label -> path relative to Sample/Builds. All PS-X EXE format MIPS binaries.
TARGET_EXES = {
    "apocalypse_final": "Apocalypse (1998-11-17, PSX - Final)/SLUS_003.73",
    "thps1_proto": "Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)/PSX.EXE",
    "thps1_final": "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)/SLUS_008.60",
    "spiderman_proto_feb": "Spider-Man (2000-2-18, PSX - Prototype)/PSX.EXE",
    "spiderman_final": "Spider-Man (2000-9-1, PSX - Final)/SLUS_008.75",
    "thps2_proto_jun": "Tony Hawk's Pro Skater 2 (2000-6-2, PSX - Prototype)/SLUS_900.86",
    "thps2_final": "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)/SLUS_010.66",
    "sm2ee_proto": "Spider-Man 2 - Enter Electro (2001-8-14, PSX - Prototype)/SLUS_013.78",
    "sm2ee_final_pal": "Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)/SLES_036.23",
    "sm2ee_rev1": "Spider-Man 2 - Enter Electro (2001-9-28, PSX - Rev1)/SLUS_013.78",
}

GP = 28  # $gp
SP = 29  # $sp

LOAD_STORE_OPS = {
    0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,  # lb lh lwl lw lbu lhu lwr
    0x28, 0x29, 0x2A, 0x2B, 0x2E,              # sb sh swl sw swr
    0x31, 0x32, 0x39, 0x3A,                    # lwc1 lwc2 swc1 swc2
}


def load_psx_exe(path: Path) -> tuple[int, bytes]:
    data = path.read_bytes()
    if data[:8] != b"PS-X EXE":
        raise ValueError(f"not a PS-X EXE: {path}")
    t_addr = struct.unpack_from("<I", data, 0x18)[0]
    t_size = struct.unpack_from("<I", data, 0x1C)[0]
    text = data[0x800 : 0x800 + t_size]
    return t_addr, text


def load_symbols(path: Path) -> dict[str, int]:
    table: dict[str, int] = {}
    for line in path.read_text().splitlines():
        parts = line.split()
        if len(parts) == 2 and parts[0].startswith("0x"):
            table[parts[1]] = int(parts[0], 16)
    return table


def word_mask(word: int) -> int:
    """Mask covering the relocation/layout-invariant bits of one instruction."""
    op = word >> 26
    rs = (word >> 21) & 31
    if op in (0x02, 0x03):  # j / jal: target moves between builds
        return 0xFC000000
    if op == 0x0F:  # lui: address upper half
        return 0xFFFF0000
    if op in (0x08, 0x09, 0x0D):  # addi/addiu/ori
        # $sp adjustments and li-from-$zero are source-derived constants;
        # anything else may be the low half of an address or a $gp offset.
        if rs in (SP, 0):
            return 0xFFFFFFFF
        return 0xFFFF0000
    if op in LOAD_STORE_OPS and rs == GP:  # .sdata layout differs per build
        return 0xFFFF0000
    return 0xFFFFFFFF


def build_pattern(
    ref_words: np.ndarray, ref_base: int, addr: int, length_words: int, window: int
) -> tuple[np.ndarray, np.ndarray]:
    start = (addr - ref_base) // 4
    count = min(window, length_words)
    pat = ref_words[start : start + count]
    mask = np.array([word_mask(int(w)) for w in pat], dtype=np.uint32)
    return pat & mask, mask


def scan(target_words: np.ndarray, pat: np.ndarray, mask: np.ndarray):
    k = len(pat)
    n = len(target_words) - k + 1
    if n <= 0:
        return None
    score = np.zeros(n, dtype=np.int32)
    for i in range(k):
        score += (target_words[i : i + n] & mask[i]) == pat[i]
    best = int(np.argmax(score))
    best_score = score[best] / k
    lo, hi = max(0, best - 16), min(n, best + 16)
    score[lo:hi] = -1
    runner = float(np.max(score)) / k if n > hi - lo else 0.0
    return best, best_score, runner


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--window", type=int, default=64, help="pattern length in words")
    ap.add_argument("--json", type=Path, help="also write results as JSON")
    args = ap.parse_args()

    symbols = load_symbols(SYMBOLS)
    sorted_addrs = sorted(set(symbols.values()))
    ref_base, ref_text = load_psx_exe(REFERENCE_EXE)
    ref_words = np.frombuffer(
        ref_text[: len(ref_text) // 4 * 4], dtype="<u4"
    ).astype(np.uint32)

    funcs = []
    for name in TARGET_FUNCTIONS:
        addr = symbols.get(name)
        if addr is None:
            print(f"WARN: symbol not found: {name}", file=sys.stderr)
            continue
        idx = sorted_addrs.index(addr)
        end = sorted_addrs[idx + 1] if idx + 1 < len(sorted_addrs) else addr + 4 * args.window
        funcs.append((name, addr, (end - addr) // 4))

    results: dict[str, dict[str, dict]] = {}
    for label, rel in TARGET_EXES.items():
        exe = BUILDS / rel
        if not exe.is_file():
            print(f"WARN: missing target exe: {exe}", file=sys.stderr)
            continue
        t_base, t_text = load_psx_exe(exe)
        t_words = np.frombuffer(
            t_text[: len(t_text) // 4 * 4], dtype="<u4"
        ).astype(np.uint32)

        rows = {}
        for name, addr, length in funcs:
            pat, mask = build_pattern(ref_words, ref_base, addr, length, args.window)
            hit = scan(t_words, pat, mask)
            if hit is None:
                continue
            best, best_score, runner = hit
            rows[name] = {
                "vaddr": t_base + 4 * best,
                "score": round(best_score, 3),
                "runner_up": round(runner, 3),
                "pattern_words": len(pat),
            }
        results[label] = rows

        print(f"\n## {label}  ({rel})")
        print(f"| function | candidate | score | runner-up |")
        print(f"|---|---|---|---|")
        for name, r in rows.items():
            tag = "" if r["score"] >= 0.95 else (" (drift)" if r["score"] >= 0.80 else " (weak)")
            print(
                f"| {name} | 0x{r['vaddr']:08X} | {r['score']:.3f}{tag} "
                f"| {r['runner_up']:.3f} |"
            )

    if args.json:
        args.json.write_text(json.dumps(results, indent=2))
        print(f"\nJSON written to {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
