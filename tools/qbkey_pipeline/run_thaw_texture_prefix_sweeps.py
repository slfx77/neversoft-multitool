#!/usr/bin/env python3
"""Run THAW-focused prefix-constrained GPU brute-force sweeps."""

from __future__ import annotations

import argparse
import json
import math
import os
import re
import subprocess
import tempfile
import time
from collections import Counter
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir()))
MATCH_RE = re.compile(r"^(0x[0-9A-Fa-f]{8})\s*:\s*(.+)$")


def load_texture_stems(path: Path) -> set[str]:
    stems: set[str] = set()
    for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        if "=" not in line:
            continue
        name = line.split("=", 1)[0].strip().lower()
        stem = Path(name).name
        while True:
            next_stem = re.sub(r"\.(png|qb|q|json|trg|ps2|wpc|ngc|xbx|tex|img|mdl|skin|scn)$", "", stem)
            if next_stem == stem:
                break
            stem = next_stem
        if "_" in stem:
            stems.add(stem)
    return stems


def derive_prefixes(stems: set[str], min_count: int, max_prefixes: int) -> list[tuple[str, int]]:
    counts: Counter[str] = Counter()
    for stem in stems:
        tokens = stem.split("_")
        if len(tokens) < 2:
            continue
        for width in (1, 2):
            if len(tokens) <= width:
                continue
            prefix = "_".join(tokens[:width]) + "_"
            if 2 <= len(prefix) <= 20:
                counts[prefix] += 1

    ranked = sorted(counts.items(), key=lambda item: (-item[1], len(item[0]), item[0]))
    return [item for item in ranked if item[1] >= min_count][:max_prefixes]


def build_trigram_model(stems: set[str]) -> tuple[Counter[str], int]:
    counts: Counter[str] = Counter()
    total = 0
    for stem in stems:
        text = f"^{stem}$"
        for index in range(len(text) - 2):
            trigram = text[index : index + 3]
            counts[trigram] += 1
            total += 1
    return counts, total


def score_stem(stem: str, trigram_counts: Counter[str], total_trigrams: int) -> float:
    text = f"^{stem}$"
    vocabulary = max(len(trigram_counts), 1)
    score = 0.0
    count = 0
    for index in range(len(text) - 2):
        trigram = text[index : index + 3]
        score += math.log((trigram_counts[trigram] + 1) / (total_trigrams + vocabulary))
        count += 1
    return score / max(count, 1)


