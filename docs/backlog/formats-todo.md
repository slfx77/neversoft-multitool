# Backlog — Unimplemented / Deferred Formats

Created 2026-07-03. Distilled from `CLAUDE.md` (*Deferred Items* / *Not Yet Implemented*) + `memory/`.
**Re-verified 2026-07-07** with a full-corpus extension census (all 27 build dirs), magic-byte probes, and
conversion sweeps — several entries here turned out stale (see *Done* below). NxTools (`Sample/nxtools`)
was surveyed as a reference source: it covers THUG2/THAW scene+tex families across xbx/wpc/xen/ps3 and
Downhill Jam (`thdj` = ngc/wii, later engine gen), plus a full PS1 `.psx` importer — but has NO coverage
for `.stex` payloads or THAW GameCube.
**Re-verified 2026-07-26 vs HEAD (v1.3.4, 60d0b81) — full-domain audit.** Two more entries shipped since
07-07 (THAW `.tex.ps2` scene metadata, CIF2 `0x508AE2F2`) and moved to *Done*; the THPG/P8 `.col` entry
was corrected (it is version 10 and parses — the real gap is bare-`.col`/`.skin` extension routing, not a
new format). NxTools' P8/THPG `.col` "gap" was likewise struck: the format is already supported.

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
  The shipped parser and its corpus coverage are pinned by `NgcSceneFileTests`.
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
  hits, 0 mismatches; `PakArchiveTests` pins the offset rules. 48 `*_sfx.pak.ngc` = raw audio
  blobs (skipped). Routed: `archive` CLI, `unpack`, GUI Archive Extractor.
  ⚠️ **Sample/Builds pak-extracted subtrees predate the offset fix** — payloads extracted from
  multi-entry paks (qb.pak, cutscene mains, cas paks, worldzone paks) are byte-garbled on disk
  and need regeneration via `tools/corpus/SampleGenerator`.
