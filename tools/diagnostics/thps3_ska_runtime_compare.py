#!/usr/bin/env python3
"""Compare THPS3 SKA decode variants against runtime Q/T pose dumps.

This is diagnostic-only. It tests parser-level hypotheses before changing the
exporter default:

- quaternion field order: `xyzw` vs targeted `wxyz`
- quaternion transform: raw vs conjugated
- duplicate timestamp policy: first-inserted vs last-inserted record

Input pose JSONs come from `thps3_pose_dump.py` or `thps3_pose_scan.py`.
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import statistics
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence


Q_RECORD_SIZE = 24
T_RECORD_SIZE = 20
HEADER_SIZE = 28
PRE_Q_METADATA_SIZE = 12
QT_PADDING = 4


@dataclass(frozen=True)
class RawKey:
    x: float
    y: float
    z: float
    w: float
    time: float
    prev: int
    rec_index: int


@dataclass(frozen=True)
class DecodedTrack:
    q_keys: list[tuple[float, tuple[float, float, float, float], int]]
    t_keys: list[tuple[float, tuple[float, float, float], int]]


@dataclass(frozen=True)
class DecodedSka:
    path: Path
    version: int
    flags: int
    duration: float
    num_q_keys: int
    num_t_keys: int
    pre_q_metadata_hex: str
    q_order: str
    q_transform: str
    duplicate_policy: str
    tracks: list[DecodedTrack]


@dataclass(frozen=True)
class CompareResult:
    target: str
    slot: str
    q_order: str
    q_transform: str
    duplicate_policy: str
    time: float
    time_mode: str
    q_rmse: float
    t_rmse: float
    combined: float
    worst_bone: int
    worst_bone_error: float


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ska", type=Path, required=True, help="THPS3 .ska file to parse")
    parser.add_argument("--pose", type=Path, action="append", required=True, help="runtime pose JSON")
    parser.add_argument("--q-order", action="append", choices=["xyzw", "wxyz"], default=None)
    parser.add_argument("--q-transform", action="append", choices=["raw", "conjugated"], default=None)
    parser.add_argument("--duplicate-policy", action="append", choices=["first", "last"], default=None)
    parser.add_argument("--time", type=float, help="override comparison time for all poses")
    parser.add_argument(
        "--global-time-only",
        action="store_true",
        help="do not use per-bone q_record/t_record times for source slots",
    )
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--out", type=Path, help="write full JSON report")
    return parser.parse_args()


def normalize_quat(q: Sequence[float]) -> tuple[float, float, float, float]:
    x, y, z, w = (float(q[0]), float(q[1]), float(q[2]), float(q[3]))
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length <= 0.0 or not math.isfinite(length):
        return (0.0, 0.0, 0.0, 1.0)
    inv = 1.0 / length
    return (x * inv, y * inv, z * inv, w * inv)


def quat_error(left: Sequence[float], right: Sequence[float]) -> float:
    a = normalize_quat(left)
    b = normalize_quat(right)
    direct = sum((x - y) * (x - y) for x, y in zip(a, b))
    negated = sum((x + y) * (x + y) for x, y in zip(a, b))
    return math.sqrt(min(direct, negated) / 4.0)


def vec_error(left: Sequence[float], right: Sequence[float]) -> float:
    return math.sqrt(sum((float(a) - float(b)) ** 2 for a, b in zip(left, right)) / 3.0)


def slerp(a: Sequence[float], b: Sequence[float], t: float) -> tuple[float, float, float, float]:
    ax, ay, az, aw = normalize_quat(a)
    bx, by, bz, bw = normalize_quat(b)
    dot = ax * bx + ay * by + az * bz + aw * bw
    if dot < 0.0:
        bx, by, bz, bw = -bx, -by, -bz, -bw
        dot = -dot
    dot = max(-1.0, min(1.0, dot))
    if dot > 0.9995:
        return normalize_quat(
            (
                ax + (bx - ax) * t,
                ay + (by - ay) * t,
                az + (bz - az) * t,
                aw + (bw - aw) * t,
            )
        )

    theta_0 = math.acos(dot)
    theta = theta_0 * t
    sin_theta = math.sin(theta)
    sin_theta_0 = math.sin(theta_0)
    s0 = math.cos(theta) - dot * sin_theta / sin_theta_0
    s1 = sin_theta / sin_theta_0
    return normalize_quat((ax * s0 + bx * s1, ay * s0 + by * s1, az * s0 + bz * s1, aw * s0 + bw * s1))


def lerp_vec(a: Sequence[float], b: Sequence[float], t: float) -> tuple[float, float, float]:
    return (
        float(a[0]) + (float(b[0]) - float(a[0])) * t,
        float(a[1]) + (float(b[1]) - float(a[1])) * t,
        float(a[2]) + (float(b[2]) - float(a[2])) * t,
    )


def read_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def read_i32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<i", data, offset)[0]


def read_f32(data: bytes, offset: int) -> float:
    return struct.unpack_from("<f", data, offset)[0]


def read_records(data: bytes, start: int, count: int, stride: int, kind: str) -> tuple[list[RawKey], list[int]]:
    keys: list[RawKey] = []
    sentinels: list[int] = []
    for index in range(count):
        offset = start + index * stride
        if kind == "q":
            prev = read_i32(data, offset)
            x = read_f32(data, offset + 4)
            y = read_f32(data, offset + 8)
            z = read_f32(data, offset + 12)
            w = read_f32(data, offset + 16)
            time = read_f32(data, offset + 20)
        else:
            x = read_f32(data, offset)
            y = read_f32(data, offset + 4)
            z = read_f32(data, offset + 8)
            time = read_f32(data, offset + 12)
            prev = read_i32(data, offset + 16)
            w = 0.0

        keys.append(RawKey(x, y, z, w, time, prev, index))
        is_sentinel = prev < 0 or prev >= index * stride or (prev % stride) != 0
        if is_sentinel:
            sentinels.append(index)
    return keys, sentinels


def assign_by_prev_chain(keys: list[RawKey], sentinels: list[int], stride: int) -> list[list[RawKey]]:
    if not sentinels:
        return []

    bone_of = [-1] * len(keys)
    sentinel_rank = 0
    for index, key in enumerate(keys):
        if sentinel_rank < len(sentinels) and sentinels[sentinel_rank] == index:
            bone_of[index] = sentinel_rank
            sentinel_rank += 1
            continue

        prev_index = key.prev // stride
        if prev_index < 0 or prev_index >= index:
            bone_of[index] = -1
        else:
            bone_of[index] = bone_of[prev_index]

    result: list[list[RawKey]] = [[] for _ in sentinels]
    for index, bone in enumerate(bone_of):
        if 0 <= bone < len(result):
            result[bone].append(keys[index])
    return result


def dedup_by_time(keys: list[RawKey], policy: str) -> list[RawKey]:
    if len(keys) <= 1:
        return sorted(keys, key=lambda item: (item.time, item.rec_index))

    selected: dict[int, RawKey] = {}
    for key in keys:
        bucket = round(key.time * 60.0)
        existing = selected.get(bucket)
        if existing is None:
            selected[bucket] = key
        elif policy == "first" and key.rec_index < existing.rec_index:
            selected[bucket] = key
        elif policy == "last" and key.rec_index > existing.rec_index:
            selected[bucket] = key
    return sorted(selected.values(), key=lambda item: (item.time, item.rec_index))


def decode_quat(key: RawKey, q_order: str, q_transform: str) -> tuple[float, float, float, float]:
    if q_order == "xyzw":
        quat = normalize_quat((key.x, key.y, key.z, key.w))
    elif q_order == "wxyz":
        quat = normalize_quat((key.y, key.z, key.w, key.x))
    else:
        raise ValueError(f"unsupported q order: {q_order}")

    if q_transform == "raw":
        return quat
    if q_transform == "conjugated":
        x, y, z, w = quat
        return (-x, -y, -z, w)
    raise ValueError(f"unsupported q transform: {q_transform}")


def parse_ska(path: Path, q_order: str, q_transform: str, duplicate_policy: str) -> DecodedSka:
    data = path.read_bytes()
    if len(data) < HEADER_SIZE + PRE_Q_METADATA_SIZE:
        raise ValueError(f"{path} is too short for a THPS3 SKA")

    version = read_u32(data, 0)
    flags = read_u32(data, 4)
    duration = read_f32(data, 8)
    num_q_keys = read_u32(data, 12)
    num_t_keys = read_u32(data, 16)
    q_actual = max(0, int(num_q_keys) - 1)
    q_start = HEADER_SIZE + PRE_Q_METADATA_SIZE
    q_end = q_start + q_actual * Q_RECORD_SIZE
    t_start = q_end + QT_PADDING
    t_count = int(num_t_keys)
    if t_start + t_count * T_RECORD_SIZE > len(data):
        t_count = max(0, (len(data) - t_start) // T_RECORD_SIZE)

    q_keys, q_sentinels = read_records(data, q_start, q_actual, Q_RECORD_SIZE, "q")
    t_keys, t_sentinels = read_records(data, t_start, t_count, T_RECORD_SIZE, "t")
    q_by_bone = assign_by_prev_chain(q_keys, q_sentinels, Q_RECORD_SIZE)
    t_by_bone = assign_by_prev_chain(t_keys, t_sentinels, T_RECORD_SIZE)

    tracks: list[DecodedTrack] = []
    for bone_index in range(len(q_by_bone)):
        q_track = [
            (key.time, decode_quat(key, q_order, q_transform), key.rec_index)
            for key in dedup_by_time(q_by_bone[bone_index], duplicate_policy)
        ]
        t_track = []
        if bone_index < len(t_by_bone):
            t_track = [
                (key.time, (key.x, key.y, key.z), key.rec_index)
                for key in dedup_by_time(t_by_bone[bone_index], duplicate_policy)
            ]
        tracks.append(DecodedTrack(q_track, t_track))

    return DecodedSka(
        path=path,
        version=version,
        flags=flags,
        duration=duration,
        num_q_keys=int(num_q_keys),
        num_t_keys=int(num_t_keys),
        pre_q_metadata_hex=data[HEADER_SIZE : HEADER_SIZE + PRE_Q_METADATA_SIZE].hex(" "),
        q_order=q_order,
        q_transform=q_transform,
        duplicate_policy=duplicate_policy,
        tracks=tracks,
    )


def sample_track(keys: list[tuple[float, Any, int]], time: float, path: str) -> Any | None:
    if not keys:
        return None
    if time <= keys[0][0]:
        return keys[0][1]
    if time >= keys[-1][0]:
        return keys[-1][1]

    times = [item[0] for item in keys]
    right = bisect.bisect_left(times, time)
    left = max(0, right - 1)
    t0, v0, _ = keys[left]
    t1, v1, _ = keys[right]
    factor = 0.0 if t1 == t0 else (time - t0) / (t1 - t0)
    if path == "rotation":
        return slerp(v0, v1, factor)
    return lerp_vec(v0, v1, factor)


def sorted_bones(payload: dict[str, Any]) -> list[dict[str, Any]]:
    return sorted(payload["bones"], key=lambda item: int(item["bone"]))


def infer_record_time(payload: dict[str, Any]) -> float | None:
    values: list[float] = []
    for bone in payload.get("bones", []):
        for record_name, index in (("q_record", 4), ("t_record", 3)):
            record = bone.get(record_name) or []
            if len(record) > index:
                value = float(record[index])
                if math.isfinite(value):
                    values.append(value)
    return statistics.median(values) if values else None


def infer_pose_time(payload: dict[str, Any]) -> float:
    slot = str(payload.get("slot") or "output")
    record_time = infer_record_time(payload)
    if slot != "output" and record_time is not None:
        return record_time

    metadata = payload.get("pose_metadata")
    if isinstance(metadata, dict):
        metadata_time = metadata.get("animation_time")
        if metadata_time is not None and math.isfinite(float(metadata_time)):
            return float(metadata_time)

    if record_time is not None:
        return record_time

    payload_time = payload.get("time")
    if payload_time is not None and math.isfinite(float(payload_time)):
        return float(payload_time)
    return 0.0


def record_time_for_bone(bone: dict[str, Any], record_name: str, index: int, fallback: float) -> float:
    record = bone.get(record_name) or []
    if len(record) > index:
        value = float(record[index])
        if math.isfinite(value):
            return value
    return fallback


def compare_pose(
    target_name: str,
    payload: dict[str, Any],
    decoded: DecodedSka,
    time: float,
    use_bone_record_times: bool,
) -> CompareResult:
    bones = sorted_bones(payload)
    count = min(len(bones), len(decoded.tracks))
    q_sq = 0.0
    t_sq = 0.0
    q_count = 0
    t_count = 0
    worst_bone = -1
    worst_bone_error = -1.0

    for bone_index in range(count):
        runtime = bones[bone_index]
        track = decoded.tracks[bone_index]
        q_time = record_time_for_bone(runtime, "q_record", 4, time) if use_bone_record_times else time
        t_time = record_time_for_bone(runtime, "t_record", 3, time) if use_bone_record_times else time
        expected_q = sample_track(track.q_keys, q_time, "rotation")
        expected_t = sample_track(track.t_keys, t_time, "translation")
        q_err = 0.0
        t_err = 0.0
        if expected_q is not None:
            q_err = quat_error(runtime["q"], expected_q)
            q_sq += q_err * q_err
            q_count += 1
        if expected_t is not None:
            t_err = vec_error(runtime["t"], expected_t)
            t_sq += t_err * t_err
            t_count += 1
        bone_error = q_err + t_err
        if bone_error > worst_bone_error:
            worst_bone = bone_index
            worst_bone_error = bone_error

    q_rmse = math.sqrt(q_sq / q_count) if q_count else 0.0
    t_rmse = math.sqrt(t_sq / t_count) if t_count else 0.0
    return CompareResult(
        target=target_name,
        slot=str(payload.get("slot") or "output"),
        q_order=decoded.q_order,
        q_transform=decoded.q_transform,
        duplicate_policy=decoded.duplicate_policy,
        time=time,
        time_mode="per-bone-record" if use_bone_record_times else "global",
        q_rmse=q_rmse,
        t_rmse=t_rmse,
        combined=q_rmse + t_rmse,
        worst_bone=worst_bone,
        worst_bone_error=worst_bone_error,
    )


def result_to_dict(result: CompareResult) -> dict[str, Any]:
    return {
        "target": result.target,
        "slot": result.slot,
        "q_order": result.q_order,
        "q_transform": result.q_transform,
        "duplicate_policy": result.duplicate_policy,
        "time": result.time,
        "time_mode": result.time_mode,
        "q_rmse": result.q_rmse,
        "t_rmse": result.t_rmse,
        "combined": result.combined,
        "worst_bone": result.worst_bone,
        "worst_bone_error": result.worst_bone_error,
    }


def print_results(results: Sequence[CompareResult], top: int) -> None:
    print("target\tslot\tq_order\tq_xform\tdup_policy\ttime\ttime_mode\tq_rmse\tt_rmse\tcombined\tworst_bone\tworst_bone_error")
    for result in results[:top]:
        print(
            "\t".join(
                [
                    result.target,
                    result.slot,
                    result.q_order,
                    result.q_transform,
                    result.duplicate_policy,
                    f"{result.time:.6f}",
                    result.time_mode,
                    f"{result.q_rmse:.6g}",
                    f"{result.t_rmse:.6g}",
                    f"{result.combined:.6g}",
                    str(result.worst_bone),
                    f"{result.worst_bone_error:.6g}",
                ]
            )
        )


def main() -> int:
    args = parse_args()
    q_orders = args.q_order or ["xyzw", "wxyz"]
    q_transforms = args.q_transform or ["raw", "conjugated"]
    duplicate_policies = args.duplicate_policy or ["first", "last"]

    decoded_variants = [
        parse_ska(args.ska, q_order, q_transform, duplicate_policy)
        for q_order in q_orders
        for q_transform in q_transforms
        for duplicate_policy in duplicate_policies
    ]

    results: list[CompareResult] = []
    for pose_path in args.pose:
        payload = json.loads(pose_path.read_text(encoding="utf-8"))
        target_name = pose_path.stem
        time = args.time if args.time is not None else infer_pose_time(payload)
        use_bone_record_times = not args.global_time_only and str(payload.get("slot") or "output") != "output"
        for decoded in decoded_variants:
            results.append(compare_pose(target_name, payload, decoded, time, use_bone_record_times))

    results.sort(key=lambda item: (item.combined, item.t_rmse, item.q_rmse))
    first = decoded_variants[0]
    print(
        f"SKA {args.ska} duration={first.duration:.6f} "
        f"q_keys={first.num_q_keys} t_keys={first.num_t_keys} pre_q={first.pre_q_metadata_hex}"
    )
    print_results(results, args.top)

    if args.out:
        payload = {
            "ska": str(args.ska),
            "duration": first.duration,
            "num_q_keys": first.num_q_keys,
            "num_t_keys": first.num_t_keys,
            "pre_q_metadata_hex": first.pre_q_metadata_hex,
            "results": [result_to_dict(result) for result in results],
        }
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, KeyError, TypeError, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
