#!/usr/bin/env python3
"""Decrypt the exact SafeDisc-protected THUG2 executable into a standalone PE.

By default this is the one-command, fail-closed SafeDisc decryptor pipeline.
It takes the exact protected THUG2 executable plus the owner's CD1 BIN, runs
that executable's own loader under ``safedisc_emu.py``, retains a full runtime
checkpoint and log, and emits a standalone PE only after every completion gate
passes.  Advanced ``--memory`` mode finalizes an existing ``main.runtime.bin``
and matching checkpoint.  Neither mode requires the CD3 no-CD executable or
uses its bytes for reconstruction/output; ``--oracle`` may read it solely for
comparison.

The CD3 executable is not required in production.  It can be supplied through
the optional ``--oracle`` validation switch, which compares bytes after
neutralizing its 264 ``RLD!\\0`` padding tags and two structurally proven
unreachable alignment gaps.  Oracle bytes are never copied into the output.

Incomplete or pre-key-repair emulator outputs are intentionally rejected.
After an attested loader run, this tool normalizes the recovered IAT, removes
the two SafeDisc sections, restores the original five-section raw layout, and
writes a standalone PE.

The lazy code fragments are recovered from the protected run's own
``PfdRun.pfd``.  The decoder authenticates every selected row with the MD5
selector used by SecServ, requires the exact typed record population and
untouched ``CC`` targets, and applies the fragments atomically.  No plaintext
executable participates in that reconstruction.

SafeDisc protects the USER32, GDI32, and ADVAPI32 import-name vectors
separately.  A matching ``checkpoint.json`` records SecServ's live decoder
key and input/output hashes; this tool reproduces the whitening, TEA, and
keyed-scatter transform and verifies those hashes before accepting the key.
The key is not taken from the no-CD oracle.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import struct
import sys
from dataclasses import dataclass
from pathlib import Path

import pefile


PROTECTED_SHA256 = "c34ea46e041d08d7d85565a262473c29b90ed8a4d5b740d6cc04d4fe48d52347"
ORACLE_SHA256 = "52fc88849654b34839ec2f96bff3a8c0b7a855df9a207aab9f2fca2e6bd440f3"
OUTPUT_SHA256 = "f7ca9c1d0e4eed40808ce3dec6a9df854c0236c4916aa6d09cb1a3405d2676ae"
PROTECTED_SIZE = 3_926_726
IMAGE_BASE = 0x00400000
ORIGINAL_ENTRY_RVA = 0x22583D
IMPORT_TABLE_RVA = 0x27A564
IMPORT_TABLE_SIZE = 0xF0
MIN_MEMORY_SIZE = 0x3DF000
OUTPUT_SIZE = 0x292000
OUTPUT_IMAGE_SIZE = 0x3DF000
RUNTIME_IMAGE_SIZE = 0x3E6000
STXT_RVA_LO = 0x3DF000
STXT_RVA_HI = 0x3E6000
RESTORATION_TABLE_RVA = 0x3E1000
RESTORATION_RECORD_COUNT = 11
RESTORATION_PAYLOAD_END_RVA = 0x3E1063
SAFE_DISC_REDIRECT_RVA = 0x1D79
CRT_INITIALIZER_RVA = 0x27C000
CRT_INITIALIZER_END_RVA = 0x27C13C
OEP_ADDRESS = IMAGE_BASE + ORIGINAL_ENTRY_RVA
PIPELINE_MAX_INSTRUCTIONS = 400_000_000
IMPORT_CIPHER_R = 0xA0D91537
IMPORT_CIPHER_MULTIPLIER = 0x29CBFA71
IMPORT_CIPHER_FIRST_ADDEND = 0x0DFA374A
IMPORT_CIPHER_SECOND_SUBTRAHEND = 0x6E11D8D8
IMPORT_PERMUTATION_MULTIPLIER = 0x35E85A6D
IMPORT_PERMUTATION_ADDEND = 0x361962E9
IMPORT_NAME_FIRST_XOR = 0x1437A1E9
IMPORT_NAME_XOR_MULTIPLIER = 0x1CD58624
IMPORT_NAME_SECOND_BASE = 0x274B0EFA
IMPORT_NAME_NEXT_BASE = 0x292881ED
TEA_DELTA = 0x9E3779B9
TEA_DECRYPT_SUM = 0xC6EF3720
EMULATED_HEAP_BASE = 0x01000000
SECSERV_RUNTIME_SIZE = 0x16F000
SECSERV_ORIGINAL_IMAGE_BASE = 0x66700000
SECSERV_TIMESTAMP = 0x4007F112
SECSERV_REDIRECT_TABLE_POINTER_RVA = 0xAF484
SECSERV_REDIRECT_COUNT = 77
SECSERV_REDIRECT_SERIALIZED_SIZE = 0x125B
SECSERV_REDIRECT_SLOT_COUNT = 128
SECSERV_REDIRECT_RECORD_SIZE = 0xFC
SECSERV_REDIRECT_RECORDS_OFFSET = 0x108
SECSERV_REDIRECT_KEY_OFFSET = 0x28
SECSERV_REDIRECT_LENGTH_OFFSET = 0xC8
SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET = 0xCC
SECSERV_REDIRECT_PAYLOAD_OFFSET = 0xCE
SECSERV_REDIRECT_KEY_XOR = 0x2EF77DB9
SECSERV_REDIRECT_SPECIAL_SLOT = 18
SECSERV_REDIRECT_SPECIAL_KEY = 0x61065B12
SECSERV_REDIRECT_SPECIAL_SITE_RVA = 0x56AAB
SECSERV_REDIRECT_PATCH_BYTE_COUNT = 386
SECSERV_IMPORT_NAME_DECRYPT_SIGNATURE = bytes.fromhex(
    "558bec53568b750885f67476837d100074708b450c8bd8c1eb0375125056e8ae"
)
SECSERV_STXT_TABLE_RVA = 0xB0444
SECSERV_IMPORT_MANAGER_POINTER_RVA = 0x12CE98
SECSERV_IMPORT_MASK_POINTERS_RVA = 0x12CE94
SECSERV_IMPORT_SELECTOR_ENABLED_RVA = 0x12CEF4
SECSERV_IMPORT_SELECTOR_FIRST_RVA = 0x929BD
SECSERV_IMPORT_SELECTOR_FIRST_SHA256 = (
    "93467daa4ef1091df177a2237aa70f32e0a3a9a2eb4bd0763e0f5e4d35bd0013"
)
SECSERV_IMPORT_SELECTOR_SECOND_RVA = 0x928A8
SECSERV_IMPORT_SELECTOR_FUNCTION_SIZE = 0x115
SECSERV_IMPORT_SELECTOR_SECOND_SHA256 = (
    "9b08e0504971a2854f78af45e3052890e261dbba47f044c7300a70d0924c2c03"
)
SECSERV_MAIN_RANGE_COUNT_RVA = 0x12CEE0
SECSERV_MAIN_RANGE_TABLE_RVA = 0x160780
SECSERV_MAIN_RANGE_RECORD_SIZE = 12
SECSERV_EXPECTED_MAIN_RANGES = (
    (0x001000, 0x243ED2, IMAGE_BASE),
    (0x3DF000, 0x002063, IMAGE_BASE),
    (0x3E2000, 0x0033D2, IMAGE_BASE),
)
SECSERV_IMPORT_RECORD_BASE = 0x3F
SECSERV_IMPORT_RECORD_SIZE = 0x8D
SECSERV_IMPORT_PERMUTATION_POINTER_OFFSET = 0x0D
SECSERV_IMPORT_COUNT_OFFSET = 0x19
SECSERV_IMPORT_ITEMS_POINTER_OFFSET = 0x84
SECSERV_IMPORT_ITEM_SIZE = 0x4C3
SECSERV_IMPORT_ITEM_DISPATCHER_OFFSET = 0x477
SECSERV_IMPORT_ITEM_IAT_OFFSET = 0x4AA
SECSERV_IMPORT_ITEM_DESCRIPTOR_OFFSET = 0x4B7
SECSERV_IMPORT_ITEM_INDEX_OFFSET = 0x4BB
SECSERV_IMPORT_FF15_CANDIDATES = 49
SECSERV_IMPORT_FF15_DISPATCHERS = 48
SECSERV_IMPORT_FF15_SELECTED = 19
SECSERV_IMPORT_FF15_CHANGED = 18
PFD_FILENAME = "PfdRun.pfd"
PFD_SIZE = 45_056
PFD_OUTER_TEA_KEY = b"78eabdg4232ewz1\0"
PFD_PASSWORD = b"o7t43y(cuiykhbdfon|2qwec2!46gh&wo7\0"
PFD_ALT_CHUNKS = (
    (0x600C, 0x700C),
    (0x7018, 0x8018),
    (0x8024, 0x9024),
    (0x9030, 0x9104),
)
PFD_ALT_ROW_SIZE = 20
PFD_ALT_ROW_COUNT = 625
PFD_ALT_TABLE_SIZE = PFD_ALT_ROW_SIZE * PFD_ALT_ROW_COUNT
PFD_ALT_TABLE_SHA256 = (
    "6183dd3faba576114e6987368e9d6e1b0ea811a7821efbdcc76d7c42651dc4f7"
)
PFD_ALT_CONTEXT_MULTIPLIER = 0x215D7FC6
PFD_ALT_WINDOW_XOR = 0xFA
PFD_ALT_TYPED_RECORD_COUNT = 291
PFD_ALT_INACTIVE_RECORD_COUNT = 178
PFD_ALT_FRAGMENT_COUNT = 113
PFD_ALT_PATCH_BYTE_COUNT = 287
PFD_ALT_TOUCHED_RUN_COUNT = 89
PFD_ALT_CONTROL_HISTOGRAM = {2: 92, 3: 8, 6: 12, 7: 1}
ORACLE_PADDING_GAPS = (
    # (RVA, runtime gap, bytes immediately before, bytes immediately after)
    (0x125B, bytes.fromhex("44260f1000"), b"\xC2\x04\x00",
     bytes.fromhex("568b742408")),
    (0x3409, b"\xCC" * 7, b"\xC3", bytes.fromhex("8bc18b4c2404")),
)


class DecryptError(ValueError):
    """Raised when an input is incomplete or is not the exact supported build."""


@dataclass(frozen=True)
class SectionSpec:
    name: str
    virtual_address: int
    virtual_size: int
    raw_offset: int
    raw_size: int
    characteristics: int


CORE_SECTIONS = (
    SectionSpec(".text", 0x1000, 0x243ED2, 0x1000, 0x244000, 0x60000020),
    SectionSpec(".rdata", 0x245000, 0x365D5, 0x245000, 0x37000, 0x40000040),
    SectionSpec(".data", 0x27C000, 0x160A80, 0x27C000, 0x14000, 0xC0000040),
    SectionSpec(".tls", 0x3DD000, 0x9, 0x290000, 0x1000, 0xC0000040),
    SectionSpec(".rsrc", 0x3DE000, 0xDA6, 0x291000, 0x1000, 0x40000040),
)


@dataclass(frozen=True)
class ImportSpec:
    dll: str
    original_first_thunk: int
    first_thunk: int
    count: int


@dataclass(frozen=True)
class RuntimeRepairSources:
    """OEP checkpoint artifacts used to materialize lazy SecServ records."""

    heap_base: int
    heap: bytes
    secserv: bytes
    pfd_alt_rows: bytes


@dataclass(frozen=True)
class AltFragment:
    """One authenticated lazy code fragment decoded from PfdRun resource 3FC."""

    site_rva: int
    row_index: int
    control: int
    window: bytes


@dataclass(frozen=True)
class RuntimeImportRecord:
    descriptor_index: int
    spec: ImportSpec
    mask_rva: int
    mask: bytes
    permutation: tuple[int, ...]
    items_pointer: int


@dataclass(frozen=True)
class RedirectRecord:
    """One expanded SecServ redirect record and its trailing-byte policy."""

    slot: int
    key: int
    payload: bytes
    append_zero: bool


IMPORTS = (
    ImportSpec("binkw32.dll", 0x27A93C, 0x2452E8, 15),
    ImportSpec("WS2_32.dll", 0x27A8C4, 0x245270, 17),
    ImportSpec("d3d9.dll", 0x27A97C, 0x245328, 1),
    ImportSpec("WINMM.dll", 0x27A8B8, 0x245264, 2),
    ImportSpec("DINPUT8.dll", 0x27A674, 0x245020, 1),
    ImportSpec("DSOUND.dll", 0x27A67C, 0x245028, 2),
    ImportSpec("KERNEL32.dll", 0x27A690, 0x24503C, 114),
    ImportSpec("USER32.dll", 0x27A85C, 0x245208, 22),
    ImportSpec("GDI32.dll", 0x27A688, 0x245034, 1),
    ImportSpec("ADVAPI32.dll", 0x27A654, 0x245000, 7),
    ImportSpec("WSOCK32.dll", 0x27A90C, 0x2452B8, 11),
)

# SecServ 0x10044F2A supplies these missing first dwords before calling the
# protected-vector decoder.  The other eight import vectors are already
# plaintext in the exact protected executable.
PROTECTED_IMPORT_SEEDS = {
    "USER32.dll": 0x48351DEF,
    "GDI32.dll": 0x0027B0F8,
    "ADVAPI32.dll": 0x99CA2F1D,
}

# SecServ's static table at runtime RVA 0xB0444 consists of these three
# (return RVA, IAT RVA) pairs followed by a zero/zero terminator.  The six-byte
# call starts at return_rva - 6.  The residue bytes are the exact surviving
# lazy E9 transfer plus the byte it replaces; they were observed in the
# protected loader output, not taken from the no-CD oracle.
STXT_IMPORT_CALL_TABLE = (
    (0x0E2C3D, 0x245228, bytes.fromhex("e9e1cb2f00df")),
    (0x0E2C2F, 0x245230, bytes.fromhex("e9d2cb2f0083")),
    (0x0E1919, 0x245008, bytes.fromhex("e99ddc2f00df")),
    (0, 0, b""),
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def expected_secserv_stxt_table() -> bytes:
    return b"".join(
        struct.pack("<II", return_rva, iat_rva)
        for return_rva, iat_rva, _residue in STXT_IMPORT_CALL_TABLE
    )


def is_exact_secserv_runtime_snapshot(data: bytes) -> bool:
    """Recognize the exact relocated SecServ flat image used by this build."""
    if len(data) != SECSERV_RUNTIME_SIZE or data[:2] != b"MZ":
        return False
    if (data[SECSERV_STXT_TABLE_RVA:
             SECSERV_STXT_TABLE_RVA + len(expected_secserv_stxt_table())]
            != expected_secserv_stxt_table()):
        return False
    signature_rva = 0x453CB
    if (data[signature_rva:signature_rva + len(
            SECSERV_IMPORT_NAME_DECRYPT_SIGNATURE)]
            != SECSERV_IMPORT_NAME_DECRYPT_SIGNATURE):
        return False
    try:
        pe = pefile.PE(data=data, fast_load=True)
    except pefile.PEFormatError:
        return False
    section_names = tuple(
        section.Name.rstrip(b"\0") for section in pe.sections
    )
    return (
        pe.FILE_HEADER.Machine == 0x14C
        and pe.FILE_HEADER.NumberOfSections == 9
        and pe.FILE_HEADER.TimeDateStamp == SECSERV_TIMESTAMP
        and pe.OPTIONAL_HEADER.ImageBase == SECSERV_ORIGINAL_IMAGE_BASE
        and pe.OPTIONAL_HEADER.SizeOfImage == SECSERV_RUNTIME_SIZE
        and section_names == (
            b".txt2", b".text", b".txt", b".txt5", b".txt4",
            b".rdata", b".data", b".reloc", b"stxt774",
        )
    )


def load_runtime_repair_sources(checkpoint_path: Path, memory_path: Path,
                                manifest: dict) -> RuntimeRepairSources:
    """Load checkpoint-sibling heap, SecServ, and protected PFD artifacts."""
    checkpoint_dir = checkpoint_path.resolve().parent
    if memory_path.resolve().parent != checkpoint_dir:
        raise DecryptError(
            "main.runtime.bin and checkpoint.json are not sibling artifacts"
        )

    heap_base = manifest.get("heap_base")
    heap_used = manifest.get("heap_used")
    if (isinstance(heap_base, bool) or not isinstance(heap_base, int)
            or heap_base != EMULATED_HEAP_BASE):
        raise DecryptError(
            f"checkpoint heap base is not 0x{EMULATED_HEAP_BASE:08X}"
        )
    if (isinstance(heap_used, bool) or not isinstance(heap_used, int)
            or not 0 < heap_used <= 0x10000000
            or heap_base + heap_used > 0x100000000):
        raise DecryptError("checkpoint heap_used is invalid")

    heap_path = checkpoint_dir / "heap.bin"
    if not heap_path.is_file():
        raise DecryptError(f"checkpoint sibling heap.bin is missing: {heap_path}")
    heap = heap_path.read_bytes()
    if len(heap) != heap_used:
        raise DecryptError(
            f"heap.bin size 0x{len(heap):X} disagrees with checkpoint "
            f"heap_used 0x{heap_used:X}"
        )

    candidates: list[tuple[Path, bytes]] = []
    for path in sorted(checkpoint_dir.glob("*.runtime.bin")):
        if path.resolve() == memory_path.resolve():
            continue
        data = path.read_bytes()
        if is_exact_secserv_runtime_snapshot(data):
            candidates.append((path, data))
    if len(candidates) != 1:
        rendered = ", ".join(path.name for path, _data in candidates) or "none"
        raise DecryptError(
            "checkpoint directory does not contain exactly one signature-matched "
            f"SecServ runtime snapshot (found {rendered})"
        )
    _secserv_path, secserv = candidates[0]

    table_pointer = struct.unpack_from(
        "<I", secserv, SECSERV_REDIRECT_TABLE_POINTER_RVA
    )[0]
    minimum_table_size = (
        SECSERV_REDIRECT_RECORDS_OFFSET
        + SECSERV_REDIRECT_SLOT_COUNT * SECSERV_REDIRECT_RECORD_SIZE
    )
    if (table_pointer < heap_base
            or table_pointer + minimum_table_size > heap_base + len(heap)):
        raise DecryptError(
            f"SecServ redirect-table pointer 0x{table_pointer:08X} is outside "
            "the checkpoint heap"
        )

    pfd_path = checkpoint_dir / PFD_FILENAME
    if not pfd_path.is_file():
        raise DecryptError(
            f"checkpoint sibling {PFD_FILENAME} is missing: {pfd_path}"
        )
    pfd = pfd_path.read_bytes()
    if len(pfd) != PFD_SIZE:
        raise DecryptError(
            f"{PFD_FILENAME} size is 0x{len(pfd):X}; expected 0x{PFD_SIZE:X}"
        )
    pfd_alt_rows = decode_pfd_alt_rows(pfd)
    return RuntimeRepairSources(heap_base, heap, secserv, pfd_alt_rows)


def read_ascii_z(image: bytes | bytearray, rva: int, limit: int = 260) -> str:
    if not 0 <= rva < len(image):
        raise DecryptError(f"string RVA 0x{rva:X} is outside the memory image")
    end = image.find(b"\0", rva, min(len(image), rva + limit))
    if end < 0:
        raise DecryptError(f"unterminated string at RVA 0x{rva:X}")
    try:
        value = bytes(image[rva:end]).decode("ascii")
    except UnicodeDecodeError as exc:
        raise DecryptError(f"non-ASCII string at RVA 0x{rva:X}") from exc
    if not value or any(ord(char) < 0x20 or ord(char) > 0x7E for char in value):
        raise DecryptError(f"invalid import string at RVA 0x{rva:X}")
    return value


def protected_pe(data: bytes) -> pefile.PE:
    if len(data) != PROTECTED_SIZE:
        raise DecryptError(
            f"protected executable size mismatch: expected {PROTECTED_SIZE:,}, "
            f"got {len(data):,}"
        )
    digest = sha256(data)
    if digest != PROTECTED_SHA256:
        raise DecryptError(
            f"protected executable SHA-256 mismatch: expected {PROTECTED_SHA256}, "
            f"got {digest}"
        )
    try:
        pe = pefile.PE(data=data, fast_load=False)
    except pefile.PEFormatError as exc:
        raise DecryptError(f"protected executable is not a PE: {exc}") from exc
    actual = tuple(
        (
            section.Name.rstrip(b"\0").decode("ascii"),
            section.VirtualAddress,
            section.Misc_VirtualSize,
            section.PointerToRawData,
            section.SizeOfRawData,
            section.Characteristics,
        )
        for section in pe.sections[:len(CORE_SECTIONS)]
    )
    expected = tuple(
        (
            section.name,
            section.virtual_address,
            section.virtual_size,
            section.raw_offset,
            section.raw_size,
            section.characteristics,
        )
        for section in CORE_SECTIONS
    )
    if actual != expected or len(pe.sections) != 7:
        raise DecryptError("protected executable section layout is not the supported build")
    if pe.OPTIONAL_HEADER.ImageBase != IMAGE_BASE:
        raise DecryptError("protected executable image base is not 0x00400000")
    return pe


def parse_import_key(value: str) -> bytes:
    compact = value.replace(" ", "").replace(":", "")
    try:
        key = bytes.fromhex(compact)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("import key is not hexadecimal") from exc
    if len(key) != 16:
        raise argparse.ArgumentTypeError(
            f"import key must contain exactly 16 bytes, got {len(key)}"
        )
    return key


def tea_decrypt_pair(left: int, right: int,
                     key_words: tuple[int, int, int, int]) -> tuple[int, int]:
    """Classic 32-round little-endian TEA decryption used by SecServ."""
    mask = 0xFFFFFFFF
    total = TEA_DECRYPT_SUM
    k0, k1, k2, k3 = key_words
    for _ in range(32):
        right = (
            right
            - ((((left << 4) + k2) ^ (left + total) ^ ((left >> 5) + k3)) & mask)
        ) & mask
        left = (
            left
            - ((((right << 4) + k0) ^ (right + total) ^ ((right >> 5) + k1)) & mask)
        ) & mask
        total = (total - TEA_DELTA) & mask
    return left, right


def tea_encrypt_pair(left: int, right: int,
                     key_words: tuple[int, int, int, int]) -> tuple[int, int]:
    """Classic 32-round little-endian TEA encryption used by SecServ."""
    mask = 0xFFFFFFFF
    total = 0
    key0, key1, key2, key3 = key_words
    for _round in range(32):
        total = (total + TEA_DELTA) & mask
        left = (
            left + (
                ((right << 4) + key0)
                ^ (right + total)
                ^ ((right >> 5) + key1)
            )
        ) & mask
        right = (
            right + (
                ((left << 4) + key2)
                ^ (left + total)
                ^ ((left >> 5) + key3)
            )
        ) & mask
    return left, right


def native_overlap_tea_decrypt(data: bytes, key: bytes) -> bytes:
    """Reproduce the PFD codec's overlapping-tail TEA traversal exactly."""
    if len(key) != 16:
        raise DecryptError("PFD TEA key must contain exactly 16 bytes")
    if len(data) < 8:
        raise DecryptError("PFD TEA payload is shorter than one block")
    key_words = struct.unpack("<4I", key)
    output = bytearray(data)

    def decrypt_at(offset: int) -> None:
        if offset < 0 or offset + 8 > len(output):
            raise DecryptError("PFD TEA traversal exceeded its source chunk")
        left, right = struct.unpack_from("<2I", output, offset)
        left, right = tea_decrypt_pair(left, right, key_words)
        struct.pack_into("<2I", output, offset, left, right)

    position = 0
    remaining = len(output)
    while remaining >= 16:
        decrypt_at(position)
        position += 8
        remaining -= 8
    if remaining == 8:
        decrypt_at(position)
    elif remaining > 8:
        # The native codec covers a partial tail by decrypting the final eight
        # bytes and then the overlapping block at the sequential position.
        decrypt_at(position + remaining - 8)
        decrypt_at(position)
    elif remaining:
        raise DecryptError("PFD TEA traversal ended with an undersized tail")
    return bytes(output)


