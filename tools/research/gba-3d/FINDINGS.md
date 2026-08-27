# Vicarious Visions GBA engine — investigation record

Status: **the headline questions are answered and shipped.** There are no 3D
meshes (the render path is 2D isometric tile art); THPS2's full-screen images
(`gba-image`), GAX music (`gba-music`), and levels — geometry *and* true colour —
(`gba-level`) all extract from media, pinned by `GbaRomImagesTests`,
`GbaGaxMusicTests`, and `GbaLevelImagesTests`. What remains open is listed under
"Next steps".

Reusable tools: `tools/reverse-engineering/gba/gba_disasm.py` (ARM/THUMB Capstone)
and the vendored emulator at `tools/vendor/bizhawk/` (dynamic analysis; see its
README for the Lua patterns).

**The throwaway probes are retired.** They lived in `TestOutput/gba-probe/`
(gitignored) and were deleted once their conclusions were implemented and pinned
by C# tests, per `tools/README.md`. Probe scripts named below are therefore
*historical* — they record how each conclusion was reached, not files on disk.
Everything load-bearing is preserved in this document, in `Core/Formats/Gba/`,
and in the tests; the emulator captures are re-creatable with the vendored
BizHawk and the Lua recipes described here.

The seven Tony Hawk GBA carts (THPS2 → Downhill Jam, Activision game codes
ATHE52…BXSE52) run one evolving Vicarious Visions software-3D engine. Audio is
Shin'en's GAX Sound Engine (THPS2 v1.99d … Sk8land 3.05A) — out of scope.

## Method

- `lz77_sweep.py` — census of valid GBA BIOS LZ77 (SWI 0x11) streams per ROM.
  (Its decoder is now shipped as `Core/Formats/Gba/GbaBiosLz77.cs`.)
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

Ghidra (12.0, headless, ARM:LE:32:v4t @0x08000000 — the scratch project has since
been discarded; re-import the ROM to reproduce) plus a BizHawk memory-read
watchpoint pinned the geometry code. The watchpoint
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
the level layout, not a 3D model.

## SHIPPED (2026-08-21): isometric level reconstruction (`gba-level`)

Reconstructs THPS2's 9 distinct levels to PNG from the ROM alone — no capture.
Two corrections to the notes above fell out of getting it working:

