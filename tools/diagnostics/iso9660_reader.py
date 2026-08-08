#!/usr/bin/env python3
"""Minimal ISO9660 reader for raw MODE1/2352 CD images.

Exists to serve REAL disc sectors to safedisc_emu.py. SafeDisc's media check
issues SCSI READ(10) against the physical disc, and answering it with a
synthesised Primary Volume Descriptor means guessing the volume label, size and
timestamp -- all of which the check can compare. With the actual image, those
answers are simply true.

A MODE1/2352 sector is 12 bytes of sync, a 4-byte header, 2048 bytes of user
data, then error correction. So user data for LBA n starts at n*2352 + 16.

Usage:
    python tools/diagnostics/iso9660_reader.py <image.bin> --pvd
    python tools/diagnostics/iso9660_reader.py <image.bin> --ls
    python tools/diagnostics/iso9660_reader.py <image.bin> --extract THUG2.EXE -o out.exe
    python tools/diagnostics/iso9660_reader.py <image.bin> --sector 16
"""

from __future__ import annotations

import argparse
import hashlib
import struct
import sys
from pathlib import Path

RAW_SECTOR = 2352
USER_DATA = 2048
USER_OFFSET = 16


class DiscImage:
    """Raw MODE1/2352 image addressed by logical block."""

    def __init__(self, path: Path):
        self.path = path
        self.handle = path.open("rb")
        self.sectors = path.stat().st_size // RAW_SECTOR

    def read_sector(self, lba: int) -> bytes:
        """The 2048 bytes of user data at this LBA, or zeros past the end."""
        if lba < 0 or lba >= self.sectors:
            return b"\x00" * USER_DATA
        self.handle.seek(lba * RAW_SECTOR + USER_OFFSET)
        return self.handle.read(USER_DATA).ljust(USER_DATA, b"\x00")

    def read_raw(self, lba: int) -> bytes:
        if lba < 0 or lba >= self.sectors:
            return b"\x00" * RAW_SECTOR
        self.handle.seek(lba * RAW_SECTOR)
        return self.handle.read(RAW_SECTOR).ljust(RAW_SECTOR, b"\x00")

    # --- ISO9660 ----------------------------------------------------------

    def pvd(self) -> dict:
        data = self.read_sector(16)
        return {
            "type": data[0],
            "magic": data[1:6].decode("latin1"),
            "system_id": data[8:40].decode("latin1").rstrip(),
            "volume_id": data[40:72].decode("latin1").rstrip(),
            "sectors": struct.unpack_from("<I", data, 80)[0],
            "block_size": struct.unpack_from("<H", data, 128)[0],
            "created": data[813:830].decode("latin1", "replace"),
            "root_lba": struct.unpack_from("<I", data, 156 + 2)[0],
            "root_size": struct.unpack_from("<I", data, 156 + 10)[0],
        }

    def walk(self, lba: int, size: int, prefix: str = ""):
        """Yield (path, lba, size) for every file under a directory extent."""
        blocks = (size + USER_DATA - 1) // USER_DATA
        data = b"".join(self.read_sector(lba + i) for i in range(blocks))
        offset = 0
        while offset < len(data):
            length = data[offset]
            if length == 0:
                # Directory records never straddle a sector boundary; skip pad.
                offset = (offset // USER_DATA + 1) * USER_DATA
                if offset >= len(data):
                    break
                continue
            record = data[offset : offset + length]
            child_lba = struct.unpack_from("<I", record, 2)[0]
            child_size = struct.unpack_from("<I", record, 10)[0]
            flags = record[25]
            name_len = record[32]
            name = record[33 : 33 + name_len].decode("latin1", "replace")
            offset += length
            if name in ("\x00", "\x01"):        # . and ..
                continue
            clean = name.split(";")[0]
            if flags & 0x02:                     # directory
                yield from self.walk(child_lba, child_size, prefix + clean + "/")
            else:
                yield prefix + clean, child_lba, child_size

    def read_file(self, lba: int, size: int) -> bytes:
        blocks = (size + USER_DATA - 1) // USER_DATA
        return b"".join(self.read_sector(lba + i) for i in range(blocks))[:size]


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("image", type=Path)
    ap.add_argument("--pvd", action="store_true")
    ap.add_argument("--ls", action="store_true")
    ap.add_argument("--sector", type=lambda v: int(v, 0))
    ap.add_argument("--extract", help="Path inside the image (case-insensitive)")
    ap.add_argument("-o", "--output", type=Path)
    args = ap.parse_args()

    disc = DiscImage(args.image)
    if args.pvd or not any((args.ls, args.sector is not None, args.extract)):
        info = disc.pvd()
        print(f"{args.image.name}: {disc.sectors:,} sectors")
        for key, value in info.items():
            print(f"  {key:<11} {value}")

    if args.sector is not None:
        data = disc.read_sector(args.sector)
        print(f"\nLBA {args.sector} user data (sha256 {hashlib.sha256(data).hexdigest()[:32]}):")
        print(data[:96].hex(" "))

    if args.ls or args.extract:
        info = disc.pvd()
        entries = list(disc.walk(info["root_lba"], info["root_size"]))
        if args.ls:
            for name, lba, size in entries:
                print(f"  {size:>12,}  LBA {lba:>7}  {name}")
            print(f"  ({len(entries)} files)")
        if args.extract:
            want = args.extract.lower().replace("\\", "/")
            for name, lba, size in entries:
                if name.lower() == want or name.lower().endswith("/" + want):
                    payload = disc.read_file(lba, size)
                    print(f"\n{name}: {size:,} bytes, "
                          f"sha256 {hashlib.sha256(payload).hexdigest()}")
                    if args.output:
                        args.output.write_bytes(payload)
                        print(f"  wrote {args.output}")
                    return 0
            print(f"not found: {args.extract}", file=sys.stderr)
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
