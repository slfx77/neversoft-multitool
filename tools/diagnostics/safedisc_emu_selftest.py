#!/usr/bin/env python3
"""Focused, fixture-free probes for SafeDisc clock, SCSI, and disc metadata."""

import hashlib
import struct
import tempfile
from collections import Counter
from pathlib import Path
from types import SimpleNamespace

from unicorn import Uc, UC_ARCH_X86, UC_MODE_32

from safedisc_emu import (
    FILETIME_BASE_2020,
    INSTRUCTIONS_PER_MS,
    SCSI_STATUS_CHECK_CONDITION,
    THUG2_RETAIL_BAD_SECTOR_PROFILE,
    THUG2_RETAIL_ROOT_ALIASES,
    SafeDiscEmulator,
    is_bad_disc_sector,
    thug2_retail_descriptor_sectors,
)


MEMORY = 0x00100000


def bare_emulator() -> SafeDiscEmulator:
    """Construct only the state these unit probes need; no game image required."""
    emu = SafeDiscEmulator.__new__(SafeDiscEmulator)
    emu.uc = Uc(UC_ARCH_X86, UC_MODE_32)
    emu.uc.mem_map(MEMORY, 0x1000)
    emu.instructions = 0
    emu.sleep_ms = 0
    emu.api_calls = Counter()
    emu.api_order = []
    emu.api_tail = []
    emu.last_error = 0
    return emu


def test_system_time_as_filetime() -> None:
    emu = bare_emulator()
    first_ptr = MEMORY
    second_ptr = MEMORY + 8

    result = emu.handle_api("GetSystemTimeAsFileTime", [first_ptr])
    first = struct.unpack("<Q", bytes(emu.uc.mem_read(first_ptr, 8)))[0]
    assert result == 0, "GetSystemTimeAsFileTime is a void API"
    assert first == FILETIME_BASE_2020 + emu.virtual_ms() * 10_000

    emu.instructions += 7 * INSTRUCTIONS_PER_MS
    emu.handle_api("GetSystemTimeAsFileTime", [second_ptr])
    second = struct.unpack("<Q", bytes(emu.uc.mem_read(second_ptr, 8)))[0]
    assert second - first == 70_000, "seven virtual milliseconds must be 70,000 FILETIME ticks"

    system_ptr = MEMORY + 0x10
    roundtrip_ptr = MEMORY + 0x20
    emu.handle_api("GetSystemTime", [system_ptr])
    system_time = struct.unpack("<8H", bytes(emu.uc.mem_read(system_ptr, 16)))
    assert system_time[:2] == (2020, 1)
    assert system_time[2] == 3  # 2020-01-01 was Wednesday; Win32 Sunday is zero.
    assert emu.handle_api("SystemTimeToFileTime", [system_ptr, roundtrip_ptr]) == 1
    roundtrip = struct.unpack("<Q", bytes(emu.uc.mem_read(roundtrip_ptr, 8)))[0]
    assert roundtrip == second

    converted_ptr = MEMORY + 0x30
    assert emu.handle_api("FileTimeToSystemTime", [roundtrip_ptr, converted_ptr]) == 1
    assert bytes(emu.uc.mem_read(converted_ptr, 16)) == bytes(emu.uc.mem_read(system_ptr, 16))

    local_filetime_ptr = MEMORY + 0x40
    assert emu.handle_api("FileTimeToLocalFileTime", [roundtrip_ptr, local_filetime_ptr]) == 1
    assert bytes(emu.uc.mem_read(local_filetime_ptr, 8)) == struct.pack("<Q", roundtrip)

    timezone_ptr = MEMORY + 0x100
    assert emu.handle_api("GetTimeZoneInformation", [timezone_ptr]) == 0
    timezone = bytes(emu.uc.mem_read(timezone_ptr, 172))
    assert struct.unpack_from("<l", timezone, 0)[0] == 0
    assert timezone[4:12].decode("utf-16le").rstrip("\0") == "UTC"
    assert timezone[68:88] == bytes(20)
    assert timezone[152:172] == bytes(20)

    invalid_system_ptr = MEMORY + 0x200
    emu.uc.mem_write(
        invalid_system_ptr,
        struct.pack("<8H", 1600, 1, 0, 1, 0, 0, 0, 0),
    )
    assert emu.handle_api(
        "SystemTimeToFileTime", [invalid_system_ptr, roundtrip_ptr]
    ) == 0


