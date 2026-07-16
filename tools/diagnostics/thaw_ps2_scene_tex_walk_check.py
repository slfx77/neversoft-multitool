#!/usr/bin/env python3
"""
Verifies ThawSceneTexFile's sequential data-walk against the .tex.ps2 DMA
chain's REF tags (ITEM 2g: THAW PS2 meshes rendering untextured/garbled).

The .tex.ps2 DMA chain (off1..off2) is a VIF DIRECT GS-upload program whose
REF DMA tags carry the AUTHORITATIVE byte offset of every CLUT/pixel blob:
  addr_field (u32 low 24 bits of the tag's second word, 0xEB marker in the
  top byte) = byte offset into the data region + 0xA bias (blob N's file
  offset = off2 + addr - first_addr... empirically off2 + (addr - 0xA)).

For every .tex.ps2 in the THAW PS2 build:
  - parse metadata TEX0 records (checksum, dims, PSM, CPSM, mip field),
  - replicate the C# sequential walk (CLUT then pixels per unique checksum,
    mip skip per the -0x08 field),
  - parse the DMA chain uploads (BITBLTBUF dest + TRXREG size + REF addr),
  - compare: does each texture's pixel blob land where the walk thinks?
Reports per-file desyncs and textures whose walk position overruns the file
(the C# decoder returns null pixels for those -> untextured material).

Usage: python tools/diagnostics/thaw_ps2_scene_tex_walk_check.py [-v]
"""

import struct
import sys
from pathlib import Path

BUILD = Path(r"c:/Users/mmc99/source/repos/NeversoftMultitool/Sample/Builds"
             r"/Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)")

VALID_PSM = {0x00, 0x01, 0x02, 0x0A, 0x13, 0x14}
BPP = {0x00: 32, 0x01: 24, 0x02: 16, 0x0A: 16, 0x13: 8, 0x14: 4}
PAL_ENTRIES = {0x13: 256, 0x14: 16}


def scan_tex0(data, off1, num_tex):
    recs = []
    for off in range(0x40, off1 - 7, 8):
        val = struct.unpack_from("<Q", data, off)[0]
        tbp = val & 0x3FFF
        tbw = (val >> 14) & 0x3F
        psm = (val >> 20) & 0x3F
        tw = (val >> 26) & 0xF
        th = (val >> 30) & 0xF
        cpsm = (val >> 51) & 0xF
        if psm not in VALID_PSM:
            continue
        if not (1 <= tw <= 10 and 1 <= th <= 10):
            continue
        if tbp < 0x2BC0 or tbw < 1:
            continue
        if off - 0x10 < 0x40:
            continue
        ck = struct.unpack_from("<I", data, off - 0x10)[0]
        if ck <= 0xFFFF:
            continue
        mip = struct.unpack_from("<I", data, off - 8)[0]
        if not 0 <= mip <= 7:
            mip = 0
        cbp = (val >> 37) & 0x3FFF
        recs.append({"ck": ck, "tbp": tbp, "cbp": cbp, "psm": psm,
                     "cpsm": cpsm, "w": 1 << tw, "h": 1 << th, "mip": mip})
    if len(recs) > num_tex:
        recs = recs[:num_tex]
    return recs


def sequential_walk(recs, off2, file_len):
    """Replicates ThawSceneTexFile.DecodeEntries/DecodeEntry positions.
    Returns dict ck -> (clut_off, pix_off, ok) using first-encounter order."""
    pos = off2
    out = {}
    for r in recs:
        if r["ck"] in out:
            continue
        pal = PAL_ENTRIES.get(r["psm"], 0)
        clut_off = pos
        if pal:
            clut_bytes = pal * BPP.get(r["cpsm"], 32) // 8
            if pos + clut_bytes > file_len:
                out[r["ck"]] = (clut_off, None, False)
                continue
            pos += clut_bytes
        pix_bytes = r["w"] * r["h"] * BPP[r["psm"]] // 8
        if pos + pix_bytes > file_len:
            out[r["ck"]] = (clut_off, pos, False)
            continue
        pix_off = pos
        pos += pix_bytes
        for m in range(1, r["mip"] + 1):
            mw = max(1, r["w"] >> m)
            mh = max(1, r["h"] >> m)
            mb = mw * mh * BPP[r["psm"]] // 8
            if mb < 1 or pos + mb > file_len:
                break
            pos += mb
        out[r["ck"]] = (clut_off, pix_off, True)
    return out, pos


