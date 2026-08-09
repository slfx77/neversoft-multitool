#!/usr/bin/env python3
"""Compare a THPS3 runtime Q/T pose dump against diagnostic GLBs."""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Sequence

import thps3_matrix_compare as gltf


@dataclass(frozen=True)
class PoseResult:
    pose: str
    glb: str
    mode: str
    animation: str
    time: float
    q_rmse: float
    t_rmse: float
    combined: float
    worst_bone: int
    worst_bone_error: float


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pose", type=Path, action="append", required=True, help="pose JSON from thps3_pose_dump/scan")
    parser.add_argument("--glb", type=Path, action="append", default=None, help="diagnostic GLB to compare")
    parser.add_argument("--sweep-root", type=Path, help="variant sweep root; finds */glb/<animation>.glb")
    parser.add_argument("--time", type=float, help="explicit GLB sample time")
    parser.add_argument("--use-record-time", action="store_true", help="infer sample time from q_record[4]/t_record[3]")
    parser.add_argument("--scan-samples", type=int, default=0, help="scan N evenly spaced GLB times and keep the best")
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--out", type=Path)
    return parser.parse_args()


def normalize_quat(q: Sequence[float]) -> tuple[float, float, float, float]:
    x, y, z, w = (float(q[0]), float(q[1]), float(q[2]), float(q[3]))
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length == 0:
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


def animation_duration(glb: gltf.GlbData) -> float:
    if not glb.model.get("animations"):
        return 0.0
    duration = 0.0
    for sampler in glb.model["animations"][0].get("samplers", []):
        times = gltf.accessor_values(glb, sampler["input"])
        if times:
            duration = max(duration, float(times[-1]))
    return duration


def node_pose_at(glb: gltf.GlbData, time: float) -> dict[int, dict[str, Sequence[float]]]:
    nodes = glb.model.get("nodes", [])
    animated: dict[int, dict[str, Any]] = {}
    if glb.model.get("animations"):
        animation = glb.model["animations"][0]
        for channel in animation.get("channels", []):
            target = channel["target"]
            node_index = int(target["node"])
            path = target["path"]
            sampler = animation["samplers"][channel["sampler"]]
            times = gltf.accessor_values(glb, sampler["input"])
            values = gltf.accessor_values(glb, sampler["output"])
            interpolation = sampler.get("interpolation", "LINEAR")
            animated.setdefault(node_index, {})[path] = gltf.sample_track(times, values, time, path, interpolation)

    result: dict[int, dict[str, Sequence[float]]] = {}
    for index, node in enumerate(nodes):
        translation, rotation, scale = gltf.node_trs(node)
        overrides = animated.get(index, {})
        result[index] = {
            "translation": overrides.get("translation", translation),
            "rotation": overrides.get("rotation", rotation),
            "scale": overrides.get("scale", scale),
        }
    return result


def runtime_pose(payload: dict[str, Any]) -> list[dict[str, Sequence[float]]]:
    bones = sorted(payload["bones"], key=lambda item: int(item["bone"]))
    return [{"q": item["q"], "t": item["t"]} for item in bones]


def infer_record_time(payload: dict[str, Any]) -> float | None:
    values: list[float] = []
    for bone in payload.get("bones", []):
        q_record = bone.get("q_record") or []
        t_record = bone.get("t_record") or []
        if len(q_record) >= 5 and math.isfinite(float(q_record[4])):
            values.append(float(q_record[4]))
        if len(t_record) >= 4 and math.isfinite(float(t_record[3])):
            values.append(float(t_record[3]))
    if not values:
        return None
    return statistics.median(values)


def glbs_for_pose(args: argparse.Namespace, payload: dict[str, Any], pose_path: Path) -> list[Path]:
    explicit = [path.resolve() for path in args.glb or []]
    if explicit:
        return explicit
    if args.sweep_root is None:
        raise ValueError("supply --glb or --sweep-root")

    animation = str(payload.get("animation") or pose_path.stem)
    stem = Path(animation).stem
    matches = sorted(args.sweep_root.resolve().glob(f"*/glb/{stem}.glb"))
    if not matches:
        raise FileNotFoundError(f"no diagnostic GLBs for {stem} under {args.sweep_root}")
    return matches