def test_iat_scan_requires_executable_reference() -> None:
    emu = bare_emulator()
    executable = SimpleNamespace(
        VirtualAddress=0x1000,
        SizeOfRawData=0x1000,
        Characteristics=0x20000000,
    )
    non_executable = SimpleNamespace(
        VirtualAddress=0x2000,
        SizeOfRawData=0x1000,
        Characteristics=0x40000040,
    )
    emu.pe = SimpleNamespace(sections=[executable, non_executable])
    emu.stubs = {
        0x70000001: ("GetProcAddress", 2),
        0x70000005: ("RegCreateKeyA", 4),
        0x70000068: ("GetStartupInfoA", 1),
    }
    emu.stub_dll = {
        "GetProcAddress": "kernel32.dll",
        "RegCreateKeyA": "advapi32.dll",
        "GetStartupInfoA": "kernel32.dll",
    }

    image = bytearray(0x3000)
    for offset, value in (
        (0x1100, 0x70000068),
        (0x1200, 0x70000001),
        (0x1300, 0x70000005),
    ):
        struct.pack_into("<I", image, offset, value)
    image[0x1400:0x1406] = b"\xFF\x15" + struct.pack("<I", 0x00402000)
    image[0x1410:0x1416] = b"\xFF\x25" + struct.pack("<I", 0x00402008)
    struct.pack_into("<I", image, 0x2000, 0x70000001)
    struct.pack_into("<I", image, 0x2004, 0x70000005)
    struct.pack_into("<I", image, 0x2008, 0x70000068)

    assert emu.find_iat_slots(image, 0x00400000) == [
        (0x2000, "GetProcAddress", "kernel32.dll"),
        (0x2008, "GetStartupInfoA", "kernel32.dll"),
    ]


def dump_image_fixture(with_reference: bool) -> tuple[SafeDiscEmulator, bytearray]:
    """Build the PE-header state needed by write_unpacked_pe without a fixture."""
    image = bytearray(0x3000)
    nt = 0x80
    optional = nt + 0x18
    section_table = optional + 0xE0
    image[:2] = b"MZ"
    struct.pack_into("<I", image, 0x3C, nt)
    image[nt:nt + 4] = b"PE\0\0"
    struct.pack_into("<H", image, nt + 6, 2)
    struct.pack_into("<H", image, nt + 20, 0xE0)
    struct.pack_into("<I", image, optional + 0x10, 0x1000)
    struct.pack_into("<I", image, optional + 0x24, 0x200)
    struct.pack_into("<I", image, optional + 0x38, len(image))
    struct.pack_into("<II", image, optional + 0x68, 0x2100, 0x28)
    struct.pack_into("<II", image, optional + 0xC0, 0x2200, 0x10)

    def section(index: int, name: bytes, rva: int, characteristics: int) -> None:
        header = section_table + index * 40
        image[header:header + 8] = name.ljust(8, b"\0")
        struct.pack_into(
            "<IIIIIIHHI", image, header + 8,
            0x1000, rva, 0x1000, rva, 0, 0, 0, 0, characteristics,
        )

    section(0, b".text", 0x1000, 0x60000020)
    section(1, b".rdata", 0x2000, 0x40000040)
    if with_reference:
        image[0x1000:0x1006] = b"\xFF\x15" + struct.pack("<I", 0x00402000)
    struct.pack_into("<I", image, 0x2000, 0x70000001)

    emu = SafeDiscEmulator.__new__(SafeDiscEmulator)
    emu.image_base = 0x00400000
    emu.entry = 0x00401000
    emu.oep_found = None
    emu.uc = SimpleNamespace(mem_read=lambda _address, size: bytes(image[:size]))
    emu.pe = SimpleNamespace(
        FILE_HEADER=SimpleNamespace(SizeOfOptionalHeader=0xE0),
        OPTIONAL_HEADER=SimpleNamespace(
            SizeOfImage=len(image), SectionAlignment=0x1000, SizeOfHeaders=0x400,
        ),
        sections=[
            SimpleNamespace(
                VirtualAddress=0x1000, SizeOfRawData=0x1000,
                Characteristics=0x60000020,
            ),
            SimpleNamespace(
                VirtualAddress=0x2000, SizeOfRawData=0x1000,
                Characteristics=0x40000040,
            ),
        ],
    )
    emu.stubs = {0x70000001: ("GetProcAddress", 2)}
    emu.stub_dll = {"GetProcAddress": "kernel32.dll"}
    return emu, image


