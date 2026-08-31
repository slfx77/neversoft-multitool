#!/usr/bin/env python3
"""Pure-CPU, protected-only proof for THUG2 SafeDisc Alt fragments.

The PFD row selector is MD5(rva || rva*0x215D7FC6), with both inputs packed
little-endian.  No plaintext executable or oracle participates in selection or
decoding.  The CD3 image is consulted only for a final validation statistic.
"""
from __future__ import annotations

import collections
import hashlib
import json
import struct
from pathlib import Path

import pefile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
OUT = ROOT / "TestOutput"
# Retained OEP runtime evidence named by docs/backlog/safedisc-emulation-handoff.md.
# Its main.runtime.bin/heap.bin are byte-identical to the historical keyfix7 snapshot.
WORK = OUT / "THUG2_decrypted_end_to_end_v2.safedisc-work"
MAIN = WORK / "main.runtime.bin"
ROWS = HERE / "pfd_query_bb8_3fc.bin"
ORACLE = OUT / "thug2_cd3_crack_oracle.exe"  # optional validation only
OUTPUT = OUT / "pfd_alt_cpu_recovered_main.bin"
MANIFEST = OUT / "pfd_alt_cpu_manifest.json"
REFERENCE_MANIFEST = HERE / "pfd_alt_cpu_manifest.json"

TEXT_LO = 0x1000
TEXT_HI = 0x244ED2
CONTEXT_MULTIPLIER = 0x215D7FC6
EXPECTED_ROWS_SHA256 = "6183dd3faba576114e6987368e9d6e1b0ea811a7821efbdcc76d7c42651dc4f7"


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def mapped_image(path: Path) -> bytes:
    return pefile.PE(str(path)).get_memory_mapped_image()