def decode_pfd_alt_rows(pfd: bytes) -> bytes:
    """Decode protected PfdRun resource 3FC into its 625 typed Alt rows."""
    if len(pfd) != PFD_SIZE:
        raise DecryptError(
            f"{PFD_FILENAME} size is 0x{len(pfd):X}; expected 0x{PFD_SIZE:X}"
        )
    decoded_chunks: list[bytes] = []
    for index, (start, end) in enumerate(PFD_ALT_CHUNKS):
        if not 0 <= start < end <= len(pfd):
            raise DecryptError(f"PFD Alt chunk {index} is outside {PFD_FILENAME}")
        outer = native_overlap_tea_decrypt(
            pfd[start:end], PFD_OUTER_TEA_KEY
        )
        whitened = bytes(
            value ^ PFD_PASSWORD[offset % len(PFD_PASSWORD)]
            for offset, value in enumerate(outer)
        )
        decoded_chunks.append(
            native_overlap_tea_decrypt(whitened, PFD_PASSWORD[:16])
        )
    rows = b"".join(decoded_chunks)
    if len(rows) != PFD_ALT_TABLE_SIZE:
        raise DecryptError(
            f"decoded PFD Alt table is 0x{len(rows):X} bytes; expected "
            f"0x{PFD_ALT_TABLE_SIZE:X}"
        )
    digest = sha256(rows)
    if digest != PFD_ALT_TABLE_SHA256:
        raise DecryptError(
            "decoded PFD Alt-table SHA-256 mismatch: expected "
            f"{PFD_ALT_TABLE_SHA256}, got {digest}"
        )
    return rows


def rotate_left32(value: int, count: int) -> int:
    count &= 31
    value &= 0xFFFFFFFF
    if not count:
        return value
    return ((value << count) | (value >> (32 - count))) & 0xFFFFFFFF


def rotate_right32(value: int, count: int) -> int:
    return rotate_left32(value, -count)


def secserv_import_selector_first(value: int) -> int:
    """Linearized first 0x115-byte round at SecServ 0x100929BD."""
    value = (value + 1) & 0xFFFFFFFF
    value = rotate_left32(value, 0x78)
    value = (value + 0x03BA347A) & 0xFFFFFFFF
    value ^= 0x2897128B
    value = rotate_right32(value, 0xFD)
    value = (value + 0x2CF4138B) & 0xFFFFFFFF
    value ^= 0x56C33681
    value ^= 0x2843792C
    value = rotate_left32(value, 0xB0)
    value = (value - 0x30A620F9) & 0xFFFFFFFF
    value = rotate_left32(value, 0x46)
    value = rotate_left32(value, 0x6E)
    value = (-value) & 0xFFFFFFFF
    value = (value - 0x0ED0507D) & 0xFFFFFFFF
    value = (-value) & 0xFFFFFFFF
    value = rotate_right32(value, 0x48)
    value = rotate_right32(value, 0xC4)
    value = (-value) & 0xFFFFFFFF
    value = rotate_right32(value, 0x7F)
    value = (value + 0x3D117208) & 0xFFFFFFFF
    value = rotate_right32(value, 0x41)
    value ^= 0x261B5787
    value = (value - 0x28B62BAD) & 0xFFFFFFFF
    value = (-value) & 0xFFFFFFFF
    value = rotate_right32(value, 0x45)
    value = (value - 0x40E45916) & 0xFFFFFFFF
    value = (-value) & 0xFFFFFFFF
    value = (value + 0x50CE75A1) & 0xFFFFFFFF
    value = (value - 0x44D75305) & 0xFFFFFFFF
    value = (value + 0x45615407) & 0xFFFFFFFF
    value = (-value) & 0xFFFFFFFF
    value = (-value) & 0xFFFFFFFF
    value = rotate_left32(value, 0xDA)
    value = (-value) & 0xFFFFFFFF
    value ^= 0x0609446C
    value = (value - 0x21670A53) & 0xFFFFFFFF
    value = (value + 0x2A6E140D) & 0xFFFFFFFF
    value = rotate_left32(value, 0x53)
    value = (value + 1) & 0xFFFFFFFF
    value ^= 0x199C1FF8
    value = (value + 1) & 0xFFFFFFFF
    value = (value + 0x1E4F241B) & 0xFFFFFFFF
    value = rotate_right32(value, 0x5C)
    return (-value) & 0xFFFFFFFF