def test_dump_preserves_import_directories_when_no_slots_are_verified() -> None:
    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        emu, original = dump_image_fixture(with_reference=False)
        preserved_path = root / "preserved.exe"
        info = emu.write_unpacked_pe(preserved_path)
        preserved = preserved_path.read_bytes()
        optional = 0x80 + 0x18

        assert not info["imports_rebuilt"]
        assert info["iat_slots"] == 0
        assert len(preserved) == len(original)
        assert preserved[optional + 0x68:optional + 0x70] == original[
            optional + 0x68:optional + 0x70
        ]
        assert preserved[optional + 0xC0:optional + 0xC8] == original[
            optional + 0xC0:optional + 0xC8
        ]
        assert struct.unpack_from("<H", preserved, 0x80 + 6)[0] == 2
        assert b".idata\0\0" not in preserved[:0x400]

        emu, _original = dump_image_fixture(with_reference=True)
        rebuilt_path = root / "rebuilt.exe"
        info = emu.write_unpacked_pe(rebuilt_path)
        rebuilt = rebuilt_path.read_bytes()
        import_rva, import_size = struct.unpack_from("<II", rebuilt, optional + 0x68)

        assert info["imports_rebuilt"]
        assert info["iat_slots"] == 1
        assert len(rebuilt) == 0x4000
        assert import_rva == 0x3000 and import_size > 20
        assert rebuilt[optional + 0xC0:optional + 0xC8] == bytes(8)
        assert struct.unpack_from("<H", rebuilt, 0x80 + 6)[0] == 3
        assert rebuilt[optional + 0xE0 + 80:optional + 0xE0 + 88] == b".idata\0\0"


def test_mode_select_uses_scsi_rejection() -> None:
    emu = bare_emulator()
    request = bytearray(92)
    struct.pack_into("<H", request, 0x00, 0x2C)  # SCSI_PASS_THROUGH.Length
    request[0x06] = 6                            # CdbLength
    request[0x07] = 0x18                         # SenseInfoLength
    struct.pack_into("<I", request, 0x0C, 12)   # DataTransferLength
    struct.pack_into("<I", request, 0x14, 0x50) # DataBufferOffset
    struct.pack_into("<I", request, 0x18, 0x30) # SenseInfoOffset
    request[0x1C:0x22] = bytes.fromhex("15 10 00 00 0c 00")
    parameter_list = bytes.fromhex("00 00 00 08 00 00 00 00 00 00 09 24")
    request[0x50:0x5C] = parameter_list
    emu.uc.mem_write(MEMORY, bytes(request))

    handled = emu.answer_scsi(bytes(request), MEMORY, len(request))
    response = bytes(emu.uc.mem_read(MEMORY, len(request)))
    sense = response[0x30:0x42]

    assert handled, "the pass-through transport itself must succeed"
    assert response[0x02] == SCSI_STATUS_CHECK_CONDITION
    assert response[0x07] == 18
    assert struct.unpack_from("<I", response, 0x0C)[0] == 0
    assert sense[0] == 0x70 and sense[2] == 0x05
    assert sense[12:14] == bytes([0x26, 0x00])
    assert response[0x50:0x5C] == parameter_list, "MODE SELECT must not mutate a block-size state"


def test_scsi_direct_short_transfer() -> None:
    emu = bare_emulator()
    request = bytearray(92)
    data_ptr = MEMORY + 0x300
    struct.pack_into("<H", request, 0x00, 0x2C)
    request[0x06] = 6
    request[0x07] = 0x18
    struct.pack_into("<I", request, 0x0C, 96)
    struct.pack_into("<I", request, 0x14, data_ptr)
    struct.pack_into("<I", request, 0x18, 0x30)
    request[0x1C:0x22] = bytes.fromhex("12 00 00 00 60 00")
    emu.uc.mem_write(MEMORY, bytes(request))

    assert emu.answer_scsi(bytes(request), MEMORY, len(request), direct=True)
    response = bytes(emu.uc.mem_read(MEMORY, len(request)))
    inquiry = bytes(emu.uc.mem_read(data_ptr, 36))
    assert response[0x02] == 0
    assert response[0x07] == 0
    assert struct.unpack_from("<I", response, 0x0C)[0] == 36
    assert inquiry[8:16] == b"SafeDisc"

    mode_request = bytearray(0x80)
    struct.pack_into("<H", mode_request, 0x00, 0x2C)
    mode_request[0x06] = 10
    mode_request[0x07] = 0x18
    struct.pack_into("<I", mode_request, 0x0C, 36)
    struct.pack_into("<I", mode_request, 0x14, 0x50)
    struct.pack_into("<I", mode_request, 0x18, 0x30)
    mode_request[0x1C:0x26] = bytes.fromhex("5a 00 2a 00 00 00 00 00 24 00")
    emu.uc.mem_write(MEMORY, bytes(mode_request))
    assert emu.answer_scsi(bytes(mode_request), MEMORY, len(mode_request))
    mode_response = bytes(emu.uc.mem_read(MEMORY, len(mode_request)))
    assert mode_response[0x50:0x52] == bytes.fromhex("00 22")
    assert mode_response[0x58:0x5A] == bytes.fromhex("2a 1a")


