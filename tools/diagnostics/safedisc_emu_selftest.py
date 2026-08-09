#!/usr/bin/env python3
"""Focused, fixture-free probes for SafeDisc clock, SCSI, and disc metadata."""

import hashlib
import json
import struct
import tempfile
from collections import Counter
from pathlib import Path
from types import SimpleNamespace

from unicorn import Uc, UC_ARCH_X86, UC_MODE_32
from unicorn.x86_const import (
    UC_X86_REG_EAX,
    UC_X86_REG_EBP,
    UC_X86_REG_EIP,
    UC_X86_REG_ESP,
)

from safedisc_emu import (
    FILETIME_BASE_2020,
    INSTRUCTIONS_PER_MS,
    SCSI_STATUS_CHECK_CONDITION,
    THUG2_AUTHSERV_CD_CHECK_KEY_HOOK_RVA,
    THUG2_AUTHSERV_CD_CHECK_KEY_RETURN_RVA,
    THUG2_AUTHSERV_RAW_KEYS_RVA,
    THUG2_RETAIL_BAD_SECTOR_PROFILE,
    THUG2_RETAIL_ROOT_ALIASES,
    THUG2_SD3_CD_CHECK_RETURN_VALUE,
    THUG2_SD3_IMPORTANT_KEY_COPY_SIZE,
    THUG2_SD3_TABLE2_OFFSET,
    THUG2_SD3_TABLE3_OFFSET,
    THUG2_SECSERV_FIRST_COPY_SLOT,
    THUG2_SECSERV_CJUMP_DISPATCHER_RVA,
    THUG2_SECSERV_KEY_MANAGER_RVA,
    THUG2_SECSERV_SECOND_COPY_SLOT,
    THUG2_SECSERV_THIRD_COPY_SLOT,
    SafeDiscEmulator,
    derive_thug2_sd3_important_key,
    is_bad_disc_sector,
    parse_hex_range,
    ranges_overlap,
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


def test_stop_write_range_parsing_and_overlap() -> None:
    assert parse_hex_range("00401000-00405E20") == (0x00401000, 0x00405E20)
    assert ranges_overlap(0x00400FFC, 8, 0x00401000, 0x00405E20)
    assert ranges_overlap(0x00405E1F, 4, 0x00401000, 0x00405E20)
    assert not ranges_overlap(0x00400FFC, 4, 0x00401000, 0x00405E20)
    assert not ranges_overlap(0x00405E20, 4, 0x00401000, 0x00405E20)
    assert not ranges_overlap(0x00401000, 0, 0x00401000, 0x00405E20)

    emu = SafeDiscEmulator.__new__(SafeDiscEmulator)
    emu.stop_write_ranges = [(0x00401000, 0x00401001)]
    emu.stop_write_hit = False
    emu.stop_write_match_count = 0
    emu.stop_write_match_target = 2
    assert not emu.matches_stop_write(0x00401000, 4), "first copy must be skipped"
    assert emu.stop_write_match_count == 1
    assert emu.matches_stop_write(0x00401000, 4), "second copy must be selected"
    assert emu.stop_write_match_count == 2


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


def test_thug2_sd3_important_key_derivation() -> None:
    size = THUG2_AUTHSERV_RAW_KEYS_RVA + THUG2_SD3_TABLE3_OFFSET + 1024
    auth = bytearray(size)
    first = 0x11223344
    second = 0xAABBCCDD
    struct.pack_into("<I", auth, THUG2_AUTHSERV_RAW_KEYS_RVA, first)
    struct.pack_into(
        "<I", auth, THUG2_AUTHSERV_RAW_KEYS_RVA + THUG2_SD3_TABLE2_OFFSET,
        second,
    )
    source_offset = THUG2_AUTHSERV_RAW_KEYS_RVA + THUG2_SD3_TABLE3_OFFSET
    source = bytes((index * 29 + 7) & 0xFF for index in range(1024))
    auth[source_offset:source_offset + len(source)] = source

    xor_key, derived = derive_thug2_sd3_important_key(bytes(auth))
    assert xor_key == first ^ second
    expected = bytearray(source)
    for offset in range(0, len(expected), 4):
        value = struct.unpack_from("<I", expected, offset)[0] ^ xor_key
        struct.pack_into("<I", expected, offset, value)
    assert derived == bytes(expected)


def test_thug2_sd3_published_cd_check_hook() -> None:
    """Raw pages publish at Auth return; FirstCopy publishes only in the hook."""
    emu = bare_emulator()
    auth_base = 0x00200000
    sec_base = 0x00400000
    storage_base = 0x00600000
    auth_size = 0x40000
    emu.uc.mem_map(auth_base, auth_size)
    emu.uc.mem_map(sec_base, 0x100000)
    emu.uc.mem_map(storage_base, 0x10000)

    auth = bytearray(auth_size)
    first_dword = 0x11223344
    second_dword = 0xAABBCCDD
    struct.pack_into("<I", auth, THUG2_AUTHSERV_RAW_KEYS_RVA, first_dword)
    struct.pack_into(
        "<I", auth, THUG2_AUTHSERV_RAW_KEYS_RVA + THUG2_SD3_TABLE2_OFFSET,
        second_dword,
    )
    source_offset = THUG2_AUTHSERV_RAW_KEYS_RVA + THUG2_SD3_TABLE3_OFFSET
    raw_storage = bytes((index * 17 + 3) & 0xFF for index in range(1024))
    auth[source_offset:source_offset + len(raw_storage)] = raw_storage
    emu.uc.mem_write(auth_base, bytes(auth))

    manager = storage_base
    slots = storage_base + 0x100
    emu.uc.mem_write(
        sec_base + THUG2_SECSERV_KEY_MANAGER_RVA, struct.pack("<I", manager)
    )
    emu.uc.mem_write(manager, struct.pack("<4I", 0, 6, 0, slots))
    pointers = []
    for index in range(6):
        pointer = storage_base + 0x1000 + index * 0x500
        pointers.append(pointer)
        emu.uc.mem_write(
            slots + index * 0x10,
            struct.pack("<4I", 0, pointer, 1024, 0x10),
        )

    emu.loaded_modules = {
        "~de36b4.tmp": SimpleNamespace(base=auth_base, size=auth_size),
        "~df394b.tmp": SimpleNamespace(base=sec_base, size=0x100000),
    }
    emu.thug2_sd3_raw_storage_installed = False
    emu.thug2_sd3_raw_storage_install_count = 0
    emu.thug2_sd3_raw_storage = None
    emu.thug2_sd3_derived_storage = None
    emu.thug2_sd3_storage_slots = None
    emu.thug2_sd3_key_installed = False
    emu.thug2_sd3_key_install_count = 0
    emu.thug2_sd3_key_xor = 0
    emu.thug2_sd3_key_hook_address = (
        auth_base + THUG2_AUTHSERV_CD_CHECK_KEY_HOOK_RVA
    )
    emu.thug2_sd3_key_return_address = (
        auth_base + THUG2_AUTHSERV_CD_CHECK_KEY_RETURN_RVA
    )
    emu.stop_locked = False
    emu.stop_reason = "not started"

    # THUG2 takes SafeDiscLoader2's direct v40 TablePtr branch.  The alternative
    # HookDecodeTable signature is absent, so installing raw pages must not
    # schedule a guest DecryptFunc call or disturb its suspended continuation.
    emu.pending_returns = []
    emu.uc.reg_write(UC_X86_REG_ESP, MEMORY + 0x800)
    emu.uc.reg_write(UC_X86_REG_EIP, 0x12345678)
    assert emu.install_thug2_sd3_raw_storage(emu.uc)
    needed = THUG2_SD3_IMPORTANT_KEY_COPY_SIZE
    assert needed == 1024
    assert emu.pending_returns == []
    assert emu.uc.reg_read(UC_X86_REG_ESP) == MEMORY + 0x800
    assert emu.uc.reg_read(UC_X86_REG_EIP) == 0x12345678
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_SECOND_COPY_SLOT], needed
    )) == raw_storage[:needed]
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_THIRD_COPY_SLOT], needed
    )) == raw_storage[:needed]
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_FIRST_COPY_SLOT], needed
    )) == bytes(needed), "FirstCopy storage must remain delayed"
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_SECOND_COPY_SLOT] + 1014, 10
    )) == raw_storage[1014:], "v40 must copy the final ten storage bytes"

    _xor_key, derived_storage = derive_thug2_sd3_important_key(bytes(auth))
    emu.uc.reg_write(UC_X86_REG_EAX, 0xDEADBEEF)
    emu.uc.reg_write(UC_X86_REG_EIP, emu.thug2_sd3_key_hook_address)
    assert emu.run_thug2_sd3_cd_check_hook(emu.uc)
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_FIRST_COPY_SLOT], needed
    )) == derived_storage[:needed]
    assert bytes(emu.uc.mem_read(
        pointers[THUG2_SECSERV_FIRST_COPY_SLOT] + 1014, 10
    )) == derived_storage[1014:], "HookCDCheck must copy the full derived page"
    assert emu.uc.reg_read(UC_X86_REG_EAX) == THUG2_SD3_CD_CHECK_RETURN_VALUE
    assert emu.uc.reg_read(UC_X86_REG_EIP) == emu.thug2_sd3_key_return_address
    assert emu.thug2_sd3_key_install_count == 1

    # Conflicting raw storage must stop instead of silently deriving from a
    # state the published hook never accepts.
    emu.uc.mem_write(pointers[THUG2_SECSERV_THIRD_COPY_SLOT], b"\0")
    emu.stop_locked = False
    emu.stop_reason = "not started"
    assert not emu.run_thug2_sd3_cd_check_hook(emu.uc)
    assert "raw ThirdCopy storage changed" in emu.stop_reason