def secserv_import_selector_second(value: int) -> int:
    """Linearized second 0x115-byte round at SecServ 0x100928A8."""
    value = rotate_left32(value, 0x54)
    value = (value + 0x03BA347A) & 0xFFFFFFFF
    value ^= 0x2E24717E
    value = (value + 0x2897128B) & 0xFFFFFFFF
    value = rotate_left32(value, 0x81)
    value = rotate_right32(value, 0x2C)
    value = rotate_right32(value, 0x76)
    value ^= 0x7AA45DB0
    value = (value - 0x15176D56) & 0xFFFFFFFF
    value = rotate_left32(value, 0x46)
    value = rotate_right32(value, 0x6E)
    value = rotate_right32(value, 0xF1)
    value = (value - 0x61121C46) & 0xFFFFFFFF
    value = rotate_right32(value, 0x6E)
    value = (value - 0x3C6A1159) & 0xFFFFFFFF
    value = (value - 0x6A5822DB) & 0xFFFFFFFF
    value = (value - 0x3D117208) & 0xFFFFFFFF
    value = rotate_left32(value, 0x41)
    value = (value - 0x261B5787) & 0xFFFFFFFF
    value = (value + 0x562E2B95) & 0xFFFFFFFF
    value = (value + 0x40E45916) & 0xFFFFFFFF
    value ^= 0x17815F55
    value = (value + 0x50CE75A1) & 0xFFFFFFFF
    value = rotate_left32(value, 5)
    value = rotate_right32(value, 7)
    value = rotate_right32(value, 0x94)
    value = rotate_left32(value, 0xD2)
    value = rotate_right32(value, 0x49)
    value = rotate_right32(value, 0xDA)
    value = (value + 0x3FA614A2) & 0xFFFFFFFF
    value = rotate_left32(value, 0x3B)
    value = rotate_right32(value, 0x6C)
    value ^= 0x21670A53
    value = rotate_right32(value, 0x96)
    value ^= 0x2A6E140D
    value = (value - 0x04E37453) & 0xFFFFFFFF
    value = rotate_left32(value, 0xC2)
    value = rotate_left32(value, 0xF8)
    value ^= 0x5A0F04F5
    value = rotate_left32(value, 0x1B)
    value = (value - 0x34EF137E) & 0xFFFFFFFF
    value = (value + 0x52D47E52) & 0xFFFFFFFF
    value ^= 0x6C4713EC
    return rotate_left32(value, 0x67)


def secserv_import_site_selected(site_offset: int) -> bool:
    value = site_offset & 0xFFFFFFFF
    value ^= secserv_import_selector_first(value)
    value ^= secserv_import_selector_second(value)
    return (value & 3) < 2


def decode_import_vector_pairs(values: list[int], key: bytes) -> list[int]:
    """Reproduce SecServ 0x100454C6 whitening and TEA pair decode."""
    if len(key) != 16:
        raise DecryptError("import-vector key is not 16 bytes")
    key_words = struct.unpack("<4I", key)
    decoded = list(values)
    mask = 0xFFFFFFFF
    random = IMPORT_CIPHER_R
    for index in range(0, len(decoded) - 1, 2):
        decoded[index] ^= random
        next_random = (
            random * IMPORT_CIPHER_MULTIPLIER + IMPORT_CIPHER_FIRST_ADDEND
        ) & mask
        decoded[index + 1] ^= next_random
        random = (
            next_random * IMPORT_CIPHER_MULTIPLIER
            - IMPORT_CIPHER_SECOND_SUBTRAHEND
        ) & mask
        decoded[index], decoded[index + 1] = tea_decrypt_pair(
            decoded[index], decoded[index + 1], key_words
        )

    return decoded


def scatter_import_vector(decoded: list[int], key: bytes) -> list[int]:
    """Reproduce SecServ 0x1004596E's keyed permutation and scatter."""
    if len(key) != 16:
        raise DecryptError("import-vector key is not 16 bytes")
    key_words = struct.unpack("<4I", key)
    mask = 0xFFFFFFFF
    # 0x1004596E starts with the identity permutation, advances the LCG before
    # every selection, and uses multiply-high to avoid modulo bias.
    permutation = list(range(len(decoded)))
    state = key_words[0]
    for index in range(len(permutation)):
        state = (
            state * IMPORT_PERMUTATION_MULTIPLIER + IMPORT_PERMUTATION_ADDEND
        ) & mask
        selected = (state * len(permutation)) >> 32
        permutation[index], permutation[selected] = (
            permutation[selected], permutation[index]
        )

    scattered = [0] * len(decoded)
    for index, value in enumerate(decoded):
        scattered[permutation[index]] = value
    return scattered


def decrypt_import_vector(values: list[int], key: bytes) -> list[int]:
    """Reproduce SecServ 0x100454C6 followed by 0x1004596E scatter."""
    return scatter_import_vector(decode_import_vector_pairs(values, key), key)


def decrypt_import_name_bytes(encoded: bytes, key: bytes) -> bytes:
    """Reproduce SecServ 0x100453CB for one encoded API-name buffer.

    The protected IMAGE_IMPORT_BY_NAME record stores the buffer length in its
    first byte and the encoded buffer at +2.  SecServ decrypts an overlapping
    trailing block first when the length is not a multiple of eight, then
    whitens and TEA-decrypts each aligned block.  Short buffers use its small
    byte-XOR path.  The returned buffer includes the trailing NUL.
    """
    if len(key) != 16:
        raise DecryptError("import-name key is not 16 bytes")
    if not encoded:
        raise DecryptError("encoded import name is empty")

    decoded = bytearray(encoded)
    if len(decoded) < 8:
        state = 0x56
        for index in range(len(decoded)):
            decoded[index] ^= state
            state = (state * 0x32 + 0x34) & 0xFF
        return bytes(decoded)

    key_words = struct.unpack("<4I", key)
    remainder = len(decoded) & 7
    if remainder:
        offset = len(decoded) - 8
        left, right = struct.unpack_from("<2I", decoded, offset)
        left, right = tea_decrypt_pair(left, right, key_words)
        struct.pack_into("<2I", decoded, offset, left, right)

    state = IMPORT_NAME_FIRST_XOR
    aligned_length = len(decoded) & ~7
    for offset in range(0, aligned_length, 8):
        left, right = struct.unpack_from("<2I", decoded, offset)
        left ^= state
        second = (
            IMPORT_NAME_SECOND_BASE
            - state * IMPORT_NAME_XOR_MULTIPLIER
        ) & 0xFFFFFFFF
        right ^= second
        state = (
            IMPORT_NAME_NEXT_BASE
            - second * IMPORT_NAME_XOR_MULTIPLIER
        ) & 0xFFFFFFFF
        left, right = tea_decrypt_pair(left, right, key_words)
        struct.pack_into("<2I", decoded, offset, left, right)
    return bytes(decoded)


def encrypt_import_name_bytes(plaintext: bytes, key: bytes) -> bytes:
    """Reproduce inverse SecServ 0x10045300 API-name encryption."""
    if len(key) != 16:
        raise DecryptError("import-name key is not 16 bytes")
    if not plaintext:
        raise DecryptError("plaintext import name is empty")

    encoded = bytearray(plaintext)
    if len(encoded) < 8:
        state = 0x56
        for index in range(len(encoded)):
            encoded[index] ^= state
            state = (state * 0x32 + 0x34) & 0xFF
        return bytes(encoded)

    key_words = struct.unpack("<4I", key)
    state = IMPORT_NAME_FIRST_XOR
    aligned_length = len(encoded) & ~7
    for offset in range(0, aligned_length, 8):
        left, right = struct.unpack_from("<2I", encoded, offset)
        left, right = tea_encrypt_pair(left, right, key_words)
        left ^= state
        second = (
            IMPORT_NAME_SECOND_BASE
            - state * IMPORT_NAME_XOR_MULTIPLIER
        ) & 0xFFFFFFFF
        right ^= second
        state = (
            IMPORT_NAME_NEXT_BASE
            - second * IMPORT_NAME_XOR_MULTIPLIER
        ) & 0xFFFFFFFF
        struct.pack_into("<2I", encoded, offset, left, right)

    if len(encoded) & 7:
        offset = len(encoded) - 8
        left, right = struct.unpack_from("<2I", encoded, offset)
        left, right = tea_encrypt_pair(left, right, key_words)
        struct.pack_into("<2I", encoded, offset, left, right)
    return bytes(encoded)


def protected_import_vectors(protected_image: bytes) -> list[tuple[ImportSpec, list[int]]]:
    vectors: list[tuple[ImportSpec, list[int]]] = []
    for spec in IMPORTS:
        seed = PROTECTED_IMPORT_SEEDS.get(spec.dll)
        if seed is None:
            continue
        end = spec.original_first_thunk + 4 * spec.count
        if end > len(protected_image):
            raise DecryptError(f"protected {spec.dll} vector is outside the image")
        encoded = [seed]
        encoded.extend(
            struct.unpack_from(
                f"<{spec.count - 1}I", protected_image,
                spec.original_first_thunk + 4,
            )
        )
        vectors.append((spec, encoded))
    return vectors


def decoded_protected_import_vectors(
        protected_image: bytes, key: bytes,
        image_size: int) -> list[tuple[ImportSpec, list[int]]]:
    """Decode and structurally validate the three protected INT vectors.

    This stage attests only the vector key and the resulting name-record RVAs.
    It deliberately does not require those records to be plaintext yet: their
    lazy metadata restoration is a separate protection stage.  Every decoded
    RVA must nevertheless be unique and lie inside this build's ``.rdata``.
    """
    recovered: list[tuple[ImportSpec, list[int]]] = []
    seen: set[int] = set()
    rdata = CORE_SECTIONS[1]
    name_lo = rdata.virtual_address
    name_hi = min(image_size, name_lo + rdata.virtual_size)
    for spec, encoded in protected_import_vectors(protected_image):
        entries = decrypt_import_vector(encoded, key)
        if len(entries) != spec.count:
            raise DecryptError(f"internal {spec.dll} vector-count invariant failed")
        for entry in entries:
            if entry & 0x80000000:
                raise DecryptError(
                    f"decoded {spec.dll} entry 0x{entry:08X} is an unexpected ordinal"
                )
            if entry in seen:
                raise DecryptError(
                    f"decoded import-name RVA 0x{entry:X} occurs more than once"
                )
            if not name_lo <= entry <= name_hi - 3:
                raise DecryptError(
                    f"decoded {spec.dll} name RVA 0x{entry:X} is outside .rdata"
                )
            seen.add(entry)
        recovered.append((spec, entries))
    return recovered


def restore_protected_import_vectors(protected_image: bytes, image: bytearray,
                                     key: bytes) -> int:
    """Restore the three encoded INTs and their lazy API-name records.

    All sources, decoded names, and current destination bytes are validated
    before the first write.  A destination must contain either the exact
    protected record or the exact reconstructed IMAGE_IMPORT_BY_NAME record;
    mixed or independently modified runtime bytes are rejected.
    """
    recovered = decoded_protected_import_vectors(
        protected_image, key, len(image)
    )
    name_writes: list[tuple[int, bytes]] = []
    spans: list[tuple[int, int]] = []
    for spec, entries in recovered:
        for entry in entries:
            encoded_length = protected_image[entry]
            end = entry + 2 + encoded_length
            if encoded_length < 2 or end > len(protected_image) or end > len(image):
                raise DecryptError(
                    f"encoded {spec.dll} import record at RVA 0x{entry:X} "
                    "has an invalid length"
                )
            encoded_record = bytes(protected_image[entry:end])
            decoded_name = decrypt_import_name_bytes(encoded_record[2:], key)
            if encrypt_import_name_bytes(decoded_name, key) != encoded_record[2:]:
                raise DecryptError(
                    f"decoded {spec.dll} import name at RVA 0x{entry:X} "
                    "does not re-encrypt to its protected source"
                )
            if not decoded_name.endswith(b"\0") or b"\0" in decoded_name[:-1]:
                raise DecryptError(
                    f"decoded {spec.dll} import name at RVA 0x{entry:X} "
                    "does not have exactly one trailing NUL"
                )
            try:
                name = decoded_name[:-1].decode("ascii")
            except UnicodeDecodeError as exc:
                raise DecryptError(
                    f"decoded {spec.dll} import name at RVA 0x{entry:X} is not ASCII"
                ) from exc
            if (not name
                    or any(ord(char) < 0x20 or ord(char) > 0x7E for char in name)):
                raise DecryptError(
                    f"decoded {spec.dll} import name at RVA 0x{entry:X} is invalid"
                )

            # Hint zero is a valid PE normalization.  SecServ's source begins
            # at +2 and does not retain a usable IMAGE_IMPORT_BY_NAME hint.
            restored_record = b"\0\0" + decoded_name
            current = bytes(image[entry:end])
            if current not in (encoded_record, restored_record):
                raise DecryptError(
                    f"{spec.dll} import-name destination at RVA 0x{entry:X} "
                    "matches neither the protected nor restored record"
                )
            name_writes.append((entry, restored_record))
            spans.append((entry, end))

    spans.sort()
    for previous, current in zip(spans, spans[1:]):
        if previous[1] > current[0]:
            raise DecryptError(
                "decoded IMAGE_IMPORT_BY_NAME records overlap at RVA "
                f"0x{current[0]:X}"
            )

    for entry, restored_record in name_writes:
        image[entry:entry + len(restored_record)] = restored_record

    for spec, entries in recovered:
        descriptor_index = IMPORTS.index(spec)
        descriptor = IMPORT_TABLE_RVA + 20 * descriptor_index
        struct.pack_into("<I", image, descriptor, spec.original_first_thunk)
        for thunk_index, entry in enumerate(entries):
            struct.pack_into(
                "<I", image, spec.original_first_thunk + 4 * thunk_index, entry
            )
        struct.pack_into(
            "<I", image, spec.original_first_thunk + 4 * len(entries), 0
        )
    return sum(len(entries) for _spec, entries in recovered)


