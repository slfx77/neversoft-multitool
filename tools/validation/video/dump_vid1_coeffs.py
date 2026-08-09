#!/usr/bin/env python3
"""
Probe THAW GameCube VID1 coefficient decoders against real VIDD frame payloads.

This is diagnostic-only. It ports the two DOL-backed VLC helpers at:
  - 0x802A08B4
  - 0x802A0B64

and lets us try them at arbitrary bit offsets inside a parsed VIDD coded payload.

Usage:
  python tools/validation/video/dump_vid1_coeffs.py <file.vid> --frame 2
  python tools/validation/video/dump_vid1_coeffs.py <file.vid> --frame 2 --offsets 0 8 16 24
"""

from __future__ import annotations

import argparse
import dataclasses
import pathlib
import struct
from functools import lru_cache
from typing import Iterable


FRAME_CHILD_OFFSET = 0x20
VIDD_CUSTOM_HEADER_OFFSET = 4
VIDD_TAIL_SIZE = 8
ESCAPE_CODE = 0x1BFF
CALLER_998F8_A878_BUNDLE = "A"
CALLER_998F8_A878_SCAN = "auto"
CALLER_998F8_A878_NUDGE_BEFORE = 32
CALLER_998F8_A878_NUDGE_AFTER = 32

# The three scan tables copied into the embedded subdecoder from 0x80325EF8.
SCAN_TABLES: dict[str, bytes] = {
    "zigzag": bytes(
        [
            0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
            12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
            35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
            58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
        ]
    ),
    "horizontal": bytes(
        [
            0, 1, 2, 3, 8, 9, 16, 17, 10, 11, 4, 5, 6, 7, 15, 14,
            13, 12, 19, 18, 24, 25, 32, 33, 26, 27, 20, 21, 22, 23, 28, 29,
            30, 31, 34, 35, 40, 41, 48, 49, 42, 43, 36, 37, 38, 39, 44, 45,
            46, 47, 50, 51, 56, 57, 58, 59, 52, 53, 54, 55, 60, 61, 62, 63,
        ]
    ),
    "vertical": bytes(
        [
            0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49,
            41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42, 50, 58, 35, 43,
            51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45,
            53, 61, 22, 30, 7, 15, 23, 31, 38, 46, 54, 62, 39, 47, 55, 63,
        ]
    ),
}

# These bundles come from 0x8029D038:
#   0x80326860 / 0x80326A20 / 0x80326BA0 / 0x80325FB8 / 0x803260B8
#   0x80326D80 / 0x80326F40 / 0x803270C0 / 0x80325FB8 / 0x803260B8
VLC_BUNDLE_A_PRIMARY = (
    0x00E1081, 0x00E1071, 0x00E1061, 0x00E1051, 0x00E00C1, 0x00E00B1, 0x00E00A1, 0x00E0004,
    0x00C1041, 0x00C1041, 0x00C1031, 0x00C1031, 0x00C1021, 0x00C1021, 0x00C1011, 0x00C1011,
    0x00C1001, 0x00C1001, 0x00A2081, 0x00A2081, 0x00A2071, 0x00A2071, 0x00A2061, 0x00A2061,
    0x00A2051, 0x00A2051, 0x00A2041, 0x00A2041, 0x00A2031, 0x00A2031, 0x00A2021, 0x00A2021,
    0x00A2011, 0x00A2011, 0x00A2001, 0x00A2001, 0x0083041, 0x0083041, 0x0083031, 0x0083031,
    0x0083021, 0x0083021, 0x0083011, 0x0083011, 0x00641C1, 0x00641C1, 0x00641B1, 0x00641B1,
    0x00641A1, 0x00641A1, 0x0064191, 0x0064191, 0x0064181, 0x0064181, 0x0064171, 0x0064171,
    0x0064161, 0x0064161, 0x0064151, 0x0064151, 0x0064141, 0x0064141, 0x0064131, 0x0064131,
)
VLC_BUNDLE_A_SECONDARY = (
    0x0140009, 0x0140008, 0x0121181, 0x0121181, 0x0121171, 0x0121171, 0x0121161, 0x0121161,
    0x0121151, 0x0121151, 0x0121141, 0x0121141, 0x0121131, 0x0121131, 0x0121121, 0x0121121,
    0x0121111, 0x0121111, 0x0121101, 0x0121101, 0x01210F1, 0x01210F1, 0x01210E1, 0x01210E1,
)
VLC_BUNDLE_A_TERTIARY = (
    0x0161012, 0x0161012, 0x0161003, 0x0161003, 0x016000B, 0x016000B, 0x016000A, 0x016000A,
    0x01411C1, 0x01411C1, 0x01411C1, 0x01411C1, 0x01411B1, 0x01411B1, 0x01411B1, 0x01411B1,
    0x01411A1, 0x01411A1, 0x01411A1, 0x01411A1, 0x0141191, 0x0141191, 0x0141191, 0x0141191,
    0x0141181, 0x0141181, 0x0141181, 0x0141181, 0x0141171, 0x0141171, 0x0141171, 0x0141171,
)
VLC_BUNDLE_B_PRIMARY = (
    0x00F0401, 0x00F0301, 0x00E0601, 0x00F0501, 0x00E0701, 0x00E0202, 0x00E0103, 0x00E0009,
    0x00D0002, 0x00D0002, 0x00C0501, 0x00C0501, 0x00C0401, 0x00C0401, 0x00C0301, 0x00C0301,
    0x00C0202, 0x00C0202, 0x00B0103, 0x00B0103, 0x00A0701, 0x00A0701, 0x00A0601, 0x00A0601,
    0x00A0501, 0x00A0501, 0x00A0401, 0x00A0401, 0x00A0301, 0x00A0301, 0x00A0202, 0x00A0202,
    0x0081101, 0x0081101, 0x0081001, 0x0081001, 0x00621C1, 0x00621C1, 0x00621B1, 0x00621B1,
    0x00621A1, 0x00621A1, 0x0062191, 0x0062191, 0x0062181, 0x0062181, 0x0062171, 0x0062171,
    0x0062161, 0x0062161, 0x0062151, 0x0062151, 0x0062141, 0x0062141, 0x0062131, 0x0062131,
    0x0062121, 0x0062121, 0x0062111, 0x0062111, 0x0062101, 0x0062101, 0x0043021, 0x0043021,
)
VLC_BUNDLE_B_SECONDARY = (
    0x0140012, 0x0140011, 0x0130E01, 0x0130E01, 0x0130D01, 0x0130D01, 0x0130C01, 0x0130C01,
    0x0130B01, 0x0130B01, 0x0130A01, 0x0130A01, 0x0130901, 0x0130901, 0x0130801, 0x0130801,
    0x0130701, 0x0130701, 0x0130601, 0x0130601, 0x0130501, 0x0130501, 0x0130401, 0x0130401,
)
VLC_BUNDLE_B_TERTIARY = (
    0x0170007, 0x0170007, 0x0170006, 0x0170006, 0x0160016, 0x0160016, 0x0160015, 0x0160015,
    0x0150202, 0x0150202, 0x0150202, 0x0150202, 0x0150103, 0x0150103, 0x0150103, 0x0150103,
    0x0140F01, 0x0140F01, 0x0140F01, 0x0140F01, 0x0140E01, 0x0140E01, 0x0140E01, 0x0140E01,
    0x0140D01, 0x0140D01, 0x0140D01, 0x0140D01, 0x0140C01, 0x0140C01, 0x0140C01, 0x0140C01,
)


@lru_cache(maxsize=1)
def load_dol_vlc_tables() -> dict[str, tuple[int, ...]] | None:
    repo_root = pathlib.Path(__file__).resolve().parents[3]
    try:
        from disasm_dol_ppc import DolImage
    except Exception:
        try:
            import importlib.util
            import sys

            helper_path = repo_root / "tools" / "reverse-engineering" / "ppc" / "disasm_dol_ppc.py"
            spec = importlib.util.spec_from_file_location("viddiag_disasm_dol_ppc", helper_path)
            if spec is None or spec.loader is None:
                return None

            module = importlib.util.module_from_spec(spec)
            sys.modules[spec.name] = module
            spec.loader.exec_module(module)
            DolImage = module.DolImage
        except Exception:
            return None

    candidates = (
        repo_root / "TestOutput" / "vid_gc_system_manual" / "main.dol",
        repo_root / "TestOutput" / "sample_generator_gc_system" / "main.dol",
        repo_root / "Sample" / "Builds" / "Tony Hawk's American Wasteland (2005-8-22, GC - Final)" / "System" / "main.dol",
    )

    for dol_path in candidates:
        if not dol_path.exists():
            continue

        try:
            dol = DolImage(dol_path)

            def read_table(address: int, size_in_bytes: int) -> tuple[int, ...]:
                entry_count = size_in_bytes // 4
                data = dol.read(address, size_in_bytes)
                if len(data) != size_in_bytes:
                    raise ValueError(f"short DOL read at 0x{address:X}")
                return struct.unpack(">" + ("I" * entry_count), data)

            def read_bytes(address: int, size_in_bytes: int) -> bytes:
                data = dol.read(address, size_in_bytes)
                if len(data) != size_in_bytes:
                    raise ValueError(f"short DOL read at 0x{address:X}")
                return data

            return {
                "A_PRIMARY": read_table(0x80326860, 0x1C0),
                "A_SECONDARY": read_table(0x80326A20, 0x180),
                "A_TERTIARY": read_table(0x80326BA0, 0x1E0),
                "B_PRIMARY": read_table(0x80326D80, 0x1C0),
                "B_SECONDARY": read_table(0x80326F40, 0x180),
                "B_TERTIARY": read_table(0x803270C0, 0x1E0),
                "CORRECTION_64": read_bytes(0x80325FB8, 0x100),
                "CORRECTION_256": read_bytes(0x803260B8, 0x400),
            }
        except Exception:
            continue

    return None


if (dol_vlc_tables := load_dol_vlc_tables()) is not None:
    VLC_BUNDLE_A_PRIMARY = dol_vlc_tables["A_PRIMARY"]
    VLC_BUNDLE_A_SECONDARY = dol_vlc_tables["A_SECONDARY"]
    VLC_BUNDLE_A_TERTIARY = dol_vlc_tables["A_TERTIARY"]
    VLC_BUNDLE_B_PRIMARY = dol_vlc_tables["B_PRIMARY"]
    VLC_BUNDLE_B_SECONDARY = dol_vlc_tables["B_SECONDARY"]
    VLC_BUNDLE_B_TERTIARY = dol_vlc_tables["B_TERTIARY"]

C214_LUMA_TABLE = (
    0x00000000,
    0x00060004,
    0x00060003,
    0x00060000,
    0x00040002,
    0x00040002,
    0x00040001,
    0x00040001,
)
CORRECTION_64 = bytes([27, 10, 5, 4, 3, 3, 3, 3, 2, 2, 1, 1, 1, 1, 1, 0])
CORRECTION_256 = bytes([0, 14, 9, 7, 3, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0])

if dol_vlc_tables is not None:
    CORRECTION_64 = dol_vlc_tables["CORRECTION_64"]
    CORRECTION_256 = dol_vlc_tables["CORRECTION_256"]