def parse_dma_uploads(data, off1, off2):
    """Returns list of uploads: (dbp, width, height, byte_len, addr_field)."""
    uploads = []
    i = off1
    cur_dbp = cur_w = cur_h = None
    while i + 16 <= off2:
        lo = struct.unpack_from("<Q", data, i)[0]
        hi = struct.unpack_from("<Q", data, i + 8)[0]
        reg = hi & 0xFF
        w0 = lo & 0xFFFFFFFF
        w1 = lo >> 32
        if reg == 0x50 and (lo >> 32) & 0x3FFF >= 0x2BC0:
            cur_dbp = (lo >> 32) & 0x3FFF
        elif reg == 0x52 and cur_dbp is not None:
            cur_w = lo & 0xFFF
            cur_h = (lo >> 32) & 0xFFF
        elif (w0 >> 28) == 3 and (w1 >> 24) == 0xEB and cur_dbp is not None:
            qwc = w0 & 0xFFFF
            addr = w1 & 0xFFFFFF
            uploads.append((cur_dbp, cur_w, cur_h, qwc * 16, addr))
            cur_dbp = cur_w = cur_h = None
        i += 8
    return uploads


def main():
    verbose = "-v" in sys.argv
    files = sorted(BUILD.rglob("*.tex.ps2"))
    n_files = n_chain_ok = n_walk_ok = 0
    n_tex = n_tex_pos_ok = n_tex_desync = n_tex_overrun = 0
    bad_files = []

    for path in files:
        data = path.read_bytes()
        if len(data) < 0x40 or struct.unpack_from("<H", data, 0)[0] != 6:
            continue
        num_tex, off1, off2 = struct.unpack_from("<III", data, 4)
        if not (0 < num_tex <= 100 and 0x40 < off1 < len(data)
                and off1 < off2 < len(data)):
            continue
        n_files += 1
        recs = scan_tex0(data, off1, num_tex)
        walk, end_pos = sequential_walk(recs, off2, len(data))
        uploads = parse_dma_uploads(data, off1, off2)
        if not uploads:
            bad_files.append((path.name, "no DMA uploads parsed"))
            continue
        n_chain_ok += 1
        base = off2 - uploads[0][4]  # first blob starts at off2

        # Ground truth: pair uploads to ALL records in metadata order
        # (duplicate-checksum records re-upload from the shared address).
        # Each record uploads [CLUT (dest=cbp)] for paletted PSMs, then
        # pixels (dest=tbp), then optional mip-level uploads (absorbed:
        # consecutive uploads that don't match the next record's start).
        truth_pix = {}
        mip_uploads = {}
        ui = 0
        paired = True
        for ri, r in enumerate(recs):
            has_clut = r["psm"] in PAL_ENTRIES
            if has_clut:
                if ui >= len(uploads) or uploads[ui][0] != r["cbp"]:
                    paired = False
                    break
                ui += 1
            if ui >= len(uploads) or uploads[ui][0] != r["tbp"]:
                paired = False
                break
            truth_pix.setdefault(r["ck"], base + uploads[ui][4])
            ui += 1
            # absorb mip uploads: consecutive uploads that don't line up
            # with the next record's first destination
            nxt = recs[ri + 1] if ri + 1 < len(recs) else None
            while ui < len(uploads):
                dest = uploads[ui][0]
                if nxt is not None:
                    nxt_first = nxt["cbp"] if nxt["psm"] in PAL_ENTRIES else nxt["tbp"]
                    if dest == nxt_first:
                        break
                mip_uploads.setdefault(r["ck"], []).append(uploads[ui])
                ui += 1
        if not paired or ui != len(uploads):
            bad_files.append(
                (path.name, f"upload pairing failed (ui={ui}/{len(uploads)})"))
            continue

        unique_recs = []
        seen = set()
        for r in recs:
            if r["ck"] in seen:
                continue
            seen.add(r["ck"])
            unique_recs.append(r)

        file_desync = 0
        file_overrun = 0
        for r in unique_recs:
            n_tex += 1
            clut_off, pix_off, ok = walk[r["ck"]]
            if not ok:
                n_tex_overrun += 1
                file_overrun += 1
                continue
            truth = truth_pix.get(r["ck"])
            if truth is None:
                continue
            if truth == pix_off:
                n_tex_pos_ok += 1
            else:
                n_tex_desync += 1
                file_desync += 1
                if verbose:
                    print(f"  {path.name}: ck=0x{r['ck']:08X} walk=0x{pix_off:X}"
                          f" truth=0x{truth:X} (delta {pix_off - truth})")
        if file_desync == 0 and file_overrun == 0:
            n_walk_ok += 1
        else:
            bad_files.append(
                (path.name, f"desync={file_desync} overrun={file_overrun}"))

    print("================ AGGREGATE ================")
    print(f".tex.ps2 files (v6):            {n_files}")
    print(f"  DMA chain parsed:             {n_chain_ok}")
    print(f"  walk fully matches chain:     {n_walk_ok}")
    print(f"unique textures:                {n_tex}")
    print(f"  walk position == DMA truth:   {n_tex_pos_ok}")
    print(f"  DESYNCED (wrong pixels):      {n_tex_desync}")
    print(f"  OVERRUN (null pixels):        {n_tex_overrun}")
    print(f"\nFiles with problems ({len(bad_files)}):")
    for name, why in bad_files[:40]:
        print(f"  {name:44} {why}")


if __name__ == "__main__":
    main()