def restore_stxt_import_calls(image: bytearray) -> int:
    """Materialize the three lazy IAT calls described by SecServ's table."""
    if STXT_IMPORT_CALL_TABLE[-1] != (0, 0, b""):
        raise DecryptError("internal stxt import-call table has no null terminator")
    records = STXT_IMPORT_CALL_TABLE[:-1]
    if len(records) != 3 or any(not return_rva or not iat_rva
                                for return_rva, iat_rva, _residue in records):
        raise DecryptError("internal stxt import-call table shape changed")

    valid_iat_slots = {
        spec.first_thunk + 4 * index
        for spec in IMPORTS
        for index in range(spec.count)
    }
    writes: list[tuple[int, bytes]] = []
    for return_rva, iat_rva, residue in records:
        site = return_rva - 6
        replacement = b"\xFF\x15" + struct.pack("<I", IMAGE_BASE + iat_rva)
        if iat_rva not in valid_iat_slots:
            raise DecryptError(
                f"stxt repair at RVA 0x{site:X} names invalid IAT RVA 0x{iat_rva:X}"
            )
        if not 0 <= site <= len(image) - 6:
            raise DecryptError(f"stxt repair site RVA 0x{site:X} is outside image")
        current = bytes(image[site:site + 6])
        if current not in (residue, replacement):
            raise DecryptError(
                f"stxt import-call site RVA 0x{site:X} does not match its exact "
                "protected transfer or restored FF15 call"
            )
        if current == residue:
            displacement = struct.unpack_from("<i", residue, 1)[0]
            target = site + 5 + displacement
            if not STXT_RVA_LO <= target < STXT_RVA_HI:
                raise DecryptError(
                    f"stxt import-call residue at RVA 0x{site:X} targets "
                    f"unexpected RVA 0x{target:X}"
                )
        writes.append((site, replacement))

    if len({site for site, _replacement in writes}) != len(writes):
        raise DecryptError("stxt import-call table contains duplicate sites")
    for site, replacement in writes:
        image[site:site + len(replacement)] = replacement
    return len(writes)


def secserv_redirect_dictionary(
        sources: RuntimeRepairSources) -> dict[int, RedirectRecord]:
    """Parse and validate SecServ's fixed-slot lazy redirect dictionary."""
    table_pointer = struct.unpack_from(
        "<I", sources.secserv, SECSERV_REDIRECT_TABLE_POINTER_RVA
    )[0]
    table = table_pointer - sources.heap_base
    if not 0 <= table <= len(sources.heap) - SECSERV_REDIRECT_RECORDS_OFFSET:
        raise DecryptError("SecServ redirect-table pointer is outside heap.bin")
    count, serialized_size = struct.unpack_from("<II", sources.heap, table)
    if count != SECSERV_REDIRECT_COUNT:
        raise DecryptError(
            f"SecServ redirect dictionary count is {count}; expected "
            f"{SECSERV_REDIRECT_COUNT}"
        )
    if serialized_size != SECSERV_REDIRECT_SERIALIZED_SIZE:
        raise DecryptError(
            f"SecServ redirect dictionary serialized size is "
            f"0x{serialized_size:X}; expected 0x{SECSERV_REDIRECT_SERIALIZED_SIZE:X}"
        )

    flags = struct.unpack_from(
        f"<{SECSERV_REDIRECT_SLOT_COUNT}H", sources.heap, table + 8
    )
    if any(flag not in (0, 1) for flag in flags):
        raise DecryptError("SecServ redirect dictionary has a non-boolean slot flag")
    if sum(flags) != count:
        raise DecryptError(
            f"SecServ redirect dictionary has {sum(flags)} occupied slots; "
            f"expected {count}"
        )

    records: dict[int, RedirectRecord] = {}
    special_records: list[RedirectRecord] = []
    for slot, occupied in enumerate(flags):
        if not occupied:
            continue
        record = (
            table + SECSERV_REDIRECT_RECORDS_OFFSET
            + slot * SECSERV_REDIRECT_RECORD_SIZE
        )
        record_end = record + SECSERV_REDIRECT_RECORD_SIZE
        if record < 0 or record_end > len(sources.heap):
            raise DecryptError(
                f"SecServ redirect dictionary slot {slot} is outside heap.bin"
            )
        key = (
            struct.unpack_from(
                "<I", sources.heap, record + SECSERV_REDIRECT_KEY_OFFSET
            )[0]
            ^ SECSERV_REDIRECT_KEY_XOR
        )
        length = struct.unpack_from(
            "<I", sources.heap, record + SECSERV_REDIRECT_LENGTH_OFFSET
        )[0] + 1
        if length != 5:
            raise DecryptError(
                f"SecServ redirect dictionary slot {slot} payload length is "
                f"{length}; expected 5"
            )
        payload_start = record + SECSERV_REDIRECT_PAYLOAD_OFFSET
        payload_end = payload_start + length
        if payload_end > record_end:
            raise DecryptError(
                f"SecServ redirect dictionary slot {slot} payload exceeds record"
            )
        special_flags = bytes(sources.heap[
            record + SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET:
            record + SECSERV_REDIRECT_SPECIAL_FLAGS_OFFSET + 2
        ])
        if special_flags not in (b"\0\0", b"\x01\x01"):
            raise DecryptError(
                f"SecServ redirect dictionary slot {slot} has invalid special flags"
            )
        if key in records:
            raise DecryptError(
                f"SecServ redirect dictionary key 0x{key:08X} is duplicated"
            )
        parsed = RedirectRecord(
            slot,
            key,
            bytes(sources.heap[payload_start:payload_end]),
            special_flags == b"\x01\x01",
        )
        records[key] = parsed
        if parsed.append_zero:
            special_records.append(parsed)
    if len(records) != SECSERV_REDIRECT_COUNT:
        raise DecryptError("SecServ redirect dictionary did not yield 77 records")
    if len(special_records) != 1:
        raise DecryptError(
            f"SecServ redirect dictionary has {len(special_records)} special "
            "records; expected 1"
        )
    special = special_records[0]
    if (special.slot, special.key) != (
            SECSERV_REDIRECT_SPECIAL_SLOT, SECSERV_REDIRECT_SPECIAL_KEY):
        raise DecryptError(
            "SecServ redirect dictionary special-record identity changed"
        )
    return records


def redirect_site_key(site_rva: int) -> int:
    digest = hashlib.md5(
        struct.pack("<I", site_rva + 5), usedforsecurity=False
    ).digest()
    return struct.unpack_from("<I", digest)[0]


def restore_redirect_dictionary(image: bytearray,
                                sources: RuntimeRepairSources) -> int:
    """Restore all 77 redirect records, including the flagged six-byte one."""
    stxt_hits, sites = rel32_residue(image)
    if stxt_hits:
        raise DecryptError(
            "stxt import calls must be restored before the redirect dictionary"
        )
    if len(sites) != SECSERV_REDIRECT_COUNT:
        raise DecryptError(
            f"main image contains {len(sites)} redirect markers; expected "
            f"{SECSERV_REDIRECT_COUNT}"
        )
    if len(set(sites)) != len(sites):
        raise DecryptError("main image redirect marker sites are not unique")

    records = secserv_redirect_dictionary(sources)
    consumed: set[int] = set()
    writes: list[tuple[int, bytes]] = []
    for site in sites:
        current = bytes(image[site:site + 5])
        if len(current) != 5 or current[0] != 0xE8:
            raise DecryptError(f"redirect marker RVA 0x{site:X} is truncated")
        target = site + 5 + struct.unpack_from("<i", current, 1)[0]
        if target != SAFE_DISC_REDIRECT_RVA:
            raise DecryptError(
                f"redirect marker RVA 0x{site:X} targets unexpected "
                f"RVA 0x{target:X}"
            )
        key = redirect_site_key(site)
        record = records.get(key)
        if record is None:
            raise DecryptError(
                f"redirect marker RVA 0x{site:X} has no SecServ dictionary record"
            )
        if key in consumed:
            raise DecryptError(
                f"SecServ dictionary record 0x{key:08X} maps more than one site"
            )
        consumed.add(key)
        if record.payload == current:
            raise DecryptError(
                f"SecServ dictionary record for RVA 0x{site:X} retains its marker"
            )
        replacement = record.payload
        if record.append_zero:
            if (site != SECSERV_REDIRECT_SPECIAL_SITE_RVA
                    or record.slot != SECSERV_REDIRECT_SPECIAL_SLOT
                    or record.key != SECSERV_REDIRECT_SPECIAL_KEY):
                raise DecryptError(
                    "SecServ special redirect record maps to an unexpected site"
                )
            if site + 5 >= len(image) or image[site + 5] != 0xCC:
                raise DecryptError(
                    f"SecServ special redirect trailing byte at RVA "
                    f"0x{site + 5:X} changed"
                )
            replacement += b"\0"
        writes.append((site, replacement))

    if consumed != set(records):
        missing = len(set(records) - consumed)
        raise DecryptError(
            f"{missing} SecServ redirect dictionary record(s) have no marker site"
        )
    occupied: set[int] = set()
    for site, payload in writes:
        span = set(range(site, site + len(payload)))
        if occupied & span:
            raise DecryptError(
                f"SecServ redirect write at RVA 0x{site:X} overlaps another record"
            )
        occupied.update(span)
    if len(occupied) != SECSERV_REDIRECT_PATCH_BYTE_COUNT:
        raise DecryptError(
            f"SecServ redirect writes cover {len(occupied)} bytes; expected "
            f"{SECSERV_REDIRECT_PATCH_BYTE_COUNT}"
        )
    for site, payload in writes:
        image[site:site + len(payload)] = payload
    if rel32_residue(image)[1]:
        raise DecryptError("redirect dictionary restoration left marker residues")
    return len(writes)


def checked_heap_offset(sources: RuntimeRepairSources, pointer: int,
                        size: int, label: str) -> int:
    if (isinstance(pointer, bool) or not isinstance(pointer, int)
            or isinstance(size, bool) or not isinstance(size, int) or size < 0):
        raise DecryptError(f"{label} has invalid pointer/size fields")
    offset = pointer - sources.heap_base
    if offset < 0 or size > len(sources.heap) - offset:
        raise DecryptError(
            f"{label} range 0x{pointer:08X}+0x{size:X} is outside heap.bin"
        )
    return offset


