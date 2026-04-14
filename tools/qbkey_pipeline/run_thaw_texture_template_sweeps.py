#!/usr/bin/env python3
"""Run THAW-focused one-slot template brute-force sweeps using learned grammar families."""

from __future__ import annotations

import argparse
import importlib.util
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import time
from collections import Counter
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir()))
MATCH_RE = re.compile(r"^(0x[0-9A-Fa-f]{8})\s*:\s*(.+)$")
SOURCE_PRIORITY = {"matched": 0, "build": 1, "known": 2, "script": 3}


@dataclass(frozen=True)
class TemplateJob:
    prefix: str
    suffix: str
    family: tuple[str, str]
    slot_index: int
    source_name: str
    source_stem: str
    observed_lengths: tuple[int, ...]
    distinct_values: int
    family_support: int
    include_digits: bool
    min_length: int
    brute_max: int
    mitm_max: int


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def estimate_candidate_count(max_len: int, include_digits: bool) -> int:
    charset_size = 37 if include_digits else 27
    return sum(charset_size ** length for length in range(1, max_len + 1))


def build_trigram_model(values: list[str]) -> tuple[Counter[str], int]:
    counts: Counter[str] = Counter()
    total = 0
    for value in values:
        text = f"^{value}$"
        for index in range(len(text) - 2):
            trigram = text[index : index + 3]
            counts[trigram] += 1
            total += 1
    return counts, total


def score_value(value: str, trigram_counts: Counter[str], total_trigrams: int) -> float:
    text = f"^{value}$"
    vocabulary = max(len(trigram_counts), 1)
    score = 0.0
    count = 0
    for index in range(len(text) - 2):
        trigram = text[index : index + 3]
        score += math.log((trigram_counts[trigram] + 1) / (total_trigrams + vocabulary))
        count += 1
    return score / max(count, 1)


def extract_unknown_token(name: str, prefix: str, suffix: str) -> str:
    lower = Path(name).name.lower()
    if not lower.startswith(prefix.lower()):
        return ""
    if suffix and not lower.endswith(suffix.lower()):
        return ""
    end = len(lower) - len(suffix) if suffix else len(lower)
    if end < len(prefix):
        return ""
    return lower[len(prefix):end]


