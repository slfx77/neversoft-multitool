#!/usr/bin/env python3
"""Decompile QB/TRG files and extract reusable script tokens for qbkey_pipeline."""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Iterable


BUILDS_DEFAULT = Path(r"C:\Users\mmc99\Desktop\Games\TCRF\Spider-Man Research\Builds")
TOKEN_RE = re.compile(r"[A-Za-z][A-Za-z0-9_./\\-]{1,255}")


def repo_root() -> Path:
    return Path(__file__).resolve().parents[2]


def default_project() -> Path:
    return repo_root() / "src" / "NeversoftMultitool" / "NeversoftMultitool.csproj"


def normalize(text: str) -> str | None:
    text = text.strip()
    if len(text) < 2 or len(text) > 255:
        return None
    if not any(ch.isalpha() for ch in text):
        return None
    return text.lower()


def add_candidate(store: set[str], text: str) -> None:
    candidate = normalize(text)
    if candidate:
        store.add(candidate)


def extract_from_text(text: str) -> set[str]:
    values: set[str] = set()
    for match in TOKEN_RE.finditer(text):
        add_candidate(values, match.group(0))
    return values


def extract_from_json_value(value: object, store: set[str]) -> None:
    if isinstance(value, str):
        add_candidate(store, value)
        for token in extract_from_text(value):
            store.add(token)
        return
    if isinstance(value, list):
        for item in value:
            extract_from_json_value(item, store)
        return
    if isinstance(value, dict):
        for item in value.values():
            extract_from_json_value(item, store)


def extract_tokens(path: Path) -> set[str]:
    text = path.read_text(encoding="utf-8", errors="ignore")
    suffix = path.suffix.lower()
    if suffix == ".json":
        values = extract_from_text(text)
        try:
            parsed = json.loads(text)
        except json.JSONDecodeError:
            return values

        extract_from_json_value(parsed, values)
        return values
    return extract_from_text(text)


def run_command(args: list[str], cwd: Path) -> int:
    print("Running:", " ".join(f'"{arg}"' if " " in arg else arg for arg in args))
    proc = subprocess.run(args, cwd=cwd, text=True)
    return proc.returncode


def build_cli_command(project: Path, mode: str, builds: Path, output: Path) -> list[str]:
    return [
        "dotnet",
        "run",
        "--framework",
        "net10.0",
        "--project",
        str(project),
        "--",
        mode,
        str(builds),
        "-o",
        str(output),
    ]


def walk_outputs(root: Path) -> Iterable[Path]:
    if not root.exists():
        return []
    return (
        path
        for path in root.rglob("*")
        if path.is_file() and path.suffix.lower() in {".q", ".json"}
    )


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Decompile QB/TRG files and write script-derived candidate names."
    )
    parser.add_argument(
        "--builds",
        type=Path,
        default=BUILDS_DEFAULT,
        help="Build root to scan for QB/TRG files.",
    )
    parser.add_argument(
        "--project",
        type=Path,
        default=default_project(),
        help="Path to NeversoftMultitool.csproj.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=Path(__file__).resolve().parent,
        help="Directory for decompiled_script_names.txt and stats.",
    )
    parser.add_argument(
        "--work-dir",
        type=Path,
        default=None,
        help="Optional directory for intermediate decompile output.",
    )
    parser.add_argument(
        "--keep-intermediate",
        action="store_true",
        help="Keep decompiled .q/.json outputs instead of deleting them.",
    )
    parser.add_argument(
        "--skip-qb",
        action="store_true",
        help="Skip QB decompilation.",
    )
    parser.add_argument(
        "--skip-trg",
        action="store_true",
        help="Skip TRG parsing.",
    )
    args = parser.parse_args()

    args.builds = args.builds.resolve()
    args.project = args.project.resolve()

    if not args.builds.exists():
        parser.error(f"build root not found: {args.builds}")
    if not args.project.exists():
        parser.error(f"project not found: {args.project}")

    output_dir = args.output_dir.resolve()
    output_dir.mkdir(parents=True, exist_ok=True)

    if args.work_dir is None:
        work_dir = Path(tempfile.mkdtemp(prefix="qbkey_script_names_"))
        remove_work_dir = not args.keep_intermediate
    else:
        work_dir = args.work_dir.resolve()
        work_dir.mkdir(parents=True, exist_ok=True)
        remove_work_dir = False

    qb_out = work_dir / "qb"
    trg_out = work_dir / "trg"

    stats: dict[str, object] = {
        "builds": str(args.builds),
        "project": str(args.project),
        "work_dir": str(work_dir),
        "qb_exit_code": None,
        "trg_exit_code": None,
        "output_files": 0,
        "token_count": 0,
    }

    try:
        if not args.skip_qb:
            qb_out.mkdir(parents=True, exist_ok=True)
            qb_exit = run_command(build_cli_command(args.project, "qb", args.builds, qb_out), repo_root())
            stats["qb_exit_code"] = qb_exit
            if qb_exit not in (0, 1):
                return qb_exit

        if not args.skip_trg:
            trg_out.mkdir(parents=True, exist_ok=True)
            trg_exit = run_command(build_cli_command(args.project, "trg", args.builds, trg_out), repo_root())
            stats["trg_exit_code"] = trg_exit
            if trg_exit not in (0, 1):
                return trg_exit

        names: set[str] = set()
        output_files = 0
        for root in (qb_out, trg_out):
            for path in walk_outputs(root):
                output_files += 1
                names.update(extract_tokens(path))

        stats["output_files"] = output_files
        stats["token_count"] = len(names)

        names_path = output_dir / "decompiled_script_names.txt"
        stats_path = output_dir / "decompiled_script_names_stats.json"

        names_path.write_text("\n".join(sorted(names)) + ("\n" if names else ""), encoding="utf-8")
        stats_path.write_text(json.dumps(stats, indent=2), encoding="utf-8")

        print(f"Wrote {len(names)} names to {names_path}")
        print(f"Wrote stats to {stats_path}")
        return 0
    finally:
        if remove_work_dir and work_dir.exists():
            shutil.rmtree(work_dir, ignore_errors=True)


if __name__ == "__main__":
    sys.exit(main())
