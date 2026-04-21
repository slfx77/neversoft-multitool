#!/usr/bin/env python3
"""Compare THPS3 runtime matrix dumps against diagnostic GLBs.

Use this after `thps3_matrix_dump.py` has captured a PCSX2/PINE or savestate
matrix buffer. The script samples a diagnostic GLB at the same animation time
and reports errors for the plausible transform conventions called out by the
THPS3 SKA runbook.
"""

from __future__ import annotations

import argparse
import bisect
import json
import math
import struct
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence


COMPONENT_COUNTS = {
    "SCALAR": 1,
    "VEC2": 2,
    "VEC3": 3,
    "VEC4": 4,
    "MAT4": 16,
}

COMPONENT_FORMATS = {
    5126: ("f", 4),  # FLOAT
    5123: ("H", 2),  # UNSIGNED_SHORT
    5125: ("I", 4),  # UNSIGNED_INT
}

ALL_CONVENTIONS = [
    "local",
    "local-transpose",
    "model",
    "model-transpose",
    "model-no-root",
    "model-no-root-transpose",
    "skin",
    "skin-transpose",
    "skin-no-root",
    "skin-no-root-transpose",
]


Matrix = list[list[float]]


@dataclass(frozen=True)
class GlbData:
    path: Path
    model: dict[str, Any]
    bin_chunk: bytes


@dataclass(frozen=True)
class CompareResult:
    runtime: str
    glb: str
    mode: str
    animation: str
    time: float
    convention: str
    rmse: float
    max_abs: float
    worst_bone: int
    worst_bone_rmse: float


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", type=Path, action="append", required=True, help="runtime JSON from thps3_matrix_dump.py")
    parser.add_argument("--glb", type=Path, action="append", default=None, help="diagnostic GLB to compare")
    parser.add_argument("--sweep-root", type=Path, help="variant sweep root; finds */glb/<animation>.glb")
    parser.add_argument("--time", type=float, help="override the runtime JSON time")
    parser.add_argument("--top", type=int, default=12, help="number of best result rows to print")
    parser.add_argument("--out", type=Path, help="write full JSON report")
    parser.add_argument("--convention", action="append", choices=ALL_CONVENTIONS, default=None)
    return parser.parse_args()


def read_glb(path: Path) -> GlbData:
    data = path.read_bytes()
    if len(data) < 12 or data[:4] != b"glTF":
        raise ValueError(f"{path} is not a binary glTF (.glb)")

    version, declared_length = struct.unpack_from("<II", data, 4)
    if version != 2:
        raise ValueError(f"{path} has unsupported glTF version {version}")
    if declared_length != len(data):
        raise ValueError(f"{path} length mismatch: header {declared_length}, file {len(data)}")

    json_chunk: bytes | None = None
    bin_chunk: bytes | None = None
    offset = 12
    while offset < len(data):
        chunk_len, chunk_type = struct.unpack_from("<II", data, offset)
        offset += 8
        chunk = data[offset : offset + chunk_len]
        offset += chunk_len
        if chunk_type == 0x4E4F534A:  # JSON
            json_chunk = chunk
        elif chunk_type == 0x004E4942:  # BIN
            bin_chunk = chunk

    if json_chunk is None or bin_chunk is None:
        raise ValueError(f"{path} is missing JSON or BIN chunks")

    model = json.loads(json_chunk.rstrip(b" \t\r\n\x00").decode("utf-8"))
    return GlbData(path=path, model=model, bin_chunk=bin_chunk)


def accessor_values(glb: GlbData, accessor_index: int) -> list[Any]:
    accessor = glb.model["accessors"][accessor_index]
    view = glb.model["bufferViews"][accessor["bufferView"]]
    component_format, component_size = COMPONENT_FORMATS[accessor["componentType"]]
    component_count = COMPONENT_COUNTS[accessor["type"]]
    count = accessor["count"]
    base_offset = view.get("byteOffset", 0) + accessor.get("byteOffset", 0)
    stride = view.get("byteStride", component_size * component_count)
    fmt = "<" + component_format * component_count

    values = []
    for index in range(count):
        offset = base_offset + index * stride
        unpacked = struct.unpack_from(fmt, glb.bin_chunk, offset)
        if component_count == 1:
            values.append(unpacked[0])
        elif accessor["type"] == "MAT4":
            values.append(column_major_to_matrix(unpacked))
        else:
            values.append(tuple(float(v) for v in unpacked))
    return values


