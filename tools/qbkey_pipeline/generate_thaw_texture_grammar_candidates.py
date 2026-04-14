#!/usr/bin/env python3
"""Generate THAW texture candidates from learned prefix-specific grammar families."""

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
MAP_SUFFIXES = ("n", "s", "x")
PART_BASES = {
    "arm",
    "arml",
    "armr",
    "bag",
    "belt",
    "body",
    "coin",
    "eye",
    "eyes",
    "face",
    "faceparts",
    "feet",
    "foot",
    "fore_l",
    "fore_r",
    "hair",
    "hand",
    "hands",
    "head",
    "hood",
    "jacket",
    "keys",
    "laces",
    "legl",
    "legr",
    "legs",
    "logo",
    "mouth",
    "pants",
    "shoe",
    "shoes",
    "teeth",
    "torso",
}
TOKEN_SWAPS = {
    "l": "r",
    "r": "l",
    "left": "right",
    "right": "left",
    "arml": "armr",
    "armr": "arml",
    "legl": "legr",
    "legr": "legl",
}
PREFIX_SLOT_LIMITS = {
    "ped": 24,
    "pro": 18,
    "cut": 18,
    "shirt": 16,
    "hair": 12,
    "hat": 12,
    "veh": 12,
}
PART_SLOT_LIMIT = 12
NUMERIC_SLOT_LIMIT = 12
WORD_SLOT_LIMIT = 18


@dataclass(frozen=True)
class TokenInfo:
    raw: str
    base: str
    digits: str
    category: str

    @property
    def slot_key(self) -> str:
        return self.category + ("#" if self.digits else "")


@dataclass(frozen=True)
class StemInfo:
    stem: str
    prefix: str
    tokens: tuple[TokenInfo, ...]
    map_suffix: str | None
    body_signature: str


@dataclass(frozen=True)
class MatchRecord:
    hash_value: int
    candidate: str
    rule: str
    family: str
    source_stem: str