- ✅ **THAW QB decoding — shipped 2026-07-10 for ALL THREE platforms** (`.qb.ps2`/`.qb.wpc`/
  `.qb.ngc` + `.sqb.*`): THAW uses the sectioned QB format (Guitar Hero family, Queen-Bee
  reference at `Sample/queen-bee`), NOT the raw THPS3-THUG2 token stream and NOT "BE tokens with
  a size prefix" as previously guessed. `QbSectionParser` (auto endian + old/new info-encoding
  detection, LZSS scripts, THAW tokens 0x47-0x4A, inline-script struct items) synthesizes classic
  token streams for the existing decompiler. Sweep: 11,909/11,909 files, 49,755 scripts, 0
  failures. **Name resolution 97.1% PS2 / 99.2% PC / 89.1% GC** via 137,054 re-hash-validated
  pairs recovered from the shipped `dbg.pak` debug archives and embedded as
  `QbKeyNames.ThawDbg.txt`.
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
  Durable coverage: `ThawSkeletonFileTests`, `ThawSkaFileTests`, and the cross-game animation
  corpus tests. Remaining niceties: bit28 custom-key event decode (35 files carry
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

### ✅ Spider-Man TRG POWERUP placements from items.psx — SHIPPED 2026-07-23
- Source: user request 2026-07-22 ("if we're going to place objects, we should probably also see if
  the trg files mention objects directly or by filename, as I believe items.psx is used across most
  levels") + the l1a1 "?" investigation.
- Findings so far: TRG never references models by filename (0 filename strings). PLATFORM/MANIPOB
  nodes reference bank models by NAME CHECKSUM (already placed); **POWERUP nodes carry a numeric
  `pickupType`** (proto census: type 8 ×49, 15 ×25, 14 ×21, 11 ×6, 16 ×6 across 22 levels) that
  indexes a game-code item table selecting an items.psx MODEL INDEX (`CItem::InitItem("items")` +
  `mModel = N`, spidey-decomp `ob.cpp`/`shell.cpp`). Engine-proven mappings: in-world "?" marker =
  items model 5 (`Spidey_CIcon`, scale 2048 = ×0.5); web projectiles use the items region at the
  same half scale. items.psx (proto) = 6 models: 0 white wedge, 1 blue gear (web cartridge), 2
  yellow gear, 3 grey gear, 4 grey dome, 5 the "?".
- Shipped 2026-07-23 instead: `PsxItemsBankSubstitution` — bank meshes sharing a name hash with an
  items model render from the items copy (fixes the l1a1 "?" to its vivid staggered-blue pulse).
- **Table PINNED 2026-07-23** by disassembling the `CPowerUp` ctor's `switch(mType-8)` in both PSX
  binaries (Capstone via `dis_crossgame.py` in the external THPS2 decomp project; ctor found by xref'ing the
  `"items"` string + the 1.0-confidence `Spool_GetModel` anchor). The ctor stores its type arg to
  `mType` (0x38 proto / 0x34 final) then loads an items.psx mesh-name hash per case →
  `Spool_GetModel(hash, ItemsRegion)`. `Trig_CreateObject` passes the TRG node's `pickupType`
  straight in, so **TRG pickupType == ctor mType** (verified: census values map to sensible models
  — 8=web cartridge matches the user's screenshot, 11=the "?"). The tables were read directly
  from each shipped executable, resolved against items.psx, and cross-checked against the TRG corpus.
  **No per-type scale** — the ctor's
  0xDE/0xD8/0xD0 stores are spin/counter fields, not mScale; the spidey-decomp `Spidey_CIcon` ×0.5
  is a DIFFERENT class (the HUD nav icon), not the type-11 CPowerUp "?".
  - proto ctor @0x800349CC, jumptable @0x800B03A4 (mType 8..16):
    8→0x17646B0D (web cartridge/m1), 9→0xC6739C3B (yellow gear/m2), 10/12/13→default,
    11→0x7F648179 ("?"/m5), 14/15/16→0x7E74F3D4 (grey gear/m3). Census {8,11,14,15,16} 100% mapped.
  - Apr-29 proto ctor @0x8001EA70, jumptable @0x80091570 (mType 8..16, 7-mesh items.psx):
    8→0x17646B0D, 9→0xC6739C3B, 10→default, 11→0x7F648179, 12→0x12820A41 (m6), 13→0xC6739C3B,
    14/15/16→0x7E74F3D4. A hybrid (Feb's 9-case structure + Sep's 12/13 assignments). Census
    {8,11,12,13,14,15,16} 100% mapped.
  - final ctor @0x8001DE00-region, jumptable @0x80093674 (mType 8..18, 9-mesh items.psx):
    8→0x17646B0D, 9/17→default, 10&18→0xA092D785 (m7, the ubiquitous final pickup — type 18 ×210),
    11→0x7F648179, 12→0x12820A41 (m6), 13→0xC6739C3B, 14/15/16→0x7E74F3D4. Census 100% mapped.
  - **Census subset {8,11,14,15,16} is identical across ALL THREE builds** (three confirmations);
    non-census types drift gradually Feb→Apr→Sep but no type ever maps to two DIFFERENT non-default
    models. The per-build items.psx (6 / 7 / 9 meshes; m6=0x12820A41, m7=0xA092D785 presence)
    selects the right table. April-29 added to Sample/Builds 2026-07-23.
- **Placement layer SHIPPED 2026-07-23** (`PsxPowerupPlacementResolver`): POWERUP nodes render as
  items.psx pickups (translation-only — POWERUP nodes carry no angles), merged into the single items
  geometry pass in `MeshModelParser.PopulatePsxLevelObjectCompanion`; works with or without an `_o`
  bank (the bank layer swallows its own failures so a missing/malformed/unreadable bank still emits
  pickups). **POWERUP is authoritative for pickups**: bank objects whose mesh a POWERUP node already
  places are suppressed (`PsxItemsBankSubstitution.Split(suppressHashes:)`) — l1a1's bank "?" drops
  in favour of its 3 POWERUP "?" nodes; a bank pickup with no POWERUP node (the demo level lda1's
  "?", the only such case corpus-wide) still redirects to the items copy. Required a TRG parser fix:
  `ParsePowerup` now skips the node's link list before reading position (`ReadLinks`), which the old
  "read link COUNT only" code botched — POWERUP nodes with links (the "?" markers, 4-5 links each)
  had million-unit garbage coordinates. Grounded-flag terrain snap remains out of scope (authored Y).
  Durable coverage: `PsxPowerupPlacementResolverTests`, `PsxItemsBankSubstitutionTests`,
  `TrgFileTests.Parse_SpiderManPowerupWithLinks_*`.
- **Generalized to the PS1 lineage 2026-07-24** (`MeshCompanionResolver.TryResolvePsxLevelCompanions`):
  THPS1/THPS2 get the FULL bank + PLATFORM-overlay + POWERUP stack. THPS pickup table transcribed
  verbatim from the **matched THPS2 decomp** `POWERUP.cpp` `CPowerUp::CPowerUp` (`switch(mType)`,
  no `-8`): 4/5/6/10/15 = K/S/A/T/E letters, 16/18 = tape, 21-32 = bonus/money, medals 0x664-0x666
  omitted (they spool from `skmedals`, not `items`). Letter/bonus hashes are byte-identical to THPS1's
  items.psx → one hash-keyed table serves both; `SelectTable` picks it by the 'S' letter 0x311D55D4.
  PLATFORM overlay verified coincident (THPS1 24/30, THPS2 12/17 refs at δ≈0, div 2.25). 6-12 proto
  added to Sample/Builds (9-mesh items = final table). See memory `psx_crossgame_level_objects.md`.
- **Apocalypse SHIPPED 2026-07-24** (full parity): the pickup table was reverse-engineered from
  `apocalypse_final.exe` (SLUS_003.73, no SYM) by **signature-matching against the THPS2 decomp** —
  located the "items" string + the items.psx hash-load cluster at 0x8001FEC0, read the `CPowerUp`
  ctor jump table @0x800A11EC (keyed by mType-1), and cross-checked
  against the TRG POWERUP census (types 4/5/6/10/14/15/16 = 176/281 nodes; 14/15/16 are three spin
  variants of the shared grey-gear 0x7E74F3D4; 17=plus_one region, non-items). TRG pickupType == mType,
  no per-type scale, node scale div 2.25 verified in-bounds. POWERUP + PLATFORM overlay both enabled
  (`ApplyTriggerOverlay=true`); the overlay's Apocalypse refs are mostly authored BADDY/PLATFORM spawn
  re-instances (worth a visual eyeball, one-line revert via the flag if too busy).

### 🔶 PSX level-object animation export (skeletal path)
- Source: decomp contract `thps2-psx-proto docs/level_object_anim_binding.md` (2026-07-09; RunAnim/CycleAnim/CalculateAnimOrder PERFECT).
- Binding chain is fully known: item→region by filename (`Spool_FindRegion`), stream selected by the item's own `mAnim` index into the region's `pAnimFile` table (stride 8, count-prefixed — NOT stream-i→item-i), per-bone positional with parent tree from `pHierarchy` (`mapTable[bone]=parent`), cross-model retarget by name via CalculateAnimOrder. `has pAnimFile ≡ IsSuper` — animated level objects (traffic cars etc.) are CSuper instances on the same skeletal path as characters.
- What's left: teach the PSX level exporter to enumerate anim streams in hier-level files (skdown: 836 placed objects) and emit glTF animations per placed object. MEDIUM-confidence open question: whether placed level geometry also uses the name-keyed tag-0x45 packet path (all observed `Spool_FindAnim` callers are UI).

### 🔴 THPG / Project 8 `.col` + `.skin` — bare-extension ROUTING gap (S each)
- Cross-ref: `game-thpg-p8.md`. **Corrected 2026-07-26:** there is NO "newer `0x00FF00FF` collision
  version" — that header was GARBAGE from the pre-2026-07-10 absolute-offset PAK-extraction bug
  (`memory/pak_header_relative_offsets.md`); the builds were re-extracted after the fix. At HEAD all
  **85 THPG + 79 P8 `.col` start `0a 00 00 00` = version 10** and `Core/Formats/Collision/ColFile.cs`
  (v9/10) parses them cleanly. The data is fully supported.
- What's left (both S, user-facing, parser already handles the data):
  - Bare `.col` (no platform suffix) is not dispatched to the collision parser — adding the extension
    to mesh/collision discovery unblocks ALL THPG/P8 collision.
  - Bare `.skin` (no platform suffix) is likewise not routed to the scene parser — unblocks THPG/P8
    level/cutscene scene geometry.

---

## Census 2026-07-10 — newly surfaced items + working priorities

Full-corpus extension census (`tools/validation/support/corpus_extension_census.py`). **User-set priority
order: 1) hashes → 2) archives/containers → 3) image formats → 4) mesh formats → 5) animation
formats. NO planned support for shaders (`.shd.ngc`) or particles (`.pfx`).**