# Compact range encodings of the inter/intra macroblock header VLCs:
#   0x8029CE58
#   0x8029CE08
#   0x8029CDA0
CE58_RANGES = (
    (0, 0, 0xFFFFFFFF),
    (1, 1, 0x001200FF),
    (2, 2, 0x00120034),
    (3, 3, 0x00120024),
    (4, 4, 0x00120014),
    (5, 5, 0x00120031),
    (6, 7, 0x00100023),
    (8, 9, 0x00100013),
    (10, 11, 0x00100032),
    (12, 15, 0x000E0033),
    (16, 19, 0x000E0022),
    (20, 23, 0x000E0012),
    (24, 27, 0x000E0021),
    (28, 31, 0x000E0011),
    (32, 39, 0x000C0004),
    (40, 47, 0x000C0030),
    (48, 63, 0x000A0003),
    (64, 95, 0x00080020),
    (96, 127, 0x00080010),
    (128, 191, 0x00060002),
    (192, 255, 0x00060001),
    (256, 256, 0x00020000),
)
CE08_RANGES = (
    (0, 0, 0xFFFFFFFF),
    (1, 1, 0x000C0014),
    (2, 2, 0x000C0024),
    (3, 3, 0x000C0034),
    (4, 7, 0x00080004),
    (8, 15, 0x00060013),
    (16, 23, 0x00060023),
    (24, 31, 0x00060033),
    (32, 63, 0x00020003),
    (64, 64, 0xFFFFFFFF),
    (65, 65, 0x001200FF),
    (66, 66, 0x00120034),
    (67, 67, 0x00120024),
    (68, 68, 0x00120014),
    (69, 69, 0x00120031),
    (70, 71, 0x00100023),
    (72, 73, 0x00100013),
    (74, 75, 0x00100032),
    (76, 79, 0x000E0033),
    (80, 83, 0x000E0022),
    (84, 87, 0x000E0012),
    (88, 91, 0x000E0021),
    (92, 95, 0x000E0011),
    (96, 103, 0x000C0004),
    (104, 111, 0x000C0030),
    (112, 127, 0x000A0003),
    (128, 159, 0x00080020),
    (160, 191, 0x00080010),
    (192, 255, 0x00060002),
    (256, 256, 0x00060001),
)
CDA0_RANGES = (
    (0, 1, 0xFFFFFFFF),
    (2, 2, 0x000C0006),
    (3, 3, 0x000C0009),
    (4, 5, 0x000A0008),
    (6, 7, 0x000A0004),
    (8, 9, 0x000A0002),
    (10, 11, 0x000A0001),
    (12, 15, 0x00080000),
    (16, 19, 0x0008000C),
    (20, 23, 0x0008000A),
    (24, 27, 0x0008000E),
    (28, 31, 0x00080005),
    (32, 35, 0x0008000D),
    (36, 39, 0x00080003),
    (40, 43, 0x0008000B),
    (44, 47, 0x00080007),
    (48, 63, 0x0004000F),
)
CC50_LONG_RANGES = (
    (0, 0, 0x00080003),
    (1, 1, 0x0009FFFD),
    (2, 3, 0x00060002),
    (4, 5, 0x0007FFFE),
    (6, 9, 0x00040001),
    (10, 13, 0x0005FFFF),
)
CC50_MID_RANGES = (
    (0, 0, 0x0014000C),
    (1, 1, 0x0015FFF4),
    (2, 2, 0x0014000B),
    (3, 3, 0x0015FFF5),
    (4, 5, 0x0012000A),
    (6, 7, 0x0013FFF6),
    (8, 9, 0x00120009),
    (10, 11, 0x0013FFF7),
    (12, 13, 0x00120008),
    (14, 15, 0x0013FFF8),
    (16, 23, 0x000E0007),
    (24, 31, 0x000FFFF9),
    (32, 39, 0x000E0006),
    (40, 47, 0x000FFFFA),
    (48, 55, 0x000E0005),
    (56, 63, 0x000FFFFB),
    (64, 79, 0x000C0004),
    (80, 95, 0x000DFFFC),
)
CC50_SHORT_RANGES = (
    (0, 0, 0x00180020),
    (1, 1, 0x0019FFE0),
    (2, 2, 0x0018001F),
    (3, 3, 0x0019FFE1),
    (4, 5, 0x0016001E),
    (6, 7, 0x0017FFE2),
    (8, 9, 0x0016001D),
    (10, 11, 0x0017FFE3),
    (12, 13, 0x0016001C),
    (14, 15, 0x0017FFE4),
    (16, 17, 0x0016001B),
    (18, 19, 0x0017FFE5),
    (20, 21, 0x0016001A),
    (22, 23, 0x0017FFE6),
    (24, 25, 0x00160019),
    (26, 27, 0x0017FFE7),
    (28, 31, 0x00140018),
    (32, 35, 0x0015FFE8),
    (36, 39, 0x00140017),
    (40, 43, 0x0015FFE9),
    (44, 47, 0x00140016),
    (48, 51, 0x0015FFEA),
    (52, 55, 0x00140015),
    (56, 59, 0x0015FFEB),
    (60, 63, 0x00140014),
    (64, 67, 0x0015FFEC),
    (68, 71, 0x00140013),
    (72, 75, 0x0015FFED),
    (76, 79, 0x00140012),
    (80, 83, 0x0015FFEE),
    (84, 87, 0x00140011),
    (88, 91, 0x0015FFEF),
    (92, 95, 0x00140010),
    (96, 99, 0x0015FFF0),
    (100, 103, 0x0014000F),
    (104, 107, 0x0015FFF1),
    (108, 111, 0x0014000E),
    (112, 115, 0x0015FFF2),
    (116, 119, 0x0014000D),
    (120, 123, 0x0015FFF3),
)

CEE0_TABLE = (
    0xFFFFFFFF, 0x000C0014, 0x000C0024, 0x000C0034, 0x00080004, 0x00080004, 0x00080004, 0x00080004,
    0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013,
    0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023,
    0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033,
    0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
    0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
    0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
    0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
)

QP_DELTA_TABLE = (-1, -2, 1, 2)


@dataclasses.dataclass(frozen=True)
class Chunk:
    tag: str
    offset: int
    size: int

    @property
    def end(self) -> int:
        return self.offset + self.size


@dataclasses.dataclass(frozen=True)
class ParsedFrame:
    index: int
    tag16: int
    preamble_class: int
    is_partial: bool
    coded_payload: bytes
    quantizer: int
    forward_code: int | None
    backward_code: int | None


@dataclasses.dataclass(frozen=True)
class CoefficientToken:
    kind: str
    last: int
    run: int
    level: int
    bit_offset_before: int
    bit_offset_after: int


@dataclasses.dataclass(frozen=True)
class MacroblockHeader:
    stage: str
    raw_code: int
    mode: int
    bit_offset_after_mode: int
    bit_offset_after_stage: int
    cbpc_high: int | None
    cbpy: int | None
    cbp: int | None
    intra_selector: int | None
    intra_extra_flag: int | None
    error: str | None = None


@dataclasses.dataclass(frozen=True)
class InterBlockPrepassTrace:
    block_index: int
    prepass_width: int
    prepass_value: int | None
    bit_position_after_prepass: int
    coded: bool
    residual_nudge: int | None = None
    residual_scan: str | None = None


@dataclasses.dataclass(frozen=True)
class SimpleInterCandidate:
    caller_cr4: int
    pre_cbp_flag: int | None
    cbp: int
    f_code: int
    feature_flag_enabled: bool
    alternate_postfilter_flag: int | None
    predicted_residual_start_bit: int
    residual_start_bit: int
    residual_nudge: int
    motion_code_x: int
    motion_code_y: int
    motion_delta_x: int
    motion_delta_y: int
    residual_end_bit: int
    decoded_block_indices: tuple[int, ...]
    prepass_trace: tuple[InterBlockPrepassTrace, ...]
    residual_complete: bool


@dataclasses.dataclass(frozen=True)
class CallerControlProbe:
    path: str
    start_bit: int
    end_bit: int
    gate_bit: int
    stage: str
    raw_code: int | None
    macroblock_type: int | None
    control_prefix: int | None
    selector: int | None
    control_word: int | None
    feature_bit: int | None
    pre_cbp_flag: int | None
    qdelta_index: int | None
    qdelta: int | None
    quantizer: int | None


@dataclasses.dataclass(frozen=True)
class A878BlockProbe:
    block_index: int
    coded: bool
    prepass_width: int
    prepass_value: int | None
    bit_position_after_prepass: int
    origin_model_family: int | None = None
    origin_model_class: int | None = None
    origin_model_base0: int | None = None
    residual_setup: "A878BlockResidualSetup | None" = None
    residual_bundle: str | None = None
    residual_start_bit: int | None = None
    residual_end_bit: int | None = None
    residual_nudge: int | None = None
    residual_scan: str | None = None
    residual_token_count: int | None = None
    residual_error: str | None = None


@dataclasses.dataclass(frozen=True)
class A878MacroblockProbe:
    caller: CallerControlProbe
    blocks: tuple[A878BlockProbe, ...]
    end_bit: int
    error: str | None = None


@dataclasses.dataclass(frozen=True)
class A878BlockResidualSetup:
    prepass_width: int
    prepass_value: int | None
    bit_position_after_prepass: int
    predictor_scale: int | None = None
    effective_class_hint: str | None = None
    predictor_class_raw: int | None = None
    predictor_layout: str | None = None
    predictor_pivot_source: str | None = None
    predictor_family_one_source: str | None = None
    predictor_family_two_source: str | None = None
    residual_bundle: str | None = None
    residual_scan: str | None = None
    residual_nudge: int | None = None
    initial_coefficient_index: int = 1


@dataclasses.dataclass(frozen=True)
class A044cOriginModelState:
    family: int
    predictor_class: int
    base0: int


_A878_PROBE_CACHE: dict[tuple[int, int, int, str | None, str, int, int], A878MacroblockProbe] = {}
_CALLER_998F8_BEST_CACHE: dict[
    tuple[int, int, int, int, int, int, str, str, int, int],
    CallerControlProbe | None,
] = {}
_CALLER_998F8_SEQUENCE_CACHE: dict[tuple[int, int, int, int, int, int, str, str, int, int], int] = {}


def frame_cache_key(frame: ParsedFrame) -> tuple[int, int, int]:
    return (id(frame.coded_payload), len(frame.coded_payload), frame.quantizer)


class BitReader:
    def __init__(self, data: bytes, bit_position: int = 0):
        self._data = data
        self._bit_position = bit_position

    def clone(self) -> "BitReader":
        return BitReader(self._data, self._bit_position)

    def peek_bits(self, bit_count: int) -> int:
        reader = self.clone()
        return reader.read_bits(bit_count)

    def skip_bits(self, bit_count: int) -> None:
        self.read_bits(bit_count)

    def read_bits(self, bit_count: int) -> int:
        if bit_count < 0 or self._bit_position + bit_count > len(self._data) * 8:
            raise EOFError("bitstream is truncated")

        value = 0
        for _ in range(bit_count):
            byte_index = self._bit_position >> 3
            bit_index = 7 - (self._bit_position & 7)
            value = (value << 1) | ((self._data[byte_index] >> bit_index) & 1)
            self._bit_position += 1

        return value

    def align_to_next_byte(self) -> None:
        if self._bit_position & 7:
            self._bit_position += 8 - (self._bit_position & 7)

    @property
    def bit_position(self) -> int:
        return self._bit_position


def read_be_u16(data: bytes, offset: int) -> int:
    return struct.unpack_from(">H", data, offset)[0]


def read_be_u32(data: bytes, offset: int) -> int:
    return struct.unpack_from(">I", data, offset)[0]


def lookup_range_table(ranges: tuple[tuple[int, int, int], ...], index: int) -> int:
    for start, end, value in ranges:
        if start <= index <= end:
            return value
    raise IndexError(f"table index {index} out of range")


def read_chunk(data: bytes, offset: int, limit: int | None = None) -> Chunk:
    if limit is None:
        limit = len(data)

    if offset + 8 > limit:
        raise ValueError(f"truncated chunk header at 0x{offset:X}")

    size = read_be_u32(data, offset + 4)
    if size < 8:
        raise ValueError(f"invalid chunk size 0x{size:X} at 0x{offset:X}")

    end = offset + size
    if end > limit or end > len(data):
        raise ValueError(f"chunk at 0x{offset:X} extends beyond input")

    return Chunk(data[offset : offset + 4].decode("ascii", "replace"), offset, size)


def is_zero_padding(data: bytes, start: int, end: int) -> bool:
    return all(byte == 0 for byte in data[start:end])


def parse_matrix(reader: BitReader) -> None:
    while True:
        if reader.read_bits(8) == 0:
            return