def load_seed_module():
    path = REPO_ROOT / "tools" / "qbkey_pipeline" / "generate_thaw_texture_seeds.py"
    spec = importlib.util.spec_from_file_location("thaw_seed_module", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def split_digits(token: str) -> tuple[str, str]:
    end = len(token)
    while end > 0 and token[end - 1].isdigit():
        end -= 1
    return token[:end], token[end:]


def classify_base(base: str) -> str:
    if base in {"f", "m"}:
        return "gender"
    if base in {"l", "r", "left", "right"}:
        return "side"
    if base in PART_BASES:
        return "part"
    return "word"


def parse_stem(stem: str, max_body_tokens: int) -> StemInfo | None:
    stem = stem.strip().lower()
    if not stem:
        return None
    tokens = stem.split("_")
    if len(tokens) < 2:
        return None

    map_suffix = None
    if tokens[-1] in MAP_SUFFIXES:
        map_suffix = tokens[-1]
        tokens = tokens[:-1]

    prefix = tokens[0]
    body_tokens = tokens[1:]
    if not body_tokens or len(body_tokens) > max_body_tokens:
        return None

    parsed: list[TokenInfo] = []
    for raw in body_tokens:
        base, digits = split_digits(raw)
        if not base:
            base = raw
            digits = ""
        parsed.append(
            TokenInfo(
                raw=raw,
                base=base,
                digits=digits,
                category=classify_base(base),
            )
        )

    return StemInfo(
        stem=stem,
        prefix=prefix,
        tokens=tuple(parsed),
        map_suffix=map_suffix,
        body_signature="/".join(token.slot_key for token in parsed),
    )


def compose_stem(prefix: str, tokens: list[TokenInfo], map_suffix: str | None) -> str:
    parts = [prefix, *(token.base + token.digits for token in tokens)]
    if map_suffix:
        parts.append(map_suffix)
    return "_".join(parts)


def choose_slot_limit(prefix: str, category: str) -> int:
    if category == "part":
        return PART_SLOT_LIMIT
    if category in {"gender", "side"}:
        return 4
    if category == "word":
        return PREFIX_SLOT_LIMITS.get(prefix, WORD_SLOT_LIMIT)
    return WORD_SLOT_LIMIT


def choose_digit_limit(prefix: str, category: str) -> int:
    if category == "part":
        return 8
    return PREFIX_SLOT_LIMITS.get(prefix, NUMERIC_SLOT_LIMIT)


def load_unresolved_hashes(seed, hash_file: Path, matched_hashes: Path) -> list[int]:
    if hash_file.exists():
        return [
            int(line.strip(), 16)
            for line in hash_file.read_text(encoding="utf-8", errors="ignore").splitlines()
            if line.strip()
        ]
    return seed.load_unresolved_texture_hashes(seed.load_investigator(), matched_hashes)


def build_family_profiles(
    stems: set[str],
    max_body_tokens: int,
    min_family_count: int,
) -> tuple[
    dict[tuple[str, str], list[StemInfo]],
    dict[tuple[str, str], set[str | None]],
    dict[tuple[str, str], list[Counter[str]]],
    dict[tuple[str, str], list[dict[str, Counter[str]]]],
]:
    family_examples: dict[tuple[str, str], list[StemInfo]] = defaultdict(list)
    family_maps: dict[tuple[str, str], set[str | None]] = defaultdict(set)
    slot_values: dict[tuple[str, str], list[Counter[str]]] = {}
    slot_digits: dict[tuple[str, str], list[dict[str, Counter[str]]]] = {}

    for stem in sorted(stems):
        info = parse_stem(stem, max_body_tokens)
        if info is None:
            continue
        family = (info.prefix, info.body_signature)
        family_examples[family].append(info)
        family_maps[family].add(info.map_suffix)

    filtered_examples: dict[tuple[str, str], list[StemInfo]] = {}
    filtered_maps: dict[tuple[str, str], set[str | None]] = {}
    for family, examples in family_examples.items():
        if len(examples) < min_family_count:
            continue
        filtered_examples[family] = examples
        filtered_maps[family] = family_maps[family]

        counters = [Counter() for _ in range(len(examples[0].tokens))]
        digits = [defaultdict(Counter) for _ in range(len(examples[0].tokens))]
        for info in examples:
            for index, token in enumerate(info.tokens):
                counters[index][token.base] += 1
                digits[index][token.base][token.digits] += 1
        slot_values[family] = counters
        slot_digits[family] = digits

    return filtered_examples, filtered_maps, slot_values, slot_digits


def add_match(
    matches: dict[int, list[MatchRecord]],
    hash_value: int,
    candidate: str,
    rule: str,
    family: tuple[str, str],
    source_stem: str,
) -> None:
    record = MatchRecord(
        hash_value=hash_value,
        candidate=candidate,
        rule=rule,
        family=f"{family[0]}:{family[1]}",
        source_stem=source_stem,
    )
    if record not in matches[hash_value]:
        matches[hash_value].append(record)


def main() -> int:
    parser = argparse.ArgumentParser(description="Generate THAW texture candidates from learned grammar families.")
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
    parser.add_argument(
        "--write-all-candidates",
        action="store_true",
        help="Also write the full generated candidate list for inspection.",
    )
    args = parser.parse_args()

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    seed = load_seed_module()
    known_stems, prefix_counts = seed.load_known_texture_stems(args.known_textures.resolve())
    known_prefixes = {stem.split("_", 1)[0] for stem in known_stems if "_" in stem}

    matched_stems = seed.load_matched_stems(args.matched_hashes.resolve())
    script_stems = seed.load_script_stems(args.script_names.resolve(), known_prefixes)
    build_roots = seed.get_default_build_roots()
    build_stems = seed.load_build_stems(build_roots, known_prefixes)

    unresolved = load_unresolved_hashes(seed, args.hash_file.resolve(), args.matched_hashes.resolve())
    unresolved_set = set(unresolved)

    family_examples, family_maps, slot_values, slot_digits = build_family_profiles(
        known_stems,
        max_body_tokens=args.max_body_tokens,
        min_family_count=args.min_family_count,
    )
    allowed_families = set(family_examples)

    source_stems = {
        "known": known_stems,
        "matched": matched_stems,
        "script": script_stems,
        "build": build_stems,
    }

    seen_candidates: set[str] = set()
    generated_by_rule: Counter[str] = Counter()
    matches: dict[int, list[MatchRecord]] = defaultdict(list)
    source_count: Counter[str] = Counter()

    def emit(candidate_stem: str, rule: str, family: tuple[str, str], source_stem: str) -> None:
        if not seed.SIMPLE_STEM_RE.fullmatch(candidate_stem):
            return
        candidate = candidate_stem + ".png"
        if candidate in seen_candidates:
            return
        seen_candidates.add(candidate)
        generated_by_rule[rule] += 1
        hash_value = seed.qb_hash_lower(candidate)
        if hash_value in unresolved_set:
            add_match(matches, hash_value, candidate, rule, family, source_stem)

    for source_name, stems in source_stems.items():
        for stem in sorted(stems):
            info = parse_stem(stem, args.max_body_tokens)
            if info is None:
                continue
            family = (info.prefix, info.body_signature)
            if family not in allowed_families:
                continue

            source_count[source_name] += 1
            allowed_maps = sorted(family_maps[family], key=lambda value: (value is not None, value or ""))
            current_tokens = list(info.tokens)

            for map_suffix in allowed_maps:
                if map_suffix != info.map_suffix:
                    emit(compose_stem(info.prefix, current_tokens, map_suffix), "map_variant", family, info.stem)

            for index, token in enumerate(info.tokens):
                limit = choose_slot_limit(info.prefix, token.category)
                bases = [
                    base
                    for base, _count in slot_values[family][index].most_common(limit)
                    if base != token.base
                ]

                if token.base in TOKEN_SWAPS and TOKEN_SWAPS[token.base] not in bases:
                    bases.insert(0, TOKEN_SWAPS[token.base])

                for base in bases:
                    replacement_digits = [
                        digits
                        for digits, _count in slot_digits[family][index].get(base, Counter()).most_common(
                            choose_digit_limit(info.prefix, token.category)
                        )
                    ]
                    if not replacement_digits:
                        replacement_digits = [token.digits]
                    for digits in replacement_digits:
                        replacement = TokenInfo(
                            raw=base + digits,
                            base=base,
                            digits=digits,
                            category=token.category,
                        )
                        next_tokens = current_tokens[:]
                        next_tokens[index] = replacement
                        emit(
                            compose_stem(info.prefix, next_tokens, info.map_suffix),
                            "slot_swap",
                            family,
                            info.stem,
                        )

                digit_variants = [
                    digits
                    for digits, _count in slot_digits[family][index].get(token.base, Counter()).most_common(
                        choose_digit_limit(info.prefix, token.category)
                    )
                    if digits != token.digits
                ]
                for digits in digit_variants:
                    replacement = TokenInfo(
                        raw=token.base + digits,
                        base=token.base,
                        digits=digits,
                        category=token.category,
                    )
                    next_tokens = current_tokens[:]
                    next_tokens[index] = replacement
                    emit(
                        compose_stem(info.prefix, next_tokens, info.map_suffix),
                        "numeric_variant",
                        family,
                        info.stem,
                    )

    matched_hashes = sorted(matches)
    matched_names = sorted({record.candidate for records in matches.values() for record in records})
    remaining = [value for value in unresolved if value not in matches]

    names_path = output_dir / "thaw_grammar_match_names.txt"
    names_path.write_text("".join(f"{name}\n" for name in matched_names), encoding="ascii")

    remaining_path = output_dir / "thaw_grammar_remaining_texture_hashes.txt"
    remaining_path.write_text("".join(f"0x{value:08X}\n" for value in remaining), encoding="ascii")

    if args.write_all_candidates:
        candidate_path = output_dir / "thaw_grammar_candidate_names.txt"
        candidate_path.write_text("".join(f"{name}\n" for name in sorted(seen_candidates)), encoding="ascii")

    summary_lines = [
        "THAW Grammar Texture Candidate Summary",
        "",
        f"Known texture stems: {len(known_stems)}",
        f"Matched-name stems: {len(matched_stems)}",
        f"Script stems: {len(script_stems)}",
        f"Build stems: {len(build_stems)}",
        f"Grammar families: {len(family_examples)}",
        f"Unresolved THAW texture hashes: {len(unresolved)}",
        f"Eligible source stems: {sum(source_count.values())}",
        f"Generated candidate names: {len(seen_candidates)}",
        f"Matched candidate names: {len(matched_names)}",
        f"Matched unresolved hashes: {len(matched_hashes)}",
        f"Remaining unresolved hashes: {len(remaining)}",
        "",
        "Source counts:",
    ]
    for source_name, count in source_count.items():
        summary_lines.append(f"  {source_name}: {count}")
    summary_lines.append("")
    summary_lines.append("Rule counts:")
    for rule, count in generated_by_rule.most_common():
        summary_lines.append(f"  {rule}: {count}")

    if matched_hashes:
        summary_lines.append("")
        summary_lines.append("Matches:")
        for hash_value in matched_hashes:
            summary_lines.append(f"  0x{hash_value:08X}")
            for record in matches[hash_value]:
                summary_lines.append(
                    f"    {record.candidate} [{record.rule}, family={record.family}, source={record.source_stem}]"
                )

    text_path = output_dir / "thaw_grammar_texture_matches.txt"
    text_path.write_text("\n".join(summary_lines) + "\n", encoding="utf-8")

    json_path = output_dir / "thaw_grammar_texture_matches.json"
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
                    "eligible_source_stems": sum(source_count.values()),
                    "generated_candidates": len(seen_candidates),
                    "matched_names": len(matched_names),
                    "matched_hashes": len(matched_hashes),
                    "remaining_hashes": len(remaining),
                    "source_counts": dict(source_count),
                    "rule_counts": dict(generated_by_rule),
                },
                "matches": {
                    f"0x{hash_value:08X}": [
                        {
                            "candidate": record.candidate,
                            "rule": record.rule,
                            "family": record.family,
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
        print(f"Wrote candidate file: {output_dir / 'thaw_grammar_candidate_names.txt'}")
    print(f"Generated candidates: {len(seen_candidates)}")
    print(f"Matched hashes:       {len(matched_hashes)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