- ✅ **Priority 1 — pak type-hash identification** (DONE 2026-07-10): every observed type hash
  is now in `PakArchive.KnownTypes` (~35 added, incl. bruted `0x689028A5=.pimg`,
  `0x6290993B=.mcol`, and `0x52D95838=QbKey("unknown")` — the pak builder's fallback type for
  unclassified files; a RIFF sniff in `ExtractFiles` renames those to `.wav` when the payload is
  a WAV). Filename-hash recovery shipped alongside: `QbKeyNames.ThpgDbg.txt` (55,530
  re-hash-validated pairs from THPG's dbg/dbgq paks) + `QbKeyNames.ThawGcPaks.txt` (715 GC entry
  names proven by matching QB strings against archive key hashes; GC key rule = QbKey of the
  lowercased full path minus the last extension). The coverage audit found
  53.9% → **57.2%** named (65,878/115,205); GC
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
  - ✅ **`0x508AE2F2` CIF2 layout — SHIPPED** (`= QbKey("cifstruct")`, THUG2 CIF replacement). It is a
    `CStruct WriteToBuffer` stream, decoded by `QbStructBuffer` (`Core/Formats/Qb/QbStructBuffer.cs`)
    and integrated into `CutArchive`; **105/105 corpus payloads parse**, objects land in the
    `{stem}.cif.json` manifest with file cross-links. (Was "dumped raw"; the dictionary reverse-lookup
    plan is superseded.)
  - **Still open (not blockers):** bare-`.cut` ver=3 INTERMEDIATE|UNCOMPRESSED master anims (43 files,
    the richest uncompressed authoring keys) — extract raw now, parse in the animation phase. WGT/CAS
    payload decoding beyond raw dump — no consumer yet.
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
- ✅ **`.dff` — DONE 2026-08-07.** Was a routing-only gap; `.dff` now resolves through
  `MeshTypeDetector` alongside `.skn`. 477 files.