1. **The element library is BIOS-LZ77 COMPRESSED, not raw.** `param_1[0] == 0x10`;
   the header decompresses to 24288 bytes = **253 elements × 96 bytes**. The earlier
   `F0 07`/`01 F0` "bit-rows" were *compressed* bytes. Decompressed (via the shipped
   `GbaBiosLz77`), each 96-byte element is a **24×24 1-bpp bitmap**, one u32 per row,
   pixel column c = bit c (LSB first — derived from the blitter's shift math).
2. **The blitter writes a hardcoded value 0** (`mov r5,#0`), and the SMC table at
   0x087FE004 is a **left-clip computed-goto** (skip leading pixel-stores), NOT a
   blit-value table. The tile pass is therefore MONOCHROME index-0; the mid-tone
   in `zoom_surface.png` is a **50% checkerboard dither**, real drawn pixels, not a
   solid fill or an occlusion plane.

**ROM level table** (independently verified, `level_table_probe.py`): stride
**0x15C** at ~0x08753540, record `{ shared@+0, objectListPtr@+4, elementLibraryPtr@+8,
meta@+0xC }` (the RE agent's field order was off by one word; `@+0xC` is a pointer,
not an id). 14 valid records → 9 distinct object-list/element-library pairs (records
9–13 reuse Hangar). The table is located by content (4 consecutive records whose
object list opens on a real object and whose element library LZ77-decompresses to
N×96). **Object list**: 96-byte records `{s32 bbox@0, u32 gridPtr@0x10, u32
gridWidth@0x14}` ending at a **zero terminator** (level 0 = 31 objects); cell V≠0
blits element V at `(minX+col*24, minY+row*24)`.

`Core/Formats/Gba/GbaLevelImages.cs` + CLI `gba-level`, pinned by `GbaLevelImagesTests`
(9 levels, level-0 coverage SHA). Verified visually: level 0 is a recognizable Hangar
(rails, helicopter with rotor, halfpipe), level 2 a quarterpipe skatepark. **Faithful
in shape/layout; rendered 2-tone (ink coverage).**

### Colour: palette SOLVED (2026-08-21), surface colour SOLVED (2026-08-23)

**Correction to the record layout** (from the loader `FUN_08010CC8`): the true level
table is base **0x087533FC**, stride 0x15C, record `{ … palette@+0x3C, dims W/H@+0x13C/
+0x13E, colourMap u16[W*H]@+0x140, cellRecTable(32B)@+0x144, objectList@+0x148,
elementLibrary@+0x14C }`. `GbaLevelImages` content-scans the {obj,elem} pair, so its
"record" pointer is **+0x144** past the true base — extraction still works (fields
repeat at stride), and the palette is now read at scan-record −0x108 (= true +0x3C).

- **Palette — SOLVED, media-derived.** Each level's BG palette is BIOS-LZ77 at
  record+0x3C → **512 bytes = 256 BGR555** (index 0 = green transparent key). All 9
  levels have one. The attract-demo palette (0x0851EF5C) decompresses **byte-identical**
  to the emulator PAL dump, and re-quantising the demo screenshot to it gives mean
  error 2.79/255 (just LCD gamma) — proof it is the colour source.
  `GbaLevelImages.TryGetPalette` exposes it; `gba-level` emits `level_NN_palette.png`.
- **Geometry (heightfield) — SHIPPED.** The per-cell grid at the true record's
  `+0x13C/+0x13E` (W,H) + `+0x140` (colourMap `u16[W*H]`) + `+0x144` (32-byte cellRecs:
  `[0]`=shape 0–7 = D4 orientation, `[+2]`=material 0–36, `[+8]`=height 20.12) is
  media-derived and renders as an accurate **isometric heightfield** — the real 3D
  level structure (cell = 3 world units; iso basis TW18/TH9). Verified across all 9
  levels (0 bad cells; level 0 = Hangar, level 4 = a two-section skatepark).
  `GbaLevelImages.RenderIsoHeightfield`; `gba-level` emits `level_NN_iso.png`. Surfaces
  are height-shaded and tinted per material FOR STRUCTURE VISIBILITY (not the engine's
  colours). NB the cellRec heightfield is the **collision/physics terrain** (its vtables
  are called from collision code 0x0801Bxxx, e.g. slope tests), which mirrors the
  visual surface — so it is a faithful geometry render, not the literal render mesh.
- **Surface colour — SOLVED + SHIPPED (2026-08-23, dynamic analysis).** The level
  colour is NOT a shader and NOT a software framebuffer rasterizer (the material
  vtable system is PHYSICS; `0x06000000` is never a literal because there is no CPU
  write to VRAM). The engine draws the level as **GBA Mode-2 with two affine 8bpp
  tiled backgrounds** — the appearance is **pre-baked isometric 8-bit tile art in
  ROM**, DMA'd to VRAM and hardware-affine-transformed. Live BizHawk read: DISPCNT
  0x1C42 (Mode 2), BG2/BG3 8bpp affine. Colour = the tile art through the palette
  (frame quantises to the +0x3C palette at **1.03/255**; every tile byte-exact vs the
  pool). **Decode** (all offsets from the true record base 0x087533FC): `+0x24/+0x26`
  = 2×(tile width, height); `+0x2C` = raw 8bpp 64-byte tile pool (index 0 transparent);
  `+0x34` = plane 0 tilemap (main surface: floor/corrugated wall, drawn IN FRONT);
  `+0x38` = plane 1 tilemap (detail, behind); `+0x3C` = palette. `tile(V)=pool+V·64`,
  V=0 transparent, no masking/flip. Two gotchas cost a day: the pool base was first
  read 44 tiles too high (→ scrambled wall), and the plane order was inverted (+0x34
  must occlude +0x38, else the detail plane covers the surface). `GbaLevelImages.
  RenderColourSurface`; `gba-level` emits `level_NN_colour.png`. All 9 levels composite
  coherent full-colour isometric surfaces (Hangar = corrugated metal roof + concrete
  floor; level 2 = a full skatepark). Pinned by `GbaLevelImagesTests` (level-0 1032×672
  SHA). Residual: a minority of +0x38 detail tiles show noise (off-screen areas the
  emulator frames don't cover, plus ~24 non-pool sprite/overlay tiles) — the surface is
  faithful, the fine detail-plane decode is a follow-up.
- The two earlier candidate fns were NOT the fill: **0x087FE660** = screen-space
  clip/project; **0x087FEB90** = OAM affine setup for the skater sprite.

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

## SHIPPED (2026-08-21): GAX sequenced music (`gba-music`)

**Audio — sequenced music.** The THPS2 GAX
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

## FIXED (2026-08-23) — the two defects user review caught

Both are resolved; the levels now render as the real game art. Kept on the record
because the *cause* of each was not what the evidence first suggested.

1. **Garbled colour render — the maps are METATILED.** The pool base and the 8bpp
   64-byte tile format were **already correct**; the missing piece was an indirection.
   `+0x34`/`+0x38` hold **metatile** indices, not tile indices. `+0x30` is an LZ77
   stream of `nMeta × 4` u16 — each metatile names a 2×2 block of 8×8 tiles in
   row-major order (TL, TR, BL, BR). `+0x24/+0x26` are the level size in **tiles**
   (Hangar 258×168 → a 2064×1344 image), so each map is `(w8/2)×(h8/2)` metatiles.
   Counts at `+0x28`/`+0x2A` bound both tables exactly on all 9 levels, with zero
   out-of-range entries — so there are no flip/palette-bank/bias bits anywhere.
   Two red herrings worth recording: (a) *"art flows across tile boundaries in a
   contact sheet"* does **not** imply a bad base — this game's iso floor/wall tiles are
   genuinely near-seamless and were authored in horizontal runs; (b) the earlier
   "44 tiles too high" theory was wrong in both directions — a 381-candidate sweep
   (whole-tile and sub-tile phase) scored the declared base `+0x2C` best outright.
   Validation: seam scores 2–4× better than the old decode on all 9 levels, and NY
   City's "WHERE'S RIO?" ticker renders as **legible text**, which a misaligned tile
   decode cannot produce.
2. **Iso heightfield viewed from the wrong corner — it needed a mirror.** The
   horizontal term is `(gy − gx)`, not `(gx − gy)`. Measured, not assumed: every
   level's tall structures form a grid **row**, which `(gx − gy)` draws going
   right-and-**down**, whereas the art (e.g. the Hangar's vert ramp) runs
   right-and-**up**. The depth term `(gx + gy)` is unchanged by the mirror, so
   painter's ordering is unaffected. Corroborated by edge correlation against the
   corrected art (mirror wins 5/7 levels, by 2.7× on the Hangar and 8× on Marseille;
   the dissenters include Rooftops, where the correlation is negative either way, i.e.
   no signal). Note what did **not** work: silhouette IoU is degenerate here because
   the collision grid is ~100% occupied, so all 8 orientations give an identical
   filled diamond — and global edge correlation alone put a different winner on each
   level. Only the asymmetric structural feature settled it.

## SHIPPED (2026-08-24): shape-aware collision surface (`gba-level` `_iso` render)

User review caught that the old iso render turned quarter-pipes into walls and slopes
into staircases. Root cause: it sampled only the cell's base height (`cellRec[+8]`)
and ignored the shape byte — but the surface within a cell is a FUNCTION, not a value.

**The engine's height query** (ROM 0x08023168, transcribed): `gx = worldX/0x3000`,
`rec = cellRecs[cellIndex[gy*W+gx]]`, `(a,b) = shape[rec[0]](u,v)` where `(u,v)` is
the sub-cell offset and the 8 shapes are the **D4 square symmetries** with span
constant 0x2FFF (jump table 0x080231D4); then `h = materialVtable[rec[2]].slot0(a,b,rec)`
— the material vtable at 0x08745028 (37 × 20 bytes, five THUMB method pointers) has
**27 distinct slot-0 height accessors**. 74.7% of cells use `return rec[+8]` (flat);
the other quarter are real surfaces: 1-D cubic-Hermite quarter-pipe transitions,
piecewise-linear ramps, raised-region steps, thin rails, radial Hermites, bilinear
patches, diagonal splits.

**Implementation executes the ROM, not a transcription**: `Core/Formats/Gba/GbaThumbCpu.cs`
is a minimal ARMv4T THUMB interpreter (the accessors use only 44 instruction forms /
30 mnemonics, measured over all 8,520 cells × 9 levels; anything outside that set
throws rather than silently mis-computing — same approach as the N64 ERZ/MIPS work).
Three hooks: divide-by-cell (a runtime fn pointer at 0x03001E9C the code branches
through), signed divide (0x08001F6C), BIOS sqrt (SWI 8). `GbaCollisionSurface` owns the
grid/records/shape transform; `GbaCollisionRenderer` renders sub-sampled cell patches
through a z-buffer with true-normal shading and neighbour skirts. The one-cell
out-of-bounds kill-wall ring (base > 30 world units; Hangar 182 cells at 34.375) is
omitted so the playfield is visible — and note the base height at `+8` is a **signed
32-bit** value (the kill walls exceed 16 bits; a u16 read let them leak into the render
as a giant box around the level).

**Validation**: the C# heights match the research reference bit-for-bit — 213,000
samples (8,520 cells × 5×5) across all 9 levels, aggregate SHA pinned in
`GbaCollisionSurfaceTests` (plus per-level SHAs). The reference itself was validated
by a cross-cell edge-continuity test: the transcribed D4 table matches 78.5% of 52,110
border samples within 8/4096 world units (median 0), while all 11 controls (ignoring
the shape byte, the 7 wrong D4 relabellings, 3 random permutations) score 21–62% with
p75 residuals **1,400× worse**. Renders: the pool level is an actual bowl with curved
transitions and coping; the Hangar's vert ramp and quarter-pipe walls curve.

Known residuals (documented, not blocking): thin rails alias at 4 sub-samples per cell
(the raised band is narrower than a sub-sample); 18 of 27 accessors are executed
exactly but only classified numerically (4.3% of cells — a documentation gap, not a
rendering gap); the divide routine is modelled as truncate-toward-zero (differs from
floor only for negative operands, by 1/4096 unit); not validated against a live skater.

### Collision↔art alignment — SOLVED (2026-08-24), overlay SHIPPED

The engine's world→art transform, closed dynamically and then found to be ROM-stored:

```
artX = X0 + 16·(wy − wx)          (world units; collision cell = 3 units)
artY = Y0 +  8·(wx + wy) − 16·z
```

The 16/8/−16 px-per-world-unit constants are engine-wide; **the per-level origin
(X0, Y0) is a stored ROM field at the true record's +0x64/+0x68** (signed 24.8 fixed —
all 9 levels decode to whole pixels; verified 84–100% of interior playfield cells land
in-canvas per level, with the shortfall being the known asymmetric canvas crops). In
raw fixed-point the transform is pure shift arithmetic (`artX_24.8 = rec[+0x64] +
(wyRaw − wxRaw)`), corroborating the quantization.

**How it was closed** (BizHawk, attract demo — Hangar/School II/Marseille): an
execute-hook at the engine's own collision query 0x08023168 captured true world
coordinates; the absolute screen→art anchor came from NCC-matching screenshots into
the shipped colour render (median 0.96, proving the display is a 1:1 window of the
baked art); and the height anchor was the skater's **shadow** sprite (the skater
itself is airborne too often — an ill-conditioned RANSAC found three different wrong
consensus sets before the shadow fixed it). Joint fit: a=(−16.01,+15.99), b=(+7.98,
+8.01), c=−16.10 → exact (−16,+16,+8,+8,−16); median residual ~1.0–1.4 px over 417
anchors after resolving one frame of OAM pipeline lag. BG2X/BG2Y could not be read
(write-only, and this BizHawk's onmemorywrite passes no addr) — the NCC window match
replaced them. One instructive negative: the painted floor-tile seams sit ~(+4.5,−8.9)
px off the collision grid — same pitch, authored phase offset — which is why blind
FFT texture correlation was flat. Match geometry, never texture phase.

**Shipped**: `GbaCollisionRenderer.RenderArtOverlay` — the collision lattice + per-
material tint drawn over the level's own art via this transform (`gba-level` emits
`level_NN_overlay.png`; lattice lines bend up the quarter-pipe transitions exactly
where the art curves). Also corrected from user review: the standalone `_iso` render
was **vertically amplified 2×** (its projection halves the horizontal terms but didn't
halve the height term); HeightScale now matches the engine's 1:3 height-per-unit to
horizontal-per-cell proportion. Caveats: c=−16 was measured over ground z 0–10.5
(kill walls extrapolate); six levels' origins are ROM-read but not demo-visited; the
+0x64/+0x68 identification is numeric (three independently fitted origins matched the
fields), not traced through the loader's ldr instructions.

## SHIPPED (2026-08-24): textured 3D level models (`.gba` → Mesh tab → GLB)

A THPS2 `.gba` ROM now opens as an archive of its levels (`GbaLevelCarver`: one
0x15C level record per level, named from the ROM's own strings — record `+0x00` =
name, `+0x04` = location — plus the `rom.gbarom` companion the records dereference
into). Carved `levels/N_<name>.lvl.gba` records route through the mesh pipeline
(`MeshFileKind.GbaLevel`) and convert to a **textured 3D level model**: the
engine-exact collision surface (ROM-executed height functions, skirts, kill-wall
ring omitted) with the level's own composited art applied via the engine's art
transform as the UV projection — each surface point samples the art pixel that
draws it. All 9 levels convert (267,544 triangles), 0 glTF validator errors; the
pool renders as a real textured bowl viewable from angles the game never drew.
Iso-grazed steep walls stretch their art strip — the honest limit of one
pre-rendered view. Scale: 16 GLB units per world unit; Fly/Walk camera (eye 22).

## RESEARCH COMPLETE (2026-08-24): GAX timbre — awaiting C# port

The full GAX v1.99 synthesis pipeline is decoded and hard-validated (per-voice:
1758/1758 + 2113/2113 live mixer events exact; audio: 0.927 log-spectrogram
correlation vs the emulator's real output, control 0.334). Key mechanics: waves
are 1-based slots into the song's `sampleAddr` record array (133 records — the
shipped 101-record contiguity scan UNDERCOUNTS; index the table directly); wave
records at instr+0x0C carry loop/ping-pong/window + s16 finetune (1/32-semitone);
pitch = perf + vibrato + note + **orderTranspose×32** (the shipped "transpose not
applied" claim is RETRACTED — it applies at mix time) + finetune, stepped through
the pitch table at banner+0x60 (`523.251 Hz × 2^(p/384) × 2048`); envelopes are
timed slope points with sustain/loop; tempo = 59.7275/speed rows/s (default
speed 6 ≈ 9.955 — the 10.0 policy was 0.5% fast); mix rates are call-site
(15769/18158 Hz). Porting spec: the timbre research spec (kept with the probes
until ported). Remaining gap: the boot jingle's 13380 Hz init path (irrelevant
to the 11 songs).

## RESEARCH COMPLETE (2026-08-24): sprites/UI — and the skater is REALTIME 3D

**The skater has no sprite frames in ROM — it is a runtime software-rendered 3D
character** (64×64 8bpp OBJ whose pixels match no ROM stream; 17 consecutive
captures show smooth continuous rotation). The old "2496-byte streams = skater
anim frames" hypothesis is REFUTED: those are per-character COLOUR streams (first
256 B = the 128-colour skater palette uploaded to OBJ entries 100–227, proven
live; the rest BGR555 shading ramps — i.e. the software renderer's shading
tables). **So the GBA engine does have a 3D character pipeline** — model format
unknown, a new research target. What extracts cleanly (colour-proven vs live
palette RAM, arrangement via OAM): 123 skateboard decks (table 0x0874FECC,
LZ77 2048 B = 32×128 4bpp + 32-byte palette at the art's aligned end — beware
the off-by-one: the table-adjacent palette belongs to the NEXT deck and still
looks plausible), 15 skater portraits (char table 0x08775870 stride 0x4C, 32×32
8bpp, select palette 0x084A0B68), 14 level-select venue photos (level record
+0x44/+0x48, 64×64 8bpp, palette 0x084A0C4C), HUD/menu fonts, badges, icons.
In-level OBJ palette = record +0x40 entries 0–99 (the tail is a default-skater
bake the character stream overwrites). Unpinned (kept greyscale): grind sparks
(likely code-generated), SWITCH/NOLLIE/FAKIE badges, three dither sheets.

