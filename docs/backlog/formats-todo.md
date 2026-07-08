# Backlog — Unimplemented / Deferred Formats

Created 2026-07-03. Distilled from `CLAUDE.md` (*Deferred Items* / *Not Yet Implemented*) + `memory/`.
**Re-verified 2026-07-07** with a full-corpus extension census (all 27 build dirs), magic-byte probes, and
conversion sweeps — several entries here turned out stale (see *Done* below). NxTools (`Sample/nxtools`)
was surveyed as a reference source: it covers THUG2/THAW scene+tex families across xbx/wpc/xen/ps3 and
Downhill Jam (`thdj` = ngc/wii, later engine gen), plus a full PS1 `.psx` importer — but has NO coverage
for `.stex` payloads, P8/THPG `.col`, or THAW GameCube.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔶 THAW GameCube platform — textures ✅ shipped 2026-07-07; meshes/collision remain
- Source: 2026-07-07 corpus census + format RE session.
- ✅ **Textures done**: `.tex.ngc` (722) + `.img.ngc` (2,647) parse via `NgcTexFile` (extended from the
  earlier committed skeleton). Format (established via PC↔GC Rosetta pairs, pixel-exact on `anl_pigeon`,
  MAE 0.46 vs the PC DXT decode): dictionary header (u8 ver=1, u8, u16be count, u32be tableOffset=8) +
  count×32B record table + data region. Record: ver=4, depth=32, u16, u32be checksum, u16, log2W, log2H,
  mips, gxFormat, u8, u8, u32be colorSize, u32be dataOffset (ABSOLUTE), u32be alphaOffset (ABSOLUTE,
  FFFFFFFF=none), u32be 0. `.img.ngc` = bare record, no dict header. Only two GX formats in the corpus:
  CMPR (0x0E, 4,266 records — 8×8 tiles of four DXT1 blocks, BE colors, MSB indices; DXT5-equivalents get
  a SECOND CMPR chain at alphaOffset whose GREEN channel is the alpha, the same trick as THUG GC
  `texture.cpp`) and format byte 0x06 (224 records) which covers BOTH RGBA8 (180 — 4×4 tiles, AR/GB 32B
  planes; real height = colorSize/(4·width)) and C8+RGB5A3 palette (44 — CAS icons/banners; distinguished
  arithmetically: colorSize == width×rows + 512). Images are stored bottom-up (y-flip on decode).
  **Sweep: 3,369/3,369 files, 4,630+ textures, 0 failures** (one 32-byte count=0 stub parses as empty).
- 🔴 **Meshes** (`.skin.ngc` 588, `.mdl.ngc` 134): NOT the THAW PC/Xbox scene format (no BABEFACE, no
  shared structure). THUG GC source (`Sample/thug/Code/Gfx/NGC/p_nx.cpp` `s_plat_load_scene_guts` +
  `NGC/NX/scene.cpp`/`mesh.cpp`/`material.cpp`) shows the ancestor: sSceneHeader + blend/texture DL
  tables + material headers/passes + object headers, with geometry inside **GX display lists** (GPU
  command streams). A converter = GX display-list parser (vertex attribute arrays + indexed draw
  commands) — comparable scope to the THAW PS2 VIF replay work. PC↔GC Rosetta pairs exist for validation
  (e.g. `anl_pigeon.skin.wpc` 3,120B vs `.skin.ngc` 1,812B).
- 🔴 **Collision** (`.col.ngc` 722): layout fully mapped 2026-07-07 but **conversion is blocked on the
  mesh project — GC col files ship WITHOUT vertex positions.** Layout: 24B BE header (version=10,
  numObjects, totalVerts, totalFaces, ssRows, ssCols) + 32B scene bounds + 64B object records (checksum,
  u32 numVerts, u16 numFaces, u32 firstFaceOffset in bytes, bboxMin/Max 4×f32 each, u32 0, u32 firstVert
  INDEX, u32 optOffset, pad) + data: vertex+intensity region (ALL 0xFF-wiped — verified on trigger boxes,
  props, and the 3,950-vert sec_jimbo_xen level file), faces (always 10-byte large records: u16be flags,
  terrain, i0, i1, i2 — triangulation matches the PC pairs exactly), then BSP/opt tables with per-object
  face-index lists. The engine reconstructs collision vertices at runtime (likely from the render scene) —
  so standalone .col.ngc → glTF is impossible; fold into the GX display-list mesh project and share its
  vertex sources.
- ⚪ `.apk.ngc` (4,424) = anim packs (likely BE .ska variants); `.mpk.ngc` = 32-byte padding stubs.

### 🔴 `.stex` — raw streaming-texture payloads (NOT a self-contained container)
- Source: 2026-07-07 probe (re-scoped). ~3,400 files: THAW PS2 (2,423), P8 (365), THPG (627), extracted
  from PAKs via QbKey `.stex` = 0x2B0A3095.