def run_job(
    exe: Path,
    hash_file: Path,
    prefix: str,
    brute_max: int,
    mitm_max: int,
    include_digits: bool,
    local_size: int,
    items_per_work_item: int,
) -> dict[str, object]:
    cmd = [
        str(exe),
        "brute-gpu",
        "-f",
        str(hash_file),
        "-m",
        str(brute_max),
        "-M",
        str(mitm_max),
    ]
    if include_digits:
        cmd.append("-d")
    cmd.extend(
        [
            "-p",
            prefix,
            "-s",
            ".png",
            "-w",
            str(local_size),
            "-n",
            str(items_per_work_item),
        ]
    )

    started = time.perf_counter()
    completed = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="ignore")
    elapsed = time.perf_counter() - started

    matches: list[dict[str, str]] = []
    for line in completed.stdout.splitlines():
        match = MATCH_RE.match(line.strip())
        if match:
            matches.append({"hash": match.group(1).upper(), "name": match.group(2)})

    return {
        "prefix": prefix,
        "elapsed_seconds": round(elapsed, 3),
        "returncode": completed.returncode,
        "match_count": len(matches),
        "matches": matches,
        "stderr": completed.stderr,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Run THAW prefix-constrained GPU brute-force sweeps.")
    parser.add_argument(
        "--exe",
        type=Path,
        default=TEMP_DIR / "qbkey_pipeline_test.exe",
        help="Path to qbkey_pipeline executable.",
    )
    parser.add_argument(
        "--hash-file",
        type=Path,
        default=TEMP_DIR / "thaw_remaining_texture_hashes.txt",
        help="Path to THAW unresolved texture hash list.",
    )
    parser.add_argument(
        "--known-textures",
        type=Path,
        default=REPO_ROOT / "src" / "NeversoftMultitool" / "Core" / "QbKey" / "QbKeyNames.ThawGcTextures.txt",
        help="Known THAW texture names used to derive prefixes.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=TEMP_DIR,
        help="Directory for sweep outputs.",
    )
    parser.add_argument("--min-prefix-count", type=int, default=8, help="Minimum occurrences for derived prefixes.")
    parser.add_argument("--max-prefixes", type=int, default=12, help="Maximum number of prefixes to sweep.")
    parser.add_argument("--m", type=int, default=4, help="Max brute-force variable length.")
    parser.add_argument("--M", type=int, default=6, help="Max MITM variable length.")
    parser.add_argument("--digits", action="store_true", help="Include digits in the brute-force charset.")
    parser.add_argument("--w", type=int, default=128, help="GPU local work-group size.")
    parser.add_argument("--n", type=int, default=1, help="Candidates per GPU work-item.")
    parser.add_argument(
        "--score-percentile",
        type=float,
        default=5.0,
        help="Percentile cutoff from known stems for marking brute-force hits as plausible.",
    )
    args = parser.parse_args()

    exe = args.exe.resolve()
    hash_file = args.hash_file.resolve()
    known_textures = args.known_textures.resolve()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    if not exe.exists():
        raise SystemExit(f"Executable not found: {exe}")
    if not hash_file.exists():
        raise SystemExit(f"Hash file not found: {hash_file}")
    if not known_textures.exists():
        raise SystemExit(f"Known-texture table not found: {known_textures}")

    stems = load_texture_stems(known_textures)
    trigram_counts, total_trigrams = build_trigram_model(stems)
    known_scores = sorted(score_stem(stem, trigram_counts, total_trigrams) for stem in stems)
    cutoff_index = int(len(known_scores) * (args.score_percentile / 100.0))
    cutoff_index = max(0, min(len(known_scores) - 1, cutoff_index))
    plausible_cutoff = known_scores[cutoff_index]

    prefixes = derive_prefixes(stems, args.min_prefix_count, args.max_prefixes)
    if not prefixes:
        raise SystemExit("No prefixes met the minimum-count threshold.")

    jobs: list[dict[str, object]] = []
    for prefix, count in prefixes:
        job = run_job(
            exe=exe,
            hash_file=hash_file,
            prefix=prefix,
            brute_max=args.m,
            mitm_max=args.M,
            include_digits=args.digits,
            local_size=args.w,
            items_per_work_item=args.n,
        )
        job["known_prefix_count"] = count
        jobs.append(job)
        print(f"{prefix} -> {job['match_count']} match(es) in {job['elapsed_seconds']:.3f}s")

    all_matches: dict[str, str] = {}
    for job in jobs:
        for match in job["matches"]:
            stem = Path(match["name"]).stem.lower()
            score = score_stem(stem, trigram_counts, total_trigrams)
            match["score"] = round(score, 4)
            match["plausible"] = score >= plausible_cutoff
            all_matches[match["hash"]] = match["name"]

    plausible_unique = len(
        {
            match["hash"]
            for job in jobs
            for match in job["matches"]
            if match["plausible"]
        }
    )

    summary = {
        "stats": {
            "prefix_count": len(jobs),
            "unique_matches": len(all_matches),
            "plausible_unique_matches": plausible_unique,
            "hash_file": str(hash_file),
            "brute_max": args.m,
            "mitm_max": args.M,
            "include_digits": args.digits,
            "local_size": args.w,
            "items_per_work_item": args.n,
            "score_percentile": args.score_percentile,
            "plausible_cutoff": round(plausible_cutoff, 4),
        },
        "jobs": jobs,
        "matches": [{"hash": hash_value, "name": name} for hash_value, name in sorted(all_matches.items())],
    }

    json_path = output_dir / "thaw_prefix_sweeps.json"
    json_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    text_lines = [
        "THAW Texture Prefix Sweep Summary",
        "",
        f"Hash file: {hash_file}",
        f"Unique matches: {len(all_matches)}",
        f"Plausible unique matches: {plausible_unique}",
        f"Plausibility cutoff: {plausible_cutoff:.4f}",
        "",
    ]
    for job in jobs:
        text_lines.append(
            f"{job['prefix']} ({job['known_prefix_count']} known) -> {job['match_count']} match(es) in {job['elapsed_seconds']:.3f}s"
        )
        for match in job["matches"]:
            text_lines.append(
                f"  {match['hash']} : {match['name']} [score={match['score']:.4f}, plausible={match['plausible']}]"
            )
        if job["returncode"] != 0:
            text_lines.append(f"  returncode={job['returncode']}")

    text_path = output_dir / "thaw_prefix_sweeps.txt"
    text_path.write_text("\n".join(text_lines) + "\n", encoding="utf-8")

    print(f"Wrote summary: {text_path}")
    print(f"Wrote JSON:    {json_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