## SHIPPED (2026-08-25): both research ports

- **GAX timbre** → `GaxSynth` (instruction-faithful frame machine; byte-exact
  against the validated reference on songs 0/1/5; `gba-music` now renders real
  instruments/envelopes/tempo at the true hardware rates, mono). The two
  shipped-claim corrections (mix-time order transpose; 133-record wave table)
  are folded in.
- **Sprite art** → `GbaSpriteArt` + `gba-image`: 123 decks (each with its own
  trailing palette — the aligned-end rule), 15 portraits (record 13 =
  Spider-Man), 14 venue photos; all tables located by content, select palette
  found by its 200-byte prefix pairing. Fonts/HUD glyphs/badges remain
  research-only (address-pinned tables, some palette banks unproven).

## SHIPPED (2026-08-26): the skater's 3D model — decoded, carved, converted

**The skater model is fully decoded** (`GbaSkaterModel` + `GbaModelGeometryWriter`;
carve emits `models/NN_<name>.chr.gba` per roster character, `.chr.gba` routes
through the Mesh tab/CLI to a coloured GLB). It is a **morph-target** model — no
skeleton; every animation frame stores the complete posed vertex set — and **one
mesh is shared by all 15 characters**; a character contributes only its colour
ramps and material→ramp binding.

- **Model header** 0x08775CDC (32 B): `{u32 frameStride=864; u8 vertCounts[8]
  ={6,16,18,4,99,3,26,0} (Σ172); u8 normCounts[8]={8,16,18,6,99,4,8,0};
  u8 faceCounts[8]={8,16,20,6,178,2,36,0} (Σ266); u32 facePtr=0x08779DF4}`.
  Located by content: `frameStride == 4 + Σ ceil(v/4)·12 + Σ ceil(n/2)·4` (the
  engine's own bind arithmetic) with an in-ROM face pointer. **The identity has
  exactly one other hit in the ROM** — a second, clipless mesh header at
  0x744C98 (stride 460, facePtr = header+0x20; likely a level-object mesh, an
  open lead) — so the locate walks candidates until the clip closure holds.
- **Clips** directly after the header: 221 × `{u16 tickStart, u16 tickCount}`,
  then a u16 tick→frame remap (7,874 ticks) ending exactly at facePtr — the
  boundary is solved from that closure. 4,772 distinct frames.
- **Frame pool** 0x080383BC: 4,772 × 864 B (~3.9 MB, half the cart), ending
  exactly at character 0's first asset (which is how the base is recovered:
  poolEnd − frameCount·stride). Frame = 3 s8 anchor bytes + pad, per-sub s8
  (x,y,z) triples in 12-byte-aligned blocks, then packed u16 normals
  (encoding undecoded, unused). Frame 0 spans exactly 101 z-up units
  (deck −16 … head +85).
