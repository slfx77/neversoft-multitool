#!/usr/bin/env python3
"""Reconstruct THPS3's loaded Q-track blob from a savestate.

The serialized THPS3 SKA Q records are 24 bytes:

    prev(i32) + qx,qy,qz,qw,time(f32)

In-game, the loader strips `prev` and rewrites the records into a packed
20-byte Q blob grouped by runtime bone track. This diagnostic finds that blob
in EE RAM, maps each packed record back to its file record index, and splits
tracks where time decreases.
"""

from __future__ import annotations

import argparse
import json
import math
import struct
import sys
import zipfile
from dataclasses import dataclass
from pathlib import Path
from typing import Sequence


EE_MEM_FILENAME = "eeMemory.bin"
HEADER_SIZE = 28
PRE_Q_METADATA_SIZE = 12
Q_RECORD_SIZE = 24
Q_RUNTIME_RECORD_SIZE = 20


@dataclass(frozen=True)
class QRecord:
    index: int
    prev: int
    values: tuple[float, float, float, float, float]
    packed: bytes


def parse_int(value: str) -> int:
    return int(value, 0)


def load_ee_ram(path: Path) -> bytes:
    with zipfile.ZipFile(path, "r") as z:
        names = z.namelist()
        if EE_MEM_FILENAME not in names:
            raise FileNotFoundError(f"{path} has no {EE_MEM_FILENAME}; entries: {names}")
        return z.read(EE_MEM_FILENAME)


def parse_q_records(ska_path: Path) -> tuple[dict[str, object], list[QRecord]]:
    data = ska_path.read_bytes()
    if len(data) < HEADER_SIZE + PRE_Q_METADATA_SIZE:
        raise ValueError(f"{ska_path} is too short for a THPS3 SKA")

    version = struct.unpack_from("<I", data, 0)[0]
    flags = struct.unpack_from("<I", data, 4)[0]
    duration = struct.unpack_from("<f", data, 8)[0]
    num_q_keys = struct.unpack_from("<I", data, 12)[0]
    q_count = max(0, int(num_q_keys) - 1)
    q_start = HEADER_SIZE + PRE_Q_METADATA_SIZE
    records: list[QRecord] = []
    for index in range(q_count):
        offset = q_start + index * Q_RECORD_SIZE
        if offset + Q_RECORD_SIZE > len(data):
            break
        prev = struct.unpack_from("<i", data, offset)[0]
        packed = data[offset + 4 : offset + Q_RECORD_SIZE]
        values = struct.unpack("<5f", packed)
        records.append(QRecord(index=index, prev=prev, values=values, packed=packed))

    metadata = {
        "version": version,
        "flags": flags,
        "duration": duration,
        "num_q_keys_header": int(num_q_keys),
        "serialized_q_records": len(records),
        "pre_q_metadata_hex": data[HEADER_SIZE : HEADER_SIZE + PRE_Q_METADATA_SIZE].hex(" "),
    }
    return metadata, records


def max_abs_error(left: Sequence[float], right: Sequence[float]) -> float:
    return max(abs(float(a) - float(b)) for a, b in zip(left, right))


def match_record(values: tuple[float, ...], records: list[QRecord]) -> tuple[float, QRecord]:
    best_record = records[0]
    best_error = max_abs_error(values, best_record.values)
    for record in records[1:]:
        error = max_abs_error(values, record.values)
        if error < best_error:
            best_error = error
            best_record = record
    return best_error, best_record


def score_q_base(data: bytes, base: int, records: list[QRecord], max_records: int, tolerance: float) -> int:
    score = 0
    seen: set[int] = set()
    for slot in range(max_records):
        offset = base + slot * Q_RUNTIME_RECORD_SIZE
        if offset + Q_RUNTIME_RECORD_SIZE > len(data):
            break
        values = struct.unpack_from("<5f", data, offset)
        error, record = match_record(values, records)
        if error > tolerance:
            break
        if record.index in seen:
            break
        seen.add(record.index)
        score += 1
    return score