def test_thug2_import_vector_capture() -> None:
    emu = bare_emulator()
    emu.stop_locked = False
    emu.stop_reason = "not started"
    emu.thug2_import_vector_return_address = 0x12345678
    emu.thug2_import_vector_pending = []
    emu.thug2_import_vector_captures = []
    emu.heap_sizes = {}
    emu.loaded_modules = {}
    stack = MEMORY
    data_ptr = MEMORY + 0x100
    key_ptr = MEMORY + 0x200
    encoded = struct.pack("<3I", 1, 2, 3)
    key = bytes.fromhex("0192892b9117f0c3718e9fcebf140a37")
    emu.uc.mem_write(data_ptr, encoded)
    emu.uc.mem_write(key_ptr, key)
    emu.uc.mem_write(
        stack,
        struct.pack("<4I", emu.thug2_import_vector_return_address,
                    data_ptr, 3, key_ptr),
    )
    emu.uc.reg_write(UC_X86_REG_ESP, stack)
    assert emu.capture_thug2_import_vector_entry(emu.uc)
    emu.uc.mem_write(data_ptr, struct.pack("<3I", 4, 5, 6))
    assert emu.capture_thug2_import_vector_return(
        emu.uc, emu.thug2_import_vector_return_address
    )
    assert not emu.thug2_import_vector_pending
    assert len(emu.thug2_import_vector_captures) == 1
    capture = emu.thug2_import_vector_captures[0]
    assert capture["count"] == 3
    assert capture["key_hex"] == key.hex()
    assert capture["input_sha256"] == hashlib.sha256(encoded).hexdigest()
    assert capture["output_sha256"] == hashlib.sha256(
        struct.pack("<3I", 4, 5, 6)
    ).hexdigest()

    # CJumpRun's dynamic caller returns through an allocated ten-byte heap
    # thunk.  It is accepted only when it pushes itself and jumps exactly to
    # this SecServ build's known dispatcher, and capture happens before that
    # thunk executes.
    heap = 0x01000000
    thunk = heap + 0x100
    sec_base = 0x10000000
    dispatcher = sec_base + THUG2_SECSERV_CJUMP_DISPATCHER_RVA
    emu.uc.mem_map(heap, 0x1000)
    emu.heap_sizes = {heap: 0x1000}
    emu.loaded_modules = {
        "~df394b.tmp": SimpleNamespace(base=sec_base, size=0x200000)
    }

    def write_thunk(target: int, pushed: int = thunk) -> None:
        displacement = (target - (thunk + 10)) & 0xFFFFFFFF
        emu.uc.mem_write(
            thunk, b"\x68" + struct.pack("<I", pushed)
            + b"\xE9" + struct.pack("<I", displacement)
        )

    write_thunk(dispatcher)
    dynamic_input = struct.pack("<3I", 7, 8, 9)
    emu.uc.mem_write(data_ptr, dynamic_input)
    emu.uc.mem_write(
        stack, struct.pack("<4I", thunk, data_ptr, 3, key_ptr)
    )
    assert emu.capture_thug2_import_vector_entry(emu.uc)
    assert emu.thug2_import_vector_pending[-1]["return_address"] == thunk
    assert emu.thug2_import_vector_pending[-1]["return_kind"] == "cjump-thunk"
    dynamic_output = struct.pack("<3I", 10, 11, 12)
    emu.uc.mem_write(data_ptr, dynamic_output)
    assert emu.capture_thug2_import_vector_return(emu.uc, thunk)
    dynamic_capture = emu.thug2_import_vector_captures[-1]
    assert dynamic_capture["input_sha256"] == hashlib.sha256(dynamic_input).hexdigest()
    assert dynamic_capture["output_sha256"] == hashlib.sha256(dynamic_output).hexdigest()

    # Changing either half of the attested thunk must fail closed.  This case
    # preserves the jump but tampers with its self-pushed address.
    write_thunk(dispatcher, pushed=thunk + 1)
    emu.uc.mem_write(
        stack, struct.pack("<4I", thunk, data_ptr, 3, key_ptr)
    )
    emu.stop_locked = False
    emu.stop_reason = "not started"
    assert not emu.capture_thug2_import_vector_entry(emu.uc)
    assert "not itself" in emu.stop_reason
    assert not emu.thug2_import_vector_pending