- Evidence: leading magics are all over the map (floats, 0x80808080 fill, small ints, VIF-like data) —
  these are headerless streamed texture DATA blobs whose dimensions/format metadata live elsewhere
  (zone catalogs / scene tex metadata). `ZoneTextureCatalog` already consumes `.stex`-typed PAK entries
  for worldzone texturing; the `xbxtex` CLI `.stex` route covers only ABADD00D-headed Xbox/PC variants.
- What's left: pair standalone PS2 `.stex` blobs with their metadata source (likely the same-checksum
  `.tex.ps2` scene metadata or zone blobs) before standalone conversion is possible. Research item.

### 🔴 THAW `.tex.ps2` scene texture metadata (NOT the same as THUG TEX)
- Source: `CLAUDE.md` → *Not Yet Implemented*.
- Evidence: 328 files, each a companion to the same-named `.skin.ps2`. Header = model checksum + per-texture entries with GS register values (TEX0, MIPTBP), dimensions, and texture checksums. The PC equivalent (`.tex.wpc`) uses `0xABADD00D` magic with DXT-compressed pixel data. Internal texture checksums are **not** QbKey hashes.
- What's left: parse the metadata (currently the worldzone/skin paths resolve textures by TBP/CBP or via the companion TEX pool rather than this per-model metadata file). Low urgency — textures already resolve for skins/worldzones through other paths; this would add explicit per-model texture binding.

### 🔶 STR (PS1 MDEC) video — VLC drift on longer streams
- Source: `memory/str_mdec_decoder_status.md`.
- Evidence: IDCT, YCbCr→RGB, block ordering, and the VLC table are all verified identical to jpsxdec — but **VLC decompression drifts after ~600 blocks / ~2617 codes**. Notably both our code AND jpsxdec's standalone `makeV2` fail the same way; only jpsxdec's full-disc pipeline succeeds, suggesting the bug is in stream framing / sector assembly rather than the codec core.
- What's left: diff our sector/stream assembly against jpsxdec's full pipeline (`Sample/jpsxdec_v2.0/`, source compiled under `Sample/jpsxdec/`). STR is listed as a supported format and converts short clips; this is a correctness gap on longer content, not a total failure.

### 🔴 PPV runtime container (Spider-Man PSX prototype)
- Source: `CLAUDE.md` → *Deferred Items* → *Unsupported Game Asset Formats*.
- Evidence: `BVmC` magic; 14 files under `WTC/SOUNDS` in *Spider-Man (2000-2-4, PSX — Prototype)*. Appears to be a real runtime media container (audio-first), not a tooling artifact. No in-repo or open-source parser reference yet.
- What's left: research the `BVmC` container (treat as audio-first). Deferred pending a reference or a decision it's worth the reverse-engineering effort. Low priority (14 proto-only files).

### 🔴 THPG / Project 8 `.col` (newer collision version)
- Cross-ref: `game-thpg-p8.md` (full evidence there). Newer `.col` container (`0x00FF00FF`-prefixed) not decoded. Listed here too because it's a format gap, not just a per-game gap.

---

## Done (for reference) ✅

- ✅ GS-alpha export scaling (128=opaque → PNG 255=opaque) — `memory/ps2_alpha_export_scale.md` (v1.2.1). `DecodePixels(rawGsAlpha)`: export scales ×255/128, GS replay keeps raw.
- ✅ VID1 (THAW GameCube movie container) → MP4 — shipped (`vid` CLI command + Video Converter tab); the old `CLAUDE.md` "Deferred > VID" note predates it.
- ✅ **THAW PC textures (`.tex.wpc` / `.img.wpc`, 0xABADD00D)** — already shipped as
  `ThawTexFile`/`ThawImgFile` (routed via `xbxtex`); the old "Not Yet Implemented" note was stale.
  Verified 2026-07-07: **723/723 tex.wpc + 2,480/2,480 img.wpc, 0 failures, 4,472 textures**.
- ✅ **THUG2 `.scn.xbx` level scenes** — same format as `.skin`/`.mdl` (version triple 1,1,1); extension
  routing added 2026-07-07 (`9eb2680`): 192/192 files, 3,005,651 triangles, 0 validator errors.
- ✅ **PS1 `.psx` character meshes** — the "garbled body parts" claim was stale; 2026-07-07 five-build
  sweep: 490 character files, 0 real failures (non-conversions = texture-only costume files). See
  `mesh-fidelity.md`.
- ✅ Dev-artifact non-formats identified 2026-07-07 (no work needed): `.usg`/`.usg.ps2` = memory-usage
  build logs (text), Spider-Man `.tex` = hash manifests (text), `.psh` = C headers, `.mpk.ngc` = padding
  stubs, `.cas.*`/`.fam.*` = appearance config data.

## By design / won't-fix ⚪

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums (`CLAUDE.md` → QBKey section; `tools/ghidra/thps2-psx-proto/output/psx_decompiled.c`). GHIDRA string extraction found 0 texture matches across 15 executables. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).