def secserv_import_runtime_records(
        image: bytes | bytearray, sources: RuntimeRepairSources,
        key: bytes) -> dict[int, RuntimeImportRecord]:
    """Validate the live manager, masks, permutations, and item records."""
    selector_rounds = (
        (
            "first", SECSERV_IMPORT_SELECTOR_FIRST_RVA,
            SECSERV_IMPORT_SELECTOR_FIRST_SHA256,
        ),
        (
            "second", SECSERV_IMPORT_SELECTOR_SECOND_RVA,
            SECSERV_IMPORT_SELECTOR_SECOND_SHA256,
        ),
    )
    for label, rva, expected_digest in selector_rounds:
        selector = sources.secserv[
            rva:rva + SECSERV_IMPORT_SELECTOR_FUNCTION_SIZE
        ]
        if (len(selector) != SECSERV_IMPORT_SELECTOR_FUNCTION_SIZE
                or sha256(selector) != expected_digest):
            raise DecryptError(
                f"SecServ import-site selector {label} round signature changed"
            )
    enabled_a, enabled_b, runtime_base, initialized = struct.unpack_from(
        "<HHII", sources.secserv, SECSERV_IMPORT_SELECTOR_ENABLED_RVA
    )
    if (enabled_a, enabled_b, runtime_base, initialized) != (
            1, 1, 0x10000000, 0):
        raise DecryptError(
            "SecServ import-site selector globals are not the attested OEP state"
        )

    manager_pointer = struct.unpack_from(
        "<I", sources.secserv, SECSERV_IMPORT_MANAGER_POINTER_RVA
    )[0]
    manager_size = (
        SECSERV_IMPORT_RECORD_BASE
        + len(IMPORTS) * SECSERV_IMPORT_RECORD_SIZE
    )
    manager = checked_heap_offset(
        sources, manager_pointer, manager_size, "SecServ import manager"
    )
    if sources.heap[manager + 0x26:manager + 0x36] != key:
        raise DecryptError("SecServ import manager key disagrees with checkpoint key")
    registered_limit = struct.unpack_from("<I", sources.heap, manager + 0x0F)[0]
    if registered_limit != 10:
        raise DecryptError(
            f"SecServ import manager descriptor limit is {registered_limit}; "
            "expected 10"
        )

    mask_array_pointer = struct.unpack_from(
        "<I", sources.secserv, SECSERV_IMPORT_MASK_POINTERS_RVA
    )[0]
    mask_array = checked_heap_offset(
        sources, mask_array_pointer, 4 * len(IMPORTS),
        "SecServ import-mask pointer array",
    )
    mask_pointers = struct.unpack_from(
        f"<{len(IMPORTS)}I", sources.heap, mask_array
    )
    mask_cursor = RESTORATION_TABLE_RVA + 2 + 6 * len(IMPORTS)
    masks: list[tuple[int, bytes]] = []
    for index, spec in enumerate(IMPORTS):
        mask_size = (spec.count + 7) // 8
        expected_pointer = IMAGE_BASE + mask_cursor
        if mask_pointers[index] != expected_pointer:
            raise DecryptError(
                f"SecServ import mask {index} points to 0x{mask_pointers[index]:08X}; "
                f"expected 0x{expected_pointer:08X}"
            )
        if mask_cursor + mask_size > len(image):
            raise DecryptError(f"SecServ import mask {index} is outside main image")
        mask = bytes(image[mask_cursor:mask_cursor + mask_size])
        unused_bits = 8 * mask_size - spec.count
        if unused_bits and mask[-1] & (0xFF << (8 - unused_bits)):
            raise DecryptError(
                f"SecServ import mask {index} has nonzero padding bits"
            )
        masks.append((mask_cursor, mask))
        mask_cursor += mask_size
    if mask_cursor != RESTORATION_PAYLOAD_END_RVA:
        raise DecryptError("SecServ import masks do not fill the restoration payload")

    records: dict[int, RuntimeImportRecord] = {}
    protected_indices = {
        index for index, spec in enumerate(IMPORTS)
        if spec.dll in PROTECTED_IMPORT_SEEDS
    }
    if protected_indices != {7, 8, 9}:
        raise DecryptError("internal protected import descriptor set changed")
    for descriptor_index in protected_indices:
        spec = IMPORTS[descriptor_index]
        record = (
            manager + SECSERV_IMPORT_RECORD_BASE
            + descriptor_index * SECSERV_IMPORT_RECORD_SIZE
        )
        count = struct.unpack_from(
            "<I", sources.heap, record + SECSERV_IMPORT_COUNT_OFFSET
        )[0]
        if count != spec.count:
            raise DecryptError(
                f"SecServ import record {descriptor_index} count is {count}; "
                f"expected {spec.count}"
            )
        permutation_pointer = struct.unpack_from(
            "<I", sources.heap,
            record + SECSERV_IMPORT_PERMUTATION_POINTER_OFFSET,
        )[0]
        permutation_offset = checked_heap_offset(
            sources, permutation_pointer, 4 * count,
            f"SecServ import record {descriptor_index} permutation",
        )
        permutation = struct.unpack_from(
            f"<{count}I", sources.heap, permutation_offset
        )
        if sorted(permutation) != list(range(count)):
            raise DecryptError(
                f"SecServ import record {descriptor_index} permutation is invalid"
            )

        items_pointer = struct.unpack_from(
            "<I", sources.heap, record + SECSERV_IMPORT_ITEMS_POINTER_OFFSET
        )[0]
        items_offset = checked_heap_offset(
            sources, items_pointer, count * SECSERV_IMPORT_ITEM_SIZE,
            f"SecServ import record {descriptor_index} items",
        )
        for item in range(count):
            item_offset = items_offset + item * SECSERV_IMPORT_ITEM_SIZE
            iat_address = struct.unpack_from(
                "<I", sources.heap,
                item_offset + SECSERV_IMPORT_ITEM_IAT_OFFSET,
            )[0]
            expected_iat = IMAGE_BASE + spec.first_thunk + 4 * item
            stored_descriptor = sources.heap[
                item_offset + SECSERV_IMPORT_ITEM_DESCRIPTOR_OFFSET
            ]
            stored_item = sources.heap[
                item_offset + SECSERV_IMPORT_ITEM_INDEX_OFFSET
            ]
            if (iat_address, stored_descriptor, stored_item) != (
                    expected_iat, descriptor_index, item):
                raise DecryptError(
                    f"SecServ import record {descriptor_index} item {item} "
                    "identity fields changed"
                )
        mask_rva, mask = masks[descriptor_index]
        records[descriptor_index] = RuntimeImportRecord(
            descriptor_index, spec, mask_rva, mask, permutation,
            items_pointer,
        )
    return records


def secserv_main_ranges(sources: RuntimeRepairSources,
                        image_size: int) -> tuple[tuple[int, int, int], ...]:
    """Validate the SecServ ranges whose executable call sites it registered."""
    count = struct.unpack_from(
        "<I", sources.secserv, SECSERV_MAIN_RANGE_COUNT_RVA
    )[0]
    if count != len(SECSERV_EXPECTED_MAIN_RANGES):
        raise DecryptError(
            f"SecServ main-range count is {count}; expected "
            f"{len(SECSERV_EXPECTED_MAIN_RANGES)}"
        )
    table_size = count * SECSERV_MAIN_RANGE_RECORD_SIZE
    table_end = SECSERV_MAIN_RANGE_TABLE_RVA + table_size
    if table_end > len(sources.secserv):
        raise DecryptError("SecServ main-range table is truncated")
    ranges = tuple(
        struct.unpack_from(
            "<III", sources.secserv,
            SECSERV_MAIN_RANGE_TABLE_RVA
            + index * SECSERV_MAIN_RANGE_RECORD_SIZE,
        )
        for index in range(count)
    )
    if ranges != SECSERV_EXPECTED_MAIN_RANGES:
        raise DecryptError("SecServ registered main-image ranges changed")
    previous_end = 0
    for rva, size, image_base in ranges:
        if (image_base != IMAGE_BASE or size < 6 or rva < previous_end
                or rva > image_size or size > image_size - rva):
            raise DecryptError("SecServ registered an invalid main-image range")
        previous_end = rva + size
    return ranges