def test_thug2_alt_record_capture() -> None:
    emu = bare_emulator()
    emu.stop_locked = False
    emu.stop_reason = "not started"
    emu.thug2_alt_record_captures = []
    emu.image_base = 0x00400000
    emu.image_lo = emu.image_base
    emu.image_hi = emu.image_base + 0x400000

    ebp = MEMORY + 0x800
    # Exact values captured through the authentic THUG2 setup/callback path
    # for the lazy Alt record at VA 00445DFD.
    site_rva = 0x45DFD
    site_va = emu.image_base + site_rva
    preimage = bytes.fromhex("fd5d0400ae34eaef")
    digest = hashlib.md5(preimage).digest()
    assert digest.hex() == "b9779b4ba9a87541d4cf82662a9a5c4e"
    mask = digest[4:8]
    decoded = bytes.fromhex("0200007f3a8fac71eedeac00") + digest[12:16]
    value = bytes(
        byte ^ mask[index & 3] for index, byte in enumerate(decoded)
    )
    emu.uc.mem_write(ebp - 0x1C, struct.pack("<I", 0))
    emu.uc.mem_write(ebp - 0x18, struct.pack("<I", site_va))
    emu.uc.mem_write(ebp - 0x58, struct.pack("<I", site_rva))
    emu.uc.mem_write(ebp - 0x9C, preimage)
    emu.uc.mem_write(ebp - 0x20C, digest)
    emu.uc.mem_write(ebp - 0x1FC, value)
    emu.uc.mem_write(ebp + 0x10, mask)
    emu.uc.mem_write(ebp - 0xE4, decoded)
    emu.uc.reg_write(UC_X86_REG_EBP, ebp)

    assert emu.capture_thug2_alt_record(emu.uc)
    assert len(emu.thug2_alt_record_captures) == 1
    capture = emu.thug2_alt_record_captures[0]
    assert capture["site_va"] == site_va
    assert capture["site_rva"] == site_rva
    assert capture["preimage_hex"] == preimage.hex()
    assert capture["digest_hex"] == digest.hex()
    assert capture["row_key"] == int.from_bytes(digest[:4], "big")
    assert capture["mask_hex"] == mask.hex()
    assert capture["value_hex"] == value.hex()
    assert capture["decoded_hex"] == decoded.hex()
    assert capture["control"] == 2
    assert capture["payload_hex"] == decoded[3:11].hex()

    # A changed digest cannot silently bless a checkpoint record.
    emu.uc.mem_write(ebp - 0x20C, bytes([digest[0] ^ 1]) + digest[1:])
    emu.stop_locked = False
    emu.stop_reason = "not started"
    assert not emu.capture_thug2_alt_record(emu.uc)
    assert "MD5 mismatch" in emu.stop_reason
    assert len(emu.thug2_alt_record_captures) == 1

    # Lookup misses are not records.  The exact-count finalizer gate makes a
    # missing successful row fatal without aborting CAltAsc's normal miss path.
    emu.uc.mem_write(ebp - 0x1C, struct.pack("<I", 1))
    emu.stop_locked = False
    emu.stop_reason = "not started"
    assert emu.capture_thug2_alt_record(emu.uc)
    assert len(emu.thug2_alt_record_captures) == 1


