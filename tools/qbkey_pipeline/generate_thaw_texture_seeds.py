#!/usr/bin/env python3
"""Generate seeded THAW texture-name candidates from existing names and family rules."""

from __future__ import annotations

import argparse
import importlib.util
import json
import os
import re
import tempfile
from collections import Counter, defaultdict
from dataclasses import dataclass
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parents[2]
TEMP_DIR = Path(os.environ.get("TEMP", tempfile.gettempdir()))
SIMPLE_STEM_RE = re.compile(r"^[a-z0-9_]{2,120}$")
PART_SWAP_RE = re.compile(
    r"^(.*_)(head|torso|legs|hands|hair|eye|shoes|feet|arms|face|mouth|teeth|bag|logo|laces|"
    r"arml|armr|legl|legr|fore_l|fore_r|arm_l|arm_r)(\d+)?(?:_(n|s|x))?$"
)
NUMERIC_RE = re.compile(r"^(.*?)(\d+)(?:_(n|s|x))?$")

MAP_SUFFIXES = ("n", "s", "x")
RULES_BY_SOURCE = {
    "known_texture": {"map_base", "map_family", "structural_swap", "numeric_neighbor", "part_sibling"},
    "matched_name": {"map_base", "map_family", "structural_swap", "numeric_neighbor", "part_sibling"},
    "script_name": {"map_base", "map_family", "structural_swap"},
    "build_stem": {"map_base", "map_family", "structural_swap"},
}
SWAPS = (
    ("_arml", "_armr"),
    ("_armr", "_arml"),
    ("_arm_l", "_arm_r"),
    ("_arm_r", "_arm_l"),
    ("_legl", "_legr"),
    ("_legr", "_legl"),
    ("_left", "_right"),
    ("_right", "_left"),
    ("_l", "_r"),
    ("_r", "_l"),
    ("_f_", "_m_"),
    ("_m_", "_f_"),
)
STOP_PREFIXES = (
    "goal",
    "script",
    "menu",
    "trg",
    "rail",
    "gap",
    "create",
    "unknown",
    "nodearray",
    "triggerscripts",
    "anims",
    "levellight",
    "levelgeometry",
    "textures",
    "collision",
    "waypoint",
    "camera",
    "params",
)
STRIP_SUFFIXES = (
    ".png",
    ".qb",
    ".q",
    ".json",
    ".trg",
    ".ps2",
    ".wpc",
    ".ngc",
    ".xbx",
    ".tex",
    ".img",
    ".mdl",
    ".skin",
    ".scn",
)
CRC_TABLE = []
for i in range(256):
    crc = i
    for _ in range(8):
        crc = ((crc >> 1) ^ 0xEDB88320) if (crc & 1) else (crc >> 1)
    CRC_TABLE.append(crc & 0xFFFFFFFF)

LEGACY_BUILD_NAMES = (
    "Tony Hawks Underground 2 (2004-10-4, Windows - Final)",
    "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)",
    "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)",
    "Tony Hawk's Underground (2003-10-2, PS2 - Final)",
    "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)",
)


@dataclass(frozen=True)
class MatchRecord:
    hash_value: int
    candidate: str
    base_stem: str
    rule: str
    source: str


