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

### 🔶 THAW GameCube platform — textures ✅ 2026-07-07, meshes ✅ 2026-07-08; collision remains
- Source: 2026-07-07 corpus census + format RE sessions (textures 07-07, meshes 07-08).
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
- ✅ **Meshes done** (`.skin.ngc` 588, `.mdl.ngc` 134): GX display-list parser shipped 2026-07-08 as
  `Core/Formats/Mesh/XbxScene/NgcSceneFile.cs` (produces XbxScene, shares the Xbox glTF writer; routed
  via `mesh` CLI + GUI Mesh Converter). Container = THUG GC `s_plat_load_scene_guts` layout with a
  64-byte extended header (0xAAFFEEFF sentinel at +0x2C). Full spec in CLAUDE.md. Key discoveries:
  skin positions are s16**/32** (THAW halved THUG's 1.9.6 shift), UVs s16 (u=a/1024, v=1−b/1024),
  material passes reference textures by INDEX into the companion `.tex.ngc` (record order).
  Rosetta-validated: pigeon exact vs PC (46 verts/45 tris both), ped_baller UVs+normals vs PS2 decode.
  **Sweep: 722/722 files, 427,343 triangles, 0 failures, 0 glTF validator errors**; textured renders
  verified (ped_baller Lakers jersey, pigeon alpha-cut wings, board_default griptape+trucks).
  Diagnostics: `tools/diagnostics/ngc_scene_probe.py` (structural walk + OBJ dump of any .ngc scene).
- 🔴 **Collision** (`.col.ngc` 722): layout fully mapped 2026-07-07 but **GC col files ship WITHOUT
  vertex positions** (vertex+intensity region 0xFF-wiped on disc; faces/BSP intact; engine rebuilds
  positions at runtime). Now that render meshes decode, a reconstruction pass matching col faces to
  scene geometry is feasible — but col objects (trigger boxes etc.) don't all have render twins; needs
  a study of how the engine sources the vertices. Layout: 24B BE header (version=10, numObjects,
  totalVerts, totalFaces, ssRows, ssCols) + 32B scene bounds + 64B object records (checksum, u32
  numVerts, u16 numFaces, u32 firstFaceOffset in bytes, bboxMin/Max 4×f32 each, u32 0, u32 firstVert
  INDEX, u32 optOffset, pad) + 0xFF-wiped vertex region + faces (always 10-byte large records: u16be
  flags, terrain, i0, i1, i2 — triangulation matches the PC pairs exactly) + BSP/opt tables.
- ✅ **`.apk.ngc` / `.pak.ngc` archives — extraction shipped 2026-07-09, offset model CORRECTED
  2026-07-10.** They are big-endian Neversoft PAKs (sentinel-detected; `PakArchive` handles both
  endians). `.mpk.ngc` = the companion DATA file (like PS2 .pab), not padding — 3,603 of 4,424 are
  32-byte stubs (self-contained apk), 821 carry real data for cutscene apks. GC quirks vs PS2:
  name QbKey at +0x0C, flag 0x80000000 = data-in-pak (absent = companion-resident at RAW stored
  mpk offsets). **All PAK data offsets (LE and GC in-pak) are relative to the entry's own header
  position** (Queen-Bee `HeaderStart + FileOffset`); the 2026-07-09 "hoisted tiling" model was a
  near-equivalent approximation, and the original absolute-offset reads silently garbled every
  multi-entry LE pak. Signature-validated 2026-07-10: PS2 12,120 + PC 12,756 + GC 14,325 payload
  hits, 0 mismatches (`tools/diagnostics/pak_offset_check.py`). 48 `*_sfx.pak.ngc` = raw audio
  blobs (skipped). Routed: `archive` CLI, `unpack`, GUI Archive Extractor.
  ⚠️ **Sample/Builds pak-extracted subtrees predate the offset fix** — payloads extracted from
  multi-entry paks (qb.pak, cutscene mains, cas paks, worldzone paks) are byte-garbled on disk
  and need regeneration via `tools/SampleGenerator`.
- ✅ **THAW QB decoding — shipped 2026-07-10 for ALL THREE platforms** (`.qb.ps2`/`.qb.wpc`/
  `.qb.ngc` + `.sqb.*`): THAW uses the sectioned QB format (Guitar Hero family, Queen-Bee
  reference at `Sample/queen-bee`), NOT the raw THPS3-THUG2 token stream and NOT "BE tokens with
  a size prefix" as previously guessed. `QbSectionParser` (auto endian + old/new info-encoding
  detection, LZSS scripts, THAW tokens 0x47-0x4A, inline-script struct items) synthesizes classic
  token streams for the existing decompiler. Sweep: 11,909/11,909 files, 49,755 scripts, 0
  failures. **Name resolution 97.1% PS2 / 99.2% PC / 89.1% GC** via 137,054 pairs harvested from
  the shipped `dbg.pak` debug archives (`QbKeyNames.ThawDbg.txt`,
  `tools/utilities/harvest_thaw_dbg_names.py`).