- 🔴 **Priority 4/5 — mesh/anim formats**: `.anim` (193, THPS2X, `Anm\0` magic) — Xbox-era
  animation format, unstudied.
- ✅ **`.pcm` — DONE 2026-08-07.** 2,752 files (1,376 identical on the Xbox and Windows discs).
  RIFF + Xbox ADPCM 0x0069, mono, nBlockAlign 36, wSamplesPerBlock 64, at 11025/22050/44100/48000.
  A block emits the header predictor as sample 0 then **63** nibbles — the 64th is padding;
  settled by diffing both readings against ffmpeg's `adpcm_ima_xbox` (bit-exact one way,
  mismatched the other). `Core/Formats/Audio/XboxImaAdpcm.cs` + `XboxPcmDecoder.cs`, on a new
  shared `Core/BinaryIO/RiffWaveReader.cs`.
- 🔶 **`.snd` (788, THUG2 **PC only**) — codec NOT decoded. The old claim here, "plain PCM WAV
  (rename/route only)", was FALSE on three counts** (corrected 2026-08-07): it is PC-only (0 on
  Xbox), there are 788 not 739, and it is not PCM. The `fmt ` chunk claims 16-bit mono PCM and
  lies — `nAvgBytesPerSec` carries the DECODED byte count (`4 x dataSize`, or that minus 2 for an
  odd sample count, in **788/788** files), so the payload is 2 samples per byte. Read as int16 it
  is white noise (mean|Δ|/RMS **1.105** vs 0.02–0.20 for real audio; nibble entropy 3.63).
  Shipping it as `.wav` would emit 788 files of static, so it probes as Unsupported with that
  reason and is pinned by `ThugPcSndSurveyTests`.
  - **Oracle in hand**: 350 basenames ship as both `.snd` (PC) and `.pcm` (Xbox) — the same source
    audio in two encodes — and the `.pcm` side now decodes bit-exactly. Harness:
    `tools/research/snd-codec/snd_codec_fit.py` (median windowed NCC over pairs; acceptance ≥ 0.97 over
    ≥ 100 pairs).
  - **Best current finding**: correlating the **first differences** gives a uniform **0.84–0.87**
    across every file, while the raw waveform ranges 0.26–0.99 purely by content (0.99 on
    impulsive hits, 0.26 on quiet sustained sounds). So textbook IMA already recovers the per-sample
    deltas — nibble order, step table and index table are all correct — and the divergence is the
    accumulated **predictor**. What remains unknown is the state/prediction rule, not the tables.
  - **Ruled out**: `.snd` is not the `.pcm` bitstream minus its 4-byte block headers (1–8% byte
    agreement = chance; independent encodes); nibble order (high-first drops deriv to 0.41);
    initial step index (no effect); shift-accumulate diff form; state resets at 16/32 bytes;
    Yamaha AICA; OKI/Dialogic; MS-ADPCM. A leaky integrator confirms the drift diagnosis
    directionally (raw 0.60 → 0.65) without closing the gap.
  - **Binary leads exhausted at this level**: `THUG2.exe` is SafeDisc-2 wrapped (`.text`/`.data`
    entropy ~8.0; `.rdata` is readable and contains **no** IMA / MS-ADPCM / AICA / OKI / SPU
    table), and imports DSOUND but **not MSACM32**, so the decode is in-engine software rather
    than an ACM codec. The THUG source drop has no Win32 sound backend at all (`Gel/SoundFX/`
    ships only NGPS/Xbox/ngc). `THAW.exe` (available unpacked) does carry the IMA step+index
    tables at file offset `0x2D8310` → VA `0x6D9310`, but they have **zero xrefs in either
    `.text` section** — dead linked-in library data, not a live decoder (THAW PC ships plain PCM
    `.wav`, 1,148/1,148). The Xbox XBE is plaintext but decodes ADPCM in hardware, so it holds no
    software codec either.
  - **Public state of the art (searched 2026-08-07): nobody has solved this.** The THPS modding
    community reached the same wall and no further — the thps-mods.com "THUG2 Sound format"
    thread (site now dead; not in the Wayback proxy either) reports `.snd` as "basically Xbox
    encoded wav files" that give **white noise** when played, notes the header is "4 bytes
    shorter than the xb_adpcm spec with codec type 01", and states the xb_adpcm codec that works
    on `.PCM` inside `.PRE` is **not** compatible with `.SND`. A ZenHAX thread on the THUG/THUG2
    music WAD+DAT reports the same for the PC build ("sounds like garbage"). No decoder exists in
    any public tool: not vgmstream, not aluigi's Xbox-ADPCM tools, and none of the THPS GitHub
    repos (thps2-tools, NeverScript, THPS-Level-Editor, T2CMT, thug-pro-scripting). Our findings
    above already go further than any published source.
  - **Community theory TESTED AND REJECTED**: if those "4 bytes" were only the fmt-chunk
    extension (cbSize + wSamplesPerBlock), the payload would still be 36-byte blocked and the
    working Xbox ADPCM decoder would read it. It does not — raw NCC **0.006**, deriv **-0.005**,
    and `.snd` data sizes are **never** a multiple of 36. Kept as the `xbox-blocked` model in the
    harness so it is not re-proposed.
  - **Two ways forward, both needing the user.** (a) The **LegacyThps Discord** is repeatedly
    cited as where the deep format knowledge lives and is not web-searchable — cheapest ask.
    (b) Far stronger than any static work: run the game in an XP/7 VM (the disc rip has
    `SECDRV.SYS` + `DrvMgt.dll` + `00000001.TMP`, and it is **SafeDisc 3.20.22**) and capture the
    DirectSound buffer for a known `.snd`. That yields EXACT input→output ground truth for the
    same file, versus today's oracle of two different lossy encodes, and turns recovering the
    predictor from a search into an algebra problem. Note `secdrv.sys` is CVE-2007-5587 and was
    blocked by Microsoft in KB3086255 — VM only, never the host.
