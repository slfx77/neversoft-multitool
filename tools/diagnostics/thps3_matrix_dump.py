#!/usr/bin/env python3
"""Dump THPS3 PS2 runtime bone matrices from PCSX2 PINE or a savestate.

This is a narrow diagnostic helper for the THPS3 SKA runbook. It does not find
the matrix buffer by itself; use the PCSX2 debugger/Ghidra address from the
interpolator path, then pass that address here.
"""

from __future__ import annotations

import argparse
import json
import socket
import struct
import sys
import zipfile
from pathlib import Path
from typing import BinaryIO


EE_MEM_FILENAME = "eeMemory.bin"

MSG_READ32 = 2
MSG_VERSION = 8
MSG_TITLE = 0xB
MSG_ID = 0xC
MSG_STATUS = 0xF

STATUS_NAMES = {
    0: "running",
    1: "paused",
    2: "shutdown",
}


class PineClient:
    def __init__(self, host: str, port: int) -> None:
        self._sock = socket.create_connection((host, port), timeout=5.0)

    def close(self) -> None:
        self._sock.close()

    def request(self, payload: bytes) -> bytes:
        self._sock.sendall(struct.pack("<I", len(payload) + 4) + payload)
        header = self._recv_exact(5)
        size, status = struct.unpack("<IB", header)
        if status != 0:
            raise RuntimeError("PINE request failed")
        return self._recv_exact(size - 5)

    def read_u32(self, address: int) -> int:
        data = self.request(bytes([MSG_READ32]) + struct.pack("<I", address))
        return struct.unpack("<I", data)[0]

    def read_bytes(self, address: int, count: int) -> bytes:
        chunks = []
        for offset in range(0, count, 4):
            value = self.read_u32(address + offset)
            chunks.append(struct.pack("<I", value))
        return b"".join(chunks)[:count]

    def query_string(self, opcode: int) -> str:
        data = self.request(bytes([opcode]))
        if len(data) < 4:
            return ""
        size = struct.unpack("<I", data[:4])[0]
        return data[4 : 4 + max(0, size - 1)].decode("utf-8", errors="replace")

    def query_u32(self, opcode: int) -> int:
        data = self.request(bytes([opcode]))
        return struct.unpack("<I", data[:4])[0]

    def _recv_exact(self, count: int) -> bytes:
        data = bytearray()
        while len(data) < count:
            chunk = self._sock.recv(count - len(data))
            if not chunk:
                raise RuntimeError("PINE connection closed")
            data.extend(chunk)
        return bytes(data)


class SavestateReader:
    def __init__(self, path: Path) -> None:
        with zipfile.ZipFile(path, "r") as z:
            names = z.namelist()
            if EE_MEM_FILENAME not in names:
                raise FileNotFoundError(f"{path} has no {EE_MEM_FILENAME}; entries: {names}")
            self._ee = z.read(EE_MEM_FILENAME)

    def read_bytes(self, address: int, count: int) -> bytes:
        if address < 0 or address + count > len(self._ee):
            raise ValueError(
                f"EE address range 0x{address:08X}..0x{address + count:08X} "
                f"is outside savestate RAM size 0x{len(self._ee):X}"
            )
        return self._ee[address : address + count]


def parse_int(value: str) -> int:
    return int(value, 0)


def dump_matrices(reader: PineClient | SavestateReader, base: int, count: int, stride: int) -> list[dict[str, object]]:
    matrices: list[dict[str, object]] = []
    for bone in range(count):
        address = base + bone * stride
        raw = reader.read_bytes(address, 64)
        values = struct.unpack("<16f", raw)
        matrices.append(
            {
                "bone": bone,
                "address": f"0x{address:08X}",
                "m": [
                    list(values[0:4]),
                    list(values[4:8]),
                    list(values[8:12]),
                    list(values[12:16]),
                ],
            }
        )
    return matrices


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
    parser.add_argument("--addr", type=parse_int, help="EE address of bone matrix buffer")
    parser.add_argument("--count", type=int, default=29)
    parser.add_argument("--stride", type=parse_int, default=0x40)
    parser.add_argument("--animation", default="")
    parser.add_argument("--time", type=float, default=None)
    parser.add_argument("--out", type=Path, default=Path("TestOutput/thps3_runtime_matrices.json"))
    args = parser.parse_args()

    reader: PineClient | SavestateReader
    pine: PineClient | None = None

    try:
        if args.savestate:
            reader = SavestateReader(args.savestate)
            source_name = str(args.savestate)
        else:
            pine = PineClient(args.host, args.port)
            reader = pine
            version = pine.query_string(MSG_VERSION)
            title = pine.query_string(MSG_TITLE)
            game_id = pine.query_string(MSG_ID)
            status = STATUS_NAMES.get(pine.query_u32(MSG_STATUS), "unknown")
            print(f"PINE: {version} | {game_id} | {title} | {status}")
            source_name = f"PINE {args.host}:{args.port}"

        if args.addr is None:
            print("No --addr supplied; query succeeded but no matrices were dumped.")
            return 0

        payload: dict[str, object] = {
            "source": source_name,
            "base_address": f"0x{args.addr:08X}",
            "count": args.count,
            "stride": args.stride,
            "animation": args.animation,
            "time": args.time,
            "matrices": dump_matrices(reader, args.addr, args.count, args.stride),
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
    except (OSError, RuntimeError, ValueError, FileNotFoundError) as exc:
        print(f"error: {exc}", file=sys.stderr)
        raise SystemExit(2)
