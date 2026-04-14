#!/usr/bin/env python3
"""Generate THAW texture candidates by fixing a prefix chain and permuting the remaining grammar slots."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
import tempfile
from collections import Counter, defaultdict
from dataclasses import dataclass
from itertools import product
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir()))


@dataclass(frozen=True)
class MatchRecord:
    hash_value: int
    candidate: str
    family: str
    fixed_prefix: str
    source_stem: str


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def load_unresolved_hashes(seed, hash_file: Path, matched_hashes: Path) -> list[int]:
    if hash_file.exists():
        return [
            int(line.strip(), 16)
            for line in hash_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            if line.strip()
        ]
    return seed.load_unresolved_texture_hashes(seed.load_investigator(), matched_hashes)


def add_match(
    matches: dict[int, list[MatchRecord]],
    hash_value: int,
    candidate: str,
    family: tuple[str, str],
    fixed_prefix: str,
    source_stem: str,
) -> None:
    record = MatchRecord(
        hash_value=hash_value,
        candidate=candidate,
        family=f"{family[0]}:{family[1]}",
        fixed_prefix=fixed_prefix,
        source_stem=source_stem,
    )
    if record not in matches[hash_value]:
        matches[hash_value].append(record)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate THAW prefix-fixed grammar permutations.")
    parser.add_argument("--output-dir", type=Path, default=TEMP_DIR, help="Directory for output files.")
    parser.add_argument(
        "--matched-hashes",
        type=Path,
        default=TEMP_DIR / "matched_hashes.json",
        help="matched_hashes.json path used to exclude already-resolved hashes.",
    )
    parser.add_argument(
        "--hash-file",
        type=Path,
        default=TEMP_DIR / "thaw_grammar_remaining_texture_hashes.txt",
        help="THAW unresolved texture hash list. Falls back to investigator-derived unresolved hashes if absent.",
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
    parser.add_argument("--max-body-tokens", type=int, default=5, help="Maximum body-token count to model.")
    parser.add_argument("--min-family-count", type=int, default=3, help="Minimum known-name count for a grammar family.")
    parser.add_argument("--min-fixed-body-tokens", type=int, default=1, help="Minimum number of body tokens to keep fixed after the root prefix.")
    parser.add_argument("--max-fixed-body-tokens", type=int, default=3, help="Maximum number of body tokens to keep fixed after the root prefix.")
    parser.add_argument("--max-candidates-per-prefix", type=int, default=100000, help="Skip prefix permutations whose projected cross-product exceeds this.")
    parser.add_argument(
        "--write-all-candidates",
        action="store_true",
        help="Also write the full generated candidate list for inspection.",
    )
    args = parser.parse_args()

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

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

    unresolved = load_unresolved_hashes(seed, args.hash_file.resolve(), args.matched_hashes.resolve())
    unresolved_set = set(unresolved)

    family_examples, family_maps, _slot_values, _slot_digits = grammar.build_family_profiles(
        known_stems,
        max_body_tokens=args.max_body_tokens,
        min_family_count=args.min_family_count,
    )

    source_stems = {
        "known": known_stems,
        "matched": matched_stems,
        "script": script_stems,
        "build": build_stems,
    }

    seen_prefix_jobs: set[tuple[tuple[str, str], tuple[str, ...], str]] = set()
    seen_candidates: set[str] = set()
    matches: dict[int, list[MatchRecord]] = defaultdict(list)
    candidate_count = 0
    skipped_prefix_jobs = 0
    job_count = 0
    source_count: Counter[str] = Counter()
    family_count: Counter[str] = Counter()
    fixed_prefix_count: Counter[str] = Counter()

    for source_name, stems in source_stems.items():
        for stem in sorted(stems):
            info = grammar.parse_stem(stem, args.max_body_tokens)
            if info is None:
                continue
            family = (info.prefix, info.body_signature)
            if family not in family_examples:
                continue

            examples = family_examples[family]
            map_choices = sorted(family_maps[family], key=lambda value: (value is not None, value or ""))
            max_fixed = min(len(info.tokens) - 1, args.max_fixed_body_tokens)
            if max_fixed < args.min_fixed_body_tokens:
                continue

            for fixed_body_count in range(args.min_fixed_body_tokens, max_fixed + 1):
                fixed_body_tokens = tuple(token.raw for token in info.tokens[:fixed_body_count])
                fixed_prefix = info.prefix + "_" + "_".join(fixed_body_tokens) + "_"
                source_key = (family, fixed_body_tokens)
                if source_key in seen_prefix_jobs:
                    continue
                seen_prefix_jobs.add(source_key)

                slot_choices = [
                    sorted({example.tokens[index].raw for example in examples})
                    for index in range(fixed_body_count, len(info.tokens))
                ]
                projected = max(1, len(map_choices))
                for choices in slot_choices:
                    projected *= max(1, len(choices))
                if projected > args.max_candidates_per_prefix:
                    skipped_prefix_jobs += 1
                    continue

                job_count += 1
                source_count[source_name] += 1
                family_count[f"{family[0]}:{family[1]}"] += 1
                fixed_prefix_count[fixed_prefix] += 1

                for combo in product(*slot_choices):
                    for map_suffix in map_choices:
                        parts = [info.prefix, *fixed_body_tokens, *combo]
                        if map_suffix:
                            parts.append(map_suffix)
                        candidate_stem = "_".join(parts)
                        if not seed.SIMPLE_STEM_RE.fullmatch(candidate_stem):
                            continue
                        candidate = candidate_stem + ".png"
                        if candidate in seen_candidates:
                            continue
                        seen_candidates.add(candidate)
                        candidate_count += 1
                        hash_value = seed.qb_hash_lower(candidate)
                        if hash_value in unresolved_set:
                            add_match(matches, hash_value, candidate, family, fixed_prefix, info.stem)

    matched_hashes = sorted(matches)
    matched_names = sorted({record.candidate for records in matches.values() for record in records})
    remaining = [value for value in unresolved if value not in matches]

    names_path = output_dir / "thaw_prefix_permutation_match_names.txt"
    names_path.write_text("".join(f"{name}\n" for name in matched_names), encoding="ascii")

    remaining_path = output_dir / "thaw_prefix_permutation_remaining_hashes.txt"
    remaining_path.write_text("".join(f"0x{value:08X}\n" for value in remaining), encoding="ascii")

    if args.write_all_candidates:
        candidate_path = output_dir / "thaw_prefix_permutation_candidates.txt"
        candidate_path.write_text("".join(f"{name}\n" for name in sorted(seen_candidates)), encoding="ascii")

    summary_lines = [
        "THAW Prefix Permutation Summary",
        "",
        f"Known stems: {len(known_stems)}",
        f"Matched-name stems: {len(matched_stems)}",
        f"Script stems: {len(script_stems)}",
        f"Build stems: {len(build_stems)}",
        f"Grammar families: {len(family_examples)}",
        f"Unresolved THAW texture hashes: {len(unresolved)}",
        f"Prefix permutation jobs: {job_count}",
        f"Skipped oversized prefix jobs: {skipped_prefix_jobs}",
        f"Generated candidate names: {candidate_count}",
        f"Matched candidate names: {len(matched_names)}",
        f"Matched unresolved hashes: {len(matched_hashes)}",
        f"Remaining unresolved hashes: {len(remaining)}",
        "",
        "Source counts:",
    ]
    for source_name, count in source_count.items():
        summary_lines.append(f"  {source_name}: {count}")

    summary_lines.append("")
    summary_lines.append("Top families:")
    for family, count in family_count.most_common(12):
        summary_lines.append(f"  {family}: {count}")

    summary_lines.append("")
    summary_lines.append("Top fixed prefixes:")
    for prefix, count in fixed_prefix_count.most_common(20):
        summary_lines.append(f"  {prefix}: {count}")

    if matched_hashes:
        summary_lines.append("")
        summary_lines.append("Matches:")
        for hash_value in matched_hashes:
            summary_lines.append(f"  0x{hash_value:08X}")
            for record in matches[hash_value]:
                summary_lines.append(
                    f"    {record.candidate} [family={record.family}, fixed_prefix={record.fixed_prefix}, source={record.source_stem}]"
                )

    text_path = output_dir / "thaw_prefix_permutation_matches.txt"
    text_path.write_text("\n".join(summary_lines) + "\n", encoding="utf-8")

    json_path = output_dir / "thaw_prefix_permutation_matches.json"
    json_path.write_text(
        json.dumps(
            {
                "stats": {
                    "known_stems": len(known_stems),
                    "matched_stems": len(matched_stems),
                    "script_stems": len(script_stems),
                    "build_stems": len(build_stems),
                    "grammar_families": len(family_examples),
                    "unresolved_hashes": len(unresolved),
                    "prefix_jobs": job_count,
                    "skipped_prefix_jobs": skipped_prefix_jobs,
                    "generated_candidates": candidate_count,
                    "matched_names": len(matched_names),
                    "matched_hashes": len(matched_hashes),
                    "remaining_hashes": len(remaining),
                    "source_counts": dict(source_count),
                    "family_counts": dict(family_count),
                    "fixed_prefix_counts": dict(fixed_prefix_count),
                },
                "matches": {
                    f"0x{hash_value:08X}": [
                        {
                            "candidate": record.candidate,
                            "family": record.family,
                            "fixed_prefix": record.fixed_prefix,
                            "source_stem": record.source_stem,
                        }
                        for record in matches[hash_value]
                    ]
                    for hash_value in matched_hashes
                },
                "remaining_unresolved": [f"0x{value:08X}" for value in remaining],
            },
            indent=2,
        ),
        encoding="utf-8",
    )

    print(f"Wrote matched names:  {names_path}")
    print(f"Wrote summary:        {text_path}")
    print(f"Wrote JSON:           {json_path}")
    print(f"Wrote remaining:      {remaining_path}")
    if args.write_all_candidates:
        print(f"Wrote candidate file: {output_dir / 'thaw_prefix_permutation_candidates.txt'}")
    print(f"Prefix jobs:          {job_count}")
    print(f"Generated candidates: {candidate_count}")
    print(f"Matched hashes:       {len(matched_hashes)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