def compare_at(pose_path: Path, payload: dict[str, Any], glb_path: Path, glb: gltf.GlbData, time: float) -> PoseResult:
    runtime = runtime_pose(payload)
    pose_by_node = node_pose_at(glb, time)
    joints = [int(joint) for joint in glb.model["skins"][0]["joints"]]

    q_sq = 0.0
    t_sq = 0.0
    worst_bone = -1
    worst_bone_error = -1.0
    for bone_index, joint_node in enumerate(joints[: len(runtime)]):
        expected = pose_by_node[joint_node]
        q_err = quat_error(runtime[bone_index]["q"], expected["rotation"])
        t_err = vec_error(runtime[bone_index]["t"], expected["translation"])
        q_sq += q_err * q_err
        t_sq += t_err * t_err
        bone_error = q_err + t_err
        if bone_error > worst_bone_error:
            worst_bone = bone_index
            worst_bone_error = bone_error

    count = min(len(runtime), len(joints))
    q_rmse = math.sqrt(q_sq / count) if count else 0.0
    t_rmse = math.sqrt(t_sq / count) if count else 0.0
    return PoseResult(
        pose=str(pose_path),
        glb=str(glb_path),
        mode=gltf.mode_name(glb_path),
        animation=Path(str(payload.get("animation") or pose_path.stem)).stem,
        time=time,
        q_rmse=q_rmse,
        t_rmse=t_rmse,
        combined=q_rmse + t_rmse,
        worst_bone=worst_bone,
        worst_bone_error=worst_bone_error,
    )


def candidate_times(args: argparse.Namespace, payload: dict[str, Any], glb: gltf.GlbData) -> list[float]:
    if args.scan_samples > 1:
        duration = animation_duration(glb)
        return [duration * i / (args.scan_samples - 1) for i in range(args.scan_samples)]
    if args.time is not None:
        return [args.time]
    if args.use_record_time:
        record_time = infer_record_time(payload)
        if record_time is not None:
            return [record_time]
    return [float(payload.get("time") or 0.0)]


def result_to_dict(result: PoseResult) -> dict[str, Any]:
    return {
        "pose": result.pose,
        "glb": result.glb,
        "mode": result.mode,
        "animation": result.animation,
        "time": result.time,
        "q_rmse": result.q_rmse,
        "t_rmse": result.t_rmse,
        "combined": result.combined,
        "worst_bone": result.worst_bone,
        "worst_bone_error": result.worst_bone_error,
    }


def print_results(results: Sequence[PoseResult], top: int) -> None:
    print("pose\tmode\tanimation\ttime\tq_rmse\tt_rmse\tcombined\tworst_bone\tworst_bone_error")
    for result in results[:top]:
        print(
            "\t".join(
                [
                    Path(result.pose).name,
                    result.mode,
                    result.animation,
                    f"{result.time:.6f}",
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
    results: list[PoseResult] = []
    for pose_path in args.pose:
        pose_path = pose_path.resolve()
        payload = json.loads(pose_path.read_text(encoding="utf-8"))
        for glb_path in glbs_for_pose(args, payload, pose_path):
            glb_path = glb_path.resolve()
            glb = gltf.read_glb(glb_path)
            best: PoseResult | None = None
            for time in candidate_times(args, payload, glb):
                result = compare_at(pose_path, payload, glb_path, glb, time)
                if best is None or result.combined < best.combined:
                    best = result
            if best is not None:
                results.append(best)

    results.sort(key=lambda item: (item.combined, item.t_rmse, item.q_rmse))
    print_results(results, args.top)
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps([result_to_dict(result) for result in results], indent=2), encoding="utf-8")
        print(f"wrote {args.out}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, KeyError, TypeError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