def identity() -> Matrix:
    return [
        [1.0, 0.0, 0.0, 0.0],
        [0.0, 1.0, 0.0, 0.0],
        [0.0, 0.0, 1.0, 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]


def column_major_to_matrix(values: Sequence[float]) -> Matrix:
    return [[float(values[col * 4 + row]) for col in range(4)] for row in range(4)]


def transpose(matrix: Matrix) -> Matrix:
    return [[matrix[col][row] for col in range(4)] for row in range(4)]


def matmul(left: Matrix, right: Matrix) -> Matrix:
    return [
        [sum(left[row][k] * right[k][col] for k in range(4)) for col in range(4)]
        for row in range(4)
    ]


def translation_matrix(value: Sequence[float]) -> Matrix:
    matrix = identity()
    matrix[0][3] = float(value[0])
    matrix[1][3] = float(value[1])
    matrix[2][3] = float(value[2])
    return matrix


def scale_matrix(value: Sequence[float]) -> Matrix:
    matrix = identity()
    matrix[0][0] = float(value[0])
    matrix[1][1] = float(value[1])
    matrix[2][2] = float(value[2])
    return matrix


def normalize_quat(q: Sequence[float]) -> tuple[float, float, float, float]:
    x, y, z, w = (float(q[0]), float(q[1]), float(q[2]), float(q[3]))
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length == 0:
        return (0.0, 0.0, 0.0, 1.0)
    inv = 1.0 / length
    return (x * inv, y * inv, z * inv, w * inv)


def rotation_matrix(q: Sequence[float]) -> Matrix:
    x, y, z, w = normalize_quat(q)
    xx = x * x
    yy = y * y
    zz = z * z
    xy = x * y
    xz = x * z
    yz = y * z
    xw = x * w
    yw = y * w
    zw = z * w

    return [
        [1.0 - 2.0 * (yy + zz), 2.0 * (xy - zw), 2.0 * (xz + yw), 0.0],
        [2.0 * (xy + zw), 1.0 - 2.0 * (xx + zz), 2.0 * (yz - xw), 0.0],
        [2.0 * (xz - yw), 2.0 * (yz + xw), 1.0 - 2.0 * (xx + yy), 0.0],
        [0.0, 0.0, 0.0, 1.0],
    ]


def compose_trs(translation: Sequence[float], rotation: Sequence[float], scale: Sequence[float]) -> Matrix:
    return matmul(matmul(translation_matrix(translation), rotation_matrix(rotation)), scale_matrix(scale))


def lerp(left: Sequence[float], right: Sequence[float], t: float) -> tuple[float, ...]:
    return tuple(float(a) + (float(b) - float(a)) * t for a, b in zip(left, right))


def slerp(left: Sequence[float], right: Sequence[float], t: float) -> tuple[float, float, float, float]:
    ax, ay, az, aw = normalize_quat(left)
    bx, by, bz, bw = normalize_quat(right)
    dot = ax * bx + ay * by + az * bz + aw * bw
    if dot < 0.0:
        bx, by, bz, bw = -bx, -by, -bz, -bw
        dot = -dot

    if dot > 0.9995:
        return normalize_quat(lerp((ax, ay, az, aw), (bx, by, bz, bw), t))

    theta_0 = math.acos(max(-1.0, min(1.0, dot)))
    sin_theta_0 = math.sin(theta_0)
    theta = theta_0 * t
    sin_theta = math.sin(theta)
    s0 = math.cos(theta) - dot * sin_theta / sin_theta_0
    s1 = sin_theta / sin_theta_0
    return (
        ax * s0 + bx * s1,
        ay * s0 + by * s1,
        az * s0 + bz * s1,
        aw * s0 + bw * s1,
    )


def sample_track(times: Sequence[float], values: Sequence[Any], time: float, path: str, interpolation: str) -> Any:
    if not times:
        raise ValueError("empty animation input")
    if time <= times[0]:
        return values[0]
    if time >= times[-1]:
        return values[-1]

    right = bisect.bisect_right(times, time)
    left = right - 1
    t0 = float(times[left])
    t1 = float(times[right])
    factor = 0.0 if t1 == t0 else (time - t0) / (t1 - t0)

    if interpolation == "STEP":
        return values[left]
    if path == "rotation":
        return slerp(values[left], values[right], factor)
    return lerp(values[left], values[right], factor)


def node_trs(node: dict[str, Any]) -> tuple[Sequence[float], Sequence[float], Sequence[float]]:
    return (
        node.get("translation", (0.0, 0.0, 0.0)),
        node.get("rotation", (0.0, 0.0, 0.0, 1.0)),
        node.get("scale", (1.0, 1.0, 1.0)),
    )


def local_matrices_at(glb: GlbData, time: float) -> list[Matrix]:
    nodes = glb.model.get("nodes", [])
    animated: dict[int, dict[str, Any]] = {}
    if glb.model.get("animations"):
        animation = glb.model["animations"][0]
        for channel in animation.get("channels", []):
            target = channel["target"]
            node_index = target["node"]
            path = target["path"]
            sampler = animation["samplers"][channel["sampler"]]
            times = accessor_values(glb, sampler["input"])
            values = accessor_values(glb, sampler["output"])
            interpolation = sampler.get("interpolation", "LINEAR")
            animated.setdefault(node_index, {})[path] = sample_track(times, values, time, path, interpolation)

    locals_out: list[Matrix] = []
    for index, node in enumerate(nodes):
        overrides = animated.get(index, {})
        if "matrix" in node and not overrides:
            locals_out.append(column_major_to_matrix(node["matrix"]))
            continue

        translation, rotation, scale = node_trs(node)
        translation = overrides.get("translation", translation)
        rotation = overrides.get("rotation", rotation)
        scale = overrides.get("scale", scale)
        locals_out.append(compose_trs(translation, rotation, scale))
    return locals_out


def parent_map(glb: GlbData) -> dict[int, int]:
    parents: dict[int, int] = {}
    for parent_index, node in enumerate(glb.model.get("nodes", [])):
        for child in node.get("children", []):
            parents[int(child)] = parent_index
    return parents


def global_matrices(local_mats: Sequence[Matrix], parents: dict[int, int]) -> list[Matrix]:
    cache: dict[int, Matrix] = {}

    def compute(index: int) -> Matrix:
        if index in cache:
            return cache[index]
        parent = parents.get(index)
        if parent is None:
            result = local_mats[index]
        else:
            result = matmul(compute(parent), local_mats[index])
        cache[index] = result
        return result

    return [compute(index) for index in range(len(local_mats))]


def global_matrices_without_non_joint_roots(
    local_mats: Sequence[Matrix],
    parents: dict[int, int],
    joints: set[int],
) -> list[Matrix]:
    cache: dict[int, Matrix] = {}

    def compute(index: int) -> Matrix:
        if index in cache:
            return cache[index]
        parent = parents.get(index)
        if parent is None:
            result = local_mats[index]
        elif parent not in joints:
            result = local_mats[index]
        else:
            result = matmul(compute(parent), local_mats[index])
        cache[index] = result
        return result

    return [compute(index) for index in range(len(local_mats))]


def matrices_by_convention(glb: GlbData, time: float) -> dict[str, list[Matrix]]:
    skin = glb.model["skins"][0]
    joints = [int(j) for j in skin["joints"]]
    joint_set = set(joints)
    parents = parent_map(glb)
    locals_all = local_matrices_at(glb, time)
    globals_all = global_matrices(locals_all, parents)
    globals_no_root = global_matrices_without_non_joint_roots(locals_all, parents, joint_set)
    inverse_binds = accessor_values(glb, skin["inverseBindMatrices"])

    local = [locals_all[joint] for joint in joints]
    model = [globals_all[joint] for joint in joints]
    model_no_root = [globals_no_root[joint] for joint in joints]
    skin_mats = [matmul(globals_all[joint], inverse_binds[index]) for index, joint in enumerate(joints)]
    skin_no_root = [matmul(globals_no_root[joint], inverse_binds[index]) for index, joint in enumerate(joints)]

    by_name = {
        "local": local,
        "model": model,
        "model-no-root": model_no_root,
        "skin": skin_mats,
        "skin-no-root": skin_no_root,
    }
    by_name.update({f"{name}-transpose": [transpose(m) for m in mats] for name, mats in list(by_name.items())})
    return by_name


def flatten(matrix: Matrix) -> list[float]:
    return [matrix[row][col] for row in range(4) for col in range(4)]


def runtime_matrices(payload: dict[str, Any]) -> list[Matrix]:
    matrices = sorted(payload["matrices"], key=lambda item: int(item["bone"]))
    return [[list(map(float, row)) for row in item["m"]] for item in matrices]


def compare_matrices(runtime: Sequence[Matrix], expected: Sequence[Matrix]) -> tuple[float, float, int, float]:
    total_sq = 0.0
    total_count = 0
    max_abs = 0.0
    worst_bone = -1
    worst_bone_rmse = -1.0

    for bone, (actual, predicted) in enumerate(zip(runtime, expected)):
        bone_sq = 0.0
        bone_count = 0
        for a, b in zip(flatten(actual), flatten(predicted)):
            diff = a - b
            abs_diff = abs(diff)
            bone_sq += diff * diff
            bone_count += 1
            max_abs = max(max_abs, abs_diff)

        bone_rmse = math.sqrt(bone_sq / bone_count) if bone_count else 0.0
        if bone_rmse > worst_bone_rmse:
            worst_bone = bone
            worst_bone_rmse = bone_rmse
        total_sq += bone_sq
        total_count += bone_count

    rmse = math.sqrt(total_sq / total_count) if total_count else 0.0
    return rmse, max_abs, worst_bone, worst_bone_rmse


def animation_name(payload: dict[str, Any], runtime_path: Path) -> str:
    name = str(payload.get("animation") or runtime_path.stem)
    return Path(name).stem


def glbs_for_runtime(args: argparse.Namespace, payload: dict[str, Any], runtime_path: Path) -> list[Path]:
    explicit = [path.resolve() for path in args.glb or []]
    if explicit:
        return explicit

    if args.sweep_root is None:
        raise ValueError("supply --glb or --sweep-root")

    stem = animation_name(payload, runtime_path)
    matches = sorted(args.sweep_root.resolve().glob(f"*/glb/{stem}.glb"))
    if not matches:
        raise FileNotFoundError(f"no diagnostic GLBs for {stem} under {args.sweep_root}")
    return matches


def mode_name(glb_path: Path) -> str:
    if glb_path.parent.name == "glb" and glb_path.parent.parent.name:
        return glb_path.parent.parent.name
    return glb_path.stem


def result_to_dict(result: CompareResult) -> dict[str, Any]:
    return {
        "runtime": result.runtime,
        "glb": result.glb,
        "mode": result.mode,
        "animation": result.animation,
        "time": result.time,
        "convention": result.convention,
        "rmse": result.rmse,
        "max_abs": result.max_abs,
        "worst_bone": result.worst_bone,
        "worst_bone_rmse": result.worst_bone_rmse,
    }


def print_results(results: Sequence[CompareResult], top: int) -> None:
    print("runtime\tmode\tanimation\ttime\tconvention\trmse\tmax_abs\tworst_bone\tworst_bone_rmse")
    for result in results[:top]:
        print(
            "\t".join(
                [
                    Path(result.runtime).name,
                    result.mode,
                    result.animation,
                    f"{result.time:.6f}",
                    result.convention,
                    f"{result.rmse:.6g}",
                    f"{result.max_abs:.6g}",
                    str(result.worst_bone),
                    f"{result.worst_bone_rmse:.6g}",
                ]
            )
        )


def write_report(path: Path, results: Iterable[CompareResult]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps([result_to_dict(result) for result in results], indent=2), encoding="utf-8")


def main() -> int:
    args = parse_args()
    conventions = args.convention or ALL_CONVENTIONS
    results: list[CompareResult] = []

    for runtime_path in args.runtime:
        runtime_path = runtime_path.resolve()
        payload = json.loads(runtime_path.read_text(encoding="utf-8"))
        runtime = runtime_matrices(payload)
        time = float(args.time if args.time is not None else payload.get("time") or 0.0)
        animation = animation_name(payload, runtime_path)

        for glb_path in glbs_for_runtime(args, payload, runtime_path):
            glb = read_glb(glb_path)
            matrices = matrices_by_convention(glb, time)
            for convention in conventions:
                rmse, max_abs, worst_bone, worst_bone_rmse = compare_matrices(runtime, matrices[convention])
                results.append(
                    CompareResult(
                        runtime=str(runtime_path),
                        glb=str(glb_path),
                        mode=mode_name(glb_path),
                        animation=animation,
                        time=time,
                        convention=convention,
                        rmse=rmse,
                        max_abs=max_abs,
                        worst_bone=worst_bone,
                        worst_bone_rmse=worst_bone_rmse,
                    )
                )

    results.sort(key=lambda item: (item.rmse, item.max_abs))
    print_results(results, args.top)
    if args.out:
        write_report(args.out.resolve(), results)
        print(f"wrote {args.out.resolve()}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, KeyError, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