def test_thug2_pfd_alt_materializer() -> None:
    """PFD rows authenticate code windows and apply only as an atomic plan."""
    from thug2_safedisc_decrypt import (
        AltFragment,
        CORE_SECTIONS,
        DecryptError,
        PFD_ALT_CONTEXT_MULTIPLIER,
        PFD_ALT_WINDOW_XOR,
        apply_pfd_alt_repair_plan,
        decode_pfd_alt_record,
        native_overlap_tea_decrypt,
        tea_encrypt_pair,
    )

    site = 0x12345
    context = (site * PFD_ALT_CONTEXT_MULTIPLIER) & 0xFFFFFFFF
    digest = hashlib.md5(struct.pack("<II", site, context)).digest()
    window = bytes.fromhex("558bec83ec105356")
    decoded = (
        bytes((3, 0, 0))
        + bytes(value ^ PFD_ALT_WINDOW_XOR for value in window)
        + b"\0"
        + digest[12:16]
    )
    encoded = bytes(
        value ^ digest[4 + (index & 3)]
        for index, value in enumerate(decoded)
    )
    fragment = decode_pfd_alt_record(site, 17, encoded)
    assert fragment == AltFragment(site, 17, 3, window)
    assert decode_pfd_alt_record(
        site, 17, encoded[:-1] + bytes((encoded[-1] ^ 1,))
    ) is None

    # Exercise the non-block-aligned PFD tail.  Encryption applies the inverse
    # operations in reverse traversal order because the last two blocks overlap.
    key = b"78eabdg4232ewz1\0"
    plaintext = bytes((index * 37 + 11) & 0xFF for index in range(212))
    offsets: list[int] = []
    position = 0
    remaining = len(plaintext)
    while remaining >= 16:
        offsets.append(position)
        position += 8
        remaining -= 8
    if remaining == 8:
        offsets.append(position)
    elif remaining > 8:
        offsets.extend((position + remaining - 8, position))
    encrypted = bytearray(plaintext)
    key_words = struct.unpack("<4I", key)
    for offset in reversed(offsets):
        left, right = struct.unpack_from("<2I", encrypted, offset)
        left, right = tea_encrypt_pair(left, right, key_words)
        struct.pack_into("<2I", encrypted, offset, left, right)
    assert native_overlap_tea_decrypt(bytes(encrypted), key) == plaintext

    text_end = (
        CORE_SECTIONS[0].virtual_address + CORE_SECTIONS[0].virtual_size
    )
    image = bytearray(text_end)
    first = AltFragment(0x2000, 1, 2, b"\x85\xC0abcdef")
    second = AltFragment(0x2010, 2, 3, b"\x75\x05\x90abcde")
    image[first.site_rva:first.site_rva + first.control] = b"\xCC" * first.control
    image[second.site_rva:second.site_rva + second.control] = b"\xCC\x90\xCC"
    before = bytes(image)
    try:
        apply_pfd_alt_repair_plan(image, (first, second))
    except DecryptError as exc:
        assert "target changed" in str(exc)
    else:
        raise AssertionError("tampered PFD Alt target was accepted")
    assert bytes(image) == before, "a rejected plan partially mutated the image"

    image[second.site_rva:second.site_rva + second.control] = (
        b"\xCC" * second.control
    )
    apply_pfd_alt_repair_plan(image, (first, second))
    assert image[first.site_rva:first.site_rva + first.control] == b"\x85\xC0"
    assert image[second.site_rva:second.site_rva + second.control] == b"\x75\x05\x90"


