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

## Renderer RE (2026-08-20) — it is a GRID/SECTOR-based software 3D engine

Ghidra (12.0, headless, ARM:LE:32:v4t @0x08000000, `TestOutput/gba-ghidra/`)
plus a BizHawk memory-read watchpoint pinned the geometry code. The watchpoint
on the descriptor addresses fired once per frame from **IWRAM 0x030005A8** — the
VV engine copies its hot routines into IWRAM at load (confirmed: that IWRAM code
matches ROM **0x087FE178**, in the 0x87Fxxxx IWRAM-overlay region the GAX
research also flagged). So the renderer/loader runs from IWRAM, which is why
static Ghidra xrefs to the 0x750000 region are empty (0 refs) — the data is
reached only through runtime-computed pointers.

Disassembling the ROM source (`gba_disasm.py thps2 0x087FE130 …`) decoded three
functions and reshaped the picture:

- **0x087FE150 — 2D AABB overlap test.** Compares two rects
  `{minX@0, minY@4, maxX@8, maxY@C}` and returns 1 on overlap. This is the
  per-object **visibility cull**, and it proves the descriptor's four leading
  fields are a **2D bounding box in world XZ** (the earlier "bounding box"
  reading was right). One call/object/frame = the single read the watchpoint saw.
- **0x087FE19C — the draw/traverse function.** A double-nested loop over a **2D
  grid of s16 indices** (`grid = *(descriptor+0x10)`, walked by a column
  counter). For each non-zero grid value `V`: `V*3` then `<<5` (so `V*96`) indexes
  a **96-byte geometry element** in a **runtime-built table** (`*(0x03001E50)`),
  which is handed to a rasterizer sub-call at 0x087FE068. Coordinates are **24.8
  fixed-point** (`asr #8` throughout).
- **0x087FE3AC — world→grid lookup.** Takes a world (x,y) in 24.8, `>>8` to
  integer cells, `cell = y*width + x` (`width = *(0x03002454)`), indexes a runtime
  grid `*(0x03002450)`. The spatial/collision query.

**Conclusion: THPS2 GBA renders real 3D**, but as **grid-indexed geometry**, not
triangle soup: the world is a 2D grid (world XZ); each populated cell references
a 96-byte geometry element; a software rasterizer (in IWRAM) draws them with
fixed-point transforms. This validates the user's "there is 3D" instinct while
explaining why a naive vertex-buffer scan found nothing — the drawable geometry
lives in **runtime-built RAM tables** (`*(0x03001E50)` / `*(0x03002450)` /
`*(0x03002454)`), decompressed/transformed from ROM at load. The 24-byte ROM
descriptors + s16 grids at 0x750000+ are the loader's INPUT; the 96-byte
elements are its OUTPUT.

Following those runtime pointers from the existing RAM dumps (`follow_elem.py`,
no live run needed — the IWRAM globals sit in `iwram.bin`, their targets in
`ewram_full.bin`): the attract demo has `*(0x03001E50)=0x020017BC` (element
table, EWRAM), `*(0x03002450)=0x0200769C` (grid, mostly zero in that demo),
`*(0x03002454)=0x5A` (grid width 90). The 96-byte elements are **not raw source
geometry** — they read as **processed render spans** (repeating fixed-point pairs
like `(0x8000, 0x00AA, 0x4000, 0x0055)`), i.e. the per-scanline output the
rasterizer consumes, one transform stage downstream of the model data.

**GG3 blocker (revised).** Full geometry extraction needs the **runtime-table
BUILDER** reverse-engineered — the level-load code that converts the ROM
descriptors/grids into those elements. A write-watchpoint on `0x03001E50` stalled
the emulator (the pointer is rewritten per frame, not just at load), so the next
approach is to watchpoint the ELEMENT BUFFER's first bytes during level load (set
early, before the demo), or decompile the level-load path statically. This is the
largest remaining GBA effort.

## Other formats (2026-08-20)