def test_thug2_retail_descriptor_profile() -> None:
    sectors = thug2_retail_descriptor_sectors()
    assert sorted(sectors) == [16, 17, 18, 19]
    assert all(len(sector) == 2048 for sector in sectors.values())
    assert hashlib.sha256(sectors[16]).hexdigest() == (
        "3b2270cee977516efe547147149975f8f1ddb08aa60524531acab9be1c9477c8"
    )
    assert sectors[16][1:6] == b"CD001"
    assert struct.unpack_from("<I", sectors[16], 0x9E)[0] == 24
    assert struct.unpack_from("<I", sectors[18], 0x9E)[0] == 58
    assert sectors[16][0x4B3:0x4BB] != b" " * 8

    lo, hi, *_ = THUG2_RETAIL_BAD_SECTOR_PROFILE
    bad_lbas = [lba for lba in range(lo, hi + 1)
                if is_bad_disc_sector(lba, THUG2_RETAIL_BAD_SECTOR_PROFILE)]
    assert len(bad_lbas) == 584
    assert (bad_lbas[0], bad_lbas[-1]) == (972, 10311)


def test_thug2_root_aliases() -> None:
    class FakeDisc:
        def __init__(self) -> None:
            self.sectors = {source: self.root(source)
                            for source in THUG2_RETAIL_ROOT_ALIASES.values()}

        @staticmethod
        def root(lba: int) -> bytes:
            sector = bytearray(2048)
            for offset, name in ((0, 0), (34, 1)):
                sector[offset] = 34
                struct.pack_into("<I", sector, offset + 2, lba)
                struct.pack_into(">I", sector, offset + 6, lba)
                sector[offset + 32] = 1
                sector[offset + 33] = name
            return bytes(sector)

        def read_sector(self, lba: int) -> bytes:
            return self.sectors.get(lba, bytes(2048))

    emu = bare_emulator()
    emu.disc = FakeDisc()
    emu.disc_sector_overlays = {}
    emu.disc_sector_aliases = dict(THUG2_RETAIL_ROOT_ALIASES)
    emu.disc_root_extent_override = None
    emu.disc_original_root_extent = None
    emu.disc_application_use_marker = None
    for virtual in THUG2_RETAIL_ROOT_ALIASES:
        sector = emu.read_disc_sector(virtual)
        assert struct.unpack_from("<I", sector, 2)[0] == virtual
        assert struct.unpack_from("<I", sector, 36)[0] == virtual


def main() -> int:
    test_system_time_as_filetime()
    print("  [ok] Win32 time APIs share one monotonic UTC FILETIME clock")
    test_iat_scan_requires_executable_reference()
    print("  [ok] IAT scan requires an executable FF15/FF25 slot reference")
    test_dump_preserves_import_directories_when_no_slots_are_verified()
    print("  [ok] empty IAT scans preserve directories; verified slots still rebuild")
    test_mode_select_uses_scsi_rejection()
    print("  [ok] MODE SELECT returns transport-success CHECK CONDITION 05/26/00")
    test_scsi_direct_short_transfer()
    print("  [ok] direct SCSI data pointers and short-transfer lengths are honored")
    test_thug2_retail_descriptor_profile()
    print("  [ok] embedded THUG2 retail descriptor/error profile matches the master")
    test_thug2_root_aliases()
    print("  [ok] ISO and Joliet root aliases preserve coherent dot records")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