def test_thug2_special_redirect_trailing_byte() -> None:
    """The sole flagged redirect appends a sixth zero byte atomically."""
    from thug2_safedisc_decrypt import (
        CORE_SECTIONS,
        DecryptError,
        EMULATED_HEAP_BASE,
        RuntimeRepairSources,
        SAFE_DISC_REDIRECT_RVA,
        SECSERV_REDIRECT_COUNT,
        SECSERV_REDIRECT_KEY_OFFSET,
        SECSERV_REDIRECT_KEY_XOR,
        SECSERV_REDIRECT_LENGTH_OFFSET,
        SECSERV_REDIRECT_PAYLOAD_OFFSET,
        SECSERV_REDIRECT_RECORDS_OFFSET,
        SECSERV_REDIRECT_RECORD_SIZE,
        SECSERV_REDIRECT_SERIALIZED_SIZE,
        SECSERV_REDIRECT_SLOT_COUNT,
        SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET,
        SECSERV_REDIRECT_SPECIAL_KEY,
        SECSERV_REDIRECT_SPECIAL_SITE_RVA,
        SECSERV_REDIRECT_SPECIAL_SLOT,
        SECSERV_REDIRECT_TABLE_POINTER_RVA,
        redirect_site_key,
        restore_redirect_dictionary,
        secserv_redirect_dictionary,
    )

    heap_size = (
        SECSERV_REDIRECT_RECORDS_OFFSET
        + SECSERV_REDIRECT_SLOT_COUNT * SECSERV_REDIRECT_RECORD_SIZE
    )
    heap = bytearray(heap_size)
    struct.pack_into(
        "<II", heap, 0, SECSERV_REDIRECT_COUNT,
        SECSERV_REDIRECT_SERIALIZED_SIZE,
    )
    for slot in range(SECSERV_REDIRECT_COUNT):
        struct.pack_into("<H", heap, 8 + 2 * slot, 1)

    text = CORE_SECTIONS[0]
    image_size = text.virtual_address + text.raw_size
    image = bytearray(image_size)
    sites: list[int] = []
    for slot in range(SECSERV_REDIRECT_COUNT):
        site = (
            SECSERV_REDIRECT_SPECIAL_SITE_RVA
            if slot == SECSERV_REDIRECT_SPECIAL_SLOT
            else 0x10000 + slot * 0x20
        )
        sites.append(site)
        key = redirect_site_key(site)
        if slot == SECSERV_REDIRECT_SPECIAL_SLOT:
            assert key == SECSERV_REDIRECT_SPECIAL_KEY
        record = (
            SECSERV_REDIRECT_RECORDS_OFFSET
            + slot * SECSERV_REDIRECT_RECORD_SIZE
        )
        struct.pack_into(
            "<I", heap, record + SECSERV_REDIRECT_KEY_OFFSET,
            key ^ SECSERV_REDIRECT_KEY_XOR,
        )
        struct.pack_into(
            "<I", heap, record + SECSERV_REDIRECT_LENGTH_OFFSET, 4
        )
        payload = (
            bytes.fromhex("81c6b81e00")
            if slot == SECSERV_REDIRECT_SPECIAL_SLOT
            else bytes((slot, 0x90, 0x90, 0x90, 0x90))
        )
        heap[
            record + SECSERV_REDIRECT_PAYLOAD_OFFSET:
            record + SECSERV_REDIRECT_PAYLOAD_OFFSET + 5
        ] = payload
        if slot == SECSERV_REDIRECT_SPECIAL_SLOT:
            heap[
                record + SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET:
                record + SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET + 2
            ] = b"\x01\x01"
        image[site:site + 5] = b"\xE8" + struct.pack(
            "<i", SAFE_DISC_REDIRECT_RVA - (site + 5)
        )
    image[SECSERV_REDIRECT_SPECIAL_SITE_RVA + 5] = 0xCC

    secserv = bytearray(SECSERV_REDIRECT_TABLE_POINTER_RVA + 4)
    struct.pack_into(
        "<I", secserv, SECSERV_REDIRECT_TABLE_POINTER_RVA,
        EMULATED_HEAP_BASE,
    )
    sources = RuntimeRepairSources(
        EMULATED_HEAP_BASE, bytes(heap), bytes(secserv), b""
    )

    tampered = bytearray(image)
    tampered[SECSERV_REDIRECT_SPECIAL_SITE_RVA + 5] = 0x90
    before = bytes(tampered)
    try:
        restore_redirect_dictionary(tampered, sources)
    except DecryptError as exc:
        assert "trailing byte" in str(exc)
    else:
        raise AssertionError("tampered special redirect tail was accepted")
    assert bytes(tampered) == before, "redirect rejection was not atomic"

    assert restore_redirect_dictionary(image, sources) == SECSERV_REDIRECT_COUNT
    assert image[
        SECSERV_REDIRECT_SPECIAL_SITE_RVA:
        SECSERV_REDIRECT_SPECIAL_SITE_RVA + 6
    ] == bytes.fromhex("81c6b81e0000")

    bad_heap = bytearray(heap)
    special_record = (
        SECSERV_REDIRECT_RECORDS_OFFSET
        + SECSERV_REDIRECT_SPECIAL_SLOT * SECSERV_REDIRECT_RECORD_SIZE
    )
    bad_heap[special_record + SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET + 1] = 0
    bad_sources = RuntimeRepairSources(
        EMULATED_HEAP_BASE, bytes(bad_heap), bytes(secserv), b""
    )
    try:
        secserv_redirect_dictionary(bad_sources)
    except DecryptError as exc:
        assert "invalid special flags" in str(exc)
    else:
        raise AssertionError("tampered special redirect flags were accepted")


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