- **Audio — GAX Sound Engine sample extraction SHIPPED (2026-08-20).**
  `Core/Formats/Audio/Gba/GbaGaxAudio.cs` + CLI `gba-audio`. GAX has no magic and
  (in v1.99) no per-song mixing-rate/instrument/wave fields — those are global —
  so the reliable anchor is the **wave set**: the `{u32 romAddr, u32 size}` array
  is self-validating because the samples are packed back-to-back
  (`addr[i]+size[i]==addr[i+1]`). The extractor scans for the longest such
  contiguous in-ROM run, then dumps each sample (mono **signed 8-bit PCM**,
  widened to 16-bit) to WAV. Verified on THPS2 GBA: engine banner
  `GAX Sound Engine v1.99d`, wave set at 0x7F26C0, **101 samples**, all real PCM.
  Pinned by `GbaGaxAudioTests`. NOT yet done: sequenced-music rendering (needs the
  song/instrument/perf-list playback + the global rate-table index the driver
  selects at init) — a larger second stage.
- **Images/tiles/textures — display format not yet pinned.** The entropy-based
  `stream_census` "tiles4" label is unreliable (a rendered "tiles4" payload at
  0x455FEC is noise, not art). Rendering the attract-demo VRAM as Mode-4 bitmap
  and as 4/8bpp tiles both came out garbled, so the level is NOT a trivial BG
  bitmap/tilemap — likely composited from OBJ sprites and/or an affine BG, or
  software-rasterized to a framebuffer the dump didn't capture cleanly. Next:
  read **DISPCNT/BGxCNT** (the Lua IO read via BizHawk's System Bus domain did
  not return — IO 0x04000000 may need a different domain or a savestate) to get
  the real mode, then trace the on-screen tiles/sprites back to their ROM LZ77
  source.

## Engine evolution across the 7 carts (2026-08-20)

The line spans two GBA generations and the engine clearly changed over time
(`cross_rom_survey.py`, `ptr_scan.py`):

| Cart | Game code | ROM | GAX version (build) | non-overlap LZ77 |
|---|---|---|---|---|
| THPS2 (2001) | ATHE | 8 MB | v1.99d (Mar 2001) | 384 / 12.6% |
| THPS3 (2002) | AT3E | 8 MB | 2.11 (Dec 2001) | 463 / 4.9% |
| THPS4 (2002) | AT6E | 8 MB | 3.0 (Jul 2002) | 53 / 2.7% |
| THUG (2003) | BTOE | 8 MB | 3.03A (Jul 2003) | 63 / 2.6% |
| THUG2 (2004) | B2TE | 8 MB | 3.05 (Aug 2003) | 69 / 4.9% |
| Sk8land (2005) | BH9E | 8 MB | 3.05A (Aug 2004) | 65 / 3.3% |
| **Downhill Jam (2006)** | **BXSE** | **16 MB** | **3.05 (Aug 2003)** | **3 / 0.1%** |

- **Two generations by game code:** A-prefix (THPS2/3/4) and B-prefix
  (THUG/THUG2/Sk8land/DHJ).
- **The geometry renderer changed after THPS2:** the exact THPS2 cull signature
  (`04 20 90 E5 …`) appears in no other cart, so the render code was rewritten.
- **Audio decouples from the game year:** DHJ (2006) ships the *same* GAX 3.05
  Aug-2003 build as THUG2 — its audio engine is not "new."