def load_investigator():
    path = REPO_ROOT / "tools" / "diagnostics" / "thaw_model_hash_name_investigator.py"
    spec = importlib.util.spec_from_file_location("thaw_investigator", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


def get_default_build_roots(include_legacy: bool = True) -> list[Path]:
    investigator = load_investigator()
    roots = [
        investigator.DEFAULT_PC_BUILD,
        investigator.DEFAULT_PS2_BUILD,
        investigator.DEFAULT_PS2_SAMPLE,
        investigator.DEFAULT_GC_BUILD,
    ]
    if include_legacy:
        builds_root = investigator.DEFAULT_PC_BUILD.parents[1]
        for name in LEGACY_BUILD_NAMES:
            roots.append(builds_root / name)

    unique: list[Path] = []
    seen: set[str] = set()
    for path in roots:
        key = str(path).lower()
        if key in seen:
            continue
        seen.add(key)
        unique.append(path)
    return unique


def qb_hash_lower(text: str) -> int:
    crc = 0xFFFFFFFF
    for byte in text.lower().encode("latin1", "ignore"):
        crc = ((crc >> 8) ^ CRC_TABLE[(crc ^ byte) & 0xFF]) & 0xFFFFFFFF
    return crc


def strip_known_suffixes(name: str) -> str:
    stem = Path(name).name.lower()
    changed = True
    while changed:
        changed = False
        for suffix in STRIP_SUFFIXES:
            if stem.endswith(suffix):
                stem = stem[: -len(suffix)]
                changed = True
                break
    return stem


def is_seed_stem(stem: str, allowed_prefixes: set[str]) -> bool:
    if not SIMPLE_STEM_RE.fullmatch(stem):
        return False
    if len(stem) < 3 or len(stem) > 80:
        return False
    if stem.startswith(STOP_PREFIXES):
        return False
    if stem.startswith("pro_challenge") or stem.startswith("ped_ai_"):
        return False
    tokens = stem.split("_")
    if tokens[0] in allowed_prefixes:
        return True
    if any(token in {
        "head", "torso", "legs", "hands", "hair", "eye", "shoes", "feet",
        "arms", "face", "mouth", "teeth", "bag", "logo", "laces",
        "arml", "armr", "legl", "legr", "fore_l", "fore_r", "arm_l", "arm_r",
    } for token in tokens):
        return True
    if any(ch.isdigit() for ch in stem):
        return True
    return False


def load_known_texture_stems(path: Path) -> tuple[set[str], Counter[str]]:
    stems: set[str] = set()
    prefixes: Counter[str] = Counter()
    for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        if "=" not in line:
            continue
        name = line.split("=", 1)[0]
        stem = strip_known_suffixes(name)
        if SIMPLE_STEM_RE.fullmatch(stem):
            stems.add(stem)
            prefixes[stem.split("_", 1)[0]] += 1
    return stems, prefixes


def load_matched_stems(path: Path) -> set[str]:
    if not path.exists():
        return set()
    data = json.loads(path.read_text(encoding="utf-8"))
    stems: set[str] = set()
    for value in data.get("qbkey_matches", {}).values():
        stem = strip_known_suffixes(value)
        if SIMPLE_STEM_RE.fullmatch(stem):
            stems.add(stem)
    return stems


def load_script_stems(path: Path, allowed_prefixes: set[str]) -> set[str]:
    if not path.exists():
        return set()
    stems: set[str] = set()
    for line in path.read_text(encoding="utf-8", errors="ignore").splitlines():
        stem = strip_known_suffixes(line.strip())
        if is_seed_stem(stem, allowed_prefixes):
            stems.add(stem)
    return stems


def load_build_stems(roots: list[Path], allowed_prefixes: set[str]) -> set[str]:
    stems: set[str] = set()
    for root in roots:
        if not root.exists():
            continue
        for path in root.rglob("*"):
            if not path.is_file():
                continue
            stem = strip_known_suffixes(path.name)
            if is_seed_stem(stem, allowed_prefixes):
                stems.add(stem)
    return stems


def load_unresolved_texture_hashes(investigator, matched_path: Path) -> list[int]:
    known = investigator.load_known_names()
    pools = investigator.collect_hash_pools(
        investigator.DEFAULT_PC_BUILD,
        investigator.DEFAULT_PS2_SAMPLE,
        known,
    )

    matched_hashes: set[int] = set()
    if matched_path.exists():
        matched = json.loads(matched_path.read_text(encoding="utf-8"))
        matched_hashes = {int(key, 16) for key in matched.get("qbkey_matches", {})}

    unresolved = sorted(set(pools["unresolved_texture_hashes"]) - matched_hashes)
    return unresolved


def add_match(
    matches: dict[int, list[MatchRecord]],
    hash_value: int,
    candidate: str,
    base_stem: str,
    rule: str,
    source: str,
) -> None:
    record = MatchRecord(hash_value, candidate, base_stem, rule, source)
    if record not in matches[hash_value]:
        matches[hash_value].append(record)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Generate seeded THAW texture candidate names and direct heuristic matches."
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=TEMP_DIR,
        help="Directory for seeded candidate outputs.",
    )
    parser.add_argument(
        "--matched-hashes",
        type=Path,
        default=TEMP_DIR / "matched_hashes.json",
        help="matched_hashes.json path used to exclude already-resolved hashes.",
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
    parser.add_argument(
        "--range",
        type=int,
        default=3,
        help="Numeric neighbor range for family extrapolation.",
    )
    parser.add_argument(
        "--write-all-candidates",
        action="store_true",
        help="Also write the full generated candidate list for external brute-force experiments.",
    )
    parser.add_argument(
        "--no-legacy-builds",
        action="store_true",
        help="Restrict build stems to THAW-only builds instead of also using THUG2/THUG/THPS4 roots.",
    )
    args = parser.parse_args()

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    known_stems, prefix_counts = load_known_texture_stems(args.known_textures.resolve())
    allowed_prefixes = {
        prefix for prefix, count in prefix_counts.items() if count >= 3
    } | {"pro", "ped", "cut", "cas", "cs", "body", "veh", "shirt", "pants", "shoes", "hair", "hat"}

    matched_stems = load_matched_stems(args.matched_hashes.resolve())
    script_stems = load_script_stems(args.script_names.resolve(), allowed_prefixes)

    investigator = load_investigator()
    build_roots = get_default_build_roots(include_legacy=not args.no_legacy_builds)
    build_stems = load_build_stems(build_roots, allowed_prefixes)

    unresolved = load_unresolved_texture_hashes(investigator, args.matched_hashes.resolve())
    unresolved_set = set(unresolved)

    seed_sources = {
        "known_texture": known_stems,
        "matched_name": matched_stems,
        "script_name": script_stems,
        "build_stem": build_stems,
    }

    seen_candidates: set[str] = set()
    generated_by_rule: Counter[str] = Counter()
    matches: dict[int, list[MatchRecord]] = defaultdict(list)

    def emit(stem: str, base_stem: str, rule: str, source: str) -> None:
        if rule not in RULES_BY_SOURCE[source]:
            return
        stem = strip_known_suffixes(stem)
        if not SIMPLE_STEM_RE.fullmatch(stem):
            return
        candidate = stem + ".png"
        if candidate in seen_candidates:
            return
        seen_candidates.add(candidate)
        generated_by_rule[rule] += 1
        hash_value = qb_hash_lower(candidate)
        if hash_value in unresolved_set:
            add_match(matches, hash_value, candidate, base_stem, rule, source)

    for source, stems in seed_sources.items():
        for stem in sorted(stems):
            if stem.endswith(("_n", "_s", "_x")):
                base = stem.rsplit("_", 1)[0]
                emit(base, stem, "map_base", source)
                for suffix in MAP_SUFFIXES:
                    emit(base + "_" + suffix, stem, "map_family", source)
            else:
                for suffix in MAP_SUFFIXES:
                    emit(stem + "_" + suffix, stem, "map_family", source)

            for old, new in SWAPS:
                if old in stem:
                    emit(stem.replace(old, new), stem, "structural_swap", source)

            match = NUMERIC_RE.match(stem)
            if match:
                prefix, digits, suffix = match.groups()
                width = len(digits)
                number = int(digits)
                for delta in range(-args.range, args.range + 1):
                    if delta == 0:
                        continue
                    value = number + delta
                    if value < 0 or value > 999:
                        continue
                    variant = f"{prefix}{value:0{width}d}"
                    suffixes = {suffix, None, *MAP_SUFFIXES}
                    for out_suffix in suffixes:
                        emit(
                            variant if not out_suffix else f"{variant}_{out_suffix}",
                            stem,
                            "numeric_neighbor",
                            source,
                        )

            match = PART_SWAP_RE.match(stem)
            if match:
                prefix, part, digits, suffix = match.groups()
                digits = digits or ""
                suffixes = {suffix, None, "n", "s", "x"}
                for replacement in (
                    "head", "torso", "legs", "hands", "hair", "eye", "shoes", "feet",
                    "arms", "face", "mouth", "teeth", "bag", "logo", "laces",
                    "arml", "armr", "legl", "legr", "fore_l", "fore_r", "arm_l", "arm_r",
                ):
                    if replacement == part:
                        continue
                    variant = f"{prefix}{replacement}{digits}"
                    for out_suffix in suffixes:
                        emit(
                            variant if not out_suffix else f"{variant}_{out_suffix}",
                            stem,
                            "part_sibling",
                            source,
                        )

    matched_candidate_names = sorted({record.candidate for records in matches.values() for record in records})
    seeded_matches_path = output_dir / "seeded_match_names.txt"
    seeded_matches_path.write_text(
        "".join(f"{candidate}\n" for candidate in matched_candidate_names),
        encoding="ascii",
    )

    seeded_candidates_path = output_dir / "seeded_candidate_names.txt"
    if args.write_all_candidates:
        seeded_candidates_path.write_text(
            "".join(f"{candidate}\n" for candidate in sorted(seen_candidates)),
            encoding="ascii",
        )

    unresolved_out_path = output_dir / "thaw_unmatched_texture_hashes.txt"
    unresolved_out_path.write_text(
        "".join(f"0x{value:08X}\n" for value in unresolved),
        encoding="ascii",
    )

    matched_hashes = sorted(matches)
    remaining_unresolved = [value for value in unresolved if value not in matches]

    remaining_out_path = output_dir / "thaw_remaining_texture_hashes.txt"
    remaining_out_path.write_text(
        "".join(f"0x{value:08X}\n" for value in remaining_unresolved),
        encoding="ascii",
    )

    summary_lines = [
        "THAW Seeded Texture Candidate Summary",
        "",
        f"Known THAW texture stems: {len(known_stems)}",
        f"Matched-name stems: {len(matched_stems)}",
        f"Script-derived stems: {len(script_stems)}",
        f"THAW build stems: {len(build_stems)}",
        f"Unresolved THAW texture hashes: {len(unresolved)}",
        f"Generated candidate names: {len(seen_candidates)}",
        f"Matched candidate names: {len(matched_candidate_names)}",
        f"Matched unresolved hashes: {len(matched_hashes)}",
        f"Remaining unresolved hashes: {len(remaining_unresolved)}",
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
                    f"    {record.candidate} [{record.rule}, {record.source}, base={record.base_stem}]"
                )

    summary_path = output_dir / "thaw_seeded_texture_matches.txt"
    summary_path.write_text("\n".join(summary_lines) + "\n", encoding="utf-8")

    matches_json = {
        "stats": {
            "known_texture_stems": len(known_stems),
            "matched_stems": len(matched_stems),
            "script_stems": len(script_stems),
            "build_stems": len(build_stems),
            "unresolved_hashes": len(unresolved),
            "generated_candidates": len(seen_candidates),
            "matched_hashes": len(matched_hashes),
            "remaining_unresolved": len(remaining_unresolved),
            "rule_counts": dict(generated_by_rule),
        },
        "matches": {
            f"0x{hash_value:08X}": [
                {
                    "candidate": record.candidate,
                    "rule": record.rule,
                    "source": record.source,
                    "base_stem": record.base_stem,
                }
                for record in matches[hash_value]
            ]
            for hash_value in matched_hashes
        },
        "remaining_unresolved": [f"0x{value:08X}" for value in remaining_unresolved],
    }

    json_path = output_dir / "thaw_seeded_texture_matches.json"
    json_path.write_text(json.dumps(matches_json, indent=2), encoding="utf-8")

    print(f"Wrote matched names:  {seeded_matches_path}")
    if args.write_all_candidates:
        print(f"Wrote candidate file: {seeded_candidates_path}")
    print(f"Wrote summary:        {summary_path}")
    print(f"Wrote JSON:           {json_path}")
    print(f"Wrote unresolved:     {unresolved_out_path}")
    print(f"Wrote remaining:      {remaining_out_path}")
    print(f"Generated candidates: {len(seen_candidates)}")
    print(f"Matched candidates:   {len(matched_candidate_names)}")
    print(f"Matched hashes:       {len(matched_hashes)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