def choose_jobs(
    grammar,
    seed,
    known_stems: set[str],
    matched_stems: set[str],
    build_stems: set[str],
    script_stems: set[str],
    max_body_tokens: int,
    min_family_count: int,
    min_distinct_values: int,
    min_search_length: int,
    max_search_length: int,
    max_variable_length: int,
    brute_cap: int,
) -> list[TemplateJob]:
    family_examples, _family_maps, _slot_values, _slot_digits = grammar.build_family_profiles(
        known_stems,
        max_body_tokens=max_body_tokens,
        min_family_count=min_family_count,
    )
    jobs: dict[tuple[str, str, bool, int, int], TemplateJob] = {}

    source_stems = {
        "known": known_stems,
        "matched": matched_stems,
        "build": build_stems,
        "script": script_stems,
    }

    for source_name, stems in source_stems.items():
        for stem in sorted(stems):
            info = grammar.parse_stem(stem, max_body_tokens)
            if info is None:
                continue
            family = (info.prefix, info.body_signature)
            if family not in family_examples:
                continue

            examples = family_examples[family]
            family_support = len(examples)
            map_part = f"_{info.map_suffix}" if info.map_suffix else ""

            for slot_index, token in enumerate(info.tokens):
                if token.category != "word":
                    continue

                observed_values = sorted({example.tokens[slot_index].raw for example in examples})
                if len(observed_values) < min_distinct_values:
                    continue

                observed_lengths = sorted({len(value) for value in observed_values if value})
                if not observed_lengths:
                    continue
                include_digits = any(any(ch.isdigit() for ch in value) for value in observed_values)
                prefix_parts = [info.prefix, *(item.raw for item in info.tokens[:slot_index])]
                prefix = "_".join(prefix_parts) + "_"

                suffix_tokens = [item.raw for item in info.tokens[slot_index + 1 :]]
                suffix = ""
                if suffix_tokens:
                    suffix += "_" + "_".join(suffix_tokens)
                if info.map_suffix:
                    suffix += map_part
                suffix += ".png"

                for search_length in observed_lengths:
                    if search_length < min_search_length or search_length > max_search_length:
                        continue
                    if search_length > max_variable_length:
                        continue

                    brute_max = min(search_length, brute_cap)
                    mitm_max = search_length if search_length > brute_cap else 0

                    key = (prefix, suffix, include_digits, search_length)
                    candidate = TemplateJob(
                        prefix=prefix,
                        suffix=suffix,
                        family=family,
                        slot_index=slot_index,
                        source_name=source_name,
                        source_stem=info.stem,
                        observed_lengths=tuple(observed_lengths),
                        distinct_values=len(observed_values),
                        family_support=family_support,
                        include_digits=include_digits,
                        min_length=search_length,
                        brute_max=brute_max,
                        mitm_max=mitm_max,
                    )
                    existing = jobs.get(key)
                    if existing is None:
                        jobs[key] = candidate
                    else:
                        existing_rank = (
                            SOURCE_PRIORITY.get(existing.source_name, 99),
                            -existing.family_support,
                            -existing.distinct_values,
                            len(existing.prefix) + len(existing.suffix),
                            existing.source_stem,
                        )
                        candidate_rank = (
                            SOURCE_PRIORITY.get(candidate.source_name, 99),
                            -candidate.family_support,
                            -candidate.distinct_values,
                            len(candidate.prefix) + len(candidate.suffix),
                            candidate.source_stem,
                        )
                        if candidate_rank < existing_rank:
                            jobs[key] = candidate

    ranked = sorted(
        jobs.values(),
        key=lambda job: (
            SOURCE_PRIORITY.get(job.source_name, 99),
            -job.family_support,
            -job.distinct_values,
            job.min_length,
            estimate_candidate_count(job.mitm_max or job.brute_max, job.include_digits),
            len(job.prefix) + len(job.suffix),
            job.prefix,
            job.suffix,
        ),
    )
    return ranked


def select_jobs(ranked_jobs: list[TemplateJob], max_jobs: int, selection_mode: str) -> list[TemplateJob]:
    if max_jobs <= 0:
        return []
    if selection_mode == "rank":
        return ranked_jobs[:max_jobs]
    if selection_mode != "alternate-slots":
        raise ValueError(f"Unknown selection mode: {selection_mode}")

    by_slot: dict[int, list[TemplateJob]] = {}
    for job in ranked_jobs:
        by_slot.setdefault(job.slot_index, []).append(job)

    selected: list[TemplateJob] = []
    offsets = {slot: 0 for slot in by_slot}
    while len(selected) < max_jobs:
        progressed = False
        for slot in sorted(by_slot):
            index = offsets[slot]
            if index >= len(by_slot[slot]):
                continue
            selected.append(by_slot[slot][index])
            offsets[slot] += 1
            progressed = True
            if len(selected) >= max_jobs:
                break
        if not progressed:
            break

    return selected


