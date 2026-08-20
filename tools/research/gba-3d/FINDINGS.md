# Vicarious Visions GBA 3D engine — open investigation

Status: **research in progress** (not a proven format — kept here, not in
`docs/formats/`, until the loader is disassembled and a level decodes end to
end). Reusable tool: `tools/reverse-engineering/gba/gba_disasm.py` (ARM/THUMB).
Throwaway probes and captured RAM dumps live under `TestOutput/gba-probe/`
(gitignored) and are regenerable from the scripts named below.

The seven Tony Hawk GBA carts (THPS2 → Downhill Jam, Activision game codes
ATHE52…BXSE52) run one evolving Vicarious Visions software-3D engine. Audio is
Shin'en's GAX Sound Engine (THPS2 v1.99d … Sk8land 3.05A) — out of scope.

## Method

- `TestOutput/gba-probe/lz77_sweep.py` — census of valid GBA BIOS LZ77 (SWI
  0x11) streams per ROM.
- `ptr_scan.py` / `stream_census.py` / `neighborhood.py` / `inspect_streams.py`
  — pointer-table and decompressed-payload triage.
- `attract_dump.lua` (BizHawk 2.6.3 mGBA core) — runs THPS2 to its attract
  demo and dumps EWRAM/IWRAM/VRAM/PAL/OAM three times, 3 s apart, with
  screenshots.
- `ewram_analyze.py` / `inspect_ewram.py` / `follow_table.py` / `follow_geom.py`
  / `find_verts.py` — trace the runtime state back to ROM.
  (`gdb_rsp.py` is a GDB-RSP client for mGBA's `-g` stub; superseded by the
  BizHawk Lua route because mGBA's stub rejected reads mid-run — kept for
  reference.)

## Findings

**GG1 — asset regions are pointer-indexed (like N64), not filesystem-named.**
No filename strings anywhere. LZ77 stream census: THPS2 384 streams / 12.6% of
ROM (span 0x426EBC–0x742FA8), the later titles 2.6–4.9%. Payload census
(THPS2): 175 4bpp tile-art, 53 palettes, 25 height/map arrays, 21 "geomcand"
s16-triple runs, 10 index maps. So a large fraction of level content is BIOS
LZ77-compressed and reached through pointer tables (`ptr_scan.py` finds
80–143 monotone in-ROM pointer runs per ROM).

**GG2 — geometry is STORED, not procedural.** This is the load-bearing result:
it de-risks the whole thrust (a THPS2-GBA dev interview noted *collision* is
parametric, raising the risk that render geometry might be too — it is not).
Evidence, from the attract-demo RAM trace:
- The frame-to-frame *changed* EWRAM regions (0x02013000, 0x02019400) are
  high-magnitude s16 arrays that grow/shrink each frame — the **transformed,
  screen-space vertex output**, not source data (and correctly absent from
  ROM).
- A genuine **in-RAM model directory** sits at EWRAM+0x7000 (130 ROM pointers)
  targeting a **raw, uncompressed ROM region at ~0x750000–0x7A0000** (past
  where the LZ77 streams end). Its records carry four leading u32s that read as
  a **bounding box** (e.g. `{1439, 512, 1844, 792, ptr=0x0875D460, count=17}`),
  a data pointer, and a count.
- The data pointers land on **index/face lists** — small u16 values (10, 11,
  12, 13, 14 …) referencing a vertex pool, exactly the shape of per-object
  triangle/quad connectivity.

**Open (needs the loader disassembled — GG3 blocker).** The **vertex position
encoding** is not yet cracked: an in-bounds s16(x,y,z) scan of the raw region
against the descriptors' own bounding boxes found nothing (`find_verts.py`), so
positions are either s8/packed, delta-coded, or reached by a second pointer.
The exact descriptor stride/field meaning is also unconfirmed (a naive 24-byte
tiling desyncs after the first record). Both fall out of disassembling the
model loader: locate it with `gba_disasm.py --find-word 0x08754E60` (the
descriptor-table address seen in RAM) or breakpoint the raw region's reads,
then read the record walk directly rather than guessing.

## Next steps (in order)

1. Ghidra-import THPS2 GBA (ARM v4T LE @0x08000000); find the reader that
   consumes the 0x750000+ region → exact descriptor layout + vertex codec.
2. Decode one attract-demo object to an OBJ point cloud and eyeball it against
   the screenshot (**GG3**). Only then start the implementation wave
   (`GbaRomArchive` carve route → mesh parser/writer → viewer), per the plan.
3. Cross-check the descriptor/codec shape across the other six ROMs
   (`ptr_scan.py` shows the table shapes persist), building the per-title
   support map the carver will need.
