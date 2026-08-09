#!/usr/bin/env python3
"""Dump THPS3 runtime pose Q/T buffers from PCSX2 PINE or a savestate.

This is a companion to the matrix runbook. At `FUN_00231048` / `0x00231048`,
register `a0` points to a pose struct:

- `[a0 + 0x00]` = bone count
- `[a0 + 0x2C]` = quaternion buffer, 0x18-byte records
- `[a0 + 0x30]` = translation buffer, 0x14-byte records
- `[a0 + 0x3C/0x40]` and `[a0 + 0x4C/0x50]` = source Q/T buffers

Break at function entry, copy `a0`, continue to the return at `0x00231220`,
then run this helper with `--pose-addr <a0>`.
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
from pathlib import Path
from typing import Protocol

from thps3_matrix_dump import PineClient, SavestateReader, parse_int


class Reader(Protocol):
    def read_bytes(self, address: int, count: int) -> bytes:
        ...


def read_u32(reader: Reader, address: int) -> int:
    return struct.unpack("<I", reader.read_bytes(address, 4))[0]


def read_floats(reader: Reader, address: int, count: int) -> list[float]:
    return list(struct.unpack("<" + "f" * count, reader.read_bytes(address, count * 4)))


def read_pose_metadata(reader: Reader, pose_addr: int) -> dict[str, object]:
    return {
        "count": read_u32(reader, pose_addr),
        "animation_time": read_floats(reader, pose_addr + 0x08, 1)[0],
        "key_table_address": f"0x{read_u32(reader, pose_addr + 0x0C):08X}",
        "output_quat_address": f"0x{read_u32(reader, pose_addr + 0x2C):08X}",
        "output_trans_address": f"0x{read_u32(reader, pose_addr + 0x30):08X}",
        "source_a_quat_address": f"0x{read_u32(reader, pose_addr + 0x3C):08X}",
        "source_a_trans_address": f"0x{read_u32(reader, pose_addr + 0x40):08X}",
        "source_b_quat_address": f"0x{read_u32(reader, pose_addr + 0x4C):08X}",
        "source_b_trans_address": f"0x{read_u32(reader, pose_addr + 0x50):08X}",
    }


def dump_pose(
    reader: Reader,
    pose_addr: int | None,
    quat_addr: int | None,
    trans_addr: int | None,
    count: int | None,
    quat_stride: int,
    trans_stride: int,
    slot: str,
) -> dict[str, object]:
    metadata: dict[str, object] | None = None
    if pose_addr is not None:
        metadata = read_pose_metadata(reader, pose_addr)
        pose_count = int(metadata["count"])
        slot_fields = {
            "output": ("output_quat_address", "output_trans_address"),
            "source-a": ("source_a_quat_address", "source_a_trans_address"),
            "source-b": ("source_b_quat_address", "source_b_trans_address"),
        }
        q_field, t_field = slot_fields[slot]
        pose_quat = parse_int(str(metadata[q_field]))
        pose_trans = parse_int(str(metadata[t_field]))
        count = count or pose_count
        quat_addr = quat_addr or pose_quat
        trans_addr = trans_addr or pose_trans

    if count is None:
        count = 29
    if not 1 <= count <= 128:
        raise ValueError(f"unreasonable bone count: {count}")
    if quat_addr is None or trans_addr is None:
        raise ValueError("supply --pose-addr or both --quat-addr and --trans-addr")

    bones = []
    for bone in range(count):
        q_address = quat_addr + bone * quat_stride
        t_address = trans_addr + bone * trans_stride
        q_record = read_floats(reader, q_address, quat_stride // 4)
        t_record = read_floats(reader, t_address, trans_stride // 4)
        bones.append(
            {
                "bone": bone,
                "q_address": f"0x{q_address:08X}",
                "t_address": f"0x{t_address:08X}",
                "q": q_record[:4],
                "t": t_record[:3],
                "q_record": q_record,
                "t_record": t_record,
            }
        )

    return {
        "pose_address": f"0x{pose_addr:08X}" if pose_addr is not None else None,
        "quat_address": f"0x{quat_addr:08X}",
        "trans_address": f"0x{trans_addr:08X}",
        "count": count,
        "quat_stride": quat_stride,
        "trans_stride": trans_stride,
        "slot": slot,
        "pose_metadata": metadata,
        "bones": bones,
    }


def write_json(path: Path, payload: dict[str, object]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, indent=2), encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--pine", action="store_true", help="read from a live PCSX2 PINE server")
    source.add_argument("--savestate", type=Path, help="read from a PCSX2 .p2s savestate")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=28011)
    parser.add_argument("--pose-addr", type=parse_int, help="EE address from register a0 at 0x00231048")
    parser.add_argument("--quat-addr", type=parse_int, help="explicit quaternion buffer address")
    parser.add_argument("--trans-addr", type=parse_int, help="explicit translation buffer address")
    parser.add_argument("--count", type=int, help="bone count override; defaults to pose count or 29")
    parser.add_argument("--quat-stride", type=parse_int, default=0x18)
    parser.add_argument("--trans-stride", type=parse_int, default=0x14)
    parser.add_argument("--slot", choices=["output", "source-a", "source-b"], default="output")
    parser.add_argument("--animation", default="")
    parser.add_argument("--time", type=float, default=None)
    parser.add_argument("--out", type=Path, default=Path("TestOutput/thps3_runtime_pose.json"))
    args = parser.parse_args()

    pine: PineClient | None = None
    try:
        if args.savestate:
            reader: Reader = SavestateReader(args.savestate)
            source_name = str(args.savestate)
        else:
            pine = PineClient(args.host, args.port)
            reader = pine
            source_name = f"PINE {args.host}:{args.port}"

        pose = dump_pose(
            reader,
            args.pose_addr,
            args.quat_addr,
            args.trans_addr,
            args.count,
            args.quat_stride,
            args.trans_stride,
            args.slot,
        )
        payload: dict[str, object] = {
            "source": source_name,
            "animation": args.animation,
            "time": args.time,
            **pose,
        }
        write_json(args.out, payload)
        print(f"wrote {args.out}")
        return 0
    finally:
        if pine is not None:
            pine.close()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, RuntimeError, ValueError, FileNotFoundError, struct.error) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
