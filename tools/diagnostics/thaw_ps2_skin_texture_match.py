#!/usr/bin/env python3
"""
Measures how many THAW PS2 .skin.ps2 material TextureChecksums resolve against
the same-stem companion .tex.ps2 scene-texture file (ITEM 2g: many THAW PS2
meshes render untextured).

For every .skin.ps2 under the THAW PS2 build that has a .tex.ps2 companion:
  - parse the 64B entry table (TextureChecksum at +0x20 of each entry),
  - parse the companion's TEX0 metadata records (checksum at TEX0-0x10,
    TBP/CBP from the TEX0 register) using the same scan rules as
    ThawSceneTexFile.ScanTex0Entries,
  - report the direct checksum join rate,
  - additionally parse the skin's DIRECT VIF blocks for TEX0_1 writes and
    test the (TBP,CBP) and TBP-only joins as candidate fallbacks.

Usage:
  python tools/diagnostics/thaw_ps2_skin_texture_match.py [--detail STEM ...]

Default detail stems: ped_boone_full, ped_billyjoe.
"""

import struct
import sys
from pathlib import Path

BUILD = Path(r"c:/Users/mmc99/source/repos/NeversoftMultitool/Sample/Builds"
             r"/Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)")

VALID_PSM = {0x00, 0x01, 0x02, 0x0A, 0x13, 0x14}  # PSMCT32/24/16/16S, PSMT8, PSMT4


def is_thaw_ps2_skin(data):
    if len(data) < 32:
        return False
    num_objects, tm1, tm2, data_size = struct.unpack_from("<4I", data, 0)
    if num_objects in (3, 5, 6) and tm1 in (4, 6) and tm2 == 1:
        return False
    if num_objects == 0 or num_objects > 20:
        return False
    if tm2 == 0 or tm2 > 500:
        return False
    if tm1 > tm2:
        return False
    if data_size + 16 > len(data):
        return False
    bs_r = struct.unpack_from("<f", data, 0x1C)[0]
    if not (bs_r > 0) or bs_r != bs_r:
        return False
    if len(data) > 32 and 32 + num_objects * 8 + tm2 * 64 > len(data):
        return False
    return True


def parse_skin_entries(data):
    """Returns list of dicts for each 64B entry-table record."""
    num_objects = struct.unpack_from("<I", data, 0)[0]
    tm2 = struct.unpack_from("<I", data, 8)[0]
    base = 32 + num_objects * 8
    entries = []
    for i in range(tm2):
        off = base + i * 64
        mat = struct.unpack_from("<I", data, off + 4)[0]
        tex = struct.unpack_from("<I", data, off + 0x20)[0]
        entries.append({"index": i, "material": mat, "texture": tex})
    return entries


def parse_tex_companion(data):
    """Returns (records, model_checksum) — records have checksum/tbp/cbp/psm/w/h.
    Mirrors ThawSceneTexFile.ScanTex0Entries (incl. the take-first-numTex clamp)."""
    if len(data) < 0x40 or struct.unpack_from("<H", data, 0)[0] != 6:
        return None
    num_tex = struct.unpack_from("<I", data, 4)[0]
    off1 = struct.unpack_from("<I", data, 8)[0]
    model_checksum = struct.unpack_from("<I", data, 0x18)[0]
    if num_tex <= 0 or num_tex > 100 or off1 <= 0x40 or off1 >= len(data):
        return None
    records = []
    for off in range(0x40, off1 - 7, 8):
        val = struct.unpack_from("<Q", data, off)[0]
        tbp = val & 0x3FFF
        tbw = (val >> 14) & 0x3F
        psm = (val >> 20) & 0x3F
        tw = (val >> 26) & 0xF
        th = (val >> 30) & 0xF
        if psm not in VALID_PSM:
            continue
        if not (1 <= tw <= 10 and 1 <= th <= 10):
            continue
        if tbp < 0x2BC0 or tbw < 1:
            continue
        ck_off = off - 0x10
        if ck_off < 0x40:
            continue
        checksum = struct.unpack_from("<I", data, ck_off)[0]
        if checksum <= 0xFFFF:
            continue
        cbp = (val >> 37) & 0x3FFF
        records.append({"checksum": checksum, "tbp": tbp, "cbp": cbp,
                        "psm": psm, "w": 1 << tw, "h": 1 << th})
    if len(records) > num_tex:
        records = records[:num_tex]
    return records, model_checksum