- **Faces**: 266 × 8 B `{v0,v1,v2, n0,n1,n2, u16 material(0..45)|0x80 flag}`,
  sub-object-local indices. Sub 4 = 99-vert body, sub 6 = 26-vert deck.
- **Characters** 0x0877582C, stride 0x4C, name-first (idx 13 "Spider-Man",
  14 "Mindy"): +0x40 outfit binding (8×48 B material→ramp rows), +0x44 colour
  LZ77 (2,496 B = 8 outfits × 312 B = 156 BGR555; palette entry = 2·rowValue,
  export takes the mid shade +6 of the 12-shade ramp). Verified by the
  can't-pass-by-accident render: Spider-Man comes out in his red-mask/blue-suit
  scheme, Hawk in skin/blue shirt/khaki; both GLBs Khronos-clean.

Deferred: the u16 normal encoding (lit shading), the 0x80 face flag (wheels),
and the 0x744C98 sibling mesh. (Animation export and clip naming shipped the
same day — see below.)

## SHIPPED (2026-08-26): each skater wears only its own parts

**The roster record's u32 at `+0x04` is a sub-object visibility mask** — bit `i`
draws sub-object `i`. All 15 characters share one mesh, so before this every
export drew every part: Tony Hawk came out wearing Muska's hood *and* a
ponytail, and both leg styles at once (a user report).