- ⚪ Not formats / no action: `.dep` (build path lists), `.chk` (checksum text), `.anr` (text
  anchor scripts), `.rec` replays, `.seq` ("Sequencer File" text on the DC proto), standard
  `.gif/.ogg/.jpg`, installer debris. `.zoo`/`.bfx`/`.ppv` = Codemasters WTC (see PPV entry).

---

## Done (for reference) ✅

- ✅ GS-alpha export scaling (128=opaque → PNG 255=opaque) — `memory/ps2_alpha_export_scale.md` (v1.2.1). `DecodePixels(rawGsAlpha)`: export scales ×255/128, GS replay keeps raw.
- ✅ VID1 (THAW GameCube movie container) → MP4 — shipped (`vid` CLI command + Video Converter tab); the old `CLAUDE.md` "Deferred > VID" note predates it.
- ✅ **THAW `.tex.ps2` scene texture metadata** — IMPLEMENTED (confirmed 2026-07-26; the old 🔴 "Not Yet
  Implemented" note was stale). `Core/Formats/Texture/Ps2Scene/SceneTex/ThawSceneTexFile.cs` = version-6
  TEX0-metadata scan + GIF A+D CLUT/pixel decode; **DMA-REF-verified 905/905 unique textures across
  332/332 files**. Joined on entry-table `TextureChecksum` (1,325/1,329 materials direct; the 4 misses
  are mat=0/tex=0 placeholders), with a TEX0 `(TBP,CBP)` fallback join for entries whose checksum is
  absent from the companion (`ThawPs2SkinSetupMapping.AugmentTextureOverridesWithTex0Fallback`).
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

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums. Engine analysis plus string extraction across 15 executables found 0 texture-name matches. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).