def restore_permuted_ff15_operands(
        image: bytearray, sources: RuntimeRepairSources, key: bytes) -> int:
    """Materialize SecServ's conditional FF15 operand permutations atomically."""
    records = secserv_import_runtime_records(image, sources, key)
    ranges = secserv_main_ranges(sources, len(image))
    manager_pointer = struct.unpack_from(
        "<I", sources.secserv, SECSERV_IMPORT_MANAGER_POINTER_RVA
    )[0]
    manager = checked_heap_offset(
        sources, manager_pointer, 0x2A, "SecServ import manager seed"
    )
    seed = struct.unpack_from("<I", sources.heap, manager + 0x26)[0]

    candidates = 0
    dispatchers = 0
    selected = 0
    writes: list[tuple[int, int]] = []
    seen_sites: set[int] = set()
    for range_rva, range_size, _image_base in ranges:
        range_end = range_rva + range_size
        for site_rva in range(range_rva, range_end - 5):
            if image[site_rva:site_rva + 2] != b"\xFF\x15":
                continue
            operand = struct.unpack_from("<I", image, site_rva + 2)[0]
            matching: list[tuple[RuntimeImportRecord, int]] = []
            for record in records.values():
                first_thunk_va = IMAGE_BASE + record.spec.first_thunk
                delta = operand - first_thunk_va
                if 0 <= delta < 4 * record.spec.count and not delta & 3:
                    matching.append((record, delta // 4))
            if not matching:
                continue
            if len(matching) != 1:
                raise DecryptError(
                    f"FF15 site RVA 0x{site_rva:X} ambiguously names protected IATs"
                )
            if site_rva in seen_sites:
                raise DecryptError(
                    f"FF15 site RVA 0x{site_rva:X} appears in overlapping ranges"
                )
            seen_sites.add(site_rva)
            candidates += 1
            record, position = matching[0]

            iat_rva = record.spec.first_thunk + 4 * position
            if iat_rva > len(image) - 4:
                raise DecryptError(
                    f"FF15 site RVA 0x{site_rva:X} names an out-of-image IAT slot"
                )
            live_iat = struct.unpack_from("<I", image, iat_rva)[0]
            dispatcher = (
                record.items_pointer
                + position * SECSERV_IMPORT_ITEM_SIZE
                + SECSERV_IMPORT_ITEM_DISPATCHER_OFFSET
            )
            if live_iat != dispatcher:
                continue
            dispatchers += 1

            site_offset = site_rva - range_rva
            if not secserv_import_site_selected(site_offset):
                continue
            selected += 1
            step = (seed + site_offset) % record.spec.count
            mapped_position = position
            for _attempt in range(record.spec.count):
                mapped_position = (
                    mapped_position - step
                ) % record.spec.count
                if (record.mask[mapped_position >> 3]
                        & (1 << (mapped_position & 7))):
                    break
            else:
                raise DecryptError(
                    f"SecServ import mask {record.descriptor_index} has no "
                    "reachable allowed slot"
                )

            mapped_item = checked_heap_offset(
                sources,
                record.items_pointer
                + mapped_position * SECSERV_IMPORT_ITEM_SIZE,
                SECSERV_IMPORT_ITEM_SIZE,
                f"SecServ import record {record.descriptor_index} mapped item",
            )
            replacement = struct.unpack_from(
                "<I", sources.heap,
                mapped_item + SECSERV_IMPORT_ITEM_IAT_OFFSET,
            )[0]
            expected = (
                IMAGE_BASE + record.spec.first_thunk + 4 * mapped_position
            )
            if replacement != expected:
                raise DecryptError(
                    f"SecServ import record {record.descriptor_index} mapped "
                    "item has an inconsistent IAT address"
                )
            if replacement != operand:
                writes.append((site_rva + 2, replacement))

    observed = (candidates, dispatchers, selected, len(writes))
    expected = (
        SECSERV_IMPORT_FF15_CANDIDATES,
        SECSERV_IMPORT_FF15_DISPATCHERS,
        SECSERV_IMPORT_FF15_SELECTED,
        SECSERV_IMPORT_FF15_CHANGED,
    )
    if observed != expected:
        raise DecryptError(
            "SecServ FF15 provenance counts changed: "
            f"candidates/dispatchers/selected/changed={observed}, expected {expected}"
        )
    if len({rva for rva, _replacement in writes}) != len(writes):
        raise DecryptError("SecServ FF15 repair produced duplicate writes")
    for operand_rva, replacement in writes:
        struct.pack_into("<I", image, operand_rva, replacement)
    return len(writes)


def pfd_alt_row_map(rows: bytes) -> dict[int, tuple[int, bytes]]:
    """Parse and attest the exact 625-entry PFD resource-3FC row table."""
    if len(rows) != PFD_ALT_TABLE_SIZE:
        raise DecryptError(
            f"PFD Alt table is 0x{len(rows):X} bytes; expected "
            f"0x{PFD_ALT_TABLE_SIZE:X}"
        )
    digest = sha256(rows)
    if digest != PFD_ALT_TABLE_SHA256:
        raise DecryptError(
            "PFD Alt-table SHA-256 mismatch: expected "
            f"{PFD_ALT_TABLE_SHA256}, got {digest}"
        )
    by_key: dict[int, tuple[int, bytes]] = {}
    for row_index in range(PFD_ALT_ROW_COUNT):
        offset = row_index * PFD_ALT_ROW_SIZE
        key = struct.unpack_from("<I", rows, offset)[0]
        if key in by_key:
            raise DecryptError(
                f"PFD Alt row key 0x{key:08X} is duplicated"
            )
        by_key[key] = (
            row_index,
            bytes(rows[offset + 4:offset + PFD_ALT_ROW_SIZE]),
        )
    if len(by_key) != PFD_ALT_ROW_COUNT:
        raise DecryptError("PFD Alt table did not yield 625 unique rows")
    return by_key


def decode_pfd_alt_record(site_rva: int, row_index: int,
                          encoded: bytes) -> AltFragment | None:
    """Authenticate and decode a candidate row for one executable-text RVA."""
    if len(encoded) != 16:
        raise DecryptError(f"PFD Alt row {row_index} value is not 16 bytes")
    context = (site_rva * PFD_ALT_CONTEXT_MULTIPLIER) & 0xFFFFFFFF
    digest = hashlib.md5(
        struct.pack("<II", site_rva, context), usedforsecurity=False
    ).digest()
    decoded = bytes(
        value ^ digest[4 + (index & 3)]
        for index, value in enumerate(encoded)
    )
    control = decoded[0]
    if (not 1 <= control <= 8
            or decoded[1:3] != b"\0\0"
            or decoded[11] != 0
            or decoded[12:16] != digest[12:16]):
        return None
    window = bytes(value ^ PFD_ALT_WINDOW_XOR for value in decoded[3:11])
    if len(window) != 8:
        raise DecryptError(f"PFD Alt row {row_index} has a truncated code window")
    return AltFragment(site_rva, row_index, control, window)


def plan_pfd_alt_repairs(image: bytes | bytearray,
                         rows: bytes) -> tuple[AltFragment, ...]:
    """Build a complete fail-closed Alt repair plan without mutating ``image``."""
    text = CORE_SECTIONS[0]
    text_start = text.virtual_address
    text_end = text_start + text.virtual_size
    if text_end > len(image):
        raise DecryptError("main image is truncated before the executable-text end")
    by_key = pfd_alt_row_map(rows)

    typed: list[tuple[AltFragment, bytes]] = []
    active: list[AltFragment] = []
    typed_rows: set[int] = set()
    active_rows: set[int] = set()
    for site_rva in range(text_start, text_end):
        context = (site_rva * PFD_ALT_CONTEXT_MULTIPLIER) & 0xFFFFFFFF
        digest = hashlib.md5(
            struct.pack("<II", site_rva, context), usedforsecurity=False
        ).digest()
        selected = by_key.get(int.from_bytes(digest[:4], "big"))
        if selected is None:
            continue
        row_index, encoded = selected
        fragment = decode_pfd_alt_record(site_rva, row_index, encoded)
        if fragment is None:
            continue
        if site_rva + 8 > text_end:
            raise DecryptError(
                f"PFD Alt row {row_index} code window crosses the .text boundary"
            )
        if row_index in typed_rows:
            raise DecryptError(
                f"PFD Alt row {row_index} authenticates more than one text RVA"
            )
        typed_rows.add(row_index)
        current = bytes(image[site_rva:site_rva + fragment.control])
        typed.append((fragment, current))
        if current == b"\xCC" * fragment.control:
            if row_index in active_rows:
                raise DecryptError(f"PFD Alt row {row_index} is selected twice")
            active_rows.add(row_index)
            active.append(fragment)

    if len(typed) != PFD_ALT_TYPED_RECORD_COUNT:
        raise DecryptError(
            f"PFD Alt authenticated-text count is {len(typed)}; expected "
            f"{PFD_ALT_TYPED_RECORD_COUNT}"
        )
    inactive = [entry for entry in typed if entry[0] not in active]
    if len(inactive) != PFD_ALT_INACTIVE_RECORD_COUNT:
        raise DecryptError(
            f"PFD Alt inactive-record count is {len(inactive)}; expected "
            f"{PFD_ALT_INACTIVE_RECORD_COUNT}"
        )
    inactive_shapes: dict[tuple[int, bytes, bytes], int] = {}
    for fragment, current in inactive:
        shape = (
            fragment.control,
            current[:2],
            fragment.window[:2],
        )
        inactive_shapes[shape] = inactive_shapes.get(shape, 0) + 1
    expected_inactive_shapes = {
        (7, b"\xCC\xCC", b"\xCC\xCC"): 176,
        (7, b"\xCC\x90", b"\x90\x90"): 2,
    }
    if inactive_shapes != expected_inactive_shapes:
        raise DecryptError("PFD Alt inactive padding-row shapes changed")

    if len(active) != PFD_ALT_FRAGMENT_COUNT:
        raise DecryptError(
            f"PFD Alt active fragment count is {len(active)}; expected "
            f"{PFD_ALT_FRAGMENT_COUNT}"
        )
    control_histogram: dict[int, int] = {}
    occupied: set[int] = set()
    for fragment in active:
        control_histogram[fragment.control] = (
            control_histogram.get(fragment.control, 0) + 1
        )
        span = set(range(
            fragment.site_rva,
            fragment.site_rva + fragment.control,
        ))
        if occupied & span:
            raise DecryptError(
                f"PFD Alt fragment at RVA 0x{fragment.site_rva:X} overlaps "
                "another fragment"
            )
        occupied.update(span)
    if control_histogram != PFD_ALT_CONTROL_HISTOGRAM:
        raise DecryptError(
            f"PFD Alt control histogram is {control_histogram}; expected "
            f"{PFD_ALT_CONTROL_HISTOGRAM}"
        )
    if len(occupied) != PFD_ALT_PATCH_BYTE_COUNT:
        raise DecryptError(
            f"PFD Alt patch span is {len(occupied)} bytes; expected "
            f"{PFD_ALT_PATCH_BYTE_COUNT}"
        )

    # Count the maximal CC runs in the untouched input, not merely adjacent
    # records.  Multiple authenticated fragments can occupy one original run.
    touched_runs: set[int] = set()
    for fragment in active:
        start = fragment.site_rva
        while start > text_start and image[start - 1] == 0xCC:
            start -= 1
        touched_runs.add(start)
    if len(touched_runs) != PFD_ALT_TOUCHED_RUN_COUNT:
        raise DecryptError(
            f"PFD Alt touched-CC-run count is {len(touched_runs)}; expected "
            f"{PFD_ALT_TOUCHED_RUN_COUNT}"
        )
    return tuple(active)


def restore_pfd_alt_cc_holes(image: bytearray,
                             sources: RuntimeRepairSources) -> int:
    """Restore all authenticated PFD Alt fragments atomically."""
    repairs = plan_pfd_alt_repairs(image, sources.pfd_alt_rows)
    apply_pfd_alt_repair_plan(image, repairs)
    return len(repairs)


def apply_pfd_alt_repair_plan(image: bytearray,
                              repairs: tuple[AltFragment, ...]) -> None:
    """Validate every target, then apply an already authenticated plan."""
    text = CORE_SECTIONS[0]
    text_start = text.virtual_address
    text_end = text_start + text.virtual_size
    occupied: set[int] = set()
    for fragment in repairs:
        if (not text_start <= fragment.site_rva < text_end
                or fragment.site_rva + fragment.control > text_end
                or fragment.site_rva + fragment.control > len(image)):
            raise DecryptError(
                f"PFD Alt fragment RVA 0x{fragment.site_rva:X} is out of bounds"
            )
        if (not 1 <= fragment.control <= 8
                or len(fragment.window) != 8):
            raise DecryptError(
                f"PFD Alt fragment RVA 0x{fragment.site_rva:X} is malformed"
            )
        span = set(range(
            fragment.site_rva,
            fragment.site_rva + fragment.control,
        ))
        if occupied & span:
            raise DecryptError(
                f"PFD Alt fragment RVA 0x{fragment.site_rva:X} overlaps "
                "another write"
            )
        occupied.update(span)
        current = image[
            fragment.site_rva:fragment.site_rva + fragment.control
        ]
        if current != b"\xCC" * fragment.control:
            raise DecryptError(
                f"PFD Alt fragment RVA 0x{fragment.site_rva:X} target changed"
            )

    # No image mutation occurs until every span has passed the checks above.
    for fragment in repairs:
        replacement = fragment.window[:fragment.control]
        image[
            fragment.site_rva:fragment.site_rva + fragment.control
        ] = replacement
    for fragment in repairs:
        if (image[fragment.site_rva:fragment.site_rva + fragment.control]
                != fragment.window[:fragment.control]):
            raise DecryptError(
                f"PFD Alt fragment RVA 0x{fragment.site_rva:X} did not materialize"
            )


def manifest_import_key(manifest: dict, protected_image: bytes,
                        image: bytes) -> bytes | None:
    """Select the unique live-captured key attested by all three vectors."""
    records = manifest.get("thug2_import_vectors")
    if records is None or records == []:
        return None
    if not isinstance(records, list):
        raise DecryptError("checkpoint thug2_import_vectors is not a list")

    parsed: list[tuple[dict, bytes]] = []
    for index, record in enumerate(records):
        if not isinstance(record, dict):
            raise DecryptError(f"checkpoint import-vector record {index} is not an object")
        required = ("count", "data_ptr", "key_ptr", "key_hex",
                    "input_sha256", "output_sha256")
        if any(field not in record for field in required):
            raise DecryptError(
                f"checkpoint import-vector record {index} is incomplete"
            )
        if not all(isinstance(record[field], int)
                   for field in ("count", "data_ptr", "key_ptr")):
            raise DecryptError(
                f"checkpoint import-vector record {index} has invalid numeric fields"
            )
        try:
            key = bytes.fromhex(record["key_hex"])
        except (TypeError, ValueError) as exc:
            raise DecryptError(
                f"checkpoint import-vector record {index} has invalid key_hex"
            ) from exc
        if len(key) != 16:
            raise DecryptError(
                f"checkpoint import-vector record {index} key is not 16 bytes"
            )
        for field in ("input_sha256", "output_sha256"):
            value = record[field]
            if (not isinstance(value, str) or len(value) != 64
                    or any(char not in "0123456789abcdef" for char in value.lower())):
                raise DecryptError(
                    f"checkpoint import-vector record {index} has invalid {field}"
                )
        parsed.append((record, key))

    vectors = protected_import_vectors(protected_image)
    expected_by_count = {spec.count: encoded for spec, encoded in vectors}
    if len(expected_by_count) != len(vectors):
        raise DecryptError("protected import-vector counts are not unique")

    # A CJump retry can observe the same 0x454C6 call more than once.  Collapse
    # byte-identical attestations, but reject an unexpected vector or any retry
    # that disagrees about its input, key, or pre-scatter output.
    attestations: dict[int, set[tuple[str, bytes, str]]] = {
        count: set() for count in expected_by_count
    }
    for record, key in parsed:
        count = record["count"]
        encoded = expected_by_count.get(count)
        if encoded is None:
            raise DecryptError(
                f"checkpoint attests unexpected import-vector count {count}"
            )
        encoded_bytes = struct.pack(f"<{len(encoded)}I", *encoded)
        input_digest = sha256(encoded_bytes)
        if record["input_sha256"].lower() != input_digest:
            raise DecryptError(
                f"checkpoint import-vector count {count} does not match the "
                "protected executable"
            )
        decoded = decode_import_vector_pairs(encoded, key)
        decoded_digest = sha256(struct.pack(f"<{len(decoded)}I", *decoded))
        if record["output_sha256"].lower() != decoded_digest:
            raise DecryptError(
                f"checkpoint import-vector count {count} output hash is inconsistent"
            )
        attestations[count].add((input_digest, key, decoded_digest))

    for count, records_for_count in attestations.items():
        if len(records_for_count) != 1:
            raise DecryptError(
                f"checkpoint does not uniquely attest import-vector count {count}"
            )

    candidate_keys = {
        next(iter(records_for_count))[1]
        for records_for_count in attestations.values()
    }
    if len(candidate_keys) != 1:
        raise DecryptError(
            "checkpoint does not attest exactly one import key across all three "
            f"protected vectors (found {len(candidate_keys)})"
        )
    key = next(iter(candidate_keys))
    recovered = decoded_protected_import_vectors(
        protected_image, key, len(image)
    )
    if sum(len(entries) for _spec, entries in recovered) != 30:
        raise DecryptError("checkpoint import-vector attestation is not 30 entries")
    return key


def require_manifest_import_key(manifest: dict, protected_image: bytes,
                                image: bytes,
                                diagnostic_key: bytes | None = None) -> bytes:
    """Require a live-attested key; an explicit key can only cross-check it."""
    captured_key = manifest_import_key(manifest, protected_image, image)
    if captured_key is None:
        raise DecryptError(
            "checkpoint does not contain live import-vector key attestations; "
            "--import-key-hex cannot substitute for missing runtime evidence"
        )
    if diagnostic_key is not None and diagnostic_key != captured_key:
        raise DecryptError(
            "diagnostic --import-key-hex disagrees with the live key attested "
            "by checkpoint.json"
        )
    return captured_key


def validate_checkpoint_manifest(data: bytes, memory_data: bytes) -> dict:
    """Attest that a raw main.runtime.bin was captured at the game OEP.

    A checkpoint is needed because a flat runtime snapshot retains the protected
    PE header's original SafeDisc entry RVA.  Unlike ``--dump``, it has not had
    that header rewritten to the game OEP.  The earlier SecServ return at
    0x100160B9/EAX=0x258 is not sufficient: runtime evidence shows it precedes
    import-name restoration, stxt repairs, and redirect removal.  Accept only
    the exact OEP stop reasons produced by safedisc_emu.py.  The byte-level
    import and residue gates still run afterwards; this manifest never
    substitutes for validating the image itself.
    """
    try:
        manifest = json.loads(data.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        raise DecryptError(f"checkpoint manifest is not valid JSON: {exc}") from exc
    if not isinstance(manifest, dict) or manifest.get("format") != 2:
        raise DecryptError(
            "checkpoint manifest format is not 2; legacy checkpoints do not "
            "attest whether diagnostic register overrides were configured"
        )
    conditional = manifest.get("conditional")
    register_overrides = manifest.get("register_overrides")
    if not isinstance(conditional, bool) or not isinstance(
            register_overrides, list):
        raise DecryptError(
            "checkpoint manifest has no typed conditional-run metadata"
        )
    if conditional or register_overrides:
        raise DecryptError(
            "checkpoint is conditional because diagnostic register overrides "
            "were configured; refusing reconstruction"
        )
    if manifest.get("image_base") != IMAGE_BASE:
        raise DecryptError("checkpoint image base is not 0x00400000")
    if manifest.get("image_size") != RUNTIME_IMAGE_SIZE:
        raise DecryptError(
            f"checkpoint image size is not 0x{RUNTIME_IMAGE_SIZE:X}"
        )
    if len(memory_data) != RUNTIME_IMAGE_SIZE:
        raise DecryptError(
            f"main.runtime.bin size mismatch: checkpoint says 0x{RUNTIME_IMAGE_SIZE:X}, "
            f"file is 0x{len(memory_data):X}"
        )
    memory_digest = sha256(memory_data)
    if manifest.get("main_runtime_sha256") != memory_digest:
        raise DecryptError(
            "checkpoint main_runtime_sha256 does not match main.runtime.bin: "
            f"computed {memory_digest}"
        )

    reason = manifest.get("stop_reason")
    registers = manifest.get("registers")
    if not isinstance(registers, dict):
        raise DecryptError("checkpoint manifest has no register set")
    oep_reasons = {
        f"reached the original entry point at 0x{OEP_ADDRESS:08X}",
        f"reached --stop-at 0x{OEP_ADDRESS:08X}",
    }
    if reason in oep_reasons:
        return manifest
    raise DecryptError(
        f"checkpoint was not captured at game OEP 0x{OEP_ADDRESS:08X}: "
        f"{reason!r}"
    )


def validate_and_normalize_imports(image: bytearray) -> tuple[int, int]:
    """Validate all recovered INTs, then restore each on-disk IAT from its INT."""
    for index, expected in enumerate(IMPORTS):
        at = IMPORT_TABLE_RVA + 20 * index
        original, _timestamp, _forwarders, name_rva, first = struct.unpack_from(
            "<5I", image, at
        )
        if original != expected.original_first_thunk or first != expected.first_thunk:
            raise DecryptError(
                f"import descriptor {index} thunk RVAs are incomplete: expected "
                f"0x{expected.original_first_thunk:X}/0x{expected.first_thunk:X}, "
                f"got 0x{original:X}/0x{first:X}"
            )
        dll = read_ascii_z(image, name_rva)
        if dll != expected.dll:
            raise DecryptError(
                f"import descriptor {index} DLL mismatch: expected {expected.dll!r}, "
                f"got {dll!r}"
            )

        entries: list[int] = []
        for thunk_index in range(expected.count + 1):
            entry = struct.unpack_from("<I", image, original + 4 * thunk_index)[0]
            if entry == 0:
                if thunk_index != expected.count:
                    raise DecryptError(
                        f"{dll} INT ended after {thunk_index} imports; expected "
                        f"{expected.count}"
                    )
                break
            if thunk_index == expected.count:
                raise DecryptError(f"{dll} INT has more than {expected.count} imports")
            if entry & 0x80000000:
                if entry & 0x7FFF0000:
                    raise DecryptError(f"{dll} contains invalid ordinal thunk 0x{entry:08X}")
            else:
                if entry + 2 >= len(image):
                    raise DecryptError(f"{dll} name thunk RVA 0x{entry:X} is outside image")
                read_ascii_z(image, entry + 2)
            entries.append(entry)

        # A memory snapshot may contain resolved emulator stubs in FirstThunk.
        # The disk image must contain the original name/ordinal values so the OS
        # loader can resolve them on the user's machine.
        for thunk_index, entry in enumerate(entries):
            struct.pack_into("<I", image, first + 4 * thunk_index, entry)
        struct.pack_into("<I", image, first + 4 * len(entries), 0)

    terminator = image[
        IMPORT_TABLE_RVA + 20 * len(IMPORTS):
        IMPORT_TABLE_RVA + 20 * (len(IMPORTS) + 1)
    ]
    if terminator != bytes(20):
        raise DecryptError("recovered import descriptor table has no null terminator")
    return len(IMPORTS), sum(item.count for item in IMPORTS)


def rel32_residue(image: bytes | bytearray) -> tuple[list[int], list[int]]:
    text = CORE_SECTIONS[0]
    stxt_hits: list[int] = []
    redirect_hits: list[int] = []
    end = text.virtual_address + text.raw_size - 4
    for rva in range(text.virtual_address, end):
        if image[rva] not in (0xE8, 0xE9):
            continue
        displacement = struct.unpack_from("<i", image, rva + 1)[0]
        target = rva + 5 + displacement
        if STXT_RVA_LO <= target < STXT_RVA_HI:
            stxt_hits.append(rva)
        if image[rva] == 0xE8 and target == SAFE_DISC_REDIRECT_RVA:
            redirect_hits.append(rva)
    return stxt_hits, redirect_hits


def validate_restoration_structure(image: bytearray) -> None:
    """Validate the complete 11-descriptor import-restoration table.

    Each six-byte header is ``(count << 1 | protected, FirstThunk)``.  The
    protected flag is set only for USER32, GDI32, and ADVAPI32; SecServ's
    separate value three counts those encoded vectors, not table records.
    """
    record_count = struct.unpack_from("<H", image, RESTORATION_TABLE_RVA)[0]
    if record_count != RESTORATION_RECORD_COUNT:
        raise DecryptError(
            f"SafeDisc import-restoration table count is {record_count}; expected "
            f"{RESTORATION_RECORD_COUNT} at RVA 0x{RESTORATION_TABLE_RVA:X}"
        )

    cursor = RESTORATION_TABLE_RVA + 2
    protected_records: list[tuple[int, int]] = []
    for index, expected in enumerate(IMPORTS):
        encoded_count, first_thunk = struct.unpack_from("<HI", image, cursor)
        cursor += 6
        thunk_count = encoded_count >> 1
        protected = encoded_count & 1
        expected_protected = int(expected.dll in PROTECTED_IMPORT_SEEDS)
        if (thunk_count, protected, first_thunk) != (
                expected.count, expected_protected, expected.first_thunk):
            raise DecryptError(
                f"SafeDisc restoration record {index} ({expected.dll}) is "
                f"count={thunk_count}, protected={protected}, "
                f"FirstThunk=0x{first_thunk:X}; expected count={expected.count}, "
                f"protected={expected_protected}, "
                f"FirstThunk=0x{expected.first_thunk:X}"
            )
        if protected:
            protected_records.append((index, thunk_count))

    expected_protected_records = [(7, 22), (8, 1), (9, 7)]
    if protected_records != expected_protected_records:
        raise DecryptError(
            "SafeDisc protected import records changed: expected "
            "USER32/GDI32/ADVAPI32 at indices 7/8/9"
        )
    # The serialized payload is one control byte plus one byte for each of the
    # 30 protected thunks.  This makes the exact table span 0x63 bytes.
    payload_end = cursor + 1 + sum(count for _index, count in protected_records)
    if payload_end != RESTORATION_PAYLOAD_END_RVA:
        raise DecryptError(
            f"SafeDisc restoration payload ends at RVA 0x{payload_end:X}; "
            f"expected 0x{RESTORATION_PAYLOAD_END_RVA:X}"
        )

    stxt_hits, redirect_hits = rel32_residue(image)
    if stxt_hits:
        sample = ", ".join(f"0x{IMAGE_BASE + rva:08X}" for rva in stxt_hits[:6])
        raise DecryptError(
            f"{len(stxt_hits)} .text transfer(s) still target SafeDisc stxt sections: "
            f"{sample}"
        )
    if redirect_hits:
        sample = ", ".join(f"0x{IMAGE_BASE + rva:08X}" for rva in redirect_hits[:6])
        raise DecryptError(
            f"{len(redirect_hits)} SafeDisc E8 redirect(s) to 0x00401D79 remain: {sample}"
        )
    validate_data_initializer_array(image)


def validate_data_initializer_array(image: bytes | bytearray) -> None:
    """Validate the game's restored CRT initializer table without an oracle.

    Code at 0x00624A16..0x00624A36 walks the 79 dwords in this exact range,
    skips the leading null, and indirectly calls each of the remaining 78.
    A protected/ciphertext ``.data`` tail therefore cannot pass merely because
    the stxt control-transfer repairs happened to complete.
    """
    if CRT_INITIALIZER_END_RVA > len(image):
        raise DecryptError("checkpoint does not contain the CRT initializer table")
    count = (CRT_INITIALIZER_END_RVA - CRT_INITIALIZER_RVA) // 4
    values = struct.unpack_from(f"<{count}I", image, CRT_INITIALIZER_RVA)
    if values[0] != 0:
        raise DecryptError(
            f"restored CRT initializer sentinel is 0x{values[0]:08X}; expected zero"
        )
    text_lo = IMAGE_BASE + CORE_SECTIONS[0].virtual_address
    text_hi = text_lo + CORE_SECTIONS[0].virtual_size
    invalid = [value for value in values[1:] if not text_lo <= value < text_hi]
    if invalid:
        rendered = ", ".join(f"0x{value:08X}" for value in invalid[:6])
        raise DecryptError(
            f"{len(invalid)} of 78 restored CRT initializer pointers are outside "
            f".text: {rendered}"
        )


def normalize_oracle_padding_gaps(image: bytes | bytearray,
                                  oracle: bytearray) -> None:
    """Exclude only the two unreachable, structurally bounded CD3 gaps."""
    for rva, runtime_gap, before, after in ORACLE_PADDING_GAPS:
        end = rva + len(runtime_gap)
        before_start = rva - len(before)
        after_end = end + len(after)
        if before_start < 0 or after_end > len(image) or after_end > len(oracle):
            raise DecryptError(
                f"optional oracle padding gap RVA 0x{rva:X} is out of bounds"
            )
        if (bytes(image[before_start:rva]) != before
                or bytes(image[rva:end]) != runtime_gap
                or bytes(image[end:after_end]) != after):
            raise DecryptError(
                f"runtime padding gap structure changed at RVA 0x{rva:X}"
            )
        if (bytes(oracle[before_start:rva]) != before
                or bytes(oracle[end:after_end]) != after):
            raise DecryptError(
                f"oracle padding gap boundaries changed at RVA 0x{rva:X}"
            )
        # Validation-only normalization: copy the reconstructed gap into the
        # temporary oracle buffer.  No oracle byte is ever written to output.
        oracle[rva:end] = runtime_gap


def validate_against_oracle(image: bytearray, oracle_data: bytes) -> None:
    """Compare with the no-CD oracle without returning or copying its bytes."""
    digest = sha256(oracle_data)
    if digest != ORACLE_SHA256:
        raise DecryptError(
            f"oracle SHA-256 mismatch: expected {ORACLE_SHA256}, got {digest}"
        )
    try:
        pe = pefile.PE(data=oracle_data, fast_load=False)
        oracle = bytearray(pe.get_memory_mapped_image(max_virtual_address=MIN_MEMORY_SIZE))
    except pefile.PEFormatError as exc:
        raise DecryptError(f"oracle is not the expected PE: {exc}") from exc
    text = CORE_SECTIONS[0]
    start = text.virtual_address
    end = start + text.raw_size
    tags = 0
    cursor = start
    while True:
        cursor = oracle.find(b"RLD!\0", cursor, end)
        if cursor < 0:
            break
        oracle[cursor:cursor + 5] = b"\xCC" * 5
        cursor += 5
        tags += 1
    if tags != 264:
        raise DecryptError(f"oracle padding-tag count mismatch: expected 264, got {tags}")
    normalize_oracle_padding_gaps(image, oracle)

    failures = []
    for section in CORE_SECTIONS:
        lo = section.virtual_address
        hi = lo + section.raw_size
        differing = [offset for offset in range(lo, hi) if image[offset] != oracle[offset]]
        if differing:
            failures.append(
                f"{section.name}: {len(differing):,} bytes differ; first RVA "
                f"0x{differing[0]:X}"
            )
    if failures:
        raise DecryptError("optional oracle validation failed:\n  " + "\n  ".join(failures))


def build_standalone(protected_data: bytes, memory_data: bytes,
                     oracle_data: bytes | None = None,
                     checkpoint_attested: bool = False,
                     import_key: bytes | None = None,
                     repair_sources: RuntimeRepairSources | None = None) -> bytes:
    if (not checkpoint_attested or import_key is None
            or repair_sources is None):
        raise DecryptError(
            "standalone finalization requires a format-2 OEP checkpoint, its "
            "live-attested import key, and matching runtime repair artifacts"
        )
    pe = protected_pe(protected_data)
    required_memory_size = RESTORATION_PAYLOAD_END_RVA
    if len(memory_data) < required_memory_size:
        raise DecryptError(
            f"memory image is too short: need at least 0x{required_memory_size:X} bytes, "
            f"got 0x{len(memory_data):X}"
        )
    if memory_data[:2] != b"MZ":
        raise DecryptError("memory image does not begin with an MZ header")
    try:
        memory_pe = pefile.PE(data=memory_data, fast_load=False)
    except pefile.PEFormatError as exc:
        raise DecryptError(f"memory image is not a PE-layout dump: {exc}") from exc
    if (memory_pe.OPTIONAL_HEADER.AddressOfEntryPoint != ORIGINAL_ENTRY_RVA
            and not checkpoint_attested):
        raise DecryptError(
            "memory image does not attest that the emulator reached the game OEP: "
            f"expected entry RVA 0x{ORIGINAL_ENTRY_RVA:X}, got "
            f"0x{memory_pe.OPTIONAL_HEADER.AddressOfEntryPoint:X}; pass the matching "
            "checkpoint.json when using a raw main.runtime.bin"
        )
    image = bytearray(memory_data[:RUNTIME_IMAGE_SIZE])

    if import_key is not None:
        protected_image = pe.get_memory_mapped_image(
            max_virtual_address=MIN_MEMORY_SIZE
        )
        restored = restore_protected_import_vectors(
            protected_image, image, import_key
        )
        if restored != sum(
            spec.count for spec in IMPORTS if spec.dll in PROTECTED_IMPORT_SEEDS
        ):
            raise DecryptError("internal protected-import count invariant failed")
        # Alt selection authenticates the untouched OEP text, including the
        # inactive padding rows.  Materialize it before the independent FF15,
        # stxt, and redirect repairs alter any executable bytes.
        if restore_pfd_alt_cc_holes(
                image, repair_sources) != PFD_ALT_FRAGMENT_COUNT:
            raise DecryptError("internal PFD Alt repair-count invariant failed")
        if restore_permuted_ff15_operands(
                image, repair_sources, import_key) != SECSERV_IMPORT_FF15_CHANGED:
            raise DecryptError("internal FF15 repair-count invariant failed")
        if restore_stxt_import_calls(image) != 3:
            raise DecryptError("internal stxt import-call repair invariant failed")
        if restore_redirect_dictionary(image, repair_sources) != SECSERV_REDIRECT_COUNT:
            raise DecryptError("internal redirect repair-count invariant failed")

    try:
        descriptors, thunks = validate_and_normalize_imports(image)
    except DecryptError as exc:
        if import_key is None:
            raise DecryptError(
                f"{exc}; pass --import-key-hex with the 16-byte key captured "
                "from the protected loader runtime"
            ) from exc
        raise
    if (descriptors, thunks) != (11, 193):
        raise DecryptError("internal import-count invariant failed")
    validate_restoration_structure(image)
    if oracle_data is not None:
        validate_against_oracle(image, oracle_data)

    output = bytearray(OUTPUT_SIZE)
    output[:pe.OPTIONAL_HEADER.SizeOfHeaders] = protected_data[:pe.OPTIONAL_HEADER.SizeOfHeaders]
    for section in CORE_SECTIONS:
        output[
            section.raw_offset:section.raw_offset + section.raw_size
        ] = image[
            section.virtual_address:section.virtual_address + section.raw_size
        ]

    number_sections_offset = pe.FILE_HEADER.get_field_absolute_offset("NumberOfSections")
    struct.pack_into("<H", output, number_sections_offset, len(CORE_SECTIONS))
    entry_offset = pe.OPTIONAL_HEADER.get_field_absolute_offset("AddressOfEntryPoint")
    image_size_offset = pe.OPTIONAL_HEADER.get_field_absolute_offset("SizeOfImage")
    checksum_offset = pe.OPTIONAL_HEADER.get_field_absolute_offset("CheckSum")
    struct.pack_into("<I", output, entry_offset, ORIGINAL_ENTRY_RVA)
    struct.pack_into("<I", output, image_size_offset, OUTPUT_IMAGE_SIZE)
    struct.pack_into("<I", output, checksum_offset, 0)

    import_directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IMPORT"]
    ]
    iat_directory = pe.OPTIONAL_HEADER.DATA_DIRECTORY[
        pefile.DIRECTORY_ENTRY["IMAGE_DIRECTORY_ENTRY_IAT"]
    ]
    struct.pack_into(
        "<II", output, import_directory.get_file_offset(),
        IMPORT_TABLE_RVA, IMPORT_TABLE_SIZE,
    )
    struct.pack_into("<II", output, iat_directory.get_file_offset(), 0, 0)

    section_table = (
        pe.DOS_HEADER.e_lfanew + 0x18 + pe.FILE_HEADER.SizeOfOptionalHeader
    )
    output[
        section_table + 40 * len(CORE_SECTIONS):
        section_table + 40 * len(pe.sections)
    ] = bytes(40 * (len(pe.sections) - len(CORE_SECTIONS)))
    return bytes(output)


def validate_output(data: bytes) -> tuple[int, int]:
    if len(data) != OUTPUT_SIZE:
        raise DecryptError(
            f"standalone output size mismatch: expected 0x{OUTPUT_SIZE:X}, got "
            f"0x{len(data):X}"
        )
    digest = sha256(data)
    if digest != OUTPUT_SHA256:
        raise DecryptError(
            f"standalone output SHA-256 mismatch: expected {OUTPUT_SHA256}, "
            f"got {digest}"
        )
    try:
        pe = pefile.PE(data=data, fast_load=False)
    except pefile.PEFormatError as exc:
        raise DecryptError(f"standalone output is not a PE: {exc}") from exc
    if pe.FILE_HEADER.NumberOfSections != len(CORE_SECTIONS):
        raise DecryptError("standalone output did not remove both stxt sections")
    if pe.OPTIONAL_HEADER.AddressOfEntryPoint != ORIGINAL_ENTRY_RVA:
        raise DecryptError("standalone output entry point is not the game OEP")
    if pe.OPTIONAL_HEADER.SizeOfImage != OUTPUT_IMAGE_SIZE:
        raise DecryptError("standalone output SizeOfImage is not section-aligned")
    actual_sections = tuple(
        (
            section.Name.rstrip(b"\0").decode("ascii"),
            section.VirtualAddress,
            section.Misc_VirtualSize,
            section.PointerToRawData,
            section.SizeOfRawData,
            section.Characteristics,
        )
        for section in pe.sections
    )
    expected_sections = tuple(
        (
            section.name,
            section.virtual_address,
            section.virtual_size,
            section.raw_offset,
            section.raw_size,
            section.characteristics,
        )
        for section in CORE_SECTIONS
    )
    if actual_sections != expected_sections:
        raise DecryptError("standalone output core section layout changed")

    image = bytearray(pe.get_memory_mapped_image(max_virtual_address=MIN_MEMORY_SIZE))
    descriptors, thunks = validate_and_normalize_imports(image)
    # The standalone deliberately omits stxt, including the runtime import-
    # restoration table.  Only the residue scans apply after section removal.
    stxt_hits, redirect_hits = rel32_residue(image)
    if stxt_hits or redirect_hits:
        raise DecryptError("standalone output retained a SafeDisc control transfer")
    validate_data_initializer_array(image)
    return descriptors, thunks


def write_new_output(path: Path, data: bytes) -> None:
    """Create a completed output exclusively, closing the preflight race."""
    path.parent.mkdir(parents=True, exist_ok=True)
    try:
        with path.open("xb") as output_file:
            output_file.write(data)
    except FileExistsError as exc:
        raise DecryptError(f"refusing to overwrite existing output: {path}") from exc


def decrypt(protected_path: Path, memory_path: Path, output_path: Path,
            oracle_path: Path | None = None,
            checkpoint_path: Path | None = None,
            import_key: bytes | None = None) -> tuple[int, int]:
    for label, path in (
        ("protected executable", protected_path),
        ("runtime memory image", memory_path),
    ):
        if not path.is_file():
            raise DecryptError(f"{label} does not exist: {path}")
    if output_path.exists():
        raise DecryptError(f"refusing to overwrite existing output: {output_path}")
    output_resolved = output_path.resolve()
    if output_resolved in (protected_path.resolve(), memory_path.resolve()):
        raise DecryptError("refusing to overwrite an input")
    if oracle_path is not None and not oracle_path.is_file():
        raise DecryptError(f"oracle does not exist: {oracle_path}")
    if checkpoint_path is None:
        raise DecryptError(
            "runtime finalization requires the matching format-2 checkpoint.json; "
            "an explicit import key is not a substitute"
        )
    if not checkpoint_path.is_file():
        raise DecryptError(f"checkpoint manifest does not exist: {checkpoint_path}")

    memory_data = memory_path.read_bytes()
    checkpoint_manifest = validate_checkpoint_manifest(
        checkpoint_path.read_bytes(), memory_data
    )
    repair_sources = load_runtime_repair_sources(
        checkpoint_path, memory_path, checkpoint_manifest
    )

    protected_data = protected_path.read_bytes()
    pe = protected_pe(protected_data)
    protected_image = pe.get_memory_mapped_image(
        max_virtual_address=MIN_MEMORY_SIZE
    )
    import_key = require_manifest_import_key(
        checkpoint_manifest, protected_image,
        memory_data[:RUNTIME_IMAGE_SIZE], import_key,
    )

    oracle_data = oracle_path.read_bytes() if oracle_path is not None else None
    standalone = build_standalone(
        protected_data, memory_data, oracle_data, True,
        import_key, repair_sources,
    )
    descriptors, thunks = validate_output(standalone)
    write_new_output(output_path, standalone)
    return descriptors, thunks


def preflight_output(protected_path: Path, output_path: Path,
                     oracle_path: Path | None = None) -> None:
    if not protected_path.is_file():
        raise DecryptError(f"protected executable does not exist: {protected_path}")
    protected_pe(protected_path.read_bytes())
    if output_path.exists():
        raise DecryptError(f"refusing to overwrite existing output: {output_path}")
    if output_path.resolve() == protected_path.resolve():
        raise DecryptError("refusing to overwrite the protected executable")
    if oracle_path is not None and not oracle_path.is_file():
        raise DecryptError(f"oracle does not exist: {oracle_path}")


def validate_pipeline_paths(output_path: Path, work_dir: Path) -> None:
    """Prevent the work-directory mkdir from turning the output into a directory."""
    output_resolved = output_path.resolve()
    work_resolved = work_dir.resolve()
    if (work_resolved == output_resolved
            or output_resolved in work_resolved.parents):
        raise DecryptError(
            "work directory must not equal or be contained by the output path"
        )


def run_emulator(protected_path: Path, disc_path: Path,
                 work_dir: Path) -> tuple[Path, Path, Path]:
    """Run the protected loader and tee its complete output into ``work_dir``."""
    if not disc_path.is_file():
        raise DecryptError(f"CD1 BIN does not exist: {disc_path}")
    if work_dir.exists():
        raise DecryptError(
            f"refusing to reuse or overwrite work directory: {work_dir}"
        )
    work_dir.mkdir(parents=True)
    emulator = Path(__file__).resolve().with_name("safedisc_emu.py")
    if not emulator.is_file():
        raise DecryptError(f"SafeDisc emulator does not exist: {emulator}")

    log_path = work_dir / "safedisc_emu.log"
    command = [
        sys.executable, "-u", str(emulator), str(protected_path.resolve()),
        "--disc", str(disc_path.resolve()),
        "--thug2-retail-disc-profile",
        "--thug2-sd3-key-repair",
        "--fake-secdrv",
        "--max-instructions", str(PIPELINE_MAX_INSTRUCTIONS),
        "--stop-at", f"{OEP_ADDRESS:08X}",
        "--trail", "128",
        "--dump-temp-files", str(work_dir.resolve()),
    ]
    rendered = subprocess.list2cmdline(command)
    print(f"work directory: {work_dir}")
    print(f"emulator log: {log_path}")
    print(f"running: {rendered}")
    with log_path.open("x", encoding="utf-8", newline="") as log_file:
        log_file.write(f"command: {rendered}\n")
        log_file.flush()
        process: subprocess.Popen[str] | None = None
        try:
            process = subprocess.Popen(
                command,
                cwd=Path.cwd(),
                stdout=subprocess.PIPE,
                stderr=subprocess.STDOUT,
                text=True,
                encoding="utf-8",
                errors="replace",
                bufsize=1,
            )
            assert process.stdout is not None
            for line in process.stdout:
                print(line, end="", flush=True)
                log_file.write(line)
                log_file.flush()
            return_code = process.wait()
        except KeyboardInterrupt:
            if process is not None:
                process.terminate()
                process.wait()
            raise DecryptError(
                f"emulation interrupted; preserved diagnostics in {work_dir}"
            ) from None
    if return_code != 0:
        raise DecryptError(
            f"SafeDisc emulator exited with status {return_code}; see {log_path}"
        )

    memory_path = work_dir / "main.runtime.bin"
    checkpoint_path = work_dir / "checkpoint.json"
    for label, path in (("runtime main image", memory_path),
                        ("checkpoint manifest", checkpoint_path)):
        if not path.is_file():
            raise DecryptError(
                f"emulator produced no {label}; preserved diagnostics in {work_dir}"
            )
    return memory_path, checkpoint_path, log_path


def run_pipeline(protected_path: Path, disc_path: Path, output_path: Path,
                 work_dir: Path, oracle_path: Path | None = None) -> tuple[int, int]:
    validate_pipeline_paths(output_path, work_dir)
    preflight_output(protected_path, output_path, oracle_path)
    memory_path, checkpoint_path, _log_path = run_emulator(
        protected_path, disc_path, work_dir
    )
    try:
        return decrypt(
            protected_path, memory_path, output_path, oracle_path,
            checkpoint_path,
        )
    except (OSError, DecryptError, pefile.PEFormatError, struct.error) as exc:
        raise DecryptError(
            f"runtime finalization failed: {exc}; preserved diagnostics in {work_dir}"
        ) from exc


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("protected", type=Path, help="exact protected THUG2.exe")
    parser.add_argument(
        "--disc", type=Path,
        help="CD1 MODE1/2352 BIN; required for the default end-to-end pipeline",
    )
    parser.add_argument(
        "--output", type=Path, required=True,
        help="new standalone executable; an existing path is never overwritten",
    )
    parser.add_argument(
        "--work-dir", type=Path,
        help=(
            "new directory for the checkpoint, extracted stages, and tee log; "
            "default: <output stem>.safedisc-work beside the output"
        ),
    )
    parser.add_argument(
        "--memory", type=Path,
        help=(
            "advanced mode: finalize this existing main.runtime.bin instead of "
            "running the protected loader"
        ),
    )
    parser.add_argument(
        "--checkpoint", type=Path,
        help=(
            "required in --memory mode: format-2 checkpoint.json captured "
            "beside a raw main.runtime.bin at "
            "the game OEP 0062583D; earlier SecServ returns are incomplete"
        ),
    )
    parser.add_argument(
        "--oracle", type=Path,
        help="optional exact CD3 no-CD executable for validation only; no bytes are copied",
    )
    parser.add_argument(
        "--import-key-hex", type=parse_import_key,
        help=(
            "diagnostic cross-check only: must equal the 16-byte SecServ import "
            "key attested by checkpoint.json and cannot replace it"
        ),
    )
    args = parser.parse_args()
    try:
        if args.memory is not None:
            if args.disc is not None or args.work_dir is not None:
                raise DecryptError(
                    "advanced --memory mode cannot be combined with --disc or --work-dir"
                )
            if args.checkpoint is None:
                raise DecryptError(
                    "advanced --memory mode requires --checkpoint checkpoint.json"
                )
            descriptors, thunks = decrypt(
                args.protected, args.memory, args.output, args.oracle,
                args.checkpoint, args.import_key_hex,
            )
            retained_work_dir = None
        else:
            if args.disc is None:
                raise DecryptError(
                    "the end-to-end pipeline requires --disc CD1.bin; use --memory "
                    "for advanced checkpoint finalization"
                )
            if args.checkpoint is not None or args.import_key_hex is not None:
                raise DecryptError(
                    "--checkpoint and --import-key-hex are advanced --memory options"
                )
            retained_work_dir = args.work_dir or (
                args.output.parent / f"{args.output.stem}.safedisc-work"
            )
            descriptors, thunks = run_pipeline(
                args.protected, args.disc, args.output, retained_work_dir,
                args.oracle,
            )
    except (OSError, DecryptError, pefile.PEFormatError, struct.error) as exc:
        parser.error(str(exc))
    print(f"wrote {args.output} ({args.output.stat().st_size:,} bytes)")
    print(
        f"entry RVA 0x{ORIGINAL_ENTRY_RVA:X}; {len(CORE_SECTIONS)} core sections; "
        f"{descriptors} import descriptors / {thunks} thunks"
    )
    print("all exact plaintext and protection-residue gates passed")
    if retained_work_dir is not None:
        print(f"preserved runtime checkpoint and log in {retained_work_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
