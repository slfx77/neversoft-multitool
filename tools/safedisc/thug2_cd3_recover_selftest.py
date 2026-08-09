#!/usr/bin/env python3
"""Fixture-free tests for thug2_cd3_recover.py."""

from __future__ import annotations

import hashlib
import struct
import tempfile
import unittest
from pathlib import Path

from thug2_cd3_recover import (
    PeIdentity,
    RecoveryError,
    RecoveryIdentity,
    SectionIdentity,
    recover,
)


RAW_SECTOR = 2352
USER_OFFSET = 16
USER_SIZE = 2048


def minimal_pe() -> bytes:
    """Build a small, parseable PE32 image without external fixtures."""
    data = bytearray(0x400)
    data[:2] = b"MZ"
    struct.pack_into("<I", data, 0x3C, 0x80)
    data[0x80:0x84] = b"PE\0\0"
    struct.pack_into("<HHIIIHH", data, 0x84,
                     0x14C, 1, 0x12345678, 0, 0, 0xE0, 0x010F)

    optional = 0x98
    struct.pack_into("<HBB", data, optional, 0x10B, 7, 10)
    struct.pack_into("<III", data, optional + 4, 0x200, 0, 0)
    struct.pack_into("<III", data, optional + 16, 0x1000, 0x1000, 0x1000)
    struct.pack_into("<III", data, optional + 28, 0x400000, 0x1000, 0x200)
    struct.pack_into("<HHHHHH", data, optional + 40, 4, 0, 0, 0, 4, 0)
    struct.pack_into("<III", data, optional + 52, 0, 0x2000, 0x200)
    struct.pack_into("<IHH", data, optional + 64, 0, 2, 0)
    struct.pack_into("<IIIIII", data, optional + 72,
                     0x100000, 0x1000, 0x100000, 0x1000, 0, 16)

    section = 0x178
    data[section:section + 8] = b".text\0\0\0"
    struct.pack_into("<IIIIIIHHI", data, section + 8,
                     1, 0x1000, 0x200, 0x200, 0, 0, 0, 0, 0x60000020)
    data[0x200] = 0xC3
    return bytes(data)


def directory_record(name: bytes, lba: int, size: int, directory: bool) -> bytes:
    length = 33 + len(name) + (1 if len(name) % 2 == 0 else 0)
    record = bytearray(length)
    record[0] = length
    struct.pack_into("<I", record, 2, lba)
    struct.pack_into(">I", record, 6, lba)
    struct.pack_into("<I", record, 10, size)
    struct.pack_into(">I", record, 14, size)
    record[25] = 2 if directory else 0
    struct.pack_into("<H", record, 28, 1)
    struct.pack_into(">H", record, 30, 1)
    record[32] = len(name)
    record[33:33 + len(name)] = name
    return bytes(record)


def put_user_sector(image: bytearray, lba: int, payload: bytes) -> None:
    if len(payload) > USER_SIZE:
        raise ValueError("test sector payload is too large")
    start = lba * RAW_SECTOR + USER_OFFSET
    image[start:start + len(payload)] = payload


def minimal_iso(payload: bytes) -> bytes:
    sectors = 24
    image = bytearray(sectors * RAW_SECTOR)

    pvd = bytearray(USER_SIZE)
    pvd[0] = 1
    pvd[1:6] = b"CD001"
    pvd[6] = 1
    pvd[40:72] = b"TEST_DISC".ljust(32, b" ")
    struct.pack_into("<I", pvd, 80, sectors)
    struct.pack_into(">I", pvd, 84, sectors)
    struct.pack_into("<H", pvd, 128, USER_SIZE)
    struct.pack_into(">H", pvd, 130, USER_SIZE)
    root_record = directory_record(b"\0", 20, USER_SIZE, True)
    pvd[156:156 + len(root_record)] = root_record
    put_user_sector(image, 16, pvd)

    terminator = bytearray(USER_SIZE)
    terminator[0] = 255
    terminator[1:6] = b"CD001"
    terminator[6] = 1
    put_user_sector(image, 17, terminator)

    root = b"".join((
        directory_record(b"\0", 20, USER_SIZE, True),
        directory_record(b"\1", 20, USER_SIZE, True),
        directory_record(b"CRACK", 21, USER_SIZE, True),
    ))
    put_user_sector(image, 20, root)

    crack = b"".join((
        directory_record(b"\0", 21, USER_SIZE, True),
        directory_record(b"\1", 20, USER_SIZE, True),
        directory_record(b"THUG2.EXE;1", 22, len(payload), False),
    ))
    put_user_sector(image, 21, crack)
    put_user_sector(image, 22, payload)
    return bytes(image)


def test_identity(pe_data: bytes, disc_data: bytes) -> RecoveryIdentity:
    section = SectionIdentity(".text", 0x1000, 1, 0x200, 0x200, 0x60000020)
    pe = PeIdentity(
        sha256=hashlib.sha256(pe_data).hexdigest(),
        size=len(pe_data),
        timestamp=0x12345678,
        image_base=0x400000,
        entry_rva=0x1000,
        image_size=0x2000,
        import_rva=0,
        import_size=0,
        sections=(section,),
    )
    return RecoveryIdentity(
        disc_sha256=hashlib.sha256(disc_data).hexdigest(),
        volume_id="TEST_DISC",
        volume_sectors=24,
        embedded_path="CRACK/THUG2.EXE",
        embedded_lba=22,
        protected=pe,
        recovered=pe,
    )


class RecoveryTests(unittest.TestCase):
    def test_recovers_validated_embedded_pe_and_refuses_overwrite(self) -> None:
        pe_data = minimal_pe()
        disc_data = minimal_iso(pe_data)
        identity = test_identity(pe_data, disc_data)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            disc = root / "disc.bin"
            protected = root / "protected.exe"
            output = root / "recovered.exe"
            disc.write_bytes(disc_data)
            protected.write_bytes(pe_data)

            result = recover(disc, protected, output, identity)
            self.assertEqual(output.read_bytes(), pe_data)
            self.assertEqual(result.sha256, identity.recovered.sha256)
            self.assertEqual(result.size, len(pe_data))

            with self.assertRaisesRegex(RecoveryError, "refusing to overwrite"):
                recover(disc, protected, output, identity)
            with self.assertRaisesRegex(RecoveryError, "refusing to overwrite"):
                recover(disc, protected, protected, identity)

    def test_hash_mismatch_writes_nothing(self) -> None:
        pe_data = minimal_pe()
        disc_data = minimal_iso(pe_data)
        identity = test_identity(pe_data, disc_data)

        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            disc = root / "disc.bin"
            protected = root / "protected.exe"
            output = root / "recovered.exe"
            disc.write_bytes(disc_data[:-1] + bytes([disc_data[-1] ^ 1]))
            protected.write_bytes(pe_data)

            with self.assertRaisesRegex(RecoveryError, "CD3 image SHA-256 mismatch"):
                recover(disc, protected, output, identity)
            self.assertFalse(output.exists())


if __name__ == "__main__":
    unittest.main()