Three independent facts fix the reading beyond coincidence:
- the ONLY sub-object no character draws is **sub-object 7, the only EMPTY one**
  (0 vertices);
- the only two every character draws are the **body (4)** and the **deck (6)**;
- **sub-objects 1 and 2 occupy the same space at the feet** and every character
  takes exactly one — two leg styles, 9 characters vs 6.

It then explains both reported defects unprompted: **sub-object 5** is a flat
3-vertex plane behind the head worn by exactly **Elissa Steamer and Mindy**, the
two female skaters — the ponytail; **sub-object 0** sits on top of the head and
is worn by four characters **including Chad Muska** — the hood. Triangle counts
now vary per character (Spider-Man 234, Muska 242) where they were a uniform
266.

## FIXED (2026-08-27): holes where staircases and benches stand

A user circled black holes in the collision surface that are staircases and park
benches in the artwork. They were not the art-surround problem below — those
cells were being **rejected as out-of-bounds kill wall**.

The kill-wall test read the cell record's raw base-height word at `+8`. For most
materials that word is the height, but **material 30 stores something else
there**: its cells read as absurd values (98304.75, 65536.00, 86017.00,
131073.50) while the surface their own height function returns sits on the
playfield. Since the surface is computed by *executing the material's function*
with the record, the raw word was never the thing to test.