def test_thug2_checkpoint_requires_game_oep() -> None:
    """Only an unconditional format-2 checkpoint bound to its image is valid."""
    from thug2_safedisc_decrypt import (
        DecryptError,
        IMAGE_BASE,
        OEP_ADDRESS,
        RUNTIME_IMAGE_SIZE,
        validate_checkpoint_manifest,
    )

    memory = bytes(RUNTIME_IMAGE_SIZE)
    manifest = {
        "format": 2,
        "conditional": False,
        "register_overrides": [],
        "main_runtime_sha256": hashlib.sha256(memory).hexdigest(),
        "image_base": IMAGE_BASE,
        "image_size": RUNTIME_IMAGE_SIZE,
        "registers": {"eax": 0x258},
        "stop_reason": "reached --stop-at 0x100160B9",
    }
    try:
        validate_checkpoint_manifest(
            json.dumps(manifest).encode("utf-8"), memory
        )
    except DecryptError as exc:
        assert "not captured at game OEP" in str(exc)
    else:
        raise AssertionError("pre-restoration 0x100160B9 checkpoint was accepted")

    manifest["stop_reason"] = f"reached --stop-at 0x{OEP_ADDRESS:08X}"
    legacy = dict(manifest, format=1)
    try:
        validate_checkpoint_manifest(json.dumps(legacy).encode("utf-8"), memory)
    except DecryptError as exc:
        assert "format is not 2" in str(exc)
    else:
        raise AssertionError("legacy checkpoint without override attestation passed")

    missing_metadata = dict(manifest)
    del missing_metadata["conditional"]
    try:
        validate_checkpoint_manifest(
            json.dumps(missing_metadata).encode("utf-8"), memory
        )
    except DecryptError as exc:
        assert "conditional-run metadata" in str(exc)
    else:
        raise AssertionError("checkpoint without conditional metadata passed")

    emu = SafeDiscEmulator.__new__(SafeDiscEmulator)
    emu.register_overrides = {0x10323C88: {"eax": 0x01020050}}
    emu.override_hits = Counter()
    overrides = emu.checkpoint_register_overrides()
    assert overrides == [{
        "address": 0x10323C88,
        "registers": {"eax": 0x01020050},
        "hits": 0,
    }]
    conditional = dict(
        manifest, conditional=True, register_overrides=overrides
    )
    try:
        validate_checkpoint_manifest(
            json.dumps(conditional).encode("utf-8"), memory
        )
    except DecryptError as exc:
        assert "conditional" in str(exc)
    else:
        raise AssertionError("zero-hit conditional checkpoint was accepted")

    flipped_memory = bytearray(memory)
    flipped_memory[-1] = 1
    try:
        validate_checkpoint_manifest(
            json.dumps(manifest).encode("utf-8"), bytes(flipped_memory)
        )
    except DecryptError as exc:
        assert "main_runtime_sha256" in str(exc)
    else:
        raise AssertionError("checkpoint accepted a modified main.runtime.bin")

    for reason in (
        f"reached the original entry point at 0x{OEP_ADDRESS:08X}",
        f"reached --stop-at 0x{OEP_ADDRESS:08X}",
    ):
        manifest["stop_reason"] = reason
        accepted = validate_checkpoint_manifest(
            json.dumps(manifest).encode("utf-8"), memory
        )
        assert accepted["stop_reason"] == reason