def find_raw_direct_offsets(data):
    """Port of ThawPs2SkinVifLayout.FindRawDirectOffsets."""
    num_objects, _, tm2, data_size = struct.unpack_from("<4I", data, 0)
    entry_table_end = 32 + num_objects * 8 + tm2 * 64
    vif_end = min(data_size + 16, len(data))
    offsets = []
    for off in range(entry_table_end, vif_end - 7, 4):
        word = struct.unpack_from("<I", data, off)[0]
        if word not in (0x10000000, 0x11000000):
            continue
        if data[off + 7] & 0x7F in (0x50, 0x51):
            offsets.append(off + 4)
    return offsets


def extract_tex0_from_direct(data, direct_offset):
    """Port of ThawPs2SkinSetupMapping.ExtractTex0FromDirect: (tbp, cbp) or None."""
    if direct_offset + 4 > len(data):
        return None
    qwc = struct.unpack_from("<H", data, direct_offset)[0]
    gif_start = direct_offset + 4
    if qwc == 0 or gif_start + 16 > len(data) or gif_start + (qwc << 4) > len(data):
        return None
    gif_lo = struct.unpack_from("<Q", data, gif_start)[0]
    nloop = gif_lo & 0x7FFF
    flg = (gif_lo >> 58) & 3
    nreg = (gif_lo >> 60) & 0xF or 16
    gif_hi = struct.unpack_from("<Q", data, gif_start + 8)[0]
    if flg != 0 or nreg != 1 or (gif_hi & 0xFF) != 0x0E:
        return None
    for i in range(nloop):
        off = gif_start + 16 + i * 16
        if off + 16 > len(data):
            break
        data_val = struct.unpack_from("<Q", data, off)[0]
        reg = struct.unpack_from("<Q", data, off + 8)[0] & 0xFF
        if reg == 0x06:
            return data_val & 0x3FFF, (data_val >> 37) & 0x3FFF
    return None


