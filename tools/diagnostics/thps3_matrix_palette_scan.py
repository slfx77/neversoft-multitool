#!/usr/bin/env python3
"""Scan a THPS3 PCSX2 savestate for candidate 29-bone matrix palettes.

Inputs:

- a `.p2s` savestate,
- a runtime Q/T pose dump from `thps3_pose_scan.py` or `thps3_pose_dump.py`,
- any diagnostic skater GLB with the correct skeleton and inverse bind matrices.

The scanner derives plausible local/model/skin matrix palettes from the runtime
Q/T pose, then searches EE RAM for contiguous matrix-like buffers using exact
float anchors. This is diagnostic-only: a low-error hit is evidence for the
final palette convention; no low-error hit means the final palette is likely not
stored as a simple contiguous EE float palette in one of the tested layouts.
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
from typing import Iterable, Sequence

import thps3_matrix_compare as gltf


EE_MEM_FILENAME = "eeMemory.bin"
Matrix = list[list[float]]


@dataclass(frozen=True)
class Layout:
    name: str
    stride: int
    elements: tuple[tuple[int, int, int], ...]  # row, col, byte offset within one matrix


@dataclass(frozen=True)
class Anchor:
    value: float
    bone: int
    row: int
    col: int
    offset: int
    convention: str
    layout: Layout


@dataclass(frozen=True)
class Candidate:
    base: int
    convention: str
    layout: str
    rmse: float
    rot_rmse: float
    trans_rmse: float
    max_abs: float
    anchors: int
    worst_bone: int
    worst_bone_rmse: float


def parse_int(value: str) -> int:
    return int(value, 0)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("savestate", type=Path)
    parser.add_argument("--pose", type=Path, required=True, help="pose JSON from thps3_pose_scan.py/thps3_pose_dump.py")
    parser.add_argument("--glb", type=Path, required=True, help="diagnostic GLB used only for skeleton hierarchy/inverse binds")
    parser.add_argument("--top", type=int, default=30)
    parser.add_argument("--max-anchors", type=int, default=160)
    parser.add_argument("--min-anchor-abs", type=float, default=0.05)
    parser.add_argument("--max-anchor-abs", type=float, default=200.0)
    parser.add_argument("--start", type=parse_int, default=0)
    parser.add_argument("--end", type=parse_int, default=0x02000000)
    parser.add_argument("--max-candidates", type=int, default=250000)
    parser.add_argument("--out", type=Path)
    return parser.parse_args()


def load_ee_ram(path: Path) -> bytes:
    with zipfile.ZipFile(path, "r") as z:
        names = z.namelist()
        if EE_MEM_FILENAME not in names:
            raise FileNotFoundError(f"{path} has no {EE_MEM_FILENAME}; entries: {names}")
        return z.read(EE_MEM_FILENAME)


def runtime_pose(payload: dict[str, object]) -> list[dict[str, Sequence[float]]]:
    bones = sorted(payload["bones"], key=lambda item: int(item["bone"]))  # type: ignore[index]
    return [{"q": item["q"], "t": item["t"]} for item in bones]  # type: ignore[index]


def layouts() -> list[Layout]:
    return [
        Layout(
            "mat4-row",
            0x40,
            tuple((row, col, (row * 4 + col) * 4) for row in range(4) for col in range(4)),
        ),
        Layout(
            "mat4-col",
            0x40,
            tuple((row, col, (col * 4 + row) * 4) for row in range(4) for col in range(4)),
        ),
        Layout(
            "mat3x4-row",
            0x30,
            tuple((row, col, (row * 4 + col) * 4) for row in range(3) for col in range(4)),
        ),
        Layout(
            "mat4x3-row",
            0x30,
            tuple((row, col, (row * 3 + col) * 4) for row in range(4) for col in range(3)),
        ),
        Layout(
            "mat4x3-padded",
            0x40,
            tuple((row, col, row * 0x10 + col * 4) for row in range(4) for col in range(3)),
        ),
    ]


def build_expected_palettes(pose_payload: dict[str, object], glb: gltf.GlbData) -> dict[str, list[Matrix]]:
    pose = runtime_pose(pose_payload)
    skin = glb.model["skins"][0]
    joints = [int(joint) for joint in skin["joints"]]
    joint_set = set(joints)
    parents = gltf.parent_map(glb)
    inverse_binds = gltf.accessor_values(glb, skin["inverseBindMatrices"])

    local = [gltf.identity() for _ in glb.model.get("nodes", [])]
    for index, node in enumerate(glb.model.get("nodes", [])):
        if "matrix" in node:
            local[index] = gltf.column_major_to_matrix(node["matrix"])

    for bone, runtime in enumerate(pose):
        local[joints[bone]] = gltf.compose_trs(runtime["t"], runtime["q"], (1.0, 1.0, 1.0))

    model = gltf.global_matrices(local, parents)
    model_no_root = gltf.global_matrices_without_non_joint_roots(local, parents, joint_set)

    by_name: dict[str, list[Matrix]] = {
        "local": [local[joint] for joint in joints],
        "model": [model[joint] for joint in joints],
        "model-no-root": [model_no_root[joint] for joint in joints],
        "skin": [gltf.matmul(model[joint], inverse_binds[index]) for index, joint in enumerate(joints)],
        "skin-no-root": [gltf.matmul(model_no_root[joint], inverse_binds[index]) for index, joint in enumerate(joints)],
        "skin-reversed": [gltf.matmul(inverse_binds[index], model[joint]) for index, joint in enumerate(joints)],
        "skin-no-root-reversed": [
            gltf.matmul(inverse_binds[index], model_no_root[joint]) for index, joint in enumerate(joints)
        ],
    }
    by_name.update({f"{name}-transpose": [gltf.transpose(m) for m in mats] for name, mats in list(by_name.items())})
    return by_name


def is_good_anchor_value(value: float, min_abs: float, max_abs: float) -> bool:
    if not math.isfinite(value):
        return False
    av = abs(value)
    if av < min_abs or av > max_abs:
        return False
    return abs(av - 1.0) > 0.0005


def iter_anchors(
    palettes: dict[str, list[Matrix]],
    layout: Layout,
    min_abs: float,
    max_abs: float,
) -> Iterable[Anchor]:
    seen: set[tuple[str, str, int, int, int, int]] = set()
    for convention, matrices in palettes.items():
        for bone, matrix in enumerate(matrices):
            for row, col, offset in layout.elements:
                value = matrix[row][col]
                if not is_good_anchor_value(value, min_abs, max_abs):
                    continue
                key = (convention, layout.name, bone, row, col, round(value * 100000))
                if key in seen:
                    continue
                seen.add(key)
                yield Anchor(value=value, bone=bone, row=row, col=col, offset=offset, convention=convention, layout=layout)


def choose_anchors(
    palettes: dict[str, list[Matrix]],
    layout: Layout,
    min_abs: float,
    max_abs: float,
    max_anchors: int,
) -> list[Anchor]:
    anchors = list(iter_anchors(palettes, layout, min_abs, max_abs))
    # Prefer non-root, distinctive values with larger magnitude; tiny rotations
    # and repeated 0/1 basis terms produce too many false bases.
    anchors.sort(key=lambda item: (item.bone == 0, -abs(item.value), item.bone, item.row, item.col))
    return anchors[:max_anchors]


def find_bases_for_layout(
    ee: bytes,
    palettes: dict[str, list[Matrix]],
    layout: Layout,
    start: int,
    end: int,
    min_anchor_abs: float,
    max_anchor_abs: float,
    max_anchors: int,
    max_candidates: int,
) -> dict[tuple[str, str, int], int]:
    candidates: dict[tuple[str, str, int], int] = {}
    anchors = choose_anchors(palettes, layout, min_anchor_abs, max_anchor_abs, max_anchors)
    end = min(end, len(ee))

    for anchor in anchors:
        needle = struct.pack("<f", float(anchor.value))
        pos = start
        while True:
            hit = ee.find(needle, pos, end)
            if hit < 0:
                break
            if hit % 4 == 0:
                base = hit - anchor.bone * layout.stride - anchor.offset
                if start <= base and base + len(next(iter(palettes.values()))) * layout.stride <= end:
                    key = (anchor.convention, layout.name, base)
                    candidates[key] = candidates.get(key, 0) + 1
                    if len(candidates) >= max_candidates:
                        return candidates
            pos = hit + 4

    return candidates


def read_float(ee: bytes, offset: int) -> float | None:
    value = struct.unpack_from("<f", ee, offset)[0]
    if not math.isfinite(value) or abs(value) > 1.0e7:
        return None
    return value


def score_candidate(
    ee: bytes,
    base: int,
    layout: Layout,
    expected: list[Matrix],
    anchor_hits: int,
) -> Candidate | None:
    total_sq = 0.0
    total_count = 0
    rot_sq = 0.0
    rot_count = 0
    trans_sq = 0.0
    trans_count = 0
    max_abs = 0.0
    worst_bone = -1
    worst_bone_rmse = -1.0

    for bone, matrix in enumerate(expected):
        bone_sq = 0.0
        bone_count = 0
        matrix_base = base + bone * layout.stride
        for row, col, offset in layout.elements:
            actual = read_float(ee, matrix_base + offset)
            if actual is None:
                return None
            diff = actual - matrix[row][col]
            abs_diff = abs(diff)
            total_sq += diff * diff
            total_count += 1
            bone_sq += diff * diff
            bone_count += 1
            max_abs = max(max_abs, abs_diff)

            if row < 3 and col < 3:
                rot_sq += diff * diff
                rot_count += 1
            if (row, col) in ((0, 3), (1, 3), (2, 3), (3, 0), (3, 1), (3, 2)):
                trans_sq += diff * diff
                trans_count += 1

        bone_rmse = math.sqrt(bone_sq / bone_count) if bone_count else 0.0
        if bone_rmse > worst_bone_rmse:
            worst_bone = bone
            worst_bone_rmse = bone_rmse

    if total_count == 0:
        return None

    return Candidate(
        base=base,
        convention="",
        layout=layout.name,
        rmse=math.sqrt(total_sq / total_count),
        rot_rmse=math.sqrt(rot_sq / rot_count) if rot_count else 0.0,
        trans_rmse=math.sqrt(trans_sq / trans_count) if trans_count else 0.0,
        max_abs=max_abs,
        anchors=anchor_hits,
        worst_bone=worst_bone,
        worst_bone_rmse=worst_bone_rmse,
    )


def scan(ee: bytes, palettes: dict[str, list[Matrix]], args: argparse.Namespace) -> list[Candidate]:
    layout_defs = layouts()
    all_candidates: list[Candidate] = []

    for layout in layout_defs:
        base_hits = find_bases_for_layout(
            ee,
            palettes,
            layout,
            args.start,
            args.end,
            args.min_anchor_abs,
            args.max_anchor_abs,
            args.max_anchors,
            args.max_candidates,
        )
        print(f"{layout.name}: {len(base_hits)} candidate base(s)")
        for convention, layout_name, base in base_hits:
            if layout_name != layout.name:
                continue
            candidate = score_candidate(ee, base, layout, palettes[convention], base_hits[(convention, layout_name, base)])
            if candidate is None:
                continue
            all_candidates.append(
                Candidate(
                    base=candidate.base,
                    convention=convention,
                    layout=candidate.layout,
                    rmse=candidate.rmse,
                    rot_rmse=candidate.rot_rmse,
                    trans_rmse=candidate.trans_rmse,
                    max_abs=candidate.max_abs,
                    anchors=candidate.anchors,
                    worst_bone=candidate.worst_bone,
                    worst_bone_rmse=candidate.worst_bone_rmse,
                )
            )

    # Multiple anchors can point inside the Q/T source buffers. Keep the global
    # low-error rows first, but retain anchor count as a tiebreaker.
    all_candidates.sort(key=lambda item: (item.rmse, item.rot_rmse, item.trans_rmse, -item.anchors))
    return all_candidates


def candidate_to_dict(candidate: Candidate) -> dict[str, object]:
    return {
        "base": f"0x{candidate.base:08X}",
        "convention": candidate.convention,
        "layout": candidate.layout,
        "rmse": candidate.rmse,
        "rot_rmse": candidate.rot_rmse,
        "trans_rmse": candidate.trans_rmse,
        "max_abs": candidate.max_abs,
        "anchors": candidate.anchors,
        "worst_bone": candidate.worst_bone,
        "worst_bone_rmse": candidate.worst_bone_rmse,
    }


def print_results(candidates: Sequence[Candidate], top: int) -> None:
    print("rank\tbase\tconvention\tlayout\trmse\trot_rmse\ttrans_rmse\tmax_abs\tanchors\tworst_bone\tworst_bone_rmse")
    for rank, candidate in enumerate(candidates[:top], start=1):
        print(
            "\t".join(
                [
                    str(rank),
                    f"0x{candidate.base:08X}",
                    candidate.convention,
                    candidate.layout,
                    f"{candidate.rmse:.6g}",
                    f"{candidate.rot_rmse:.6g}",
                    f"{candidate.trans_rmse:.6g}",
                    f"{candidate.max_abs:.6g}",
                    str(candidate.anchors),
                    str(candidate.worst_bone),
                    f"{candidate.worst_bone_rmse:.6g}",
                ]
            )
        )


def main() -> int:
    args = parse_args()
    ee = load_ee_ram(args.savestate)
    pose_payload = json.loads(args.pose.read_text(encoding="utf-8"))
    glb = gltf.read_glb(args.glb)
    palettes = build_expected_palettes(pose_payload, glb)
    candidates = scan(ee, palettes, args)
    print_results(candidates, args.top)

    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        payload = {
            "savestate": str(args.savestate),
            "pose": str(args.pose),
            "glb": str(args.glb),
            "candidates": [candidate_to_dict(candidate) for candidate in candidates],
        }
        args.out.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        print(f"wrote {args.out}")

    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, KeyError, zipfile.BadZipFile, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