- ✅ **THAW animation family — shipped 2026-07-10 for ALL THREE platforms** (`.ske` + `.ske.ngc`,
  `.ska` + `.ska.ngc`): the GC files are field-for-field endian mirrors of the PS2/PC ones, and
  NO platform parsed them before (the old "BE payload" framing was wrong twice over). THAW SKE
  (`ThawSkeletonFile`, 973/973): u16 version=1 + u16 hdrSize=0x30 header, vec4[N] local
  translations, mat4[N] PRECOMPUTED inverse bind matrices, name/parent/flip QbKey arrays.
  THAW SKA v0x28 (`SkaFile.ThawParser`, ~21,350/21,350 incl. P8/THPG grammar-verified): THUG
  compressed grammar + THAW deltas verified against the THAW PS2 ELF key readers (bit16 scalar
  table, bit15 compact bytes, bit8 u16 timestamps, bit19 partial mask, bit28 hi-res float
  camera/object masters, bits 14+17 additive translations). Key blobs + standardkey tables ship
  raw LE even on GC. **The rumored cutscene `.ska` "descriptor block with embedded cam pak path"
  does NOT exist** — that data is `<name>_cam_pak_info.qb.ngc`, a sectioned QB string array the
  QB parser already handles. Camera masters export as named node-TRS GLB rigs via `ska`.
  Diagnostics: `thaw_anim_pairs.py`, `thaw_ske_probe.py`, `thaw_ska_probe.py`,
  `ska_version_census.py`. Remaining niceties: bit28 custom-key event decode (35 files carry
  them; skipped, Q/T unaffected), glTF camera node with FOV for cam rigs, THAW skin+anim
  combined export (needs QbKey-based track binding through CAS rigs).

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

### ⚪ PPV container — RESOLVED 2026-07-10: not a Neversoft format (out of scope)
- The *Spider-Man (2000-2-4, PSX — Prototype)* build is a **multi-game demo disc** (SCED_026.36:
  HARNESS.EXE menu + DD/GK/INFEST/MILLE/POCKET/RAYMANII/REVOLT/SYDNEY/TENCHU2/XMEN/SPIDEY/WTC).
  Every `.ppv` (incl. `WTC/FE/FE.PPV`), every `.zoo` (79), `.bfx`, `.dhs` etc. lives under `WTC/`
  = **TOCA World Touring Cars (Codemasters)** — a different developer's engine sharing the disc.
  The actual Spider-Man demo is `SPIDEY/` (CD.WAD/CD.HED — already supported).
- This is why the 2026-07-09 exe hunt found nothing: the "overlay" was never a Spider-Man
  overlay. WTC's own code ships as plain files (`WTC/WTC.EXE`, `GAME.OVL`, `TIMTRIAL.OVL` — no
  disc dump needed), though they carry no literal `BVmC`/".PPV" references either (loader likely
  resolves via .ZOO tables). Disposition: out of scope like `.bik` — Codemasters formats belong
  to Codemasters tooling. Same applies to the `.zoo`/`.bfx` census entries.