### 🔶 N64 ROMs (THPS1/2/3 + Spider-Man, Edge of Reality) — container mapped, ERZ compression unRE'd (2026-08-05)

- Corpus: 4 .z64 big-endian ROMs in `Sample/Builds` (`* (…, N64 - Final)`), mirrored verbatim.
- **The Neversoft data lineage survived**: ROMs carry `_t.trg`/`_le.psx`/`cretex.bin` string
  fragments (LZ literals), big-endian `PSX-mesh v3/v4` headers, byte-swapped `_TRG` containers,
  and `edgeofreality.com`. Expectation: decompressed payloads are BE-mirrored PSX/TRG data → the
  endian-parameterized reader pattern (GC precedent) applies once extraction works.
- **Container**: sub-file tables of `u32 BE count` + `count+1` ascending u32 offsets (relative to
  table start; first offset == table size). THPS2 table example at ROM 0x13B74: count 15,
  entries 0x44..0x65C52.
- **Compression = "ERZ"**, Edge of Reality's own, NO public RE exists (searched n64decompress,
  en64 wiki, EmuTalk). Header: `"ERZ" u8 version | u16 0x0001 | u16 0 | u32 BE decompressedSize`,
  LZ bitstream from +12 (literals visible: "sk2de…"). THPS1 ships ERZ v1; THPS2/THPS3/Spider-Man
  ERZ v2. Census: thps1 1,124 / thps2 1,158 / thps3 1,121 / spidey 1,584 blocks — the ENTIRE
  asset corpus is ERZ-wrapped.
- **ERZ v2 DECODES (2026-08-05)**: emulated execution of the ROM's own boot-segment decompressor
  (located via its `lui 0x4552` magic-check signature; THPS2 core at
  RAM 0x80000CF8) in a minimal MIPS-BE interpreter — bit-exact by construction. Header confirmed
  from the code: `+4 u32 BE decompressedSize` (0x10000 blocks), `+8 u32 BE compressedSize`,
  bitstream from +18. THPS2 entry 0 → 64 KB of skater-definition data ("sk2def", bone names,
  gear/BMP names); entries 1-2 → MIPS code overlays. The early sub-file table is the CODE
  package; asset tables (BE PSX payloads) sit later in ROM.
- **C# decoder shipped**: `ErzDecoder` mechanically transcribes both v1 and v2, with emulator-derived
  SHA-256 fixtures pinned by `ErzDecoderTests`. Next: walk ALL sub-file tables per ROM and classify payloads; then
  `.z64` routing through `unpack` (gate `.n64`/`.v64` byte orders out with a clear message) and
  textures — if payloads are BE-mirrored PSX files, the endian-parameterized reader pattern (GC
  precedent) may cover them with no new texture code.
- Durable inventory and extraction live in `N64RomArchive`; `N64RomArchiveTests` pins header,
  master-directory, table, and standalone-block discovery.
