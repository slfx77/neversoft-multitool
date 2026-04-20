#!/usr/bin/env python3
"""
THAW PS2 Worldzone MDL — PCSX2 savestate locator.

Goal: find where (and in what form) the worldzone level MDL (0x7EA7357B, e.g.
`003B1940.mdl`) lives in PS2 EE RAM after the engine finishes loading it.
This is how we figure out the transformation the engine applies between the
raw on-disk PAK bytes and the data FUN_001D4248 (the CGeomNode relocator)
actually sees.

Workflow:
  1. Load THAW in PCSX2 (pcsx2-qt.exe), boot to a worldzone (e.g. BH).
  2. Once the level is fully loaded, save a savestate (`.p2s`).
  3. Run this script:

       python tools/diagnostics/thaw_pcsx2_mdl_locator.py <savestate.p2s> \
              <disk_mdl.mdl>

     optionally:
         --head-dump 128    # bytes to print side-by-side around each hit
         --out <dir>        # dump the matching EE RAM region to disk

Search strategy:
  The MDL's last ~452 KB is a contiguous table of 5,649 × 0x50-byte records,
  each carrying the signature `0x4B189680` at record+0x18. That repeating
  pattern at a fixed stride is essentially unique — finding two adjacent hits
  in EE RAM pins the record table to a single EE address, from which we can
  back-compute the MDL's base (if it's loaded as one contiguous blob).

Output:
  Reports the EE RAM address of the first preamble record and compares the
  first N bytes at the inferred MDL base vs the on-disk file. If the bytes
  match, the MDL is loaded as-is and the relocator must be operating on a
  DIFFERENT buffer (a copy). If they diverge, the transformation happens
  in-place before `FUN_001D4248`.
"""

import argparse
import io
import struct
import sys
import zipfile
from pathlib import Path

EE_MEM_FILENAME = "eeMemory.bin"
PREAMBLE_SIG_LE = struct.pack("<I", 0x4B189680)
PREAMBLE_STRIDE = 0x50
PREAMBLE_SIG_OFFSET = 0x18


def load_ee_ram(savestate_path: Path) -> bytes:
    with zipfile.ZipFile(savestate_path, "r") as z:
        names = z.namelist()
        if EE_MEM_FILENAME not in names:
            sys.exit(f"error: {savestate_path} has no {EE_MEM_FILENAME}; contents: {names}")
        with z.open(EE_MEM_FILENAME) as f:
            return f.read()


def find_record_table_start(mdl: bytes) -> int | None:
    """Find the offset in the on-disk MDL of the first 0x4B189680 record sig."""
    start = mdl.find(PREAMBLE_SIG_LE)
    if start < 0:
        return None
    # record_start = sig_start - 0x18
    record_start = start - PREAMBLE_SIG_OFFSET
    if record_start < 0:
        return None
    # Confirm by checking the next record has the sig at the expected stride.
    next_sig = record_start + PREAMBLE_STRIDE + PREAMBLE_SIG_OFFSET
    if mdl[next_sig : next_sig + 4] == PREAMBLE_SIG_LE:
        return record_start
    return None


def find_table_in_ee(ee: bytes, min_run: int = 16) -> list[int]:
    """Return EE byte-offsets of record tables with >= min_run consecutive
    signatures at PREAMBLE_STRIDE apart."""
    hits: list[int] = []
    pos = 0
    while True:
        i = ee.find(PREAMBLE_SIG_LE, pos)
        if i < 0:
            break
        # Count how many adjacent stride-aligned sigs follow.
        run = 1
        j = i + PREAMBLE_STRIDE
        while j + 4 <= len(ee) and ee[j : j + 4] == PREAMBLE_SIG_LE:
            run += 1
            j += PREAMBLE_STRIDE
        if run >= min_run:
            # Table base = first sig offset - PREAMBLE_SIG_OFFSET (to get record[0]).
            table_base = i - PREAMBLE_SIG_OFFSET
            hits.append(table_base)
            pos = j  # skip past this run
        else:
            pos = i + 4
    return hits