def main() -> int:
    original = MAIN.read_bytes()
    rows_blob = ROWS.read_bytes()
    if len(rows_blob) != 625 * 20:
        raise ValueError(f"PFD 3FC length is {len(rows_blob)}, expected 12500")
    if sha256(rows_blob) != EXPECTED_ROWS_SHA256:
        raise ValueError("PFD 3FC hash mismatch")

    by_key: dict[int, tuple[int, bytes]] = {}
    for row in range(625):
        key = struct.unpack_from("<I", rows_blob, row * 20)[0]
        if key in by_key:
            raise ValueError(f"duplicate PFD row key {key:08X}")
        by_key[key] = (row, rows_blob[row * 20 + 4:row * 20 + 20])

    records: list[dict[str, object]] = []
    typed_records: list[tuple[int, int, int, bytes, bytes]] = []
    for site in range(TEXT_LO, TEXT_HI):
        context = (site * CONTEXT_MULTIPLIER) & 0xFFFFFFFF
        digest = hashlib.md5(struct.pack("<II", site, context)).digest()
        selected = by_key.get(int.from_bytes(digest[:4], "big"))
        if selected is None:
            continue
        row, encoded = selected
        decoded = bytes(value ^ digest[4 + (index & 3)]
                        for index, value in enumerate(encoded))
        if (decoded[1:3] != b"\0\0" or decoded[11] != 0
                or decoded[12:16] != digest[12:16]
                or not 1 <= decoded[0] <= 8):
            continue
        control = decoded[0]
        patch = bytes(value ^ 0xFA for value in decoded[3:3 + control])
        typed_records.append((site, row, control, patch,
                              original[site:site + control]))
        if original[site:site + control] != b"\xCC" * control:
            continue
        records.append({
            "site_rva": site,
            "row_index": row,
            "context": context,
            "control": control,
            "digest": digest.hex(),
            "patch": patch.hex(),
        })

    sites = [int(item["site_rva"]) for item in records]
    row_indices = [int(item["row_index"]) for item in records]
    if len(sites) != len(set(sites)) or len(row_indices) != len(set(row_indices)):
        raise ValueError("Alt sites/rows are not unique")
    occupied: set[int] = set()
    image = bytearray(original)
    for item in records:
        site = int(item["site_rva"])
        patch = bytes.fromhex(str(item["patch"]))
        span = set(range(site, site + len(patch)))
        if occupied & span:
            raise ValueError(f"overlapping Alt record at RVA {site:08X}")
        occupied |= span
        image[site:site + len(patch)] = patch

    # Count the original maximal CC runs touched by at least one record.
    touched_runs: set[int] = set()
    for site in sites:
        start = site
        while start > TEXT_LO and original[start - 1] == 0xCC:
            start -= 1
        touched_runs.add(start)
    controls = collections.Counter(int(item["control"]) for item in records)

    # Build-specific fail-closed gates established solely by the scan above.
    if len(records) != 113:
        raise ValueError(f"Alt fragment count {len(records)} != 113")
    if len(occupied) != 287:
        raise ValueError(f"Alt patch byte count {len(occupied)} != 287")
    if len(touched_runs) != 89:
        raise ValueError(f"Alt touched-run count {len(touched_runs)} != 89")
    if controls != collections.Counter({2: 92, 6: 12, 3: 8, 7: 1}):
        raise ValueError(f"unexpected Alt control histogram {dict(controls)}")
    inactive = [item for item in typed_records
                if item[4] != b"\xCC" * item[2]]
    if len(typed_records) != 291 or len(inactive) != 178:
        raise ValueError(f"unexpected typed/inactive counts "
                         f"{len(typed_records)}/{len(inactive)}")
    if len({item[1] for item in typed_records}) != len(typed_records):
        raise ValueError("authenticated PFD row is reused in executable text")
    inactive_shapes = collections.Counter(
        (control, current[:2].hex(), patch[:2].hex())
        for _site, _row, control, patch, current in inactive)
    if inactive_shapes != collections.Counter({
            (7, "cccc", "cccc"): 176,
            (7, "cc90", "9090"): 2,
    }):
        raise ValueError(f"unexpected inactive-record shapes {inactive_shapes}")

    manifest = {
        "schema": "thug2-safedisc-alt-pfd3fc-v1",
        "main_input_sha256": sha256(original),
        "pfd_rows_sha256": sha256(rows_blob),
        "text_rva_lo": TEXT_LO,
        "text_rva_hi_exclusive": TEXT_HI,
        "context_multiplier": CONTEXT_MULTIPLIER,
        "fragment_count": len(records),
        "authenticated_text_record_count": len(typed_records),
        "inactive_padding_record_count": len(inactive),
        "unique_row_count": len(set(row_indices)),
        "touched_cc_run_count": len(touched_runs),
        "patch_byte_count": len(occupied),
        "control_histogram": {str(k): v for k, v in sorted(controls.items())},
        "output_sha256": sha256(image),
        "records": records,
    }
    rendered = json.dumps(manifest, indent=2) + "\n"
    OUT.mkdir(parents=True, exist_ok=True)
    MANIFEST.write_text(rendered, encoding="utf-8")
    OUTPUT.write_bytes(image)
    print(json.dumps({key: value for key, value in manifest.items()
                      if key != "records"}, indent=2))

    # The pinned manifest beside this script is the SHA-256 cited by
    # docs/backlog/safedisc-emulation-handoff.md; regenerating must reproduce it.
    reference = REFERENCE_MANIFEST.read_text(encoding="utf-8")
    print(f"reference_manifest_matches={rendered == reference}")
    if rendered != reference:
        return 1

    # Validation only: no bytes from this image enter reconstruction.
    if ORACLE.exists():
        oracle = mapped_image(ORACLE)
        wrong = [offset for offset in occupied if image[offset] != oracle[offset]]
        print(f"oracle_changed_byte_mismatches={len(wrong)}")
        if wrong:
            print("mismatch_rvas=" + ",".join(f"{x:08X}" for x in sorted(wrong)))
            return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