def test_thug2_finalizer_requires_attested_inputs() -> None:
    """Explicit keys, corrupt output, and overwrite races all fail closed."""
    from thug2_safedisc_decrypt import (
        DecryptError,
        OUTPUT_SIZE,
        decrypt,
        require_manifest_import_key,
        validate_pipeline_paths,
        validate_output,
        write_new_output,
    )

    for diagnostic_key in (None, bytes(range(16))):
        try:
            require_manifest_import_key({}, b"", b"", diagnostic_key)
        except DecryptError as exc:
            assert "cannot substitute" in str(exc)
        else:
            raise AssertionError("missing import-vector attestations were accepted")

    with tempfile.TemporaryDirectory() as temporary:
        root = Path(temporary)
        protected = root / "protected.exe"
        memory = root / "main.runtime.bin"
        output = root / "output.exe"
        protected.write_bytes(b"")
        memory.write_bytes(b"")
        try:
            decrypt(
                protected, memory, output,
                import_key=bytes(range(16)),
            )
        except DecryptError as exc:
            assert "matching format-2 checkpoint" in str(exc)
        else:
            raise AssertionError("explicit-only key bypassed the checkpoint gate")
        assert not output.exists()

        write_new_output(output, b"first")
        try:
            write_new_output(output, b"second")
        except DecryptError as exc:
            assert "refusing to overwrite" in str(exc)
        else:
            raise AssertionError("exclusive output creation overwrote a race winner")
        assert output.read_bytes() == b"first"

        for work_dir in (output, output / "work"):
            try:
                validate_pipeline_paths(output, work_dir)
            except DecryptError as exc:
                assert "must not equal or be contained" in str(exc)
            else:
                raise AssertionError("work directory could consume output path")
        validate_pipeline_paths(root / "work" / "game.exe", root / "work")

    corrupt = bytearray(OUTPUT_SIZE)
    for offset in (None, OUTPUT_SIZE - 1):
        if offset is not None:
            corrupt[offset] ^= 1
        try:
            validate_output(bytes(corrupt))
        except DecryptError as exc:
            assert "SHA-256 mismatch" in str(exc)
        else:
            raise AssertionError("non-canonical standalone output hash was accepted")