def dump_hex(data: bytes, offset_base: int, length: int) -> str:
    lines = []
    for off in range(0, min(length, len(data)), 16):
        row = data[off : off + 16]
        hex_part = " ".join(f"{b:02x}" for b in row)
        ascii_part = "".join(chr(b) if 32 <= b < 127 else "." for b in row)
        lines.append(f"  {offset_base + off:08x}  {hex_part:<48}  {ascii_part}")
    return "\n".join(lines)


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("savestate", type=Path, help="PCSX2 .p2s savestate file")
    ap.add_argument("mdl", type=Path, help="On-disk extracted .mdl file to compare against")
    ap.add_argument("--head-dump", type=int, default=64, help="bytes to hex-dump at each hit / MDL base (default 64)")
    ap.add_argument("--min-run", type=int, default=16, help="minimum consecutive record signatures (default 16)")
    ap.add_argument("--out", type=Path, default=None, help="optional directory to write EE RAM regions to")
    args = ap.parse_args()

    if not args.savestate.exists():
        sys.exit(f"missing savestate: {args.savestate}")
    if not args.mdl.exists():
        sys.exit(f"missing mdl: {args.mdl}")

    print(f"Loading EE RAM from {args.savestate} …")
    ee = load_ee_ram(args.savestate)
    print(f"  EE RAM size: {len(ee):,} bytes ({len(ee) >> 20} MiB)")

    mdl = args.mdl.read_bytes()
    print(f"Disk MDL: {args.mdl} ({len(mdl):,} bytes)")

    disk_table_off = find_record_table_start(mdl)
    if disk_table_off is None:
        sys.exit("error: could not find preamble record table in on-disk MDL — is this a 0x7EA7357B level MDL?")
    print(f"  Disk preamble record table starts at file offset 0x{disk_table_off:X}")

    hits = find_table_in_ee(ee, min_run=args.min_run)
    # Filter to our target MDL's record count to avoid false positives from
    # other loaded MDLs' tables floating nearby in EE RAM.
    disk_record_count = (len(mdl) - disk_table_off) // PREAMBLE_STRIDE
    print(f"\nEE RAM scan: found {len(hits)} record-table location(s) with >= {args.min_run} consecutive signatures")
    print(f"  Disk MDL has {disk_record_count} records (target for exact match)")
    for hit in hits:
        # PS2 EE RAM usually starts at virtual address 0x00000000 (cached/kseg).
        # Savestate eeMemory.bin is the raw 32 MB, so offset == physical address
        # in 0x00000000..0x01FFFFFF range.
        ee_addr = hit
        inferred_mdl_base = hit - disk_table_off
        print(f"\n  HIT: record table at EE 0x{ee_addr:08X}")
        print(f"       -> inferred MDL base = EE 0x{inferred_mdl_base:08X}")
        if inferred_mdl_base < 0 or inferred_mdl_base + len(mdl) > len(ee):
            print(f"       (MDL would run off EE RAM — layout differs from disk)")
            continue

        # Compare the first N bytes of the MDL at inferred base in EE vs disk.
        n = args.head_dump
        ee_head = ee[inferred_mdl_base : inferred_mdl_base + n]
        disk_head = mdl[:n]
        if ee_head == disk_head:
            status = "IDENTICAL"
        else:
            # How many leading bytes match?
            match = 0
            for a, b in zip(ee_head, disk_head):
                if a != b:
                    break
                match += 1
            status = f"DIVERGES after {match} bytes"
        print(f"       head[0..{n}] vs disk: {status}")
        print(f"       EE head:")
        print(dump_hex(ee_head, inferred_mdl_base, n))
        print(f"       Disk head:")
        print(dump_hex(disk_head, 0, n))

        if args.out is not None:
            args.out.mkdir(parents=True, exist_ok=True)
            out_path = args.out / f"ee_mdl_at_{inferred_mdl_base:08X}.bin"
            # Write the same byte count as the disk file, if available.
            end = min(inferred_mdl_base + len(mdl), len(ee))
            out_path.write_bytes(ee[inferred_mdl_base:end])
            print(f"       Wrote {end - inferred_mdl_base:,} bytes to {out_path}")

    if not hits:
        print("\nNo record tables found in EE RAM. Possibilities:")
        print("  - The level wasn't loaded at savestate time (try again after the level is fully visible).")
        print("  - The engine transforms the entire preamble section too (not just the header).")
        print("  - The MDL has been moved out of main RAM into scratchpad/VU memory (not in eeMemory.bin).")


if __name__ == "__main__":
    main()