- **Downhill Jam is the structural outlier** and matches the user's read that it
  "seems completely different": it is the only 16 MB cart, and it uses almost no
  BIOS LZ77 (3 non-overlapping streams / 0.1%, vs 12.6% in THPS2) and no zlib —
  so its assets are stored **largely uncompressed** in a different layout (its
  pointer tables use 68-byte-stride records, unlike THPS2's 24-byte descriptors).
  Characterizing DHJ's geometry is its own RE effort, separate from THPS2's.

## Audio — shipped across the whole line (2026-08-20)

`GbaGaxAudio` + `gba-audio` extracts samples from **all 7 carts** (the
version-token fingerprint had to stop before the version, since the leading `v`
was dropped after v1.99). Sample counts: THPS2 101, THPS3 53, THPS4 52, THUG 55,
THUG2 55, Sk8land 55, DHJ 39. Pinned by `GbaGaxAudioTests` (`[CorpusTheory]`).

## Scene architecture (2026-08-20, Ghidra decompile of the builder)

Located the level-setup builder by searching the ROM for the runtime-global
literal pools (`find_globals.py` — Ghidra's own search missed them in the huge
undefined region): the globals `elemPtr/gridPtr/gridWidth` are referenced by a
THUMB cluster at **ROM 0x08011800** (`FUN_08011800`), decompiled cleanly:

- The scene is a **list of 96-byte polymorphic objects** (iterated at stride
  `0x60`, terminated by a per-object field). Each object's first 16 bytes are a
  2D world-XZ bounding box.
- `FUN_08011800` builds a **spatial acceleration grid**: world divided into
  256-unit cells (`cols = worldW/256`, grid width = `cols*10`, matching the
  width-90 seen at runtime); it allocates `elemPtr` = a size-prefixed element
  blob (`FUN_08034ae8` = `*p >> 8`) plus the grid, then for each cell stores
  pointers to every object whose box overlaps that cell.
- Object overlap is tested via **method dispatch** — `FUN_08037954` is an
  indirect `(*method)()` call, i.e. the objects are **C++-style polymorphic**
  (vtable/method-pointer), not plain structs. `FUN_08011bec` is the allocator,
  `FUN_08034a00` the block copy.

**Why clean mesh extraction is hard (the honest blocker).** The drawable
geometry is produced by **per-object-type methods** (polymorphic), and the
render path emits **spans**, not vertex buffers — so there is no flat
mesh-in-ROM to lift. Extracting real geometry means reverse-engineering each
object type's geometry method and reconstructing meshes from the span/parametric
representation. The 3D is real (world-space boxes, 24.8 fixed-point, a spatial
grid), but it is an engine-specific scene graph, so this is a genuinely
multi-session effort — the builder + object model are now mapped, but the
per-type geometry methods are not.

## RESOLVED (2026-08-21): the render path is 2D isometric TILE art, not 3D meshes

Capturing the builder's arguments live (`capture_builder.lua`, execute-breakpoint
on `FUN_08011800`) gave the ground truth: everything is in ROM.
`param_2` (object list) `= 0x08754E60`, `param_1` (element/tile library)
`= 0x0873DC78`, world `0x810 × 0x540`. So no runtime reconstruction is needed.

- **Objects** (`decode_objects.py`): ROM 0x08754E60, **96 bytes each**
  (`{s32 bbox[4], u32 tileGridPtr@0x10, u32 count@0x14, misc fields}`). The
  earlier 24-byte reading was a wrong stride. `tileGridPtr` → `count` rows of a
  2D grid of small **tile indices** `V`.
- **The draw loop** maps each grid value `V` to element `param_1[V]` (V×96) and
  hands it to the rasterizer.
- **The rasterizer at ROM 0x087FE068** (IWRAM-overlay, disassembled) is a **1-bit-per-pixel
  stencil blitter**: per row it reads a 32-bit word and, bit by bit (an unrolled
  `lsls #2` + `strbhs`/`strbmi` chain), writes a pixel into the framebuffer at a
  scanline stride. So a `param_1` element is a **1-bpp tile bitmap**, and
  `decode_elements.py` confirms the element bytes are packed bit-rows
  (`F0 07`/`01 F0` patterns), not vertex data.

**Conclusion — there are no extractable 3D meshes in the GBA render data.** The
level is drawn as **2D isometric tile art**: a grid of tile indices per object,
each index selecting a pre-drawn 1-bpp tile bitmap that the software blitter
composites to the screen. This matches the Vicarious Visions dev interview
verbatim ("the images were 2D tiles; the collision had to be represented in 3D").
The earlier "grid-based 3D engine" framing was half-right: the *spatial grid* and
world-space bounding boxes are real (and the **collision** is a separate 3D
parametric system, not yet decoded), but the **render geometry is 2D tiles**, so
extracting polygon meshes from it is not possible — there are none.

**What IS extractable** (the achievable "render a level" deliverable, in 2D):
compose the isometric level image by walking each object's tile grid and blitting
`param_1`'s 1-bpp tile bitmaps at each cell — i.e. reconstruct the tile art and
the level layout, not a 3D model. The tile-bitmap plane/colour encoding (the
element is one bit-plane; the blit value comes from a table indexed by a stack
arg) is the remaining detail to pin before a faithful colour composite.

## SHIPPED (2026-08-21): full-screen BIOS-LZ77 image extraction (`gba-image`)

The first image deliverable is media-derived and in the tool. A census of BIOS
LZ77 (`SWI 0x11`) streams (`lz77_sweep.py` → `image_census.py`) found THPS2 packs
its front-end art as full-screen paletted screens:

- **240×160 8-bit screens** = 38400-byte LZ77 streams; **256-colour palettes** =
  512-byte streams (reduced-palette art ships a shorter one — the studio logo uses
  41 colours / 82 bytes). Pairing rule: **nearest preceding palette stream large
  enough to cover the indices used** (not nearest-512 — that mis-coloured the logo).