The test is now the sampled surface, and it is right in **both** directions:

- **62 cells gained** — School II 21, NY City 38, Skate Street 3. School II's
  are unmistakable once sampled: `(19,15)` 8.50, `(19,16)` 7.00, `(19,17)` 5.50,
  `(19,19)` 4.00, `(19,20)` 2.50 — a descending staircase — plus `(20,24)`
  0.00..1.50, a bench.
- **48 cells newly rejected** — all Marseille's top border row, material 11,
  raw word 10.50 but sampled surface at **34.38**, the kill height. The raw
  reading had been drawing a strip of kill wall across the level's edge.

Six levels are unaffected in either direction (the Hangar stays at exactly
14,739 triangles). School II goes 52,245 → 52,753. Both the 3D writer and the
2D isometric render share the one test, so both stopped holing.

## FIXED (2026-08-27): black slabs where the art draws nothing

A user reported objects "missing / displaying black" in School II. The cause is
structural: **the collision grid is a rectangle, the authored art is not.**
School II has a deep notch between its two building wings; Rooftops is two
separate buildings; the Pool is a small shape in a large canvas. Surface cells
over those regions had no art to sample and emitted as flat black slabs.

The art is the authority on where the level exists, so a cell it never draws is
no longer emitted (`GbaLevelArtCoverage`). Measured on School II: 9.0% of the
mesh's vertices sampled pure black, and they sat at a mean height of −3 against
the level's own mean of +18 — low ground in the notch, not tall walls poking
past the art.

**Undrawn is pure black REACHABLE FROM THE CANVAS EDGE, not merely pure black.**
That distinction is load-bearing: the drawn art contains black pixels of its own
(20,178 in Rooftops, 1,497 in Warehouse), and dropping cells over those would
punch holes in real geometry. The separation is clean — 99.991% of School II's
black is border-reachable surround, and **no drawn pixel in any level is pure
black** (the darkest sums to 8 of a possible 765). A quad is dropped only when
**all four** corners are undrawn, so a cell straddling the art's edge keeps the
level's rim rather than eroding it.

Four of the nine levels have no undrawn pixel at all, so no mask is built and
their geometry provably cannot change (the Hangar stays at exactly 14,739
triangles). School II drops 57,783 → 52,245; the corpus total 267,544 → 257,569,
all nine still Khronos-clean.

Residue: a facade that extends below where the art draws it still shows a black
strip (Rooftops' lower-left corner) — those quads have a drawn corner, so the
all-four rule correctly declines to remove them. Trimming skirts by their lower
edge alone was tried and reverted: it removed 324 triangles corpus-wide with no
visible change, which is not enough to justify the rule.

## OPEN: per-face shading (the u16 normals)

Characters export one flat colour per material — the **mid shade (index 6) of
that material's 12-step ramp**, which runs dark→bright (e.g. Spider-Man's red
(64,0,8) → (248,8,32)). The engine instead picks a shade per face from the
lighting, so the export reads flatter and duller than the game (user report).
Matching it needs the u16 normal block, which is **not decoded**. What IS
established:

- The face record's `n0/n1/n2` really do index the per-sub-object u16 arrays:
  **the maximum index used equals `normCount−1` for every sub-object**.
