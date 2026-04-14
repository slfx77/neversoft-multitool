#!/usr/bin/env python3
"""Generate THAW texture candidates by recombining observed token bodies."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import sys
import tempfile
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir()))


@dataclass(frozen=True)
class MatchRecord:
    hash_value: int
    candidate: str
    rule: str
    prefix: str
    source_stem: str


def load_seed_module():
    path = REPO_ROOT / "tools" / "qbkey_pipeline" / "generate_thaw_texture_seeds.py"
    spec = importlib.util.spec_from_file_location("thaw_seed_module", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def derive_prefix_counts(stems: set[str]) -> Counter[tuple[int, str]]:
    counts: Counter[tuple[int, str]] = Counter()
    for stem in stems:
        tokens = stem.split("_")
        for width in (1, 2):
            if len(tokens) <= width:
                continue
            prefix = "_".join(tokens[:width])
            counts[(width, prefix)] += 1
    return counts


def is_body_token(token: str) -> bool:
    if not token:
        return False
    if len(token) > 24:
        return False
    return token.replace(".", "").isalnum()


def build_prefix_model(
    stems: set[str],
    prefix_counts: Counter[tuple[int, str]],
    min_prefix_count: int,
    max_body_tokens: int,
) -> tuple[
    dict[str, dict[int, list[tuple[str, ...]]]],
    dict[str, dict[int, list[Counter[str]]]],
]:
    bodies_by_prefix: dict[str, dict[int, list[tuple[str, ...]]]] = defaultdict(lambda: defaultdict(list))
    position_counts: dict[str, dict[int, list[Counter[str]]]] = defaultdict(dict)

    allowed_prefixes = {
        prefix
        for (width, prefix), count in prefix_counts.items()
        if count >= min_prefix_count and width in (1, 2)
    }

    for stem in sorted(stems):
        tokens = stem.split("_")
        for width in (1, 2):
            if len(tokens) <= width:
                continue
            prefix = "_".join(tokens[:width])
            if prefix not in allowed_prefixes:
                continue
            body = tuple(tokens[width:])
            if not body or len(body) > max_body_tokens:
                continue
            if not all(is_body_token(token) for token in body):
                continue
            if body not in bodies_by_prefix[prefix][len(body)]:
                bodies_by_prefix[prefix][len(body)].append(body)

    for prefix, by_len in bodies_by_prefix.items():
        for body_len, bodies in by_len.items():
            counters = [Counter() for _ in range(body_len)]
            for body in bodies:
                for index, token in enumerate(body):
                    counters[index][token] += 1
            position_counts[prefix][body_len] = counters

    return bodies_by_prefix, position_counts


def emit_candidate(
    seed,
    unresolved_set: set[int],
    seen_candidates: set[str],
    generated_by_rule: Counter[str],
    matches: dict[int, list[MatchRecord]],
    stem: str,
    rule: str,
    prefix: str,
    source_stem: str,
) -> None:
    stem = seed.strip_known_suffixes(stem)
    if not seed.SIMPLE_STEM_RE.fullmatch(stem):
        return
    candidate = stem + ".png"
    if candidate in seen_candidates:
        return
    seen_candidates.add(candidate)
    generated_by_rule[rule] += 1
    hash_value = seed.qb_hash_lower(candidate)
    if hash_value not in unresolved_set:
        return
    record = MatchRecord(
        hash_value=hash_value,
        candidate=candidate,
        rule=rule,
        prefix=prefix,
        source_stem=source_stem,
    )
    if record not in matches[hash_value]:
        matches[hash_value].append(record)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate THAW texture candidates from observed token bodies.")
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
        default=TEMP_DIR / "thaw_remaining_texture_hashes.txt",
        help="Optional THAW-specific remainder file. If missing, unresolved hashes are derived from the investigator.",
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
    parser.add_argument("--min-prefix-count", type=int, default=6, help="Minimum source count for a prefix family.")
    parser.add_argument("--max-body-tokens", type=int, default=4, help="Maximum number of body tokens to model.")
    parser.add_argument("--top-per-position", type=int, default=10, help="Maximum tokens to borrow per body position.")
    args = parser.parse_args()

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    seed = load_seed_module()
    known_stems, prefix_source_counts = seed.load_known_texture_stems(args.known_textures.resolve())
    allowed_prefixes = {
        prefix for prefix, count in prefix_source_counts.items() if count >= 3
    } | {"pro", "ped", "cut", "cas", "cs", "body", "veh", "shirt", "pants", "shoes", "hair", "hat"}

    matched_stems = seed.load_matched_stems(args.matched_hashes.resolve())
    build_roots = seed.get_default_build_roots()
    build_stems = seed.load_build_stems(build_roots, allowed_prefixes)
    script_stems = seed.load_script_stems(args.script_names.resolve(), allowed_prefixes)

    model_stems = known_stems | matched_stems | build_stems | script_stems
    prefix_counts = derive_prefix_counts(model_stems)
    if args.hash_file.resolve().exists():
        unresolved = [
            int(line.strip(), 16)
            for line in args.hash_file.resolve().read_text(encoding="utf-8", errors="ignore").splitlines()
            if line.strip()
        ]
    else:
        unresolved = seed.load_unresolved_texture_hashes(seed.load_investigator(), args.matched_hashes.resolve())
    unresolved_set = set(unresolved)

    bodies_by_prefix, position_counts = build_prefix_model(
        model_stems,
        prefix_counts,
        min_prefix_count=args.min_prefix_count,
        max_body_tokens=args.max_body_tokens,
    )

    seen_candidates: set[str] = set()
    generated_by_rule: Counter[str] = Counter()
    matches: dict[int, list[MatchRecord]] = defaultdict(list)

    for prefix, by_len in bodies_by_prefix.items():
        root = prefix.split("_", 1)[0]
        sibling_prefixes = [
            other_prefix
            for other_prefix in bodies_by_prefix
            if other_prefix != prefix and other_prefix.split("_", 1)[0] == root
        ]

        for body_len, bodies in by_len.items():
            counters = position_counts[prefix][body_len]
            top_tokens = [
                [token for token, _count in counter.most_common(args.top_per_position)]
                for counter in counters
            ]

            for body in bodies:
                source_stem = prefix + "_" + "_".join(body)

                for index, token_choices in enumerate(top_tokens):
                    for replacement in token_choices:
                        if replacement == body[index]:
                            continue
                        candidate_body = list(body)
                        candidate_body[index] = replacement
                        emit_candidate(
                            seed,
                            unresolved_set,
                            seen_candidates,
                            generated_by_rule,
                            matches,
                            prefix + "_" + "_".join(candidate_body),
                            "positional_swap",
                            prefix,
                            source_stem,
                        )

                for sibling_prefix in sibling_prefixes:
                    sibling_bodies = bodies_by_prefix[sibling_prefix].get(body_len, [])
                    for sibling_body in sibling_bodies[: args.top_per_position]:
                        emit_candidate(
                            seed,
                            unresolved_set,
                            seen_candidates,
                            generated_by_rule,
                            matches,
                            prefix + "_" + "_".join(sibling_body),
                            "sibling_body",
                            prefix,
                            sibling_prefix + "_" + "_".join(sibling_body),
                        )

    matched_hashes = sorted(matches)
    remaining = [value for value in unresolved if value not in matches]
    matched_names = sorted({record.candidate for records in matches.values() for record in records})

    names_path = output_dir / "thaw_token_match_names.txt"
    names_path.write_text("".join(f"{name}\n" for name in matched_names), encoding="ascii")

    remaining_path = output_dir / "thaw_token_remaining_texture_hashes.txt"
    remaining_path.write_text("".join(f"0x{value:08X}\n" for value in remaining), encoding="ascii")

    summary_lines = [
        "THAW Token Texture Candidate Summary",
        "",
        f"Known stems: {len(known_stems)}",
        f"Matched-name stems: {len(matched_stems)}",
        f"Build stems: {len(build_stems)}",
        f"Script stems: {len(script_stems)}",
        f"Model stems: {len(model_stems)}",
        f"Prefix families: {len(bodies_by_prefix)}",
        f"Unresolved THAW texture hashes: {len(unresolved)}",
        f"Generated candidate names: {len(seen_candidates)}",
        f"Matched candidate names: {len(matched_names)}",
        f"Matched unresolved hashes: {len(matched_hashes)}",
        f"Remaining unresolved hashes: {len(remaining)}",
        "",
        "Rule counts:",
    ]
    for rule, count in generated_by_rule.most_common():
        summary_lines.append(f"  {rule}: {count}")

    if matched_hashes:
        summary_lines.append("")
        summary_lines.append("Matches:")
        for hash_value in matched_hashes:
            summary_lines.append(f"  0x{hash_value:08X}")
            for record in matches[hash_value]:
                summary_lines.append(
                    f"    {record.candidate} [{record.rule}, prefix={record.prefix}, source={record.source_stem}]"
                )

    text_path = output_dir / "thaw_token_texture_matches.txt"
    text_path.write_text("\n".join(summary_lines) + "\n", encoding="utf-8")

    json_path = output_dir / "thaw_token_texture_matches.json"
    json_path.write_text(
        json.dumps(
            {
                "stats": {
                    "known_stems": len(known_stems),
                    "matched_stems": len(matched_stems),
                    "build_stems": len(build_stems),
                    "script_stems": len(script_stems),
                    "model_stems": len(model_stems),
                    "prefix_families": len(bodies_by_prefix),
                    "unresolved_hashes": len(unresolved),
                    "generated_candidates": len(seen_candidates),
                    "matched_names": len(matched_names),
                    "matched_hashes": len(matched_hashes),
                    "remaining_hashes": len(remaining),
                    "rule_counts": dict(generated_by_rule),
                },
                "matches": {
                    f"0x{hash_value:08X}": [
                        {
                            "candidate": record.candidate,
                            "rule": record.rule,
                            "prefix": record.prefix,
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
    print(f"Generated candidates: {len(seen_candidates)}")
    print(f"Matched hashes:       {len(matched_hashes)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