def main():
    detail_stems = {"ped_boone_full", "ped_billyjoe"}
    args = sys.argv[1:]
    if "--detail" in args:
        detail_stems = set(args[args.index("--detail") + 1:])

    skins = sorted(BUILD.rglob("*.skin.ps2"))
    files_seen = files_with_companion = 0
    total_entries = direct_match = 0
    tbpcbp_rescue = tbp_rescue = unresolved = 0
    placeholder_misses = real_misses = 0
    files_all_direct = files_some_missing = 0
    unmatched_examples = []

    for skin_path in skins:
        data = skin_path.read_bytes()
        if not is_thaw_ps2_skin(data):
            continue
        files_seen += 1
        stem = skin_path.name[:-len(".skin.ps2")]
        tex_path = None
        for cand in (skin_path.with_name(stem + ".tex.ps2"),
                     skin_path.parent / "TEX" / (stem + ".tex.ps2"),
                     skin_path.parent / "Textures" / (stem + ".tex.ps2")):
            if cand.exists():
                tex_path = cand
                break
        if tex_path is None:
            continue
        parsed = parse_tex_companion(tex_path.read_bytes())
        if parsed is None:
            continue
        records, model_ck = parsed
        files_with_companion += 1

        companion_cks = {r["checksum"] for r in records}
        tbpcbp_map = {}
        tbp_map = {}
        for r in records:
            tbpcbp_map.setdefault((r["tbp"], r["cbp"]), r["checksum"])
            tbp_map.setdefault(r["tbp"], r["checksum"])

        entries = parse_skin_entries(data)
        directs = find_raw_direct_offsets(data)
        direct_tex0s = [extract_tex0_from_direct(data, d) for d in directs]

        file_direct = 0
        file_unmatched = []
        for e in entries:
            total_entries += 1
            if e["texture"] in companion_cks:
                direct_match += 1
                file_direct += 1
            else:
                e["placeholder"] = e["material"] == 0 and e["texture"] == 0
                file_unmatched.append(e)

        # Candidate rescues: per-DIRECT-section TEX0 joins
        sec_tbpcbp_hits = sum(1 for t in direct_tex0s if t is not None and t in tbpcbp_map)
        sec_tbp_hits = sum(1 for t in direct_tex0s
                           if t is not None and t[0] in tbp_map)

        # crude per-entry rescue estimate: if section count aligns with entry
        # count (or count+1 preamble), the TBP join can cover the misses
        if file_unmatched:
            files_some_missing += 1
            n = len(file_unmatched)
            placeholder_misses += sum(1 for e in file_unmatched if e["placeholder"])
            real_misses += sum(1 for e in file_unmatched if not e["placeholder"])
            tbpcbp_rescue += min(n, sec_tbpcbp_hits)
            tbp_rescue += min(n, sec_tbp_hits)
            unresolved += max(0, n - sec_tbp_hits)
            unmatched_examples.append(
                (skin_path.name, len(entries), n, len(directs),
                 sec_tbpcbp_hits, sec_tbp_hits))
        else:
            files_all_direct += 1

        if stem in detail_stems:
            print(f"\n=== {skin_path.name} ===")
            print(f"  model checksum in .tex.ps2 header: 0x{model_ck:08X}")
            print(f"  skin entries ({len(entries)}):")
            for e in entries:
                ok = "OK " if e["texture"] in companion_cks else "MISS"
                print(f"    [{e['index']:2}] mat=0x{e['material']:08X} "
                      f"tex=0x{e['texture']:08X}  {ok}")
            print(f"  companion records ({len(records)}):")
            for r in records:
                print(f"    ck=0x{r['checksum']:08X} tbp=0x{r['tbp']:04X} "
                      f"cbp=0x{r['cbp']:04X} psm=0x{r['psm']:02X} {r['w']}x{r['h']}")
            print(f"  DIRECT sections ({len(directs)}):")
            for d, t in zip(directs, direct_tex0s):
                if t is None:
                    print(f"    @0x{d:06X}  (no TEX0)")
                else:
                    hit = tbpcbp_map.get(t)
                    hit_s = f"-> ck=0x{hit:08X}" if hit is not None else "-> NO (tbp,cbp) hit"
                    print(f"    @0x{d:06X}  tbp=0x{t[0]:04X} cbp=0x{t[1]:04X} {hit_s}")

    print("\n================ AGGREGATE ================")
    print(f"THAW PS2 skins parsed:            {files_seen}")
    print(f"  with .tex.ps2 companion:        {files_with_companion}")
    print(f"  all entries direct-matched:     {files_all_direct}")
    print(f"  files with >=1 unmatched:       {files_some_missing}")
    print(f"entry-table materials total:      {total_entries}")
    pct = 100.0 * direct_match / total_entries if total_entries else 0
    print(f"  direct checksum matches:        {direct_match} ({pct:.1f}%)")
    print(f"  unmatched:                      {total_entries - direct_match}")
    print(f"    null placeholders (mat=tex=0): {placeholder_misses}"
          " (untextured by design)")
    print(f"    real checksum misses:          {real_misses}")
    print(f"  file sections w/ (TBP,CBP) hit: {tbpcbp_rescue}")
    print(f"  file sections w/ TBP-only hit:  {tbp_rescue}")
    print(f"  unresolved after TBP join:      {unresolved}")

    unmatched_examples.sort(key=lambda x: -x[2])
    print("\nTop files by unmatched count (name, entries, unmatched, sections, "
          "(tbp,cbp) hits, tbp hits):")
    for row in unmatched_examples[:25]:
        print(f"  {row[0]:40} e={row[1]:3} miss={row[2]:3} sec={row[3]:3} "
              f"tbpcbp={row[4]:3} tbp={row[5]:3}")


if __name__ == "__main__":
    main()
