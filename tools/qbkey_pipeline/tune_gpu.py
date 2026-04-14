#!/usr/bin/env python3
"""Benchmark qbkey_pipeline brute-gpu settings and suggest the fastest config."""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path


DUMMY_HASH = "0xDEADBEEF"
RATE_RE = re.compile(
    r"Length\s+(\d+):\s+([0-9]+)\s+strings,.*\(([0-9.]+)\s+B/s\)"
)
WG_RE = re.compile(r"Brute kernel WG limit:\s+([0-9]+), preferred multiple:\s+([0-9]+)")


def parse_csv_ints(text: str) -> list[int]:
    values = []
    for part in text.split(","):
        part = part.strip()
        if not part:
            continue
        value = int(part)
        if value <= 0:
            raise ValueError(f"expected positive integer, got {part!r}")
        values.append(value)
    if not values:
        raise ValueError("no values provided")
    return values


def run_brute_gpu(
    exe: Path,
    length: int,
    batch_millions: int,
    local_size: int,
    items_per_work_item: int,
    include_digits: bool,
    fixed_suffix: str | None,
) -> tuple[int, float, str]:
    cmd = [
        str(exe),
        "brute-gpu",
        "-m",
        str(length),
        "-M",
        "0",
        "-b",
        str(batch_millions),
        "-w",
        str(local_size),
        "-n",
        str(items_per_work_item),
    ]
    if include_digits:
        cmd.append("-d")
    if fixed_suffix:
        cmd.extend(["-s", fixed_suffix])
    cmd.append(DUMMY_HASH)

    proc = subprocess.run(cmd, capture_output=True, text=True)
    output = proc.stdout + proc.stderr
    if proc.returncode != 0:
        raise RuntimeError(
            f"command failed with exit code {proc.returncode}:\n{' '.join(cmd)}\n\n{output}"
        )

    matches = RATE_RE.findall(output)
    if not matches:
        raise RuntimeError(f"could not parse brute-gpu rate from output:\n{output}")

    parsed_length, string_count, rate = matches[-1]
    parsed_rate = float(rate)
    if parsed_rate <= 0.0:
        raise RuntimeError(
            "parsed zero throughput from brute-gpu output; use a larger benchmark length\n"
            + output
        )
    return int(string_count), parsed_rate, output


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Benchmark brute-gpu settings and print the fastest flags."
    )
    parser.add_argument(
        "--exe",
        type=Path,
        default=Path(__file__).with_name("qbkey_pipeline.exe"),
        help="Path to qbkey_pipeline executable.",
    )
    parser.add_argument(
        "-m",
        "--min-length",
        type=int,
        default=6,
        help="Minimum variable length to benchmark.",
    )
    parser.add_argument(
        "-M",
        "--max-length",
        type=int,
        default=7,
        help="Maximum variable length to benchmark.",
    )
    parser.add_argument(
        "-b",
        "--batch-millions",
        type=int,
        default=16,
        help="Batch size in millions, passed through to brute-gpu.",
    )
    parser.add_argument(
        "-w",
        "--local-sizes",
        type=parse_csv_ints,
        default=parse_csv_ints("64,128,256"),
        help="Comma-separated local work-group sizes.",
    )
    parser.add_argument(
        "-n",
        "--items-per-work-item",
        type=parse_csv_ints,
        default=parse_csv_ints("1,2,4,8"),
        help="Comma-separated candidates-per-work-item values.",
    )
    parser.add_argument(
        "-r",
        "--repeats",
        type=int,
        default=1,
        help="Repeats per configuration.",
    )
    parser.add_argument(
        "-d",
        "--digits",
        action="store_true",
        help="Include digits in the brute-force charset.",
    )
    parser.add_argument(
        "-s",
        "--suffix",
        type=str,
        default=None,
        help="Fixed suffix to append, for example .png.",
    )
    args = parser.parse_args()

    if args.min_length < 1 or args.max_length < args.min_length:
        parser.error("invalid length range")
    if args.repeats < 1:
        parser.error("repeats must be >= 1")
    if not args.exe.exists():
        parser.error(f"executable not found: {args.exe}")

    print(f"Executable: {args.exe}")
    print(f"Lengths: {args.min_length}-{args.max_length}")
    print(f"Batch size: {args.batch_millions} million")
    print(f"Local sizes: {','.join(map(str, args.local_sizes))}")
    print(
        "Items/work-item: "
        + ",".join(map(str, args.items_per_work_item))
    )
    if args.suffix:
        print(f"Suffix: {args.suffix}")
    print(f"Digits: {'yes' if args.digits else 'no'}")
    print(f"Repeats: {args.repeats}")
    print()

    best: tuple[float, int, int] | None = None
    first_output: str | None = None

    for local_size in args.local_sizes:
        for items_per_work_item in args.items_per_work_item:
            total_strings = 0
            total_seconds = 0.0
            for _ in range(args.repeats):
                for length in range(args.min_length, args.max_length + 1):
                    string_count, rate_bps, output = run_brute_gpu(
                        exe=args.exe,
                        length=length,
                        batch_millions=args.batch_millions,
                        local_size=local_size,
                        items_per_work_item=items_per_work_item,
                        include_digits=args.digits,
                        fixed_suffix=args.suffix,
                    )
                    first_output = first_output or output
                    total_strings += string_count
                    total_seconds += string_count / (rate_bps * 1_000_000_000.0)

            aggregate_rate = total_strings / total_seconds / 1_000_000_000.0
            print(f"wg={local_size:<4} items={items_per_work_item:<2} -> {aggregate_rate:6.2f} B/s")
            if best is None or aggregate_rate > best[0]:
                best = (aggregate_rate, local_size, items_per_work_item)

    print()
    if first_output:
        wg_match = WG_RE.search(first_output)
        if wg_match:
            print(
                f"Kernel WG limit: {wg_match.group(1)}, preferred multiple: {wg_match.group(2)}"
            )

    if not best:
        print("No valid benchmark results.", file=sys.stderr)
        return 1

    rate, local_size, items_per_work_item = best
    print(f"Best: wg={local_size} items={items_per_work_item} -> {rate:.2f} B/s")

    suggested = [
        "brute-gpu",
        "-w",
        str(local_size),
        "-n",
        str(items_per_work_item),
    ]
    if args.batch_millions != 16:
        suggested.extend(["-b", str(args.batch_millions)])
    if args.digits:
        suggested.append("-d")
    if args.suffix:
        suggested.extend(["-s", args.suffix])
    print("Suggested flags:", " ".join(suggested))
    return 0


if __name__ == "__main__":
    sys.exit(main())
