"""Locate files inside Sample/Builds/{Game}/ regardless of in-disc directory layout.

The SampleGenerator emits builds as a verbatim mirror of the original game disc tree
(with archives unpacked in-place), so files don't live in predictable format folders.
This module provides name-based lookup with one-time recursive indexing per build.

Usage:
    from sample_paths import find, find_all

    skin = find("Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)",
                "skater_lasek.skin.ps2")
    if skin is None:
        raise SystemExit("Sample build not populated; run tools/SampleGenerator")

    all_skins = find_all("Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)",
                         "*.skin.ps2")
"""
from pathlib import Path
from functools import lru_cache

REPO_ROOT = Path(__file__).resolve().parents[2]
SAMPLE_BUILDS = REPO_ROOT / "Sample" / "Builds"


@lru_cache(maxsize=None)
def _index(build_name: str) -> dict[str, Path]:
    """One-time recursive scan keyed by lower-case file name. Last wins on duplicates."""
    root = SAMPLE_BUILDS / build_name
    if not root.is_dir():
        return {}
    return {p.name.lower(): p for p in root.rglob("*") if p.is_file()}


def find(build_name: str, file_name: str) -> Path | None:
    """Find a file by its bare name inside Sample/Builds/{build_name}/. Returns None if absent."""
    return _index(build_name).get(file_name.lower())


def find_all(build_name: str, glob_pattern: str) -> list[Path]:
    """Return all files matching glob_pattern (rglob) under Sample/Builds/{build_name}/."""
    root = SAMPLE_BUILDS / build_name
    if not root.is_dir():
        return []
    return sorted(root.rglob(glob_pattern))


def build_dir(build_name: str) -> Path:
    """Return the absolute path to Sample/Builds/{build_name}/ (may not exist)."""
    return SAMPLE_BUILDS / build_name