- The values are **per-frame** (0 of 159 identical between frames 0 and 2000).
- **Index → direction is an exact function**: on the deck (26 vertices but only
  8 normals) each index groups faces with *zero* geometric spread — e.g. index 0
  = (0,0,1), index 3 = (0,0,−1).
- The top 3 bits are an **antipodal code**: `c` and `7−c` are exactly opposite
  directions (0↔7 = ±z, 1↔6 = ±x, 5↔2 the ±(0,∓0.45,∓0.89) pair).
- **Refuted encodings**: 5/5/5 signed in all six axis orders (best mean dot
  0.21), byte-pair spherical in four conventions (best −0.12), signed-byte xy
  with derived z (−0.06). None is the scheme.
- A **2,180-entry empirical `u16 → direction` map** can be harvested from
  flat-shaded faces alone, if a table-driven decode is ever preferred to
  cracking the encoding.

Note that even a decoded normal is only half of it: the shade-selection rule and
the light direction are equally unknown, and this repo does not invent lighting
rigs (the PSX precedent exposes only rigs the binaries name).

## SHIPPED (2026-08-26): animation export — all 221 clips, as MORPH TARGETS

The engine is a pure **morph player**: each frame stores the complete posed
vertex set and the renderer draws whichever frame the clip's tick→frame remap
names — and glTF expresses exactly that, so the export is morph targets.

**A skinned rig was tried first and rejected on measurement.** A bone-per-vertex
rig (172 bones) works and plays everywhere, but 172 bones for a humanoid + board
is not a usable rig. Reducing it needs vertices that move rigidly together, and
they do not: even the solid **deck's own vertices drift 6 units apart** across
the pool, and only 0.9% of vertex pairs hold a constant distance. Fitting rigid
bones costs (model ~101 units tall): **128 bones → 1.5% worst error, 64 → 2.2%
(RMS 0.68, below the s8 quantization noise), 30 → 9.1%, 6 → 63%.** A
humanoid-sane ~20 bones is unreachable, so the model is genuinely hand-animated
per vertex and morph targets are the honest representation.

- **Targets are the clip's distinct POSES**, keyed by pose rather than by frame:
  a hold reuses one target, and a frame whose pose IS the base contributes none.
  That matters mechanically — an all-zero target is dropped on write and would
  silently shift every later target's index, corrupting the weights.
- **Base mesh = pool frame 0**, the same neutral pose the static export writes,
  so a target is the plain difference from it and the static export is untouched.
- **Weights are one-hot per tick** (all-zero shows the base). LINEAR, at 1/60 s.
- **51 clips are motionless** — every frame of them IS the neutral pose, i.e. the
  port ships those animations as static placeholders. They export as that pose
  with no animation, byte-identical to the static export. The remaining **166
  clips animate, 4,826 targets total, mean 29 per clip.**
- **One GLB per clip**, because a weights track carries one value per target per
  key: all 217 clips in one document would mean 4,826 targets and a weights
  array in the gigabytes. Per clip it is ~29 targets and a few hundred KB.
- Verified exactly: evaluating the exported targets and weights reproduces the
  ROM's pose for **every tick at 0.000000 error**.
- Morph deltas are keyed by base-vertex geometry, so the writer emits
  **averaged per-vertex normals** rather than per-face ones — with face normals
  two distinct vertices (69 and 94, which separate by 70 units mid-clip) collide
  on one key. After averaging the only remaining collision is a pair that is
  identical in all 4,772 frames, for which sharing is exact; the exporter
  verifies that and declines to morph rather than tear if it ever fails.

Facts shared by both representations:
- **The 3 per-frame anchor bytes are the pose's AABB centre** (measured 200/200
  sampled frames) — a render/cull pivot for the 64×64 sprite, **not root
  motion**. Applying them as translation would double the motion; the exporter
  deliberately ignores them.
- **The tick→frame remap is honoured tick by tick**: 73 of the 217 non-empty
  clips hold or reorder frames, so a frame range misplays them.
