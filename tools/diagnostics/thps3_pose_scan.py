#!/usr/bin/env python3
"""Scan a THPS3 PCSX2 savestate for 29-bone pose buffers.

This avoids manual PCSX2 register capture. Save a state after the skater is
loaded, then scan `eeMemory.bin` for structures that look like the runtime pose
layout used by the SKA composition functions:

- struct+0x00 = bone count, expected 29
- struct+0x2C = quaternion buffer pointer, 0x18-byte records
- struct+0x30 = translation buffer pointer, 0x14-byte records
- struct+0x3C/0x40 and +0x4C/0x50 = source Q/T buffer pointers

The best candidates can be dumped directly in the same JSON shape produced by
`thps3_pose_dump.py`.
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


EE_MEM_FILENAME = "eeMemory.bin"
DEFAULT_SSTATES = Path(r"C:\Users\mmc99\Documents\PCSX2\sstates")


@dataclass(frozen=True)
class Candidate:
    pose_addr: int
    quat_addr: int
    trans_addr: int
    count: int
    score: float
    q_unit_score: float
    q_w_negative_ratio: float
    trans_score: float
    max_abs_t: float
    sample_q: tuple[float, float, float, float]
    sample_t: tuple[float, float, float]


def parse_int(value: str) -> int:
    return int(value, 0)


def load_ee_ram(path: Path) -> bytes:
    with zipfile.ZipFile(path, "r") as z:
        names = z.namelist()
        if EE_MEM_FILENAME not in names:
            raise FileNotFoundError(f"{path} has no {EE_MEM_FILENAME}; entries: {names}")
        return z.read(EE_MEM_FILENAME)


def default_savestate() -> Path:
    states = sorted(
        DEFAULT_SSTATES.glob("SLUS-20013*.p2s"),
        key=lambda item: item.stat().st_mtime,
        reverse=True,
    )
    if not states:
        raise FileNotFoundError(f"no THPS3 SLUS-20013 .p2s savestates under {DEFAULT_SSTATES}")
    return states[0]


def u32(data: bytes, offset: int) -> int:
    return struct.unpack_from("<I", data, offset)[0]


def floats(data: bytes, offset: int, count: int) -> tuple[float, ...]:
    return struct.unpack_from("<" + "f" * count, data, offset)


def valid_ptr(ptr: int, size: int, mem_size: int) -> bool:
    return ptr % 4 == 0 and 0 <= ptr <= mem_size - size


def finite_reasonable(values: tuple[float, ...], limit: float) -> bool:
    return all(math.isfinite(value) and abs(value) <= limit for value in values)


def quat_len(q: tuple[float, float, float, float]) -> float:
    return math.sqrt(sum(value * value for value in q))


def score_candidate(
    data: bytes,
    pose_addr: int,
    quat_addr: int,
    trans_addr: int,
    count: int,
    quat_stride: int,
    trans_stride: int,
) -> Candidate | None:
    q_size = count * quat_stride
    t_size = count * trans_stride
    if not valid_ptr(quat_addr, q_size, len(data)) or not valid_ptr(trans_addr, t_size, len(data)):
        return None
    if abs(quat_addr - trans_addr) > 0x200000:
        return None

    q_lengths = []
    negative_w = 0
    trans_ok = 0
    max_abs_t = 0.0
    sample_q = (0.0, 0.0, 0.0, 1.0)
    sample_t = (0.0, 0.0, 0.0)

    for bone in range(count):
        q_raw = floats(data, quat_addr + bone * quat_stride, 4)
        t_raw = floats(data, trans_addr + bone * trans_stride, 3)
        q = (float(q_raw[0]), float(q_raw[1]), float(q_raw[2]), float(q_raw[3]))
        t = (float(t_raw[0]), float(t_raw[1]), float(t_raw[2]))
        if bone == 0:
            sample_q = q
            sample_t = t
        if not finite_reasonable(q, 2.0) or not finite_reasonable(t, 10000.0):
            return None

        q_len = quat_len(q)
        q_lengths.append(q_len)
        if q[3] < 0:
            negative_w += 1
        if max(abs(t[0]), abs(t[1]), abs(t[2])) < 5000.0:
            trans_ok += 1
        max_abs_t = max(max_abs_t, abs(t[0]), abs(t[1]), abs(t[2]))

    q_unit_errors = [abs(length - 1.0) for length in q_lengths]
    q_unit_score = sum(1.0 for error in q_unit_errors if error <= 0.10) / count
    q_w_negative_ratio = negative_w / count
    trans_score = trans_ok / count

    # Favor normalized quats, plausible translations, adjacent Q/T buffers, and
    # the known THPS3 SKA observation that most runtime quats have negative w.
    proximity_score = max(0.0, 1.0 - abs(quat_addr - trans_addr) / 0x200000)
    negative_w_score = 1.0 - abs(q_w_negative_ratio - 0.9)
    score = q_unit_score * 4.0 + trans_score * 2.0 + proximity_score + negative_w_score
    return Candidate(
        pose_addr=pose_addr,
        quat_addr=quat_addr,
        trans_addr=trans_addr,
        count=count,
        score=score,
        q_unit_score=q_unit_score,
        q_w_negative_ratio=q_w_negative_ratio,
        trans_score=trans_score,
        max_abs_t=max_abs_t,
        sample_q=sample_q,
        sample_t=sample_t,
    )


def scan_pose_structs(
    data: bytes,
    count: int,
    start: int,
    end: int,
    quat_stride: int,
    trans_stride: int,
) -> list[Candidate]:
    candidates: list[Candidate] = []
    end = min(end, len(data) - 0x34)
    for offset in range(start, end, 4):
        if u32(data, offset) != count:
            continue
        quat_addr = u32(data, offset + 0x2C)
        trans_addr = u32(data, offset + 0x30)
        candidate = score_candidate(data, offset, quat_addr, trans_addr, count, quat_stride, trans_stride)
        if candidate is not None:
            candidates.append(candidate)
    candidates.sort(key=lambda item: item.score, reverse=True)
    return candidates


def pose_metadata(data: bytes, pose_addr: int) -> dict[str, object]:
    return {
        "count": u32(data, pose_addr),
        "animation_time": floats(data, pose_addr + 0x08, 1)[0],
        "key_table_address": f"0x{u32(data, pose_addr + 0x0C):08X}",
        "output_quat_address": f"0x{u32(data, pose_addr + 0x2C):08X}",
        "output_trans_address": f"0x{u32(data, pose_addr + 0x30):08X}",
        "source_a_quat_address": f"0x{u32(data, pose_addr + 0x3C):08X}",
        "source_a_trans_address": f"0x{u32(data, pose_addr + 0x40):08X}",
        "source_b_quat_address": f"0x{u32(data, pose_addr + 0x4C):08X}",
        "source_b_trans_address": f"0x{u32(data, pose_addr + 0x50):08X}",
    }


def dump_pose(data: bytes, candidate: Candidate, quat_stride: int, trans_stride: int) -> dict[str, object]:
    bones = []
    for bone in range(candidate.count):
        q_address = candidate.quat_addr + bone * quat_stride
        t_address = candidate.trans_addr + bone * trans_stride
        q_record = floats(data, q_address, quat_stride // 4)
        t_record = floats(data, t_address, trans_stride // 4)
        bones.append(
            {
                "bone": bone,
                "q_address": f"0x{q_address:08X}",
                "t_address": f"0x{t_address:08X}",
                "q": list(q_record[:4]),
                "t": list(t_record[:3]),
                "q_record": list(q_record),
                "t_record": list(t_record),
            }
        )
    return {
        "pose_address": f"0x{candidate.pose_addr:08X}",
        "quat_address": f"0x{candidate.quat_addr:08X}",
        "trans_address": f"0x{candidate.trans_addr:08X}",
        "count": candidate.count,
        "quat_stride": quat_stride,
        "trans_stride": trans_stride,
        "score": candidate.score,
        "pose_metadata": pose_metadata(data, candidate.pose_addr),
        "bones": bones,
    }


def candidate_to_dict(data: bytes, candidate: Candidate) -> dict[str, object]:
    return {
        "pose_address": f"0x{candidate.pose_addr:08X}",
        "quat_address": f"0x{candidate.quat_addr:08X}",
        "trans_address": f"0x{candidate.trans_addr:08X}",
        "count": candidate.count,
        "score": candidate.score,
        "q_unit_score": candidate.q_unit_score,
        "q_w_negative_ratio": candidate.q_w_negative_ratio,
        "trans_score": candidate.trans_score,
        "max_abs_t": candidate.max_abs_t,
        "sample_q": list(candidate.sample_q),
        "sample_t": list(candidate.sample_t),
        "pose_metadata": pose_metadata(data, candidate.pose_addr),
    }


def print_candidates(candidates: list[Candidate], top: int) -> None:
    print("rank\tpose\tquat\ttrans\tscore\tq_unit\tneg_w\ttrans\tmax_abs_t\tsample_q\tsample_t")
    for index, candidate in enumerate(candidates[:top], start=1):
        sample_q = ",".join(f"{value:.4g}" for value in candidate.sample_q)
        sample_t = ",".join(f"{value:.4g}" for value in candidate.sample_t)
        print(
            "\t".join(
                [
                    str(index),
                    f"0x{candidate.pose_addr:08X}",
                    f"0x{candidate.quat_addr:08X}",
                    f"0x{candidate.trans_addr:08X}",
                    f"{candidate.score:.4f}",
                    f"{candidate.q_unit_score:.3f}",
                    f"{candidate.q_w_negative_ratio:.3f}",
                    f"{candidate.trans_score:.3f}",
                    f"{candidate.max_abs_t:.3f}",
                    sample_q,
                    sample_t,
                ]
            )
        )


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("savestate", type=Path, nargs="?", help="PCSX2 .p2s savestate; defaults to newest THPS3 state")
    parser.add_argument("--count", type=int, default=29)
    parser.add_argument("--start", type=parse_int, default=0)
    parser.add_argument("--end", type=parse_int, default=0x02000000)
    parser.add_argument("--quat-stride", type=parse_int, default=0x18)
    parser.add_argument("--trans-stride", type=parse_int, default=0x14)
    parser.add_argument("--top", type=int, default=20)
    parser.add_argument("--animation", default="")
    parser.add_argument("--time", type=float, default=None)
    parser.add_argument("--out", type=Path, help="write candidate list JSON")
    parser.add_argument("--dump-best", type=Path, help="write best candidate as pose JSON")
    args = parser.parse_args()

    savestate = args.savestate or default_savestate()
    data = load_ee_ram(savestate)
    print(f"loaded {savestate} ({len(data):,} bytes EE RAM)")

    candidates = scan_pose_structs(data, args.count, args.start, args.end, args.quat_stride, args.trans_stride)
    print_candidates(candidates, args.top)

    payload = {
        "source": str(savestate),
        "animation": args.animation,
        "time": args.time,
        "count": args.count,
        "candidates": [candidate_to_dict(data, candidate) for candidate in candidates],
    }
    if args.out:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(payload, indent=2), encoding="utf-8")
        print(f"wrote {args.out}")
    if args.dump_best:
        if not candidates:
            raise RuntimeError("no candidates to dump")
        pose = {
            "source": str(savestate),
            "animation": args.animation,
            "time": args.time,
            **dump_pose(data, candidates[0], args.quat_stride, args.trans_stride),
        }
        args.dump_best.parent.mkdir(parents=True, exist_ok=True)
        args.dump_best.write_text(json.dumps(pose, indent=2), encoding="utf-8")
        print(f"wrote {args.dump_best}")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, zipfile.BadZipFile, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