def main() -> int:
    test_stop_write_range_parsing_and_overlap()
    print("  [ok] stop-on-write ranges use half-open overlap semantics")
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
    test_thug2_sd3_important_key_derivation()
    print("  [ok] THUG2 SD3 FirstCopy storage derives from AuthServ's raw tables")
    test_thug2_sd3_published_cd_check_hook()
    print("  [ok] published THUG2 SD3 HookCDCheck storage/timing semantics hold")
    test_thug2_import_vector_capture()
    print("  [ok] THUG2 import-vector key and before/after hashes are attested")
    test_thug2_alt_record_capture()
    print("  [ok] THUG2 Alt records bind MD5, PFD value, site, and payload")
    test_thug2_pfd_alt_materializer()
    print("  [ok] THUG2 PFD Alt codec and repair plan fail closed atomically")
    test_thug2_special_redirect_trailing_byte()
    print("  [ok] THUG2 flagged redirect appends its attested trailing zero")
    test_thug2_checkpoint_requires_game_oep()
    print("  [ok] THUG2 checkpoints bind image bytes and reject conditional runs")
    test_thug2_finalizer_requires_attested_inputs()
    print("  [ok] THUG2 finalization rejects unattested keys, corruption, and races")
    test_thug2_root_aliases()
    print("  [ok] ISO and Joliet root aliases preserve coherent dot records")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