- Two pixel orders, mixed within the cart and decided **per image** by a
  horizontal-smoothness score (`discriminate.py`): **linear** mode-4 framebuffer
  order (7 images — Activision/VV logos, legal screen, title, both competition-invite
  cards, Rooftops) vs **tiled** 8×8 order, 30×20 tiles (6 menu backdrops). Same
  "pick the layout that stays continuous" test as the `.fnt` nibble-order picker.

Implemented as `Core/Formats/Gba/GbaBiosLz77.cs` (strict codec that doubles as the
stream validator — the carts have no filename table, so images are located by
content) + `Core/Formats/Texture/Gba/GbaRomImages.cs` (scanner/decoder) + CLI
`gba-image`. Pinned by `GbaRomImagesTests` (synthetic codec cases + THPS2's 13
screens with an aggregate RGBA digest + a 7-cart count sweep). All 13 decode
pixel-perfect.

**Cross-cart divergence (engine evolution):** THPS2 is the ONLY cart with
BIOS-LZ77 full-screen art. THPS3 has 463 LZ77 streams but **0** of these screens
(distinctive 884-byte ×159 shape instead); THPS4→DHJ have only 53–69 LZ77 streams
total (DHJ just 3) — the later carts moved most art off BIOS LZ77 to a different
packaging. The scanner returns empty for them (pinned), and cracking the later
carts' art container is its own research arc.

## Next steps (in order)

1. **GBA tile-sheets / sprites (backlog):** THPS2's 221 32-aligned LZ77 tile
   sheets (2048-byte ×126 = 64-tile 4bpp sheets, sprite/anim frames, fonts) are
   located but their palette + arrangement binding is runtime state — needs a
   palette-pairing heuristic or a loader trace before a faithful render. Lower
   value than the full-screen screens already shipped.
2. **Later-cart art container:** RE THPS3's 884-byte streams and the THPS4→DHJ
   packaging (they abandoned BIOS LZ77) to extend image extraction across the line.
3. **Level tile-art reconstruction (hard):** compose the isometric level image by
   walking each object's tile grid (ROM 0x08754E60) and blitting `param_1`'s 1-bpp
   tile bitmaps — requires simulating the bespoke rasterizer at 0x087FE068 (per-row
   alignment shift + RLE early-out) and pinning the blit-value/colour table.
4. **Audio — sequenced music: SHIPPED (`gba-music`, 2026-08-21).** The THPS2 GAX
   v1.99 song structure is fully decoded and rendered to WAV. Song headers (20
   bytes: `{u16 channels, rows, orderLength, loopPoint, u32 notesAddr, instrAddr,
   sampleAddr}`) are found by scanning for `sampleAddr == waveSetBase` (the null
   `{0,0}` record before the `gba-audio` wave set). **Order-list stride = `pats*4`**
   (per channel), the block sitting `[header − channels*pats*4, header)` — confirmed
   both structurally (100% in-pool pattern offsets across all 11 songs) and by
   disassembly (the engine's per-channel order pointer at `[channelObj+0x18]`,
   pattern-seek at ROM 0x080362FC). Pattern grammar per the note interpreter (flag
   byte, then `0xFF n` rest / `0x80` empty / `0x80|k` k≤0x79 note+instr / k=0x7A-7E
   effect / `<0x80` note+instr+effect). The order-entry **transpose byte (+2) is
   decoded but NOT applied** — THPS2 v1.99 note-on (0x080364CC) uses only `(note-2)`
   and never reads it (likely an authoring/later-version field). `Core/Formats/Audio/
   Gba/{GbaGaxMusic (faithful decoder), GaxRenderer (tone synth)}.cs`, pinned by
   `GbaGaxMusicTests` (11 songs; song0 615 notes/D4; song1 1356 — the stride guard).
   Faithful: pitch/rhythm/order. **Approximate: tempo** (no per-song field; policy
   10 rows/s ≈ 60Hz÷speed6) and **timbre** (tone synth, not the instruments' PCM —
   the audio-rate sample-binding DMA mixer is not yet RE'd). **THPS2-only:** later
   carts use GAX 2.11/3.x header layouts (v2/v3, per gaxtapper) — cross-cart music
   and PCM-timbre rendering are the remaining backlog.
5. Later titles (THPS3+, the "real 3D editor" the dev interview mentions) may store
   scene data differently — recheck once a later-cart container is cracked.