- 4 clips are authored-empty (65, 66, 84, 85); 51 resolve to a single distinct
  frame (the pane's "Hide single-frame poses" filter catches those).
- **60 ticks/s is an explicit export policy**, not measured runtime (GBA video
  runs 59.7275 Hz).

Opt-in via `mesh --gba-animations` / `--gba-animation <n>`, or the GUI
Animations pane (which also fixed a latent gap: neither GBA kind had a scanner
arm, so carved levels and characters never appeared in the Mesh tab at all).
Every output is Khronos-clean; an authored-empty or out-of-range selection falls
back to the static export, byte-identical and SHA-pinned.

## SHIPPED (2026-08-26): the cart embeds a real `tricks.bin` — 105 clips NAMED

**The GBA port carries the same Neversoft trick-table bytecode the PS1 discs
ship** (`docs/formats/psx-tricks-bin.md`), re-encoded by Vicarious Visions —
found by tracing opcode `0x01` into the very clip table `GbaSkaterModel`
documents. Verdict: **PROVEN**.

- **Container**: base `0x0842A9E0`, 20,509 bytes. Header = 8×s16 with `[7]==0`
  (the PS1 shape); sections 0/1 are per-skater (15 entries each), 4/5/6 global,
  each addressing 8-byte record lists `{u8 kind, 3 bytes, s16 scriptOffset,
  u16 flags}`; 2/3 hold script-offset arrays whose length is **not** structurally
  bounded (an open limit — it does not affect naming).
- **The opcode-width table is READ FROM THE ROM**, not assumed: the
  `Trick_Skip` dispatcher (`ldr r0,[r0]; mov pc,r0` preceded by `cmp rX,#0x5A`,
  unique in the ROM) leads to a jump table whose case bodies state each width.
  **13 widths differ from the PS1 retail table** — `0x01` is 2 bytes here vs 3
  there, `0x39` counts u8 not u16 — so the PS1 table desynchronises within a few
  records. This is exactly why my first byte-sweep read `0x200|clip`: the record
  is `01 <u8 clip>` and the `0x02` was the *next* opcode.
- **Everything is content-located**: the base is the literal-pool word that both
  satisfies the header identity AND has a `ldr [pc]` site (the identity alone
  matches three places); the extent is the furthest terminator reachable from
  the bounded record lists. Later VV carts have no `0x5A`-bounded dispatcher, so
  the locator declines them.
- **Census**: 174 tricks, 146 distinct names, clips referenced ≤ 200 of 221.
  **105 clips are uniquely owned and get their real name**; 116 keep `anim_N`.
- **Oracles that cannot pass by accident**: (a) names stating a flip count match
  the deck's measured rotation — Triple Kickflip 1127° → 3, Double Hardflip
  608° → 2, Kickflip 369° → 1, grinds/grabs ≈ 0 — and the ±1 controls collapse;
  (b) mirror pairs roll in opposite directions 7/8 (shuffled: 47%); (c) the 16
  clips two names share are **real skating identities the extractor was never
  told** — BS Boardslide ≡ FS Lipslide (136), BS Smith ≡ FS Feeble (58), BS
  Crooked ≡ FS Overcrook (57); (d) 11/11 uppercase/lowercase twins are explained
  by a prologue `0x01` naming the twin's own clip.
- **The UPPERCASE list is the special-variant animation set**, not a duplicate:
  KICKFLIP plays clip 149 where Kickflip plays 20. Both are uniquely owned, so
  both are named and no arbitrary choice arises. **But casing is the only thing
  separating those eight pairs and consumers compare names case-insensitively**
  — the GUI pane dedupes rows by display name that way and was silently hiding
  8 clips (it reported 209 of 217 matching, which is how the defect surfaced),
  while the exporter suffixed one `KICKFLIP_2` as if it were a copy. Such names
  now carry their clip (`Kickflip (20)` / `KICKFLIP (149)`): factual, and it
  claims nothing beyond which clip each one is.
- Six named flip tricks measure 0° — `{540 Flip}`, `{Shove It Rewind}`,
  `Varial`, `{Darkslide}`, `{Ho Ho Handplant}`, `One Foot Invert`. All six are
  single-distinct-frame clips: **the port ships those animations as static
  placeholders**, so this is a fact about the cart, not a mapping error.

## Next steps (in order)

1. **Skater model follow-ups** — the u16 normal encoding, the 0x80 face flag,
   the 0x744C98 clipless sibling mesh (see §SHIPPED 2026-08-26).
2. **Fonts/HUD glyphs/badges extraction** (research complete in §sprites; needs
   either content-anchors for the pointer arrays or accepting fixed addresses).
3. **Detail-plane (`+0x38`) residue.** The main surface plane (`+0x34`) renders
   faithfully, but a minority of `+0x38` tiles still show noise: off-screen level
   areas the captured frames could not validate, plus ~24 tiles that are not in the
   pool at all (likely sprite/overlay art from another source). Bounded follow-up —
   re-capture frames covering more of a level, or find the second pool.
2. **Level-record `+0x30` face/quad list.** Decodes as stride-18 quads
   `v, v+1, v+18, v+19` with an unidentified consumer — still open. (The skater
   half of this item is RESOLVED: see §SHIPPED 2026-08-26 — the skater is a real
   stored 3D model, now exported.)
3. **GBA tile-sheets / sprites.** THPS2's 221 32-aligned LZ77 tile sheets
   (2048-byte ×126 = 64-tile 4bpp sheets, sprite/anim frames, fonts) are located but
   their palette + arrangement binding is runtime state — needs a palette-pairing
   heuristic or a loader trace before a faithful render.
4. **Later-cart art container:** RE THPS3's 884-byte streams and the THPS4→DHJ
   packaging (they abandoned BIOS LZ77) to extend image extraction across the line.
5. **GAX PCM timbre + cross-cart music** (see the music section above).
6. Later titles (THPS3+, the "real 3D editor" the dev interview mentions) may store
   scene data differently — recheck once a later-cart container is cracked.