### 🟡 PSX animated surfaces — UV wibble supported; pulsing-colour playback pending
- Source: decomp contract `thps2-psx-proto docs/wibbly_texture_animation.md` (2026-07-09; `M3dInit_FlagZeroWibbles` + `uWibble`/`vWibble` PERFECT).
- Face bit5 (0x20) "animated texture" is UV scroll + per-vertex sine wibble, never an image flipbook. Actual animation membership comes from tagged chunk 6's wibble table, not the flag alone.
- **Implemented 2026-07-17:** parse `pTexWibData`, retain every emitted vertex's velocity/frequency/amplitude/phase and texture dimensions in `ModelDocument`, write a correct frame-zero fallback, transport the parameters as application-specific GLB vertex attributes, reproduce the native 64-sample fixed-point table in the live viewer, and build a timeline-driven UV shader in `.blend` exports. Core glTF consumers that ignore custom attributes continue to display frame zero; core glTF has no portable per-vertex UV-animation channel.
- **Spider-Man PC v6 contract (verified against `SpideyPC.exe` 0x0047619F-0x00476259):** tag-6's legacy base-UV bytes are non-authoritative (zero placeholders or redundant byte-range copies); animation starts from each face's widened UVs in the fixed 512-coordinate space. The PC path doubles only the scrolling term. Treating L2A1's zero placeholders as base UVs collapsed animated faces to one texel, while normalizing motion by the decoded (often much smaller) texture dimensions made it appear several times too fast.
- What's left: animate parsed `pColourPulseData` (`{r,g,b,Interval}` keyframe lists) instead of exporting only its authored initial phase. UV formula now implemented by the viewer: `U = (u<<8) + (t*uVel>>4) + WibbleTables[amp][(t*Freq>>10)+phase*4 & 63]`, with the engine's 16×64 LUT reconstructed from `rcossin_tbl`.

### 🔶 PSX level-object animation export (skeletal path)
- Source: decomp contract `thps2-psx-proto docs/level_object_anim_binding.md` (2026-07-09; RunAnim/CycleAnim/CalculateAnimOrder PERFECT).
- Binding chain is fully known: item→region by filename (`Spool_FindRegion`), stream selected by the item's own `mAnim` index into the region's `pAnimFile` table (stride 8, count-prefixed — NOT stream-i→item-i), per-bone positional with parent tree from `pHierarchy` (`mapTable[bone]=parent`), cross-model retarget by name via CalculateAnimOrder. `has pAnimFile ≡ IsSuper` — animated level objects (traffic cars etc.) are CSuper instances on the same skeletal path as characters.
- What's left: teach the PSX level exporter to enumerate anim streams in hier-level files (skdown: 836 placed objects) and emit glTF animations per placed object. MEDIUM-confidence open question: whether placed level geometry also uses the name-keyed tag-0x45 packet path (all observed `Spool_FindAnim` callers are UI).

### 🔴 THPG / Project 8 `.col` (newer collision version)
- Cross-ref: `game-thpg-p8.md` (full evidence there). Newer `.col` container (`0x00FF00FF`-prefixed) not decoded. Listed here too because it's a format gap, not just a per-game gap.

---

## Census 2026-07-10 — newly surfaced items + working priorities

Full-corpus extension census (`tools/diagnostics/corpus_extension_census.py`). **User-set priority
order: 1) hashes → 2) archives/containers → 3) image formats → 4) mesh formats → 5) animation
formats. NO planned support for shaders (`.shd.ngc`) or particles (`.pfx`).**