def run_job(
    exe: Path,
    hash_file: Path,
    job: TemplateJob,
    local_size: int,
    items_per_work_item: int,
) -> dict[str, object]:
    cmd = [
        str(exe),
        "brute-gpu",
        "-f",
        str(hash_file),
        "-m",
        str(job.brute_max),
        "-l",
        str(job.min_length),
        "-p",
        job.prefix,
        "-s",
        job.suffix,
        "-w",
        str(local_size),
        "-n",
        str(items_per_work_item),
    ]
    if job.mitm_max > job.brute_max:
        cmd.extend(["-M", str(job.mitm_max)])
    if job.include_digits:
        cmd.append("-d")

    started = time.perf_counter()
    completed = subprocess.run(cmd, capture_output=True, text=True, encoding="utf-8", errors="ignore")
    elapsed = time.perf_counter() - started

    matches: list[dict[str, object]] = []
    for line in completed.stdout.splitlines():
        match = MATCH_RE.match(line.strip())
        if match:
            matches.append({"hash": match.group(1).upper(), "name": match.group(2)})

    return {
        "job": job,
        "elapsed_seconds": round(elapsed, 3),
        "returncode": completed.returncode,
        "matches": matches,
        "match_count": len(matches),
        "stderr": completed.stderr,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Run THAW one-slot template GPU sweeps.")
    parser.add_argument(
        "--exe",
        type=Path,
        default=TEMP_DIR / "qbkey_pipeline_test.exe",
        help="Path to qbkey_pipeline executable.",
    )
    parser.add_argument(
        "--hash-file",
        type=Path,
        default=TEMP_DIR / "thaw_grammar_remaining_texture_hashes.txt",
        help="Path to THAW unresolved texture hash list.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=TEMP_DIR,
        help="Directory for outputs.",
    )
    parser.add_argument(
        "--matched-hashes",
        type=Path,
        default=TEMP_DIR / "matched_hashes.json",
        help="matched_hashes.json path.",
    )
    parser.add_argument(
        "--script-names",
        type=Path,
        default=TEMP_DIR / "decompiled_script_names.txt",
        help="decompiled_script_names.txt path.",
    )
    parser.add_argument(
        "--known-textures",
        type=Path,
        default=REPO_ROOT / "src" / "NeversoftMultitool" / "Core" / "QbKey" / "QbKeyNames.ThawGcTextures.txt",
        help="Known THAW texture-name table.",
    )
    parser.add_argument("--max-jobs", type=int, default=12, help="Maximum template jobs to run.")
    parser.add_argument(
        "--selection-mode",
        choices=("alternate-slots", "rank"),
        default="alternate-slots",
        help="How to choose jobs from the ranked pool.",
    )
    parser.add_argument("--max-body-tokens", type=int, default=5, help="Maximum body-token count to model.")
    parser.add_argument("--min-family-count", type=int, default=3, help="Minimum known-name count for a family.")
    parser.add_argument("--min-distinct-values", type=int, default=4, help="Minimum distinct known values for the unknown slot.")
    parser.add_argument("--min-search-length", type=int, default=1, help="Minimum exact unknown-slot length to search.")
    parser.add_argument("--max-search-length", type=int, default=8, help="Maximum exact unknown-slot length to search.")
    parser.add_argument("--max-variable-length", type=int, default=8, help="Maximum unknown-slot length to search.")
    parser.add_argument("--brute-cap", type=int, default=6, help="Max pure-brute length before enabling MITM for a job.")
    parser.add_argument("--w", type=int, default=128, help="GPU local work-group size.")
    parser.add_argument("--n", type=int, default=1, help="Candidates per GPU work-item.")
    parser.add_argument(
        "--score-percentile",
        type=float,
        default=10.0,
        help="Per-job percentile cutoff from observed slot tokens for marking hits as plausible.",
    )
    parser.add_argument("--dry-run", action="store_true", help="Only emit the ranked template jobs without running brute-gpu.")
    args = parser.parse_args()

    exe = args.exe.resolve()
    hash_file = args.hash_file.resolve()
    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    if not args.dry_run and not exe.exists():
        raise SystemExit(f"Executable not found: {exe}")
    if not hash_file.exists():
        raise SystemExit(f"Hash file not found: {hash_file}")

    seed = load_module(REPO_ROOT / "tools" / "qbkey_pipeline" / "generate_thaw_texture_seeds.py", "thaw_seed_module")
    grammar = load_module(
        REPO_ROOT / "tools" / "qbkey_pipeline" / "generate_thaw_texture_grammar_candidates.py",
        "thaw_grammar_module",
    )

    known_stems, prefix_counts = seed.load_known_texture_stems(args.known_textures.resolve())
    known_prefixes = {prefix for prefix, count in prefix_counts.items() if count >= 3}
    matched_stems = seed.load_matched_stems(args.matched_hashes.resolve())
    script_stems = seed.load_script_stems(args.script_names.resolve(), known_prefixes)
    build_roots = seed.get_default_build_roots()
    build_stems = seed.load_build_stems(build_roots, known_prefixes)

    jobs = choose_jobs(
        grammar=grammar,
        seed=seed,
        known_stems=known_stems,
        matched_stems=matched_stems,
        build_stems=build_stems,
        script_stems=script_stems,
        max_body_tokens=args.max_body_tokens,
        min_family_count=args.min_family_count,
        min_distinct_values=args.min_distinct_values,
        min_search_length=args.min_search_length,
        max_search_length=args.max_search_length,
        max_variable_length=args.max_variable_length,
        brute_cap=args.brute_cap,
    )

    selected_jobs = select_jobs(jobs, args.max_jobs, args.selection_mode)
    job_lines = [
        "THAW Template Job Summary",
        "",
        f"Total candidate jobs: {len(jobs)}",
        f"Selected jobs: {len(selected_jobs)}",
        f"Selection mode: {args.selection_mode}",
        "",
    ]
    for index, job in enumerate(selected_jobs, start=1):
        job_lines.append(
            f"{index:02d}. prefix='{job.prefix}' suffix='{job.suffix}' family={job.family[0]}:{job.family[1]} "
            f"slot_index={job.slot_index} source={job.source_name}:{job.source_stem} lengths={list(job.observed_lengths)} "
            f"search_len={job.min_length} distinct={job.distinct_values} support={job.family_support} digits={job.include_digits} "
            f"brute={job.brute_max} mitm={job.mitm_max}"
        )

    jobs_path = output_dir / "thaw_template_jobs.txt"
    jobs_path.write_text("\n".join(job_lines) + "\n", encoding="utf-8")

    jobs_json_path = output_dir / "thaw_template_jobs.json"
    jobs_json_path.write_text(
        json.dumps(
            [
                {
                    "prefix": job.prefix,
                    "suffix": job.suffix,
                    "family": f"{job.family[0]}:{job.family[1]}",
                    "slot_index": job.slot_index,
                    "source_name": job.source_name,
                    "source_stem": job.source_stem,
                    "observed_lengths": list(job.observed_lengths),
                    "distinct_values": job.distinct_values,
                    "family_support": job.family_support,
                    "include_digits": job.include_digits,
                    "min_length": job.min_length,
                    "brute_max": job.brute_max,
                    "mitm_max": job.mitm_max,
                }
                for job in selected_jobs
            ],
            indent=2,
        ),
        encoding="utf-8",
    )

    if args.dry_run:
        print(f"Wrote jobs: {jobs_path}")
        print(f"Wrote JSON: {jobs_json_path}")
        return 0

    family_examples, _family_maps, _slot_values, _slot_digits = grammar.build_family_profiles(
        known_stems,
        max_body_tokens=args.max_body_tokens,
        min_family_count=args.min_family_count,
    )

    results: list[dict[str, object]] = []
    raw_unique_matches: dict[str, str] = {}
    plausible_unique_matches: dict[str, str] = {}

    for job in selected_jobs:
        result = run_job(
            exe=exe,
            hash_file=hash_file,
            job=job,
            local_size=args.w,
            items_per_work_item=args.n,
        )

        slot_values = sorted({example.tokens[job.slot_index].raw for example in family_examples[job.family]})
        trigram_counts, total_trigrams = build_trigram_model(slot_values)
        known_scores = sorted(score_value(value, trigram_counts, total_trigrams) for value in slot_values)
        cutoff_index = int(len(known_scores) * (args.score_percentile / 100.0))
        cutoff_index = max(0, min(len(known_scores) - 1, cutoff_index))
        plausible_cutoff = known_scores[cutoff_index]

        plausible_count = 0
        for match in result["matches"]:
            token = extract_unknown_token(match["name"], job.prefix, job.suffix)
            score = score_value(token, trigram_counts, total_trigrams) if token else float("-inf")
            plausible = bool(
                token
                and len(token) in job.observed_lengths
                and score >= plausible_cutoff
            )
            match["token"] = token
            match["score"] = round(score, 4) if token else None
            match["plausible"] = plausible
            raw_unique_matches[match["hash"]] = match["name"]
            if plausible:
                plausible_unique_matches[match["hash"]] = match["name"]
                plausible_count += 1

        result["plausible_count"] = plausible_count
        result["plausible_cutoff"] = round(plausible_cutoff, 4)
        results.append(result)
        print(
            f"{job.prefix}*{job.suffix} -> {result['match_count']} raw / {plausible_count} plausible "
            f"in {result['elapsed_seconds']:.3f}s"
        )

    plausible_names_path = output_dir / "thaw_template_plausible_names.txt"
    plausible_names_path.write_text(
        "".join(f"{name}\n" for _hash, name in sorted(plausible_unique_matches.items())),
        encoding="ascii",
    )

    summary = {
        "stats": {
            "candidate_jobs": len(jobs),
            "selected_jobs": len(selected_jobs),
            "selection_mode": args.selection_mode,
            "raw_unique_matches": len(raw_unique_matches),
            "plausible_unique_matches": len(plausible_unique_matches),
            "hash_file": str(hash_file),
            "local_size": args.w,
            "items_per_work_item": args.n,
            "score_percentile": args.score_percentile,
        },
        "results": [
            {
                "prefix": result["job"].prefix,
                "suffix": result["job"].suffix,
                "family": f"{result['job'].family[0]}:{result['job'].family[1]}",
                "slot_index": result["job"].slot_index,
                "source_name": result["job"].source_name,
                "source_stem": result["job"].source_stem,
                "observed_lengths": list(result["job"].observed_lengths),
                "distinct_values": result["job"].distinct_values,
                "family_support": result["job"].family_support,
                "include_digits": result["job"].include_digits,
                "min_length": result["job"].min_length,
                "brute_max": result["job"].brute_max,
                "mitm_max": result["job"].mitm_max,
                "elapsed_seconds": result["elapsed_seconds"],
                "returncode": result["returncode"],
                "match_count": result["match_count"],
                "plausible_count": result["plausible_count"],
                "plausible_cutoff": result["plausible_cutoff"],
                "matches": result["matches"],
            }
            for result in results
        ],
    }

    json_path = output_dir / "thaw_template_sweeps.json"
    json_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")

    text_lines = [
        "THAW Template Sweep Summary",
        "",
        f"Candidate jobs: {len(jobs)}",
        f"Selected jobs: {len(selected_jobs)}",
        f"Selection mode: {args.selection_mode}",
        f"Raw unique matches: {len(raw_unique_matches)}",
        f"Plausible unique matches: {len(plausible_unique_matches)}",
        "",
    ]
    for result in results:
        job = result["job"]
        text_lines.append(
            f"{job.prefix}*{job.suffix} [slot={job.slot_index}, {job.source_name}:{job.source_stem}] "
            f"family={job.family[0]}:{job.family[1]} -> "
            f"{result['match_count']} raw / {result['plausible_count']} plausible "
            f"len={job.min_length} "
            f"in {result['elapsed_seconds']:.3f}s cutoff={result['plausible_cutoff']:.4f}"
        )
        for match in result["matches"]:
            text_lines.append(
                f"  {match['hash']} : {match['name']} [token={match['token']}, score={match['score']}, plausible={match['plausible']}]"
            )
        if result["returncode"] != 0:
            text_lines.append(f"  returncode={result['returncode']}")

    text_path = output_dir / "thaw_template_sweeps.txt"
    text_path.write_text("\n".join(text_lines) + "\n", encoding="utf-8")

    print(f"Wrote jobs:      {jobs_path}")
    print(f"Wrote JSON:      {json_path}")
    print(f"Wrote summary:   {text_path}")
    print(f"Wrote plausible: {plausible_names_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