def find_q_base(data: bytes, records: list[QRecord], max_records: int, tolerance: float) -> tuple[int, int]:
    if not records:
        raise ValueError("no Q records to search for")

    pattern = records[0].packed
    best_base = -1
    best_score = 0
    start = 0
    while True:
        hit = data.find(pattern, start)
        if hit < 0:
            break
        score = score_q_base(data, hit, records, max_records, tolerance)
        if score > best_score:
            best_base = hit
            best_score = score
        start = hit + 1

    if best_base < 0:
        raise RuntimeError("could not find a plausible runtime Q blob")
    return best_base, best_score


def read_runtime_records(
    data: bytes,
    base: int,
    records: list[QRecord],
    max_records: int,
    tolerance: float,
) -> list[dict[str, object]]:
    rows: list[dict[str, object]] = []
    seen: set[int] = set()
    for slot in range(max_records):
        offset = base + slot * Q_RUNTIME_RECORD_SIZE
        if offset + Q_RUNTIME_RECORD_SIZE > len(data):
            break
        values = struct.unpack_from("<5f", data, offset)
        error, record = match_record(values, records)
        if error > tolerance or record.index in seen:
            break
        seen.add(record.index)
        rows.append(
            {
                "slot": slot,
                "address": f"0x{offset:08X}",
                "file_record": record.index,
                "prev": record.prev,
                "prev_record": record.prev // Q_RECORD_SIZE if record.prev >= 0 else None,
                "time": record.values[4],
                "q": list(record.values[:4]),
                "match_error": error,
            }
        )
    return rows


def split_tracks(rows: list[dict[str, object]], time_epsilon: float) -> list[dict[str, object]]:
    tracks: list[dict[str, object]] = []
    current: list[dict[str, object]] = []
    last_time = -math.inf
    for row in rows:
        time = float(row["time"])
        if current and time < last_time - time_epsilon:
            tracks.append({"track": len(tracks), "records": current})
            current = []
        current.append(row)
        last_time = time
    if current:
        tracks.append({"track": len(tracks), "records": current})
    return tracks


def print_tracks(q_base: int, rows: list[dict[str, object]], tracks: list[dict[str, object]]) -> None:
    print(f"q_base=0x{q_base:08X} matched_records={len(rows)} tracks={len(tracks)}")
    print("track\tcount\tfile_records\ttimes")
    for track in tracks:
        records = track["records"]
        file_records = ",".join(str(row["file_record"]) for row in records)
        times = ",".join(f"{float(row['time']):.3f}" for row in records)
        print(f"{track['track']}\t{len(records)}\t{file_records}\t{times}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--savestate", type=Path, required=True)
    parser.add_argument("--ska", type=Path, required=True)
    parser.add_argument("--q-base", type=parse_int, help="override runtime Q blob base address")
    parser.add_argument("--max-records", type=int, default=None)
    parser.add_argument("--tolerance", type=float, default=1e-6)
    parser.add_argument("--time-epsilon", type=float, default=1e-5)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    metadata, records = parse_q_records(args.ska)
    data = load_ee_ram(args.savestate)
    max_records = args.max_records or len(records)
    if args.q_base is None:
        q_base, score = find_q_base(data, records, max_records, args.tolerance)
    else:
        q_base = args.q_base
        score = score_q_base(data, q_base, records, max_records, args.tolerance)

    rows = read_runtime_records(data, q_base, records, max_records, args.tolerance)
    tracks = split_tracks(rows, args.time_epsilon)
    print_tracks(q_base, rows, tracks)

    if args.out:
        payload = {
            "source": str(args.savestate),
            "ska": str(args.ska),
            "q_base": f"0x{q_base:08X}",
            "base_score": score,
            **metadata,
            "runtime_records": rows,
            "tracks": tracks,
        }
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, zipfile.BadZipFile, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