- ✅ **Priority 1 — pak type-hash identification** (DONE 2026-07-10): every observed type hash
  is now in `PakArchive.KnownTypes` (~35 added, incl. bruted `0x689028A5=.pimg`,
  `0x6290993B=.mcol`, and `0x52D95838=QbKey("unknown")` — the pak builder's fallback type for
  unclassified files; a RIFF sniff in `ExtractFiles` renames those to `.wav` when the payload is
  a WAV). Filename-hash recovery shipped alongside: `QbKeyNames.ThpgDbg.txt` (55,530 pairs from
  THPG's dbg/dbgq paks, `tools/utilities/harvest_thaw_dbg_names.py`) + `QbKeyNames.ThawGcPaks.txt`
  (715 GC entry names proven via QB-string harvest, `tools/utilities/harvest_gc_pak_names.py`;
  GC key rule = QbKey of the lowercased full path minus the last extension). Coverage
  (`tools/diagnostics/pak_name_coverage.py`): 53.9% → **57.2%** named (65,878/115,205); GC
  unresolved 12,864 → 9,104. Hard limits: 40,223 LE entries are keyless (no key stored — offset
  names are all there is), and the remaining ~9k GC keys hash vocabulary that ships in no
  wordlist (gameplay-anim/CAS-part names in the skaterparts/anims apks: .ska 2,582, .img 1,970,
  .stex 1,263).
- ✅ **Priority 2 — archives/containers** (DONE 2026-07-11, commits cda9589/7008c2c/6b5388f/89ac11d/3a98f0d):
  - `.zip.wpc`/`.zip.ngc` (1,337) = QTex texture-SOURCE bundles (STORE PKZip, malformed central
    dir → `QZipArchive` local-header walker); hold original TIFF/PNG art + `debug.log`. Wired into
    `unpack`/probe/CLI/GUI. Sweep 1,337/1,337.
  - `.cut`/`.cut.ps2`/`.cut.xbx` (215) = `CFileLibrary` cutscene containers → `CutArchive`; extract
    SKA/CAM/OBA/SKE anims + SKIN/MDL/GEOM models + TEX + QB + CIF/CAS/WGT, plus a `{stem}.cif.json`
    object-binding manifest. Sweep 215/215. Cutscene anim payloads now convert (OBA bit24 skip,
    headerless SKE gate, `pre/Bits/anims` compress-table path — SKA+SKE → validator-clean GLB).
  - `.prd`/`.prf`/`.prg` (316) = German/French PRE v3 localizations, byte-identical to `.pre` — pure
    routing through `CompressedPreArchive` + full-name extraction dirs. Sweep 316/316.
  - Name harvest: `QbKeyNames.CutScenes.txt` (2,032 proven cut names) + zip-vocabulary GC pak names
    (+159); corpus pak naming 57.2% → 57.5%.
  - **Still open (not blockers):** `0x508AE2F2` CIF2 layout (THUG2 CIF replacement, dumped raw) —
    identify via dictionary reverse-lookup → `tools/qbkey_pipeline` brute 6-9 char → GHIDRA string
    scan → content probe for sibling TOC keys. Bare-`.cut` ver=3 INTERMEDIATE|UNCOMPRESSED master
    anims (43 files, the richest uncompressed authoring keys) — extract raw now, parse in the
    animation phase. WGT/CAS payload decoding beyond raw dump — no consumer yet.
  - **Deferred to Priority 3 (images):** the `debug.log` texture-checksum→source-name side map
    (2,005 platform-invariant pairs) for THAW texture export naming — belongs with texture work,
    NOT in `QbKeyNames*.txt` (those aren't CRC(name) pairs and would poison the harvest scripts).
- 🔴 **Priority 3 — image formats**: `.tga` DONE 2026-07-11 — all 4 corpus TGAs verified standard
  (types 1/2 uncompressed, one 32-bit with real alpha); decoded via ImageSharp through the
  `Core/Formats/Rle/BitmapFile.cs` facade (`rle` CLI + Bitmap Converter tab), alpha preserved.
  Standard `.bmp` (3,535 files, all `BM`/BITMAPINFOHEADER) shipped in the same pass. Remaining:
  `.tim` (5 files) — standard PSX TIM headers, but they live in the multi-game demo-disc build
  (`Spider-Man (2000-2-4, PSX)`, not "Spider-Man PC" as the earlier census said) under third-party
  dirs (`DD/`, `WTC/` = TOCA) — out of scope as non-Neversoft content.
- 🔴 **Priority 4/5 — mesh/anim formats**: `.dff` (477, THPS3) = standard RW clumps, our
  `RwDffFile` already parses the format — **routing-only gap** (add `.dff` to rwdff/mesh
  discovery). `.anim` (193, THPS2X, `Anm\0` magic) — Xbox-era animation format, unstudied.
- 🔶 **Audio (investigated 2026-07-10, no priority set)**: `.snd` (739+, THUG2 Xbox/PC) and the
  `0x52D95838` THAW entries = **plain PCM WAV** (rename/route only). `.pcm` (2,580+, THUG2) =
  RIFF with **Xbox ADPCM codec 0x0069** (44.1k/22k/11k mono) — needs an IMA-ADPCM-variant
  decoder or ffmpeg passthrough. 172 `.pcm` + 49 `.snd` have fmt chunks beyond the first 64
  bytes (deeper parse needed, likely fine).
- ⚪ Not formats / no action: `.dep` (build path lists), `.chk` (checksum text), `.anr` (text
  anchor scripts), `.rec` replays, `.seq` ("Sequencer File" text on the DC proto), standard
  `.gif/.ogg/.jpg`, installer debris. `.zoo`/`.bfx`/`.ppv` = Codemasters WTC (see PPV entry).

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
  build logs (text), Spider-Man `.tex` = hash manifests (text), `.psh` = C headers, `.cas.*`/`.fam.*` =
  appearance config data. (The 2026-07-07 claim that `.mpk.ngc` = padding stubs was wrong for 821 of
  them — they are apk companion data files; see the .apk.ngc entry above.)

## By design / won't-fix ⚪

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums (`CLAUDE.md` → QBKey section; `tools/ghidra/thps2-psx-proto/output/psx_decompiled.c`). GHIDRA string extraction found 0 texture matches across 15 executables. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).