def parse_vidd_frames(data: bytes, first_frame_offset: int) -> list[ParsedFrame]:
    frames: list[ParsedFrame] = []
    offset = first_frame_offset
    sprite_point_count: int | None = None

    while offset + 8 <= len(data):
        if is_zero_padding(data, offset, len(data)):
            break

        chunk = read_chunk(data, offset)
        if chunk.tag != "FRAM":
            break

        child_offset = chunk.offset + FRAME_CHILD_OFFSET
        while child_offset + 8 <= chunk.end:
            child = read_chunk(data, child_offset, chunk.end)
            child_offset = child.end
            if child.tag != "VIDD":
                continue

            payload = data[child.offset + 8 : child.end]
            is_partial = False
            coded_payload = b""
            quantizer = 0
            forward_code: int | None = None
            backward_code: int | None = None
            tag16 = read_be_u16(payload, 6)

            try:
                if len(payload) < VIDD_CUSTOM_HEADER_OFFSET + VIDD_TAIL_SIZE:
                    raise EOFError("VIDD payload is too small")

                bitstream = payload[VIDD_CUSTOM_HEADER_OFFSET:-VIDD_TAIL_SIZE]
                reader = BitReader(bitstream)

                reader.read_bits(16)
                preamble_class = reader.read_bits(2)
                has_optional_header = reader.read_bits(1)
                if has_optional_header:
                    sprite_config_present = reader.read_bits(1)
                    if sprite_config_present:
                        sprite_point_count = reader.read_bits(2)
                        reader.read_bits(2)

                    uses_custom_quant_matrices = reader.read_bits(1)
                    if uses_custom_quant_matrices:
                        if reader.read_bits(1):
                            parse_matrix(reader)
                        if reader.read_bits(1):
                            parse_matrix(reader)

                    state_flag_3c = reader.read_bits(1)
                    reader.read_bits(1)
                else:
                    state_flag_3c = 0

                reader.read_bits(1)
                reader.read_bits(3)
                quantizer = reader.read_bits(5)

                if preamble_class != 0:
                    forward_code = reader.read_bits(3)
                if preamble_class == 2:
                    backward_code = reader.read_bits(3)

                if preamble_class == 2:
                    reader.read_bits(32)
                else:
                    reader.read_bits(32)

                if preamble_class == 3 and sprite_point_count is not None:
                    for _ in range(sprite_point_count):
                        reader.read_bits(14)
                        reader.read_bits(1)
                        reader.read_bits(14)
                        reader.read_bits(1)

                if state_flag_3c:
                    reader.read_bits(1)
                    reader.read_bits(1)

                reader.align_to_next_byte()
                coded_data_offset = VIDD_CUSTOM_HEADER_OFFSET + (reader.bit_position // 8)
                coded_data_end = len(payload) - VIDD_TAIL_SIZE
                if coded_data_offset > coded_data_end:
                    raise EOFError("VIDD coded payload is truncated")

                coded_payload = payload[coded_data_offset:coded_data_end]
            except EOFError:
                preamble_class = -1
                is_partial = True

            frames.append(
                ParsedFrame(
                    index=len(frames),
                    tag16=tag16,
                    preamble_class=preamble_class,
                    is_partial=is_partial,
                    coded_payload=coded_payload,
                    quantizer=quantizer,
                    forward_code=forward_code,
                    backward_code=backward_code,
                )
            )

        offset = chunk.end

    return frames


def parse_vid1(path: pathlib.Path) -> list[ParsedFrame]:
    data = path.read_bytes()
    root = read_chunk(data, 0)
    if root.tag != "VID1":
        raise ValueError(f"{path} is not a VID1 file")

    head = read_chunk(data, root.end)
    if head.tag != "HEAD":
        raise ValueError("HEAD chunk not found after VID1 root")

    return parse_vidd_frames(data, head.end)


def decode_bundle_a(reader: BitReader) -> CoefficientToken:
    bit_offset_before = reader.bit_position
    entry = decode_primary_entry(reader, VLC_BUNDLE_A_PRIMARY, VLC_BUNDLE_A_SECONDARY, VLC_BUNDLE_A_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    if token != ESCAPE_CODE:
        last = (token >> 16) & 0x1
        run = (token >> 8) & 0xFF
        level = token & 0xFF
        if reader.read_bits(1) != 0:
            level = -level

        return CoefficientToken("A-direct", last, run, level, bit_offset_before, reader.bit_position)

    escape_mode = reader.peek_bits(2)
    if escape_mode == 3:
        reader.skip_bits(2)
        last = reader.read_bits(1)
        run = reader.read_bits(6)
        reader.skip_bits(1)
        level = reader.read_bits(12)
        reader.skip_bits(1)
        if level & 0x800:
            level |= -0x1000
        return CoefficientToken("A-escape12", last, run, level, bit_offset_before, reader.bit_position)

    reader.skip_bits(2 if escape_mode == 2 else 1)
    entry = decode_primary_entry(reader, VLC_BUNDLE_A_PRIMARY, VLC_BUNDLE_A_SECONDARY, VLC_BUNDLE_A_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    last = (token >> 16) & 0x1
    run = (token >> 8) & 0xFF
    level = token & 0xFF

    if escape_mode < 2:
        level += CORRECTION_64[run + (last << 6)]
        kind = "A-escape64"
    else:
        level_index = level + (last << 8)
        run += 1 + CORRECTION_256[level_index]
        kind = "A-escape256"

    if reader.read_bits(1) != 0:
        level = -level

    return CoefficientToken(kind, last, run, level, bit_offset_before, reader.bit_position)


def decode_bundle_b(reader: BitReader) -> CoefficientToken:
    bit_offset_before = reader.bit_position
    entry = decode_primary_entry(reader, VLC_BUNDLE_B_PRIMARY, VLC_BUNDLE_B_SECONDARY, VLC_BUNDLE_B_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    if token != ESCAPE_CODE:
        last = (token >> 12) & 0x1
        run = (token >> 4) & 0xFF
        level = token & 0xF
        if reader.read_bits(1) != 0:
            level = -level

        return CoefficientToken("B-direct", last, run, level, bit_offset_before, reader.bit_position)

    escape_mode = reader.peek_bits(2)
    if escape_mode == 3:
        reader.skip_bits(2)
        last = reader.read_bits(1)
        run = reader.read_bits(6)
        reader.skip_bits(1)
        level = reader.read_bits(12)
        reader.skip_bits(1)
        if level & 0x800:
            level |= -0x1000
        return CoefficientToken("B-escape12", last, run, level, bit_offset_before, reader.bit_position)

    reader.skip_bits(2 if escape_mode == 2 else 1)
    entry = decode_primary_entry(reader, VLC_BUNDLE_B_PRIMARY, VLC_BUNDLE_B_SECONDARY, VLC_BUNDLE_B_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    last = (token >> 12) & 0x1
    run = (token >> 4) & 0xFF
    level = token & 0xF

    correction_index_base = last + 2
    if escape_mode < 2:
        level += CORRECTION_64[run + (correction_index_base << 6)]
        kind = "B-escape64"
    else:
        level_index = level + (correction_index_base << 8)
        run += 1 + CORRECTION_256[level_index]
        kind = "B-escape256"

    if reader.read_bits(1) != 0:
        level = -level

    return CoefficientToken(kind, last, run, level, bit_offset_before, reader.bit_position)


def decode_bundle_b8(reader: BitReader) -> CoefficientToken:
    bit_offset_before = reader.bit_position
    entry = decode_primary_entry(reader, VLC_BUNDLE_B_PRIMARY, VLC_BUNDLE_B_SECONDARY, VLC_BUNDLE_B_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    if token != ESCAPE_CODE:
        last = (token >> 16) & 0x1
        run = (token >> 8) & 0xFF
        level = token & 0xFF
        if reader.read_bits(1) != 0:
            level = -level

        return CoefficientToken("B8-direct", last, run, level, bit_offset_before, reader.bit_position)

    escape_mode = reader.peek_bits(2)
    if escape_mode == 3:
        reader.skip_bits(2)
        last = reader.read_bits(1)
        run = reader.read_bits(6)
        reader.skip_bits(1)
        level = reader.read_bits(12)
        reader.skip_bits(1)
        if level & 0x800:
            level |= -0x1000
        return CoefficientToken("B8-escape12", last, run, level, bit_offset_before, reader.bit_position)

    reader.skip_bits(2 if escape_mode == 2 else 1)
    entry = decode_primary_entry(reader, VLC_BUNDLE_B_PRIMARY, VLC_BUNDLE_B_SECONDARY, VLC_BUNDLE_B_TERTIARY)
    token = entry & 0x1FFFF
    reader.skip_bits(entry >> 17)

    last = (token >> 16) & 0x1
    run = (token >> 8) & 0xFF
    level = token & 0xFF

    if escape_mode < 2:
        level += CORRECTION_64[run + (last << 6)]
        kind = "B8-escape64"
    else:
        level_index = level + (last << 8)
        run += 1 + CORRECTION_256[level_index]
        kind = "B8-escape256"

    if reader.read_bits(1) != 0:
        level = -level

    return CoefficientToken(kind, last, run, level, bit_offset_before, reader.bit_position)


def decode_primary_entry(reader: BitReader, table_a: tuple[int, ...], table_b: tuple[int, ...], table_c: tuple[int, ...]) -> int:
    prefix12 = reader.read_bits(12)
    if prefix12 >= 0x200:
        return table_a[(prefix12 >> 5) - 0x10]
    if prefix12 >= 0x80:
        return table_b[(prefix12 >> 2) - 0x20]
    if prefix12 < 8:
        return 0x00E1BFF
    return table_c[prefix12 - 8]


def decode_ce58(reader: BitReader) -> int:
    while True:
        lookahead = reader.peek_bits(9)
        if lookahead != 1:
            break
        reader.skip_bits(9)

    if lookahead > 0x100:
        lookahead = 0x100

    entry = lookup_range_table(CE58_RANGES, lookahead)
    reader.skip_bits(entry >> 17)
    return entry & 0x1FFFF


def decode_ce08(reader: BitReader) -> int:
    while True:
        lookahead = reader.peek_bits(9)
        if lookahead != 1:
            break
        reader.skip_bits(9)

    entry = lookup_range_table(CE08_RANGES, lookahead)
    reader.skip_bits(entry >> 17)
    return entry & 0x1FFFF


def decode_cee0(reader: BitReader) -> int:
    while True:
        lookahead = reader.peek_bits(9)
        if lookahead != 1:
            break
        reader.skip_bits(9)

    entry = CEE0_TABLE[lookahead >> 3]
    if entry == 0xFFFFFFFF:
        raise ValueError("CEE0 lookahead hit invalid sentinel")

    reader.skip_bits(entry >> 17)
    return entry & 0x1FFFF


def decode_cda0(reader: BitReader, direct: bool) -> int:
    lookahead = reader.peek_bits(6)
    entry = lookup_range_table(CDA0_RANGES, lookahead)
    reader.skip_bits(entry >> 17)
    symbol = entry & 0xF
    return symbol if direct else (0xF - symbol)


def decode_cc50(reader: BitReader) -> int:
    if reader.read_bits(1) != 0:
        return 0

    lookahead = reader.peek_bits(12)
    if lookahead > 0x1FF:
        entry = lookup_range_table(CC50_LONG_RANGES, (lookahead >> 8) - 2)
    elif lookahead > 0x7F:
        entry = lookup_range_table(CC50_MID_RANGES, (lookahead >> 2) - 0x20)
    else:
        entry = lookup_range_table(CC50_SHORT_RANGES, lookahead - 4)

    reader.skip_bits(entry >> 17)
    value = entry & 0xFFFF
    if value & 0x8000:
        value -= 0x10000
    return value


def decode_cfa4(reader: BitReader, f_code: int) -> tuple[int, int]:
    motion_code = decode_cc50(reader)
    residual_bits = max(f_code - 1, 0)

    if residual_bits == 0 or motion_code == 0:
        return motion_code, motion_code

    residual = reader.read_bits(residual_bits)
    magnitude = ((abs(motion_code) - 1) << residual_bits) + residual + 1
    delta = magnitude if motion_code >= 0 else -magnitude
    return motion_code, delta


def decode_c214(reader: BitReader, luma_block: bool) -> int:
    if luma_block:
        lookahead = reader.peek_bits(11)
        bit_count = 11
        for _ in range(8):
            if lookahead == 1:
                reader.skip_bits(bit_count)
                return bit_count + 1
            lookahead >>= 1
            bit_count -= 1

        entry = C214_LUMA_TABLE[lookahead]
        reader.skip_bits(entry >> 17)
        return entry & 0x1FFFF

    lookahead = reader.peek_bits(12)
    bit_count = 12
    for _ in range(10):
        if lookahead == 1:
            reader.skip_bits(bit_count)
            return bit_count
        lookahead >>= 1
        bit_count -= 1

    return 3 - reader.read_bits(2)


def decode_signed_width(reader: BitReader, bit_count: int) -> int:
    value = reader.read_bits(bit_count)
    if bit_count <= 0:
        return 0

    sign_bit = value >> (bit_count - 1)
    if sign_bit != 0:
        return value

    mask = (1 << bit_count) - 1
    return -(value ^ mask)


def clamp_quantizer(value: int) -> int:
    if value < 1:
        return 1
    if value > 0x1F:
        return 0x1F
    return value


def probe_caller_control_99a38_from_reader(
    reader: BitReader,
    current_quantizer: int,
    caller_cr4: int,
) -> CallerControlProbe:
    start_bit = reader.bit_position
    gate_bit = reader.read_bits(1)

    if gate_bit != 0:
        return CallerControlProbe(
            path="99A38",
            start_bit=start_bit,
            end_bit=reader.bit_position,
            gate_bit=gate_bit,
            stage="special",
            raw_code=None,
            macroblock_type=0x10 if caller_cr4 == 0 else 0x11,
            control_prefix=None,
            selector=None,
            control_word=None,
            feature_bit=None,
            pre_cbp_flag=None,
            qdelta_index=None,
            qdelta=None,
            quantizer=current_quantizer,
        )

    raw_code = decode_ce58(reader)
    macroblock_type = raw_code & 0x7
    control_prefix = raw_code >> 4

    if macroblock_type in (3, 4):
        feature_bit = reader.read_bits(1)
        selector = decode_cda0(reader, True)
        qdelta_index: int | None = None
        qdelta: int | None = None
        quantizer = current_quantizer
        if macroblock_type == 4:
            qdelta_index = reader.read_bits(2)
            qdelta = QP_DELTA_TABLE[qdelta_index]
            quantizer = clamp_quantizer(current_quantizer + qdelta)

        return CallerControlProbe(
            path="99A38",
            start_bit=start_bit,
            end_bit=reader.bit_position,
            gate_bit=gate_bit,
            stage="a878",
            raw_code=raw_code,
            macroblock_type=macroblock_type,
            control_prefix=control_prefix,
            selector=selector,
            control_word=control_prefix | (selector << 2),
            feature_bit=feature_bit,
            pre_cbp_flag=None,
            qdelta_index=qdelta_index,
            qdelta=qdelta,
            quantizer=quantizer,
        )

    pre_cbp_flag: int | None = None
    if caller_cr4 != 0 and (control_prefix & 0x10) == 0:
        pre_cbp_flag = reader.read_bits(1)
    selector = decode_cda0(reader, False)
    qdelta_index = None
    qdelta = None
    quantizer = current_quantizer
    if macroblock_type == 1:
        qdelta_index = reader.read_bits(2)
        qdelta = QP_DELTA_TABLE[qdelta_index]
        quantizer = clamp_quantizer(current_quantizer + qdelta)

    return CallerControlProbe(
        path="99A38",
        start_bit=start_bit,
        end_bit=reader.bit_position,
        gate_bit=gate_bit,
        stage="motion",
        raw_code=raw_code,
        macroblock_type=macroblock_type,
        control_prefix=control_prefix,
        selector=selector,
        control_word=control_prefix | (selector << 2),
        feature_bit=None,
        pre_cbp_flag=pre_cbp_flag,
        qdelta_index=qdelta_index,
        qdelta=qdelta,
        quantizer=quantizer,
    )


def probe_caller_control_998f8_from_reader(
    reader: BitReader,
    current_quantizer: int,
) -> CallerControlProbe:
    start_bit = reader.bit_position
    raw_code = decode_cee0(reader)
    macroblock_type = raw_code & 0x7
    control_prefix = raw_code >> 4
    gate_bit = reader.read_bits(1)
    selector = decode_cda0(reader, True)

    qdelta_index: int | None = None
    qdelta: int | None = None
    quantizer = current_quantizer
    if macroblock_type == 4:
        qdelta_index = reader.read_bits(2)
        qdelta = QP_DELTA_TABLE[qdelta_index]
        quantizer = clamp_quantizer(current_quantizer + qdelta)

    stage = "a878" if macroblock_type in (3, 4) else "other"
    return CallerControlProbe(
        path="998F8",
        start_bit=start_bit,
        end_bit=reader.bit_position,
        gate_bit=gate_bit,
        stage=stage,
        raw_code=raw_code,
        macroblock_type=macroblock_type,
        control_prefix=control_prefix,
        selector=selector,
        control_word=control_prefix | (selector << 2),
        feature_bit=gate_bit,
        pre_cbp_flag=None,
        qdelta_index=qdelta_index,
        qdelta=qdelta,
        quantizer=quantizer,
    )


def probe_a878_from_998f8_start(
    frame: ParsedFrame,
    start_bit: int,
    residual_bundle: str | None = None,
    residual_scan: str = "auto",
    residual_nudge_before: int = 0,
    residual_nudge_after: int = 0,
) -> A878MacroblockProbe:
    caller = probe_caller_control_998f8_from_reader(
        BitReader(frame.coded_payload, start_bit),
        frame.quantizer,
    )
    if caller.stage != "a878":
        return A878MacroblockProbe(caller=caller, blocks=(), end_bit=caller.end_bit, error="caller path is not a878")

    reader = BitReader(frame.coded_payload, caller.end_bit)
    origin_model_states = simulate_origin_a044c_model(caller.quantizer, caller.feature_bit) if start_bit == 0 else ()
    blocks: list[A878BlockProbe] = []
    try:
        for block_index in range(6):
            prepass_width, prepass_value = consume_inter_block_prepass(reader, block_index)
            bit_position_after_prepass = reader.bit_position
            residual_setup = build_a878_block_residual_setup(
                block_index,
                prepass_width,
                prepass_value,
                bit_position_after_prepass,
                caller.quantizer,
                caller.feature_bit,
                residual_bundle,
            )
            coded = False
            if caller.control_word is not None:
                coded = (caller.control_word & (1 << (5 - block_index))) != 0
            residual_start_bit: int | None = None
            residual_end_bit: int | None = None
            chosen_residual_nudge: int | None = None
            chosen_residual_scan: str | None = None
            residual_token_count: int | None = None
            residual_error: str | None = None

            if coded and residual_bundle is not None:
                residual_start_bit = reader.bit_position
                if residual_scan == "auto" or residual_nudge_before > 0 or residual_nudge_after > 0:
                    (
                        residual_start_bit,
                        chosen_residual_nudge,
                        chosen_residual_scan,
                    ) = select_block_decode_start_window(
                        frame.coded_payload,
                        residual_start_bit,
                        residual_setup.residual_bundle,
                        residual_scan,
                        residual_nudge_before,
                        residual_nudge_after,
                        residual_setup.initial_coefficient_index,
                        residual_start_bit,
                    )
                else:
                    chosen_residual_nudge = 0
                    chosen_residual_scan = residual_scan

                residual_setup = dataclasses.replace(
                    residual_setup,
                    residual_scan=chosen_residual_scan,
                    residual_nudge=chosen_residual_nudge,
                )

                residual_reader = BitReader(frame.coded_payload, residual_start_bit)
                try:
                    _, tokens = decode_block_from_reader(
                        residual_setup.residual_bundle,
                        chosen_residual_scan,
                        residual_reader,
                        residual_setup.initial_coefficient_index,
                    )
                    residual_end_bit = residual_reader.bit_position
                    residual_token_count = len(tokens)
                    reader = BitReader(frame.coded_payload, residual_end_bit)
                except (EOFError, ValueError, IndexError) as error:
                    residual_error = str(error)
                    blocks.append(
                        A878BlockProbe(
                            block_index=block_index,
                            coded=coded,
                            prepass_width=prepass_width,
                            prepass_value=prepass_value,
                            bit_position_after_prepass=bit_position_after_prepass,
                            origin_model_family=origin_model_states[block_index].family if block_index < len(origin_model_states) else None,
                            origin_model_class=origin_model_states[block_index].predictor_class if block_index < len(origin_model_states) else None,
                            origin_model_base0=origin_model_states[block_index].base0 if block_index < len(origin_model_states) else None,
                            residual_setup=residual_setup,
                            residual_bundle=residual_setup.residual_bundle,
                            residual_start_bit=residual_start_bit,
                            residual_end_bit=residual_reader.bit_position,
                            residual_nudge=chosen_residual_nudge,
                            residual_scan=chosen_residual_scan,
                            residual_token_count=None,
                            residual_error=residual_error,
                        )
                    )
                    return A878MacroblockProbe(
                        caller=caller,
                        blocks=tuple(blocks),
                        end_bit=residual_reader.bit_position,
                        error=f"block {block_index} residual decode failed: {residual_error}",
                    )

            blocks.append(
                A878BlockProbe(
                    block_index=block_index,
                    coded=coded,
                    prepass_width=prepass_width,
                    prepass_value=prepass_value,
                    bit_position_after_prepass=bit_position_after_prepass,
                    origin_model_family=origin_model_states[block_index].family if block_index < len(origin_model_states) else None,
                    origin_model_class=origin_model_states[block_index].predictor_class if block_index < len(origin_model_states) else None,
                    origin_model_base0=origin_model_states[block_index].base0 if block_index < len(origin_model_states) else None,
                    residual_setup=residual_setup,
                    residual_bundle=None if not coded else residual_setup.residual_bundle,
                    residual_start_bit=residual_start_bit,
                    residual_end_bit=residual_end_bit,
                    residual_nudge=chosen_residual_nudge,
                    residual_scan=chosen_residual_scan,
                    residual_token_count=residual_token_count,
                    residual_error=residual_error,
                )
            )
    except (EOFError, ValueError, IndexError) as error:
        return A878MacroblockProbe(
            caller=caller,
            blocks=tuple(blocks),
            end_bit=reader.bit_position,
            error=str(error),
        )

    return A878MacroblockProbe(
        caller=caller,
        blocks=tuple(blocks),
        end_bit=reader.bit_position,
    )


def get_cached_a878_probe_from_998f8_start(
    frame: ParsedFrame,
    start_bit: int,
    residual_bundle: str | None = None,
    residual_scan: str = "auto",
    residual_nudge_before: int = 0,
    residual_nudge_after: int = 0,
) -> A878MacroblockProbe:
    key = (
        *frame_cache_key(frame),
        start_bit,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    cached = _A878_PROBE_CACHE.get(key)
    if cached is not None:
        return cached

    probe = probe_a878_from_998f8_start(
        frame,
        start_bit,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    _A878_PROBE_CACHE[key] = probe
    return probe


def resolve_a878_residual_bundle(residual_bundle: str, block_index: int) -> str:
    # Experimental heuristic only. The DOL-backed A878 path calls 0x802A08B4,
    # so the real decoder model remains bundle A until we prove otherwise.
    if residual_bundle == "mixed":
        return "A" if block_index < 4 else "B"
    return residual_bundle


def compute_a878_predictor_scale(current_quantizer: int, block_index: int) -> int:
    is_luma_block = block_index <= 3
    quantizer = current_quantizer & 0xFF

    if quantizer <= 4:
        return 8

    if quantizer <= 0x18 and not is_luma_block:
        return (quantizer + 0x0D) >> 1

    if quantizer <= 8:
        return quantizer << 1

    if quantizer <= 0x18:
        return quantizer + 8

    if is_luma_block:
        return (quantizer << 1) - 0x10

    return quantizer - 6


def compute_a878_effective_class_hint(feature_bit: int | None) -> str:
    if feature_bit == 0:
        return "forced_zero"
    if feature_bit == 1:
        return "dynamic_1_or_2"
    return "unknown"


def compute_a878_predictor_class_raw(feature_bit: int | None) -> int | None:
    if feature_bit == 0:
        return 0
    return None


def describe_a044c_predictor_layout(block_index: int) -> tuple[str, str, str, str]:
    if block_index == 0:
        return ("left.block1|top.block2|diag.block3", "diag.block3.base0", "top.block2.family1", "left.block1.family2")
    if block_index == 1:
        return ("current.block0|top.block3|top.block2", "top.block2.base0", "top.block3.family1", "current.block0.family2")
    if block_index == 2:
        return ("left.block3|current.block0|left.block1", "left.block1.base0", "current.block0.family1", "left.block3.family2")
    if block_index == 3:
        return ("current.block2|current.block1|current.block0", "current.block0.base0", "current.block1.family1", "current.block2.family2")
    if block_index == 4:
        return ("left.block4|top.block4|diag.block4", "diag.block4.base0", "top.block4.family1", "left.block4.family2")
    return ("left.block5|top.block5|diag.block5", "diag.block5.base0", "top.block5.family1", "left.block5.family2")


def round_div_signed(value: int, divisor: int) -> int:
    if divisor == 0:
        raise ValueError("divisor must be non-zero")

    half = divisor >> 1
    adjusted = value + half if value >= 0 else value - half
    return int(adjusted / divisor)


def simulate_origin_a044c_model(
    current_quantizer: int | None,
    feature_bit: int | None,
) -> tuple[A044cOriginModelState, ...]:
    if current_quantizer is None:
        return ()

    sentinel_base0 = 0x0400
    current_base0s = [0] * 6
    states: list[A044cOriginModelState] = []

    for block_index in range(6):
        if block_index == 0:
            pivot_base0 = sentinel_base0
            family_one_base0 = sentinel_base0
            family_two_base0 = sentinel_base0
        elif block_index == 1:
            pivot_base0 = sentinel_base0
            family_one_base0 = sentinel_base0
            family_two_base0 = current_base0s[0]
        elif block_index == 2:
            pivot_base0 = sentinel_base0
            family_one_base0 = current_base0s[0]
            family_two_base0 = sentinel_base0
        elif block_index == 3:
            pivot_base0 = current_base0s[0]
            family_one_base0 = current_base0s[1]
            family_two_base0 = current_base0s[2]
        elif block_index == 4:
            pivot_base0 = sentinel_base0
            family_one_base0 = sentinel_base0
            family_two_base0 = sentinel_base0
        else:
            pivot_base0 = sentinel_base0
            family_one_base0 = sentinel_base0
            family_two_base0 = sentinel_base0

        diff_family_two = abs(pivot_base0 - family_two_base0)
        diff_family_one = abs(family_one_base0 - pivot_base0)
        family = 1 if diff_family_two < diff_family_one else 2
        predictor_class = 0 if feature_bit == 0 else family

        selected_base0 = family_one_base0 if family == 1 else family_two_base0
        predictor_scale = compute_a878_predictor_scale(current_quantizer, block_index)
        out_base0 = round_div_signed(selected_base0, predictor_scale)
        current_base0s[block_index] = out_base0 * predictor_scale
        states.append(
            A044cOriginModelState(
                family=family,
                predictor_class=predictor_class,
                base0=current_base0s[block_index],
            )
        )

    return tuple(states)


def build_a878_block_residual_setup(
    block_index: int,
    prepass_width: int,
    prepass_value: int | None,
    bit_position_after_prepass: int,
    current_quantizer: int | None = None,
    feature_bit: int | None = None,
    residual_bundle: str | None = None,
    residual_scan: str | None = None,
    residual_nudge: int | None = None,
) -> A878BlockResidualSetup:
    resolved_bundle = None if residual_bundle is None else resolve_a878_residual_bundle(residual_bundle, block_index)
    (
        predictor_layout,
        predictor_pivot_source,
        predictor_family_one_source,
        predictor_family_two_source,
    ) = describe_a044c_predictor_layout(block_index)
    return A878BlockResidualSetup(
        prepass_width=prepass_width,
        prepass_value=prepass_value,
        bit_position_after_prepass=bit_position_after_prepass,
        predictor_scale=None if current_quantizer is None else compute_a878_predictor_scale(current_quantizer, block_index),
        effective_class_hint=compute_a878_effective_class_hint(feature_bit),
        predictor_class_raw=compute_a878_predictor_class_raw(feature_bit),
        predictor_layout=predictor_layout,
        predictor_pivot_source=predictor_pivot_source,
        predictor_family_one_source=predictor_family_one_source,
        predictor_family_two_source=predictor_family_two_source,
        residual_bundle=resolved_bundle,
        residual_scan=residual_scan,
        residual_nudge=residual_nudge,
    )


def consume_inter_block_prepass(reader: BitReader, block_index: int) -> tuple[int, int | None]:
    width = decode_c214(reader, block_index < 4)
    if width == 0:
        return width, None

    value = decode_signed_width(reader, width)
    if width > 8:
        reader.skip_bits(1)

    return width, value


def try_decode_block_at(
    data: bytes,
    start_bit: int,
    bundle_name: str,
    scan_name: str,
    initial_coefficient_index: int = 0,
) -> tuple[bool, int]:
    if start_bit < 0:
        return False, start_bit

    reader = BitReader(data, start_bit)
    try:
        decode_block_from_reader(bundle_name, scan_name, reader, initial_coefficient_index)
    except (EOFError, ValueError, IndexError):
        return False, reader.bit_position

    return True, reader.bit_position


def iter_residual_scan_names(scan_name: str) -> tuple[str, ...]:
    if scan_name == "auto":
        return ("zigzag", "horizontal", "vertical")

    return (scan_name,)


def select_block_decode_start(
    data: bytes,
    anchor_bit: int,
    bundle_name: str,
    scan_name: str,
    nudge_window: int,
    initial_coefficient_index: int = 0,
) -> tuple[int, int, str]:
    nudge_values = (0,) if nudge_window <= 0 else tuple(range(-nudge_window, nudge_window + 1))
    scan_names = iter_residual_scan_names(scan_name)

    best_start = anchor_bit
    best_nudge = 0
    best_scan = scan_names[0]
    best_score: tuple[int, int, int, int] | None = None
    scan_priority = {"zigzag": 2, "horizontal": 1, "vertical": 0}
    for nudge in nudge_values:
        trial_start = anchor_bit + nudge
        for candidate_scan in scan_names:
            ok, end_bit = try_decode_block_at(
                data,
                trial_start,
                bundle_name,
                candidate_scan,
                initial_coefficient_index,
            )
            score = (
                int(ok),
                end_bit - trial_start,
                -abs(nudge),
                scan_priority.get(candidate_scan, -1),
            )
            if best_score is None or score > best_score:
                best_score = score
                best_start = trial_start
                best_nudge = nudge
                best_scan = candidate_scan

    return best_start, best_nudge, best_scan


def select_block_decode_start_window(
    data: bytes,
    anchor_bit: int,
    bundle_name: str,
    scan_name: str,
    nudge_before: int,
    nudge_after: int,
    initial_coefficient_index: int = 0,
    minimum_start_bit: int | None = None,
) -> tuple[int, int, str]:
    nudge_values = tuple(range(-max(nudge_before, 0), max(nudge_after, 0) + 1))
    if not nudge_values:
        nudge_values = (0,)
    scan_names = iter_residual_scan_names(scan_name)

    best_start = anchor_bit
    best_nudge = 0
    best_scan = scan_names[0]
    best_score: tuple[int, int, int, int] | None = None
    scan_priority = {"zigzag": 2, "horizontal": 1, "vertical": 0}
    for nudge in nudge_values:
        trial_start = anchor_bit + nudge
        if minimum_start_bit is not None and trial_start < minimum_start_bit:
            continue
        for candidate_scan in scan_names:
            ok, end_bit = try_decode_block_at(
                data,
                trial_start,
                bundle_name,
                candidate_scan,
                initial_coefficient_index,
            )
            score = (
                int(ok),
                end_bit - trial_start,
                -abs(nudge),
                scan_priority.get(candidate_scan, -1),
            )
            if best_score is None or score > best_score:
                best_score = score
                best_start = trial_start
                best_nudge = nudge
                best_scan = candidate_scan

    return best_start, best_nudge, best_scan


def decode_macroblock_header_from_reader(reader: BitReader) -> MacroblockHeader:
    raw_code = -1
    mode = -1
    cbpc_high: int | None = None
    bit_offset_after_mode = 0

    try:
        raw_code = decode_ce58(reader)
        mode = raw_code & 0x7
        cbpc_high = raw_code >> 4
        bit_offset_after_mode = reader.bit_position

        if mode in (3, 4):
            intra_extra_flag = reader.read_bits(1)
            intra_selector = decode_cda0(reader, True)
            return MacroblockHeader(
                stage="intra",
                raw_code=raw_code,
                mode=mode,
                bit_offset_after_mode=bit_offset_after_mode,
                bit_offset_after_stage=reader.bit_position,
                cbpc_high=cbpc_high,
                cbpy=None,
                cbp=None,
                intra_selector=intra_selector,
                intra_extra_flag=intra_extra_flag,
            )

        cbpy = decode_cda0(reader, False)
        cbp = (cbpy << 2) | cbpc_high
        return MacroblockHeader(
            stage="inter",
            raw_code=raw_code,
            mode=mode,
            bit_offset_after_mode=bit_offset_after_mode,
            bit_offset_after_stage=reader.bit_position,
            cbpc_high=cbpc_high,
            cbpy=cbpy,
            cbp=cbp,
            intra_selector=None,
            intra_extra_flag=None,
        )
    except (EOFError, IndexError) as error:
        return MacroblockHeader(
            stage="unknown",
            raw_code=raw_code,
            mode=mode,
            bit_offset_after_mode=bit_offset_after_mode,
            bit_offset_after_stage=reader.bit_position,
            cbpc_high=cbpc_high,
            cbpy=None,
            cbp=None,
            intra_selector=None,
            intra_extra_flag=None,
            error=str(error),
        )


def probe_macroblock_header(coded_payload: bytes) -> MacroblockHeader:
    return decode_macroblock_header_from_reader(BitReader(coded_payload))

def format_macroblock_header(header: MacroblockHeader) -> str:
    if header.stage == "inter":
        return (
            "Header:"
            f" stage=inter raw=0x{header.raw_code:X} mode={header.mode}"
            f" cbpc_high={header.cbpc_high} cbpy={header.cbpy} cbp=0x{header.cbp:02X}"
            f" bits={header.bit_offset_after_mode}->{header.bit_offset_after_stage}"
        )

    if header.stage == "intra":
        return (
            "Header:"
            f" stage=intra raw=0x{header.raw_code:X} mode={header.mode}"
            f" cbpc_high={header.cbpc_high} extra={header.intra_extra_flag}"
            f" selector={header.intra_selector}"
            f" bits={header.bit_offset_after_mode}->{header.bit_offset_after_stage}"
        )

    return (
        "Header:"
        f" stage=unknown raw=0x{header.raw_code:X} mode={header.mode}"
        f" bits={header.bit_offset_after_mode}->{header.bit_offset_after_stage}"
        f" error={header.error}"
    )


def format_caller_control(probe: CallerControlProbe) -> str:
    parts = [
        "CallerControl:",
        f"path={probe.path}",
        f"stage={probe.stage}",
        f"gate={probe.gate_bit}",
        f"bits={probe.start_bit}->{probe.end_bit}",
    ]
    if probe.raw_code is not None:
        parts.append(f"raw=0x{probe.raw_code:X}")
    if probe.macroblock_type is not None:
        parts.append(f"type=0x{probe.macroblock_type:X}")
    if probe.control_prefix is not None:
        parts.append(f"prefix=0x{probe.control_prefix:X}")
    if probe.selector is not None:
        parts.append(f"selector={probe.selector}")
    if probe.control_word is not None:
        parts.append(f"control=0x{probe.control_word:X}")
    if probe.feature_bit is not None:
        parts.append(f"feature={probe.feature_bit}")
    if probe.pre_cbp_flag is not None:
        parts.append(f"pre={probe.pre_cbp_flag}")
    if probe.qdelta_index is not None:
        parts.append(f"qidx={probe.qdelta_index}")
    if probe.qdelta is not None:
        parts.append(f"qdelta={probe.qdelta:+d}")
    if probe.quantizer is not None:
        parts.append(f"qp={probe.quantizer}")
    return " ".join(parts)


def format_a878_probe(probe: A878MacroblockProbe) -> str:
    block_parts: list[str] = []
    for block in probe.blocks:
        segment = (
            f"{block.block_index}{'*' if block.coded else ''}:"
            f"{block.prepass_width}/{block.prepass_value}@{block.bit_position_after_prepass}"
        )
        if block.origin_model_class is not None or block.origin_model_family is not None or block.origin_model_base0 is not None:
            segment += (
                f"{'' if block.origin_model_class is None else f' omc{block.origin_model_class}'}"
                f"{'' if block.origin_model_family is None else f' omf{block.origin_model_family}'}"
                f"{'' if block.origin_model_base0 is None else f' omb{block.origin_model_base0}'}"
            )
        if block.residual_start_bit is not None:
            segment += (
                f" r{block.residual_start_bit}->{block.residual_end_bit}"
                f"{'' if block.residual_bundle is None else f' {block.residual_bundle}'}"
                f"{'' if block.residual_setup is None else f' i{block.residual_setup.initial_coefficient_index}'}"
                f"{'' if block.residual_setup is None or block.residual_setup.predictor_scale is None else f' s{block.residual_setup.predictor_scale}'}"
                f"{'' if block.residual_setup is None or block.residual_setup.predictor_class_raw is None else f' rawc{block.residual_setup.predictor_class_raw}'}"
                f"{'' if block.residual_setup is None or block.residual_setup.effective_class_hint is None else f' c{block.residual_setup.effective_class_hint}'}"
                f"{'' if block.residual_setup is None or block.residual_setup.predictor_pivot_source is None or block.residual_setup.predictor_family_one_source is None or block.residual_setup.predictor_family_two_source is None else f' pivot={block.residual_setup.predictor_pivot_source}/f1={block.residual_setup.predictor_family_one_source}/f2={block.residual_setup.predictor_family_two_source}'}"
                f"{'' if block.residual_nudge is None else f' n{block.residual_nudge:+d}'}"
                f"{'' if block.residual_scan is None else f' {block.residual_scan}'}"
            )
        elif block.residual_setup is not None:
            segment += (
                f"{'' if block.residual_setup.predictor_scale is None else f' s{block.residual_setup.predictor_scale}'}"
                f"{'' if block.residual_setup.predictor_class_raw is None else f' rawc{block.residual_setup.predictor_class_raw}'}"
                f"{'' if block.residual_setup.effective_class_hint is None else f' c{block.residual_setup.effective_class_hint}'}"
                f"{'' if block.residual_setup.predictor_pivot_source is None or block.residual_setup.predictor_family_one_source is None or block.residual_setup.predictor_family_two_source is None else f' pivot={block.residual_setup.predictor_pivot_source}/f1={block.residual_setup.predictor_family_one_source}/f2={block.residual_setup.predictor_family_two_source}'}"
            )
        if block.residual_token_count is not None:
            segment += f" t{block.residual_token_count}"
        if block.residual_error is not None:
            segment += f" err={block.residual_error}"
        block_parts.append(segment)
    block_summary = ",".join(block_parts)
    parts = [
        "A878Probe:",
        f"start={probe.caller.start_bit}",
        f"caller_end={probe.caller.end_bit}",
        f"end={probe.end_bit}",
        f"control=0x{probe.caller.control_word:X}" if probe.caller.control_word is not None else "control=None",
        f"qp={probe.caller.quantizer}" if probe.caller.quantizer is not None else "qp=None",
        f"blocks=[{block_summary}]",
    ]
    if probe.error is not None:
        parts.append(f"error={probe.error}")
    return " ".join(parts)


def format_a878_handoffs(probe: A878MacroblockProbe) -> list[str]:
    coded_blocks = [block for block in probe.blocks if block.coded]
    if not coded_blocks:
        return ["A878 handoff: no coded blocks"]
    if len(coded_blocks) == 1:
        block = coded_blocks[0]
        line = (
            "A878 handoff:"
            f" single block={block.block_index}"
            f" anchor={block.bit_position_after_prepass}"
        )
        if block.residual_start_bit is not None:
            line += f" start={block.residual_start_bit}"
            line += f" gap_start={block.residual_start_bit - block.bit_position_after_prepass:+d}"
        if block.residual_end_bit is not None:
            line += f" end={block.residual_end_bit}"
        if block.residual_nudge is not None:
            line += f" nudge={block.residual_nudge:+d}"
        if block.residual_token_count is not None:
            line += f" tokens={block.residual_token_count}"
        if block.residual_error is not None:
            line += f" err={block.residual_error}"
        return [line]

    lines: list[str] = []
    for previous_block, next_block in zip(coded_blocks, coded_blocks[1:]):
        if previous_block.residual_end_bit is None:
            lines.append(
                "A878 handoff:"
                f" {previous_block.block_index}->{next_block.block_index}"
                " previous block has no residual end bit"
            )
            continue

        gap_to_anchor = next_block.bit_position_after_prepass - previous_block.residual_end_bit
        gap_to_start = None if next_block.residual_start_bit is None else next_block.residual_start_bit - previous_block.residual_end_bit
        line = (
            "A878 handoff:"
            f" {previous_block.block_index}->{next_block.block_index}"
            f" prev_end={previous_block.residual_end_bit}"
            f" next_anchor={next_block.bit_position_after_prepass}"
            f" gap_anchor={gap_to_anchor:+d}"
        )
        if next_block.residual_start_bit is not None:
            line += f" next_start={next_block.residual_start_bit}"
        if gap_to_start is not None:
            line += f" gap_start={gap_to_start:+d}"
        if next_block.residual_nudge is not None:
            line += f" nudge={next_block.residual_nudge:+d}"
        if next_block.residual_end_bit is not None:
            line += f" next_end={next_block.residual_end_bit}"
        if next_block.residual_token_count is not None:
            line += f" tokens={next_block.residual_token_count}"
        if next_block.residual_error is not None:
            line += f" err={next_block.residual_error}"
        lines.append(line)
    return lines


def score_a878_probe_viability(probe: A878MacroblockProbe) -> tuple[int, ...]:
    coded_blocks = 0
    successful_blocks = 0
    attempted_span = 0
    deepest_residual_bit = probe.caller.end_bit
    residual_errors = 0
    total_abs_nudge = 0
    residual_tokens = 0

    for block in probe.blocks:
        if not block.coded:
            continue

        coded_blocks += 1
        if block.residual_nudge is not None:
            total_abs_nudge += abs(block.residual_nudge)
        if block.residual_error is not None:
            residual_errors += 1
        if block.residual_start_bit is not None and block.residual_end_bit is not None:
            deepest_residual_bit = max(deepest_residual_bit, block.residual_end_bit)
            attempted_span += max(0, block.residual_end_bit - block.residual_start_bit)
        if block.residual_error is None and block.residual_token_count is not None:
            successful_blocks += 1
            residual_tokens += block.residual_token_count

    return (
        successful_blocks,
        int(coded_blocks > 0),
        int(probe.error is None),
        -residual_errors,
        -total_abs_nudge,
        residual_tokens,
        deepest_residual_bit - probe.caller.end_bit,
        attempted_span,
    )


def iter_caller_998f8_candidates(
    frame: ParsedFrame,
    anchor_bit: int,
    start_jitter: int,
    max_step_bits: int,
) -> list[CallerControlProbe]:
    candidates: list[CallerControlProbe] = []
    for start_bit in range(anchor_bit, anchor_bit + max(start_jitter, 0) + 1):
        try:
            probe = probe_caller_control_998f8_from_reader(
                BitReader(frame.coded_payload, start_bit),
                frame.quantizer,
            )
        except (EOFError, ValueError, IndexError):
            continue

        step_bits = probe.end_bit - probe.start_bit
        if probe.stage != "a878" or step_bits <= 0:
            continue
        if max_step_bits > 0 and step_bits > max_step_bits:
            continue
        candidates.append(probe)

    return candidates


def select_best_caller_998f8_candidate(
    frame: ParsedFrame,
    anchor_bit: int,
    lookahead_depth: int,
    start_jitter: int,
    max_step_bits: int,
    residual_bundle: str = CALLER_998F8_A878_BUNDLE,
    residual_scan: str = CALLER_998F8_A878_SCAN,
    residual_nudge_before: int = CALLER_998F8_A878_NUDGE_BEFORE,
    residual_nudge_after: int = CALLER_998F8_A878_NUDGE_AFTER,
) -> CallerControlProbe | None:
    cache_key = (
        *frame_cache_key(frame),
        anchor_bit,
        lookahead_depth,
        start_jitter,
        max_step_bits,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    if cache_key in _CALLER_998F8_BEST_CACHE:
        return _CALLER_998F8_BEST_CACHE[cache_key]

    best_probe: CallerControlProbe | None = None
    best_rank: tuple[int, ...] | None = None

    for probe in iter_caller_998f8_candidates(frame, anchor_bit, start_jitter, max_step_bits):
        a878_probe = get_cached_a878_probe_from_998f8_start(
            frame,
            probe.start_bit,
            residual_bundle,
            residual_scan,
            residual_nudge_before,
            residual_nudge_after,
        )
        a878_rank = score_a878_probe_viability(a878_probe)
        future_steps = score_caller_998f8_sequence(
            frame,
            probe.end_bit,
            lookahead_depth,
            start_jitter,
            max_step_bits,
            residual_bundle,
            residual_scan,
            residual_nudge_before,
            residual_nudge_after,
        )
        future_span = 0
        if lookahead_depth > 0:
            future = select_best_caller_998f8_candidate(
                frame,
                probe.end_bit,
                lookahead_depth - 1,
                start_jitter,
                max_step_bits,
                residual_bundle,
                residual_scan,
                residual_nudge_before,
                residual_nudge_after,
            )
            if future is not None:
                future_span = future.end_bit - probe.end_bit

        step_bits = probe.end_bit - probe.start_bit
        rank = (
            *a878_rank,
            future_steps,
            -(probe.start_bit - anchor_bit),
            -step_bits,
            -(future_span),
        )
        if best_rank is None or rank > best_rank:
            best_rank = rank
            best_probe = probe

    _CALLER_998F8_BEST_CACHE[cache_key] = best_probe
    return best_probe


def score_caller_998f8_sequence(
    frame: ParsedFrame,
    anchor_bit: int,
    lookahead_depth: int,
    start_jitter: int,
    max_step_bits: int,
    residual_bundle: str = CALLER_998F8_A878_BUNDLE,
    residual_scan: str = CALLER_998F8_A878_SCAN,
    residual_nudge_before: int = CALLER_998F8_A878_NUDGE_BEFORE,
    residual_nudge_after: int = CALLER_998F8_A878_NUDGE_AFTER,
) -> int:
    cache_key = (
        *frame_cache_key(frame),
        anchor_bit,
        lookahead_depth,
        start_jitter,
        max_step_bits,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    if cache_key in _CALLER_998F8_SEQUENCE_CACHE:
        return _CALLER_998F8_SEQUENCE_CACHE[cache_key]

    if lookahead_depth <= 0:
        _CALLER_998F8_SEQUENCE_CACHE[cache_key] = 0
        return 0

    best_probe = select_best_caller_998f8_candidate(
        frame,
        anchor_bit,
        lookahead_depth - 1,
        start_jitter,
        max_step_bits,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    if best_probe is None:
        _CALLER_998F8_SEQUENCE_CACHE[cache_key] = 0
        return 0

    score = 1 + score_caller_998f8_sequence(
        frame,
        best_probe.end_bit,
        lookahead_depth - 1,
        start_jitter,
        max_step_bits,
        residual_bundle,
        residual_scan,
        residual_nudge_before,
        residual_nudge_after,
    )
    _CALLER_998F8_SEQUENCE_CACHE[cache_key] = score
    return score


def decode_block(
    bundle_name: str,
    scan_name: str,
    coded_payload: bytes,
    bit_offset: int,
    initial_coefficient_index: int = 0,
    dc_value: int | None = None,
) -> tuple[list[int], list[CoefficientToken]]:
    reader = BitReader(coded_payload, bit_offset)
    coefficients = [0] * 64
    if dc_value is not None:
        coefficients[0] = dc_value
    tokens: list[CoefficientToken] = []
    scan = SCAN_TABLES[scan_name]
    coefficient_index = initial_coefficient_index

    while True:
        if bundle_name == "A":
            token = decode_bundle_a(reader)
        elif bundle_name == "B8":
            token = decode_bundle_b8(reader)
        else:
            token = decode_bundle_b(reader)
        tokens.append(token)
        coefficient_index += token.run
        if coefficient_index < 64:
            coefficients[scan[coefficient_index]] = token.level
        coefficient_index += 1
        if token.last != 0:
            return coefficients, tokens


def decode_block_from_reader(
    bundle_name: str,
    scan_name: str,
    reader: BitReader,
    initial_coefficient_index: int = 0,
) -> tuple[list[int], list[CoefficientToken]]:
    coefficients = [0] * 64
    tokens: list[CoefficientToken] = []
    scan = SCAN_TABLES[scan_name]
    coefficient_index = initial_coefficient_index

    while True:
        if bundle_name == "A":
            token = decode_bundle_a(reader)
        elif bundle_name == "B8":
            token = decode_bundle_b8(reader)
        else:
            token = decode_bundle_b(reader)
        tokens.append(token)
        coefficient_index += token.run
        if coefficient_index < 64:
            coefficients[scan[coefficient_index]] = token.level
        coefficient_index += 1
        if token.last != 0:
            return coefficients, tokens


def iter_simple_inter_candidates(
    frame: ParsedFrame,
    header: MacroblockHeader,
    macroblock_start_bit: int = 0,
    residual_nudge_before: int = 2,
    residual_nudge_after: int = 4,
    residual_bundle: str = "B",
    residual_scan: str = "zigzag",
    late_block_nudge_window: int = 0,
    caller_cr4_values: tuple[int, ...] = (0,),
) -> list[SimpleInterCandidate]:
    if header.stage != "inter" or header.mode not in (0, 1):
        return []

    candidates: list[SimpleInterCandidate] = []
    raw_forward_code = frame.forward_code or 0
    f_code_values = sorted({max(1, raw_forward_code + bias) for bias in (0, 1, 2)})

    # The long-form THAW path keeps +0x84 disabled, so probe only the non-feature branch.
    feature_flag_enabled = False
    alternate_postfilter_flag = None

    for caller_cr4 in caller_cr4_values:
        base_reader = BitReader(frame.coded_payload, macroblock_start_bit)
        raw_code = decode_ce58(base_reader)
        if raw_code != header.raw_code:
            continue

        mode = raw_code & 0x7
        cbpc_high = raw_code >> 4
        if mode != header.mode:
            continue

        pre_cbp_flag: int | None = None
        if caller_cr4 != 0 and (cbpc_high & 0x10) == 0:
            pre_cbp_flag = base_reader.read_bits(1)
            if pre_cbp_flag != 0:
                continue

        cbpy = decode_cda0(base_reader, False)
        cbp = (cbpy << 2) | cbpc_high
        if mode == 1:
            # Mode 1 adds a 2-bit qscale delta before the optional feature/MV path.
            base_reader.read_bits(2)

        for f_code in f_code_values:
            reader = base_reader.clone()
            motion_code_x, motion_delta_x = decode_cfa4(reader, f_code)
            motion_code_y, motion_delta_y = decode_cfa4(reader, f_code)
            predicted_residual_start_bit = reader.bit_position
            best_start_bit = predicted_residual_start_bit
            best_end_bit = predicted_residual_start_bit
            best_blocks: tuple[int, ...] = ()
            best_prepass_trace: tuple[InterBlockPrepassTrace, ...] = ()
            best_complete = False

            for residual_nudge in range(-residual_nudge_before, residual_nudge_after + 1):
                residual_reader = BitReader(frame.coded_payload, predicted_residual_start_bit)
                first_coded_start_bit = predicted_residual_start_bit
                decoded_blocks: list[int] = []
                prepass_trace: list[InterBlockPrepassTrace] = []
                residual_complete = True
                try:
                    adjusted_first_coded_block = False
                    for block_index in range(6):
                        prepass_width, prepass_value = consume_inter_block_prepass(residual_reader, block_index)
                        is_coded_block = (cbp & (1 << (5 - block_index))) != 0
                        block_residual_nudge: int | None = None
                        prepass_trace.append(
                            InterBlockPrepassTrace(
                                block_index=block_index,
                                prepass_width=prepass_width,
                                prepass_value=prepass_value,
                                bit_position_after_prepass=residual_reader.bit_position,
                                coded=is_coded_block,
                                residual_nudge=block_residual_nudge,
                            )
                        )
                        if is_coded_block:
                            block_scan_name = residual_scan
                            if not adjusted_first_coded_block:
                                first_coded_start_bit = residual_reader.bit_position + residual_nudge
                                if first_coded_start_bit < 0:
                                    raise EOFError("negative first coded residual start")
                                if residual_scan == "auto":
                                    block_start_bit, block_residual_nudge, block_scan_name = select_block_decode_start(
                                        frame.coded_payload,
                                        first_coded_start_bit,
                                        residual_bundle,
                                        residual_scan,
                                        0,
                                    )
                                    first_coded_start_bit = block_start_bit
                                residual_reader = BitReader(frame.coded_payload, first_coded_start_bit)
                                adjusted_first_coded_block = True
                                block_residual_nudge = residual_nudge
                            elif late_block_nudge_window > 0 or residual_scan == "auto":
                                block_start_bit, block_residual_nudge, block_scan_name = select_block_decode_start(
                                    frame.coded_payload,
                                    residual_reader.bit_position,
                                    residual_bundle,
                                    residual_scan,
                                    late_block_nudge_window,
                                )
                                residual_reader = BitReader(frame.coded_payload, block_start_bit)
                            prepass_trace[-1] = dataclasses.replace(
                                prepass_trace[-1],
                                residual_nudge=block_residual_nudge,
                                residual_scan=block_scan_name,
                            )
                            decode_block_from_reader(residual_bundle, block_scan_name, residual_reader)
                            decoded_blocks.append(block_index)
                except (EOFError, ValueError, IndexError):
                    residual_complete = False

                decoded_tuple = tuple(decoded_blocks)
                better_candidate = False
                if residual_complete and not best_complete:
                    better_candidate = True
                elif residual_complete == best_complete and len(decoded_tuple) > len(best_blocks):
                    better_candidate = True
                elif (
                    residual_complete == best_complete
                    and len(decoded_tuple) == len(best_blocks)
                    and abs(residual_nudge) < abs(best_start_bit - predicted_residual_start_bit)
                ):
                    better_candidate = True

                if better_candidate:
                    best_complete = residual_complete
                    best_start_bit = first_coded_start_bit
                    best_end_bit = residual_reader.bit_position
                    best_blocks = decoded_tuple
                    best_prepass_trace = tuple(prepass_trace)

            candidates.append(
                SimpleInterCandidate(
                    caller_cr4=caller_cr4,
                    pre_cbp_flag=pre_cbp_flag,
                    cbp=cbp,
                    f_code=f_code,
                    feature_flag_enabled=feature_flag_enabled,
                    alternate_postfilter_flag=alternate_postfilter_flag,
                    predicted_residual_start_bit=predicted_residual_start_bit,
                    residual_start_bit=best_start_bit,
                    residual_nudge=best_start_bit - predicted_residual_start_bit,
                    motion_code_x=motion_code_x,
                    motion_code_y=motion_code_y,
                    motion_delta_x=motion_delta_x,
                    motion_delta_y=motion_delta_y,
                    residual_end_bit=best_end_bit,
                    decoded_block_indices=best_blocks,
                    prepass_trace=best_prepass_trace,
                    residual_complete=best_complete,
                )
            )

    return candidates


def select_best_simple_inter_candidate(candidates: list[SimpleInterCandidate]) -> SimpleInterCandidate | None:
    if not candidates:
        return None

    return max(
        candidates,
        key=lambda candidate: (
            candidate.residual_complete,
            len(candidate.decoded_block_indices),
            -abs(candidate.residual_nudge),
            -candidate.f_code,
        ),
    )


def score_simple_inter_sequence_candidate(
    frame: ParsedFrame,
    candidate: SimpleInterCandidate,
    macroblock_start_bit: int,
    remaining_depth: int,
    residual_nudge_before: int,
    residual_nudge_after: int,
    residual_bundle: str,
    residual_scan: str,
    late_block_nudge_window: int,
    max_step_bits: int,
    caller_cr4_values: tuple[int, ...],
) -> tuple[int, int, int, int, int]:
    """Score a candidate by how far it keeps the simple-inter walk alive."""
    if max_step_bits > 0 and candidate.residual_end_bit - macroblock_start_bit > max_step_bits:
        return (0, 0, 0, -abs(candidate.residual_nudge), -candidate.f_code)

    local_score = (
        1,
        int(candidate.residual_complete),
        len(candidate.decoded_block_indices),
        -abs(candidate.residual_nudge),
        -candidate.f_code,
    )
    if remaining_depth <= 1 or candidate.residual_end_bit <= candidate.residual_start_bit:
        return local_score

    try:
        next_header = decode_macroblock_header_from_reader(BitReader(frame.coded_payload, candidate.residual_end_bit))
    except (EOFError, ValueError, IndexError):
        return local_score

    if next_header.stage != "inter" or next_header.mode not in (0, 1):
        return local_score

    next_candidates = iter_simple_inter_candidates(
        frame,
        next_header,
        candidate.residual_end_bit,
        residual_nudge_before,
        residual_nudge_after,
        residual_bundle,
        residual_scan,
        late_block_nudge_window,
        caller_cr4_values,
    )
    next_candidate = select_best_simple_inter_sequence_candidate(
        frame,
        next_candidates,
        candidate.residual_end_bit,
        remaining_depth - 1,
        residual_nudge_before,
        residual_nudge_after,
        residual_bundle,
        residual_scan,
        late_block_nudge_window,
        max_step_bits,
        caller_cr4_values,
    )
    if next_candidate is None:
        return local_score

    next_score = score_simple_inter_sequence_candidate(
        frame,
        next_candidate,
        candidate.residual_end_bit,
        remaining_depth - 1,
        residual_nudge_before,
        residual_nudge_after,
        residual_bundle,
        residual_scan,
        late_block_nudge_window,
        max_step_bits,
        caller_cr4_values,
    )
    return (
        local_score[0] + next_score[0],
        local_score[1] + next_score[1],
        local_score[2] + next_score[2],
        local_score[3] + next_score[3],
        local_score[4],
    )


def select_best_simple_inter_sequence_candidate(
    frame: ParsedFrame,
    candidates: list[SimpleInterCandidate],
    macroblock_start_bit: int,
    lookahead_depth: int,
    residual_nudge_before: int,
    residual_nudge_after: int,
    residual_bundle: str,
    residual_scan: str,
    late_block_nudge_window: int,
    max_step_bits: int,
    caller_cr4_values: tuple[int, ...],
) -> SimpleInterCandidate | None:
    if not candidates:
        return None

    return max(
        candidates,
        key=lambda candidate: (
            score_simple_inter_sequence_candidate(
                frame,
                candidate,
                macroblock_start_bit,
                lookahead_depth,
                residual_nudge_before,
                residual_nudge_after,
                residual_bundle,
                residual_scan,
                late_block_nudge_window,
                max_step_bits,
                caller_cr4_values,
            ),
            candidate.residual_end_bit - macroblock_start_bit,
        ),
    )


def select_best_simple_inter_sequence_step(
    frame: ParsedFrame,
    anchor_bit: int,
    lookahead_depth: int,
    residual_nudge_before: int,
    residual_nudge_after: int,
    residual_bundle: str,
    residual_scan: str,
    late_block_nudge_window: int,
    max_step_bits: int,
    start_jitter: int,
    caller_cr4_values: tuple[int, ...],
) -> tuple[int, MacroblockHeader, SimpleInterCandidate] | None:
    best_step: tuple[
        tuple[int, int, int, int, int, int],
        int,
        MacroblockHeader,
        SimpleInterCandidate,
    ] | None = None

    for start_bit in range(anchor_bit, anchor_bit + max(start_jitter, 0) + 1):
        try:
            header = decode_macroblock_header_from_reader(BitReader(frame.coded_payload, start_bit))
        except (EOFError, ValueError, IndexError):
            continue
        if header.stage != "inter" or header.mode not in (0, 1):
            continue

        candidates = iter_simple_inter_candidates(
            frame,
            header,
            start_bit,
            residual_nudge_before,
            residual_nudge_after,
            residual_bundle,
            residual_scan,
            late_block_nudge_window,
            caller_cr4_values,
        )
        candidate = select_best_simple_inter_sequence_candidate(
            frame,
            candidates,
            start_bit,
            lookahead_depth,
            residual_nudge_before,
            residual_nudge_after,
            residual_bundle,
            residual_scan,
            late_block_nudge_window,
            max_step_bits,
            caller_cr4_values,
        )
        if candidate is None:
            continue

        score = score_simple_inter_sequence_candidate(
            frame,
            candidate,
            start_bit,
            lookahead_depth,
            residual_nudge_before,
            residual_nudge_after,
            residual_bundle,
            residual_scan,
            late_block_nudge_window,
            max_step_bits,
            caller_cr4_values,
        )
        rank = (
            score[0],
            score[1],
            score[2],
            score[3],
            score[4],
            -(start_bit - anchor_bit),
        )
        if best_step is None or rank > best_step[0]:
            best_step = (rank, start_bit, header, candidate)

    if best_step is None:
        return None

    return best_step[1], best_step[2], best_step[3]


def iter_offsets(values: list[int] | None, max_bits: int) -> Iterable[int]:
    if values:
        yield from values
        return

    for bit_offset in range(0, min(max_bits, 256), 8):
        yield bit_offset


def format_nonzero(coefficients: list[int]) -> str:
    pairs = [f"{index}:{value}" for index, value in enumerate(coefficients) if value != 0]
    return " ".join(pairs[:16]) if pairs else "(all zero)"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser()
    parser.add_argument("input", type=pathlib.Path)
    parser.add_argument("--frame", type=int, required=True)
    parser.add_argument("--start-bit", type=int, default=0, help="bit offset of the macroblock header inside the coded payload")
    parser.add_argument("--offsets", type=int, nargs="+", help="bit offsets inside coded payload")
    parser.add_argument("--bundle", choices=["A", "B", "B8", "both"], default="both")
    parser.add_argument("--scan", choices=["zigzag", "horizontal", "vertical", "all"], default="all")
    parser.add_argument("--initial-index", type=int, default=0, help="initial coefficient index before residual token decode")
    parser.add_argument("--dc-value", type=int, help="seed coefficient 0 before residual token decode")
    parser.add_argument("--show-header", action="store_true", help="show exact CE58/CDA0 macroblock header decode")
    parser.add_argument("--show-caller-control", action="store_true", help="show the long-form 0x80299A38 caller-side macroblock control probe")
    parser.add_argument("--show-caller-sequence", type=int, metavar="COUNT", help="walk COUNT long-form caller-side macroblocks using the 998F8 a878 probe")
    parser.add_argument("--show-a878-probe", action="store_true", help="show the first-stage a878 block prepass for a 998F8 start bit")
    parser.add_argument("--show-a878-residuals", action="store_true", help="show a878 per-block residual decode attempts for a 998F8 start bit")
    parser.add_argument("--show-a878-handoff", action="store_true", help="show handoff details between coded a878 blocks for a 998F8 start bit")
    parser.add_argument("--a878-bundle", choices=["A", "B", "mixed"], default="A", help="residual VLC bundle to probe from a878 coded blocks; mixed is an experimental heuristic, not the DOL-backed default")
    parser.add_argument("--a878-scan", choices=["zigzag", "horizontal", "vertical", "auto"], default="auto", help="scan order to probe for a878 coded blocks")
    parser.add_argument("--a878-nudge-before", type=int, default=0, metavar="BITS", help="how many bits before each a878 coded-block residual start to probe")
    parser.add_argument("--a878-nudge-after", type=int, default=0, metavar="BITS", help="how many bits after each a878 coded-block residual start to probe")
    parser.add_argument("--caller-path", choices=["99A38", "998F8", "both"], default="99A38", help="caller-side long-form path to probe")
    parser.add_argument("--caller-cr4", choices=["0", "1", "auto"], default="0", help="caller context for the long-form 0x80299A38 control path")
    parser.add_argument("--caller-start-jitter", type=int, default=0, metavar="BITS", help="probe nearby starts when walking the 998F8 caller sequence")
    parser.add_argument("--caller-lookahead", type=int, default=2, metavar="DEPTH", help="lookahead depth for selecting 998F8 caller-sequence starts")
    parser.add_argument("--caller-max-step-bits", type=int, default=32, metavar="BITS", help="reject 998F8 caller steps that advance implausibly far; 0 disables the guard")
    parser.add_argument("--header-only", action="store_true", help="show header decode and skip coefficient probing")
    parser.add_argument("--show-simple-inter", action="store_true", help="probe the simple non-field inter path for modes 0/1")
    parser.add_argument("--show-simple-inter-sequence", type=int, metavar="COUNT", help="walk the first COUNT macroblocks using the simple inter probe")
    parser.add_argument("--simple-inter-lookahead", type=int, default=2, metavar="DEPTH", help="recursive lookahead depth for simple inter sequence scoring")
    parser.add_argument("--simple-inter-start-jitter", type=int, default=0, metavar="BITS", help="probe nearby macroblock starts when walking the simple inter sequence")
    parser.add_argument("--simple-inter-nudge-before", type=int, default=2, metavar="BITS", help="how many bits before the predicted residual start to probe")
    parser.add_argument("--simple-inter-nudge-after", type=int, default=4, metavar="BITS", help="how many bits after the predicted residual start to probe")
    parser.add_argument("--simple-inter-late-block-nudge", type=int, default=0, metavar="BITS", help="probe a local start adjustment for coded blocks after the first one")
    parser.add_argument("--simple-inter-max-step-bits", type=int, default=512, metavar="BITS", help="reject sequence steps that advance implausibly far; 0 disables the guard")
    parser.add_argument("--simple-inter-bundle", choices=["A", "B"], default="B", help="residual VLC bundle to probe")
    parser.add_argument("--simple-inter-scan", choices=["zigzag", "horizontal", "vertical", "auto"], default="zigzag", help="scan order to probe for residual blocks")
    parser.add_argument("--simple-inter-cr4", choices=["0", "1", "auto"], default="0", help="caller context for the modes 0/1/2 pre-CBP branch; auto probes both 0 and 1")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    frames = parse_vid1(args.input)
    frame = frames[args.frame]

    if frame.is_partial:
        raise SystemExit(f"Frame {args.frame} is partial")

    bundles = ["A", "B"] if args.bundle == "both" else [args.bundle]
    scans = ["zigzag", "horizontal", "vertical"] if args.scan == "all" else [args.scan]

    print(
        f"Frame #{frame.index} tag16=0x{frame.tag16:04X} class={frame.preamble_class} "
        f"coded_payload={len(frame.coded_payload)} bytes quantizer={frame.quantizer}"
        f" start_bit={args.start_bit}"
    )

    header = (
        decode_macroblock_header_from_reader(BitReader(frame.coded_payload, args.start_bit))
        if (args.show_header or args.show_simple_inter or args.show_simple_inter_sequence)
        else None
    )
    if args.show_header and header is not None:
        print(format_macroblock_header(header))

    if args.show_caller_control:
        print("Caller control probes:")
        if args.caller_path in ("99A38", "both"):
            caller_cr4_values = (0, 1) if args.caller_cr4 == "auto" else (int(args.caller_cr4),)
            for caller_cr4 in caller_cr4_values:
                probe = probe_caller_control_99a38_from_reader(
                    BitReader(frame.coded_payload, args.start_bit),
                    frame.quantizer,
                    caller_cr4,
                )
                print(f"  cr4={caller_cr4} {format_caller_control(probe)}")
        if args.caller_path in ("998F8", "both"):
            probe = probe_caller_control_998f8_from_reader(
                BitReader(frame.coded_payload, args.start_bit),
                frame.quantizer,
            )
            print(f"  {format_caller_control(probe)}")

    if args.show_caller_sequence is not None:
        if args.caller_path not in ("998F8", "both"):
            print("Caller sequence: requires --caller-path 998F8 or both")
        else:
            print("Caller sequence (998F8):")
            current_bit = args.start_bit
            for index in range(args.show_caller_sequence):
                probe = select_best_caller_998f8_candidate(
                    frame,
                    current_bit,
                    args.caller_lookahead,
                    args.caller_start_jitter,
                    args.caller_max_step_bits,
                    args.a878_bundle,
                    args.a878_scan,
                    args.a878_nudge_before,
                    args.a878_nudge_after,
                )
                if probe is None:
                    print(f"  step={index} start={current_bit} stop=no viable 998F8 candidate")
                    break
                a878_probe = get_cached_a878_probe_from_998f8_start(
                    frame,
                    probe.start_bit,
                    args.a878_bundle,
                    args.a878_scan,
                    args.a878_nudge_before,
                    args.a878_nudge_after,
                )
                a878_rank = score_a878_probe_viability(a878_probe)
                print(
                    f"  step={index}"
                    f" start={probe.start_bit}"
                    f" end={probe.end_bit}"
                    f" step_bits={probe.end_bit - probe.start_bit}"
                    f" type=0x{probe.macroblock_type:X}"
                    f" control=0x{probe.control_word:X}"
                    f" gate={probe.gate_bit}"
                    f" qp={probe.quantizer}"
                    f" a878={a878_rank}"
                )
                if probe.end_bit <= current_bit:
                    print(f"  step={index} stop=non-advancing candidate")
                    break
                current_bit = probe.end_bit

    if args.show_a878_probe or args.show_a878_residuals or args.show_a878_handoff:
        if args.caller_path not in ("998F8", "both"):
            print("A878 probe: requires --caller-path 998F8 or both")
        else:
            probe = get_cached_a878_probe_from_998f8_start(
                frame,
                args.start_bit,
                args.a878_bundle if (args.show_a878_residuals or args.show_a878_handoff) else None,
                args.a878_scan,
                args.a878_nudge_before,
                args.a878_nudge_after,
            )
            if args.show_a878_probe or args.show_a878_residuals:
                print(format_a878_probe(probe))
            if args.show_a878_handoff:
                for line in format_a878_handoffs(probe):
                    print(line)

    if args.header_only and not args.show_simple_inter:
        return 0

    if args.show_simple_inter:
        if header is None:
            print("Simple inter candidates: none")
        else:
            caller_cr4_values = (0, 1) if args.simple_inter_cr4 == "auto" else (int(args.simple_inter_cr4),)
            candidates = iter_simple_inter_candidates(
                frame,
                header,
                args.start_bit,
                args.simple_inter_nudge_before,
                args.simple_inter_nudge_after,
                args.simple_inter_bundle,
                args.simple_inter_scan,
                args.simple_inter_late_block_nudge,
                caller_cr4_values,
            )
            if not candidates:
                print("Simple inter candidates: none")
            else:
                print("Simple inter candidates:")
                for candidate in candidates:
                    prepass_summary = ",".join(
                        f"{trace.block_index}{'*' if trace.coded else ''}:{trace.prepass_width}/{trace.prepass_value}@{trace.bit_position_after_prepass}"
                        f"{'' if trace.residual_nudge is None else f' n{trace.residual_nudge:+d}'}"
                        f"{'' if trace.residual_scan is None else f' {trace.residual_scan}'}"
                        for trace in candidate.prepass_trace
                    )
                    print(
                        "  "
                        f"cr4={candidate.caller_cr4}"
                        f" pre={candidate.pre_cbp_flag}"
                        f" cbp=0x{candidate.cbp:02X}"
                        " "
                        f"f_code={candidate.f_code}"
                        f" raw_forward={frame.forward_code}"
                        " "
                        f"feature={int(candidate.feature_flag_enabled)}"
                        f" alt={candidate.alternate_postfilter_flag}"
                        f" mv_code=({candidate.motion_code_x},{candidate.motion_code_y})"
                        f" mv_delta=({candidate.motion_delta_x},{candidate.motion_delta_y})"
                        f" residual={candidate.predicted_residual_start_bit}->{candidate.residual_start_bit}->{candidate.residual_end_bit}"
                        f" nudge={candidate.residual_nudge:+d}"
                        f" complete={int(candidate.residual_complete)}"
                        f" blocks={candidate.decoded_block_indices}"
                        f" prepass=[{prepass_summary}]"
                    )

    if args.show_simple_inter_sequence:
        caller_cr4_values = (0, 1) if args.simple_inter_cr4 == "auto" else (int(args.simple_inter_cr4),)
        print(
            "Simple inter sequence "
            f"(limit={args.show_simple_inter_sequence}, lookahead={max(args.simple_inter_lookahead, 1)},"
            f" start_jitter={max(args.simple_inter_start_jitter, 0)}):"
        )
        current_bit = args.start_bit
        for macroblock_index in range(args.show_simple_inter_sequence):
            step_header = decode_macroblock_header_from_reader(BitReader(frame.coded_payload, current_bit))
            anchor_candidates: list[SimpleInterCandidate] = []
            anchor_candidate: SimpleInterCandidate | None = None
            if step_header.stage == "inter" and step_header.mode in (0, 1):
                anchor_candidates = iter_simple_inter_candidates(
                    frame,
                    step_header,
                    current_bit,
                    args.simple_inter_nudge_before,
                    args.simple_inter_nudge_after,
                    args.simple_inter_bundle,
                    args.simple_inter_scan,
                    args.simple_inter_late_block_nudge,
                    caller_cr4_values,
                )
                anchor_candidate = select_best_simple_inter_sequence_candidate(
                    frame,
                    anchor_candidates,
                    current_bit,
                    max(args.simple_inter_lookahead, 1),
                    args.simple_inter_nudge_before,
                    args.simple_inter_nudge_after,
                    args.simple_inter_bundle,
                    args.simple_inter_scan,
                    args.simple_inter_late_block_nudge,
                    max(args.simple_inter_max_step_bits, 0),
                    caller_cr4_values,
                )

            step_start_bit = current_bit
            candidate = anchor_candidate
            if (
                args.simple_inter_start_jitter > 0
                and (anchor_candidate is None or not anchor_candidate.residual_complete)
            ):
                jitter_step = select_best_simple_inter_sequence_step(
                    frame,
                    current_bit,
                    max(args.simple_inter_lookahead, 1),
                    args.simple_inter_nudge_before,
                    args.simple_inter_nudge_after,
                    args.simple_inter_bundle,
                    args.simple_inter_scan,
                    args.simple_inter_late_block_nudge,
                    max(args.simple_inter_max_step_bits, 0),
                    args.simple_inter_start_jitter,
                    caller_cr4_values,
                )
                if jitter_step is not None:
                    step_start_bit, step_header, candidate = jitter_step

            print(
                "  "
                f"mb={macroblock_index}"
                f" start={step_start_bit}"
                f" stage={step_header.stage}"
                f" mode={step_header.mode}"
                f" cbp={None if step_header.cbp is None else f'0x{step_header.cbp:02X}'}"
            )
            if step_header.stage != "inter" or step_header.mode not in (0, 1):
                print("    stop: unsupported macroblock for simple inter walker")
                break
            if candidate is None:
                print("    stop: no simple inter candidate")
                break

            print(
                "    "
                f"best f_code={candidate.f_code}"
                f" residual={candidate.predicted_residual_start_bit}->{candidate.residual_start_bit}->{candidate.residual_end_bit}"
                f" nudge={candidate.residual_nudge:+d}"
                f" complete={int(candidate.residual_complete)}"
                f" blocks={candidate.decoded_block_indices}"
            )
            if candidate.prepass_trace:
                prepass_summary = ",".join(
                    f"{trace.block_index}{'*' if trace.coded else ''}:{trace.prepass_width}/{trace.prepass_value}@{trace.bit_position_after_prepass}"
                    f"{'' if trace.residual_nudge is None else f' n{trace.residual_nudge:+d}'}"
                    f"{'' if trace.residual_scan is None else f' {trace.residual_scan}'}"
                    for trace in candidate.prepass_trace
                )
                print(f"      prepass=[{prepass_summary}]")
            if candidate.residual_end_bit <= current_bit:
                print("    stop: candidate did not advance bit position")
                break
            if not candidate.residual_complete:
                print("    note: candidate is partial; continuing with selected end bit")
            current_bit = candidate.residual_end_bit

    if args.header_only:
        return 0

    for bit_offset in iter_offsets(args.offsets, len(frame.coded_payload) * 8):
        for bundle_name in bundles:
            for scan_name in scans:
                try:
                    coefficients, tokens = decode_block(
                        bundle_name,
                        scan_name,
                        frame.coded_payload,
                        bit_offset,
                        args.initial_index,
                        args.dc_value,
                    )
                except (EOFError, ValueError, IndexError):
                    continue

                print(
                    f"\n[{bundle_name}/{scan_name}] bit_offset={bit_offset} "
                    f"tokens={len(tokens)} last_bit={tokens[-1].bit_offset_after}"
                )
                for token in tokens[:12]:
                    print(
                        "  "
                        f"{token.kind} last={token.last} run={token.run} level={token.level} "
                        f"bits={token.bit_offset_before}->{token.bit_offset_after}"
                    )
                print(f"  coeffs: {format_nonzero(coefficients)}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
