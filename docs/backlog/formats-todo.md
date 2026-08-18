# Backlog — Unimplemented / Deferred Formats

Created 2026-07-03. Distilled from `CLAUDE.md` (*Deferred Items* / *Not Yet Implemented*) + `memory/`.
**Re-verified 2026-07-07** with a full-corpus extension census, magic-byte probes, and
conversion sweeps — several entries here turned out stale (see *Done* below). NxTools (`Sample/nxtools`)
was surveyed as a reference source: it covers THUG2/THAW scene+tex families across xbx/wpc/xen/ps3 and
Downhill Jam (`thdj` = ngc/wii, later engine gen), plus a full PS1 `.psx` importer — but has NO coverage
for `.stex` payloads or THAW GameCube.

**Re-verified 2026-08-10 against the current tree, tests, and corpus evidence.** Standalone payload-bearing
PS2 `.stex`, bare `.col`/`.skin` routing, PSX colour-pulse playback, and the N64 ERZ/archive/texture/mesh
foundation have shipped since the earlier audit. Their investigations are retained under *Done*; do not
schedule work from their old descriptions.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔴 PS1-era residual extension survey — 2026-08-17 (answers "any PSX-side gaps left?")

Full extension census re-run over the four PS1-era final builds plus the THPS PS1 lineage, with
magic-byte probes on everything not already routed. The character-paired `.bin` mystery is settled,
and three small asset-bearing gaps remain. Everything else is code, saves, or replays.

**Settled — `.bin` paired with characters is CODE, not assets.** Spider-Man pairs a `.bin` with each
boss/NPC stem (`blackcat/carnage/chopper/cop/docock/hostage/jonah/lizman/mysterio/rhino/scorpion/
superock/thug/venom.bin`, 1–58 KB, plus `l*lsc.bin` level scripts and `shell.bin`): disassembly-shaped
MIPS throughout (`lui/addiu` pairs, `jr $ra` epilogues, stack prologues) — **per-character AI code
overlays**, each with a paired `.rel` relocation table, exactly the "modules" krystalgamer's
spidey-decomp covers. THPS2's 29 `.bin` are the same class (front-end screens `mainmenu/options/
tricksel/…` + `GAME.BIN`/`FRONT.BIN`/`EDITOR.BIN`). Not convertible as assets; the interesting
residue is the DATA tables embedded per overlay (AI params, per-character anim indices — same class
as the pickup tables RE'd out of the main EXEs), which is per-overlay RE work, not a converter.

**N64 cross-reference lead for anim naming — measured 2026-08-17.** The Spider-Man N64 cart carries
an **uncarved ~1.5 MB character-AI segment at ROM 0x1D59BB2–0x1DEBAEE+**, per-character in order
(blackcat 0x1D72, carnage 0x1D79, chopper 0x1D80, cop 0x1D8B, docock 0x1D91, lizman 0x1DA1,
mysterio 0x1DA9, rhino 0x1DAF, scorpion 0x1DB0/0x1DB8, simby 0x1DBA/0x1DC8, spclone 0x1DCB,
superock 0x1DD1, thug 0x1DDE, turret 0x1DE2, venom 0x1DEB). **Bundle-naming angle
(2026-08-17)**: current N64 naming is triggers-first (TRG scripts spell filenames; contiguous
slot-run alignment) with the PS1 content-identity resource as fallback — 418/594 slots named, and
content identity structurally cannot separate shared-rig characters. Each AI block must bind to
its model somehow (the PS1 side calls Spool_GetModel by name hash); if the N64 blocks carry a
bundle-slot immediate or hash constant, that is per-character naming evidence for exactly the
class the fallback cannot reach (82 unnamed Spider-Man slots). **Probe run 2026-08-17 — the
constant-anchored route WORKS, and the mechanism is now mapped.** Byte-identical signature
matching cannot transfer (recompiled, measured), but code-embedded constants survive: scanning the
CARVED boot.bin (the raw ROM shows nothing — boot code is ERZ-compressed in-cart, and the earlier
"no QbKey hash table exists" scan searched DATA only) for split-immediate QbKey hashes (BE MIPS
`lui`/`ori` pairs) finds a spool/unload routine at boot.bin file 0x73700 whose body:
  - loads SIXTEEN hash constants in a row, each fed to `jal 0x8008A674` — **all sixteen resolved**
    by hashing boot.bin's own strings: thug, police, hostage, cop, scorpion, rhino, jonah,
    Mysterio, simby, and the level-script overlays l2a1lsc/l5a5lsc/l5a6lsc/l5a7lsc/l6a1lsc/
    l6a2lsc/l6a3lsc — the character+overlay roster, i.e. `0x8008A674` is the N64's
    **spool-by-name-hash function** (the Spool equivalent asked about);
  - then hashes strings AT RUNTIME: `lui/addiu` string pointers (VA 0x80020cdc…) through
    `jal 0x800AA70C` (a string→hash routine) into the same spool call — so boot.bin carries
    NAMES IN PLAINTEXT. Two tables located: the character/viewer model list at file 0x2F30
    (spidey, parker, blackcat, ock_suit, brock, henchman, thug, jjviewer, scorpion, daredevl,
    police, swat, rhino, venom, lizman2, lizard, mjviewer, symbi_02, mystview, punisher, docock,
    carnage, superock, captain) and the overlay-FILE list at 0xA218 (cop, hostage, jonah,
    l2a1lsc…l6a3lsc, simby — the PS1 `.bin` stems verbatim).
  **Next blocker, named**: boot.bin is a MULTI-SEGMENT carve (concatenated decompressed boot
  packages), so jal-target VAs (0x8008A674, 0x800AA70C) do not map to file offsets under any
  single base (ROM entry 0x80000400 tried, off by segments). Recover the per-package load
  addresses from the carver/boot loader, then disassemble the spool function to find the
  hash→bundle-slot resolution — the direct naming lever. The AI segment itself showed no hash
  constants — its model binding goes through this boot-side machinery. The segment is referenced by NO master-directory
group — the whole-carve block scan finds none of it in any carved asset, so it must be DMA'd by
hardcoded ROM address — meaning `N64AssetCarver` currently misses it entirely. The CODE is
recompiled (distinct-block coverage of any PS1 overlay vs boot.bin is only 3–8%, all generic MIPS
idioms with no stable base — an earlier same-day "entire overlay present in boot.bin" reading was
wrong: 11.5k raw hits collapsed to a handful of epilogue-shaped blocks matching thousands of
positions; measure DISTINCT probe coverage and base-offset agreement, not hit counts). The DATA
survives: 34 contiguous byte-mirrored runs ≥64 bytes (u16 tables under u16-swap, u32 tables under
u32-swap, up to 808 bytes — carnage) pair 16+ PS1 overlay tails with their N64 blocks. docock's
shared tail is a table of (u32 id, u32 index) pairs — exactly the shape a per-character
anim/behaviour assignment would take, and docock's 43 anim clips are already cross-matched
PS1↔N64 sample-for-sample. Uses: (1) carve the segment (name blocks by the run anchors); (2) mine
anim-slot assignments from the shared tables on either platform; (3) where assignments are code
immediates, two independent compilations of the same source (LE PS1 + BE N64) cross-check which
constants are source-level. Probe method retained here; runs list in the 2026-08-17 session notes.

**Actionable small gaps (in rough value order):**
1. **`.fnt` bitmap fonts** — 80 THPS2 + 19 THPS1 + 5 Spider-Man. Header = per-glyph metric records
   (u32 width/height/cell rows); the engine loader is `FontTools.cpp`/`FONT.cpp` in the matched
   decomp (`FontManager`, default `mainf.fnt`), so the layout can be read straight out of it.
   Glyph art location (embedded vs companion BMP) to be established from the loader.
   **Target format decided 2026-08-17 (revised same day)**: PNG glyph atlas + schema-v1 JSON
   metrics as the primary data output, plus a **Windows `.fon` export** as the installable-font
   companion — it covers BOTH targets with one format: FreeType's winfnt driver reads `.fon`
   (GIMP-on-Windows already lists Terminal/Small Fonts, which are `.fon` files fontconfig picked
   up from C:\Windows\Fonts), and Windows natively previews/installs it, which BDF cannot do.
   BDF is dropped from the plan. Caveats recorded: `.fon` is an NE-executable shell around FNT
   resource records (fiddlier to write than BDF but documented and deterministic; multiple sizes
   pack as multiple FNT resources), 8-bit codepoints only (fine — these are ASCII-ish game
   glyphs), and Photoshop accepts no raster font at all (the PNG atlas serves it). Verify in GIMP
   with the first emitted `.fon`.
2. ✅ **`.seq` PSY-Q MIDI sequences — SHIPPED 2026-08-17.** `SeqFile` (pQES header + MIDI event
   stream with running status), `VabProgramSet` (programs→tones→PCM with SPU loop points), and
   `SeqSynthesizer` (SsPitchFromNote pitch — the same formula the SFX cue resolver pins — SPU ADSR
   envelope stepped from the register words, sample-loop sustain, tempo map, equal-power pan)
   render SEQ+VAB→WAV. Routed: CLI `audio` (`.seq`), GUI Audio tab (needs the same-stem `.vab`
   sibling, resolved via the companion API so archive entries work). All 11 Apocalypse songs render
   audibly (corpus-pinned); `city` really is a 17.5-minute piece (notes to tick 1,026,413 — checked,
   not a parser bug). **Format lesson pinned by test**: VAB `programCount` counts USED programs and
   the tone region packs used slots in ASCENDING SLOT ORDER — Apocalypse's music banks use slots
   60–75, so the slot-indexed tone walk (decomp-correct for SFX banks whose used slots are 0..N−1)
   silences everything. Documented approximations: single pass (no loop-marker repeat), ±2-semitone
   bend range, linear resampling, envelope without the SPU's stepped quantisation.
3. ✅ **`title_h.zlb` — already handled** (correcting this survey's own 2026-08-17 claim, same day:
   the bitmap facade routes `.zlb` as gzip-wrapped RLE/BMR — `RleImage`/`BitmapFile` — and
   `title_h.zlb` converts to PNG today). Not a gap; retained here only so the extension census
   stays reconciled.

**Classified, deliberately not converted:** `.rec`/`.dem` demo replays (input streams; `.rec`
already documented byte-identical on N64), `.prk` park saves, `.rel` relocation tables,
`amap<N>to<M>.dat` (80 THPS2 files, 2 KB each — anim-index permutation tables between skater rigs;
header + 0..N byte permutations visible in the raw), `trickdb.dat`/`sizes.dat`/`prefs.dat` (small
data tables/manifests), `.psh` (already parsed as part-name headers). THPS2's 1,283 `.bmp` route
through the existing BMP facade.

### 🔶 THAW GameCube platform — textures ✅ 2026-07-07, meshes ✅ 2026-07-08, collision inspection ✅ 2026-08-10
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
- ✅ **Collision structural inspection shipped 2026-08-10** (`.col.ngc`, 722 canonical loose files;
  680 apk-expanded copies are accepted but deliberately excluded from the oracle): `NgcColFile` + `ngccol` CLI emit a
  schema-v1 JSON manifest per file. The 2026-07-07 layout notes were partly wrong; the engine-exact
  layout was transcribed from the THUG source's `__PLAT_NGC__` paths (`NxScene.cpp read_collision`,
  `CollTriData.h/.cpp`) and corpus-verified byte-exact on **722/722 canonical files**: 24B BE header
  (version=10, numObjects, totalVerts, totalFaces, ssRows, ssCols) + 32B scene bounds + 64B object
  records (checksum, u16 flags, u16 numVerts, u16 numFaces, u8 small-face selector, u8 fixed-vertex selector,
  u32 faceByteOffset, bboxMin/Max 4×f32,
  u32 0 = the runtime vertex-pool pointer slot, u32 bspNodeByteOffset, u32 cornerIntensityByteOffset
  = 3×cumulative faces, u32 pad) + **totalFaces×3 per-corner INTENSITY bytes** (the region the old
  note called a "0xFF-wiped vertex region" — 0xFF is just uniform full intensity, valid data; 78 of
  722 files carry varied authored values) + align4 + 10-byte BE face records + 2-byte pad when the
  face count is odd + u32 node-array size + 8-byte BSP nodes (leaf when byte 3 == 3: u16 numFaces,
  pad, axis, u32 pool offset; interior: i32 split point with axis in the low 2 bits, u32 child byte
  offset with a left-is-greater low bit) + u16 face-index pool to exact EOF. Canonical corpus: 819 objects,
  237,175 declared external vertices, 411,057 faces, 35,944 leaves + 35,125 interior nodes, max tree depth 7;
  face indices stay within cumulative declared object ranges in 693 files and cross them in 29; the ssRows/ssCols grid
  has NO cell table — the engine builds supersectors at runtime. **Vertex positions are absent BY
  DESIGN, not wiped**: `InitCollObjTriData` binds `mp_raw_vert_pos` to the render scene's
  `mp_pos_pool`, which answers the old "needs a study of how the engine sources the vertices" —
  any future geometry reconstruction first needs an authoritative collision→scene-pool identity and index-domain
  oracle (the collision file itself provides neither); the inspector intentionally synthesizes no geometry. Pinned by
  `NgcColFileTests` (fixture + strictness + corpus totals).
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
  ✅ **Sample/Builds pak-extracted subtrees were regenerated after the offset fix on 2026-07-16.**
  A 2026-08-11 source-slice audit matched **748/748 payloads byte-for-byte** (8,183,728 bytes,
  zero missing or mismatched): PS2 `qb` 266/266, `rocket` 130/130, `storyselect` 8/8; GC `BH11`
  67/67, `qb_i` 269/269, and `storyselect` 8/8.
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
  QB parser already handles. Camera masters export as node-TRS GLB rigs via `ska`; camera masters
  do not carry the bit-24 QbKey names used by object masters, so their sole track can retain a
  checksum-style fallback name.
  Durable coverage: `ThawSkeletonFileTests`, `ThawSkaFileTests`, and the cross-game animation
  corpus tests. **Bit28 custom events shipped 2026-08-10:** the endian-aware reader consumes the
  bounded `{u32 timestamp, u32 type, u32 totalSize, payload}` records after Q/T, decodes the two
  live THAW payloads (type 1 horizontal-FOV-radians float, despite its historical
  `CHANGE_FOCAL_LENGTH` enum name, and type 4 RunScript QbKey), and preserves
  unknown payloads losslessly. The CLI writes a stable `<stem>.ska.json` inspection sidecar only
  when events exist. A 20,425-file THAW sweep pins 100 physical event-bearing files (36 PS2,
  35 GC, 29 PC), counts 2–121, only types 1/4 and 16-byte live records, exact tail consumption,
  and PS2/GC typed equality. `timestamp` stays a raw integer: its THAW v0x28 runtime unit is not
  proven and some event timelines extend beyond their local Q/T clip. **Static authored camera
  projection shipped 2026-08-10:** one-track PLATFORM camera masters with a valid timestamp-zero
  type-1 event now attach a native perspective camera to the animated track in both glTF and
  Blender. The horizontal source value converts to vertical FOV at the engine's canonical 4:3
  aspect; Blender binds the camera to the same animated pose bone with no view-axis correction.
  `ska --format glb|blend|both` routes every skeletal/object/camera branch through the shared export
  service and defaults to GLB. Later FOV events remain JSON-only because neither path implements
  lens animation. The 347-file GC camera census pins 35 eligible projections, 312 TRS-only rigs with
  no authored FOV, 391 total FOV events, and zero non-camera FOV files. Near/far `1/100000` are an
  explicit broad PS2-derived export policy, not SKA metadata. Real PS2/GC StorySelect exports pin
  matching `0.13479553` vertical FOV in GLB plus successful skeleton-only `.blend` output.
- ✅ **Explicit THAW/legacy QbKey track binding shipped 2026-08-10.** Gameplay SKAs do not name
  their tracks, so `ska --animation-ske <source.ske> --ske <target.ske>` now takes the source rig
  explicitly and maps only exact numeric bone QbKeys. Duplicate/zero names, malformed hierarchies,
  an unmapped root, a skipped parent, or any changed mapped parent edge reject; equal bone counts never
  authorize index binding. The proven `thps7_human` 52-bone source → THUG2 `thps6_human` 50-bone
  target maps 48 tracks, drops source indices 15/16/27/28, maps 17→16, and leaves target shoulders
  15/26 in bind pose. A 330-file GC skeleton audit found 133 52-bone files but **47 distinct ordered
  QbKey identities**; canonical `thps7_human` occurs in only 29, proving count is not identity.
  Skeleton-only exports and an explicitly supplied ordinary PS2 `.iskin.ps2` already authored for
  the target skeleton use the map. The general mesh parser already routes native THAW `.skin.ps2`,
  discovers the skeleton, selects a same-stem PC/Xbox weight companion, and transfers its weights.
  The narrower `ska --skin` path does not select that THAW subformat or remap skin joint indices, so
  its supplied skin must already match `--ske`. The Animations pane now accepts `.ske`, `.ske.ps2`,
  and `.ske.ngc` source rigs as extracted files or direct entries in root/nested archives; full virtual
  paths preserve duplicate identity, and a disposable catalog keeps every required handle alive through
  parse and exact-map validation. One captured plan reaches preview, GLB, and Blender export, while
  invalid, cancelled, or superseded loads preserve the previous rig and stale queued previews are
  rejected. The real GC `global_s.apk.ngc` fixture loads 52 bones and maps 48 after catalog disposal.
  Native Xbox/PC/GameCube scene weights now have caller-explicit CLI and GUI routes. `mesh --ske` accepts
  direct/prepared/exact-stem-directory rigs; the Meshes & Characters tab keeps a parsed skeleton
  selection independently on each eligible Xbox/PC/GameCube entry from an extracted file or a direct/nested
  archive entry and snapshots it for preview, GLB, Blender, PNG, GIF, and batch work. The animation archive
  policy remains narrow while the mesh policy additionally admits `.ske.xbx`; full virtual identities and
  backend ownership survive through parse, after which only the self-contained skeleton remains. Both paths
  use the same global emitted-corner influence preflight, with
  normalized four-weight output and byte-identical rigid fallback for missing, malformed,
  incompatible, or non-unit-scale inputs. Non-worldzone entries retain scale 1 even in mixed batches,
  and a rig change cannot reuse a stale cached render. THUG2 Xbox, THAW WPC, and THAW GameCube pigeon
  fixtures each pin the exact four-joint rig, 46 vertices, and 45 triangles; GameCube direct/prepared
  GLBs are byte-identical and the Blender file reopens with one bound four-bone armature at rest. Its
  `.ske.ngc` rigs ship inside mission/worldzone archives rather than beside loose skins. The real THUG2
  `skeletons.prx` exposes 58 skeleton entries; its selected pigeon rig is byte-identical to the loose fixture
  and produces the same GLB after catalog disposal. Automatic rig inference remains outside this slice.

### ✅ STR (PS1 MDEC) long-stream drift — RESOLVED 2026-08-09
- The historical mismatch was not a VLC defect. The original demuxer copied all 2,296 bytes after
  the XA/video headers from each Mode-2 Form-1 sector, inserting 280 EDC/ECC bytes after the valid
  2,016-byte video piece. The recorded bit-16,069 divergence is exactly five bits into that first
  invalid tail.
- Commit `d13e356` already switched assembly to the XA Form bit and the correct 2,016-byte Form-1
  piece. On the audited clean Apocalypse frames, current assembled bytes match jPSXdec's full
  pipeline, and its standalone STRv2 reader reaches all 1,800 blocks when fed those bytes. The
  bundled corpus contains 323 recognized STR videos, all Form 1; no Form-2 video fixture is
  currently known.
- `MdecDecoderTests` now pins the 2,016-byte synthetic sector boundary, a multi-sector Apocalypse
  frame's exact jPSXdec assembly SHA plus the local RGB regression SHA, recursive fixture discovery,
  complete-frame counting, and explicit rejection of unsupported or incomplete frames. Direct
  preview converts such a rejected frame to opaque black instead of terminating playback, matching
  the MP4 converter's existing fail-soft behavior. The first
  yielded frame of the damaged SM2 Final `E5M6` fixture (header frame 2) is separately pinned: the
  jPSXdec standalone decoder rejects our complete assembly at the same macroblock and bit. Its
  normalized RIFF payload differs from the byte-identical Prototype/Rev1 copies in only 604 bytes
  across 30 of the first 40 sectors, confirming damaged input rather than a framing rule. There is
  no remaining STR framing backlog item.

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
  had million-unit garbage coordinates. Grounded spawn semantics were completed later: Spider-Man
  grounded pickups query the level terrain and apply the engine's 128-unit hover, while the matched
  THPS/Apocalypse paths retain authored Y unless an entity is on their separate dropping path.
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

### 🔶 PSX level-object animation export (skeletal path; traffic snapshot shipped)
- Source: decomp contract `thps2-psx-proto docs/level_object_anim_binding.md` (2026-07-09; RunAnim/CycleAnim/CalculateAnimOrder PERFECT).
- Binding chain is fully known: item→region by filename (`Spool_FindRegion`), stream selected by the item's own `mAnim` index into the region's `pAnimFile` table (stride 8, count-prefixed — NOT stream-i→item-i), per-bone positional with parent tree from `pHierarchy` (`mapTable[bone]=parent`), cross-model retarget by name via CalculateAnimOrder. `has pAnimFile ≡ IsSuper` — animated level objects (traffic cars etc.) are CSuper instances on the same skeletal path as characters.
- Shipped 2026-08-10: `PsxPlacedTrafficResolver` handles the proven D5–DA constructor table and separate traffic `CSuper` files, first-road-node placement, initial Y offset, instance roots, skins, and embedded loop 0. Script-reachable non-startup nodes are deliberately behind a default-disabled snapshot group because trigger time, repeats, suspension, and route translation are not reconstructed. Final Downtown emits three taxi rigs (+711 triangles); San Francisco emits one van and two cable cars (+318); prototype Downtown uses the proven `taxi.psx` fallback. Distinct GLB/Blender roots and shared per-source actions are regression-pinned, and optional source failures roll back atomically.
- The former plan was based on a false premise: prototype `skdown.psx` has 836 level object records but no 0x2A/0x2C animation chunk. Traffic animation resides in separate TRG-selected super files. No animated-door fixture was found, and tag 0x45 remains a separate observed UI/effect path.
- What's left: runtime-accurate script timing/repeated spawns/road motion, plus any other placed skeletal family once a named fixture and binding contract exist. Do not broaden the traffic snapshot into a claim of general placed-object animation support.

### ⚪ THUG2 precompiled `.skin.ps2` without `.iskin.ps2` — no shipped orphan demonstrated
- Re-audited 2026-08-10. The old extension census counted physical preload copies as unique unsupported assets. THUG2 PS2 contains 2,478 `.skin.ps2` copies but only 739 unique payload hashes. Every one of the 739 canonical files has a same-stem `.iskin.ps2`; all 1,739 apparent bare copies are byte-identical to one of those paired canonical skins. Archive and directory scans already prefer the higher-quality intermediate file, so every shipped unique model has a supported source.
- The 746 non-THAW-conformant entry tables must continue to reject rather than replay through `ThawPs2SkinFile`. A native THUG2 precompiled VIF decoder is now evidence-gated, not active backlog: re-open only for a genuinely unique orphan fixture or an explicit detached-copy conversion requirement.

### 🔶 N64 ROMs (THPS1/2/3 + Spider-Man) — archive/texture/mesh/embedded-animation foundation shipped
- Re-verified 2026-08-10. The old “container mapped, ERZ compression unRE'd” description is obsolete. `ErzDecoder` mechanically implements both v1 and v2 with emulator-derived SHA fixtures; `N64RomArchive` walks the master directory and reassembles stream groups; `N64AssetCarver` emits typed assets; `.z64` opens through `ArchiveFileSystem`; N64 textures and render-bank meshes route through the GUI/CLI. Corpus carve counts are pinned at 2,176 / 3,962 / 3,313 / 4,286 assets, and every render bank decodes with in-bounds indices.
- The render path also covers descriptor-bound textures, per-vertex matrix placement, alpha modes, the ROM light rig, and coplanar/semi-transparent separation. Do not reopen ERZ, `.z64` routing, “missing ROM filesystem,” or “render-bank codec” from older notes.
- Concrete residuals and completed follow-ups:
  - ✅ **Stored texture mip export — SHIPPED 2026-08-10.** The earlier `abutton` premise was false:
    format word `0x0014` is a canonical RGBA16 top plus a full-resolution aligned 4bpp auxiliary
    coverage/alpha plane, not an 8×8 mip. The parser now publishes only exact, fully consumed mip
    chains: 36/9,459 dictionary records (THPS1 7, THPS2 9, THPS3 12, Spider-Man 8), with 3–5 stored
    lower levels across RGBA16/CI4/IA4/IA8/I4/I8. The CLI, legacy conversion helper, and Texture-tab
    extraction preserve `{stem}.png` and add `{stem}_mipN.png`; preview and model embedding remain
    level zero. `N64TexFileTests` pins the corpus census and all five RGBA SHAs of a real IA8 chain.
    The 69 `0x0014` auxiliary planes are identified and reported but deliberately not applied to the
    exported alpha until a separate runtime-combine/visual oracle approves that behavior change;
  - ✅ **Nintendo Sound Tools PTR/WBK inspection — SHIPPED 2026-08-10.** The ROMs do not contain
    SGI CTL/TBL `ALBankFile` graphs. `N64SoundToolsBank` instead consumes the exact big-endian
    `N64 PtrTablesV2` descriptor graph together with its paired `N64 WaveTables` payload: checked
    file-relative wave/book/loop pointers, the unaligned final-record boundary, canonical 16-byte WBK
    packing, base-note/coarse-tune bytes, signed fine-detune workspace cells, and all required padding.
    Exact WBK magic gives the four raw wavetable leaves the typed path `audio/000.wbk.n64`, while
    every other uncompressed audio leaf remains `.bin`. `n64-audio-inspect <game.z64> -o bank.json`
    pairs the unique carved assets by content magic;
    standalone PTR input requires an explicit `--wave`. Both routes produce byte-identical schema-v1
    JSON with `sampleRate: null` and cue mapping marked unresolved. The four-ROM corpus pins 1,775
    waves / 320 loops, complete asset hashes and P/A/Z offsets, and Spider-Man's final loop ending raw
    at `D+0xCC == P`. This command remains inspection-only: it reports no inferred sample rate and does
    not execute BFX/song bytecode, apply pitch, expand loops, or join Neversoft cues;
  - ✅ **N64 Sound Tools ROM-global mixer profile — SHIPPED 2026-08-11.**
    `n64-audio-runtime-inspect <game.z64> -o runtime.json` is deliberately separate from PTR/WBK/BFX/SFX
    inspection and has no standalone mode. Schema v1 resolves only the four audited final ROMs using an
    exact carved-`boot.bin` SHA allowlist, NTSC country byte `rom[0x3E] == 0x45`, the clock word at
    that build's pinned raw-ROM offset, and the exact SHA of its pinned 0x160-byte raw-ROM
    `osAiSetFrequency` routine. An unknown boot or any mismatch/truncation in those pinned evidence
    regions fails before the destination directory is created. SDK `musConfig` places
    `syn_output_rate` at `+0x2C`, and the
    cartridge oracle pins the complete call chain: literal 22050 in argument 7, propagation into that
    field, libmus loading it into `a0`, and a direct call to each exact 0x160-byte libultra
    `osAiSetFrequency`. With the pinned NTSC clock 48,681,812, the routine rounds to divisor 2208, writes
    AI DACRATE 2207, and returns 22047 by integer division. The manifest calls this a
    `romGlobalMixerOutput` and publishes the country/clock/routine evidence coordinates and routine
    hash; per-wave rate and cue mapping remain unresolved, pitch/loop scheduling is not applied, and
    playback is not executed. Existing bank schemas stay byte-identical and
    `n64-audio-decode --sample-rate` remains mandatory with no mixer-derived default;
  - ✅ **N64 ABI1 stored-wave decode — SHIPPED 2026-08-10.** `N64AdpcmDecoder` consumes the validated
    WBK slice and parsed predictor book as 9-byte frames / 16 mono samples using the signed-32 wrapping
    and saturated-history behavior of the ABI1/libultra audio-microcode runtime. Synthetic nibble,
    recurrence, saturation, and positive/negative wrap vectors plus clipped real-wave hashes distinguish
    this runtime path from Nintendo's non-bit-identical offline `vadpcm_dec` utility. The strict corpus
    dialect is pinned across 3,390,907 frames: predictors 0–3 and scales 0–12 only. The separate
    `n64-audio-decode <PTR|ROM> --index N --sample-rate Hz -o out.wav` route requires the rate from the
    caller and emits one selected stored wave once as mono PCM16; explicit PTR input also requires
    `--wave`. Parsing, range checks, decoding, and WAV-size validation complete before the destination is
    touched. Authoritative per-wave/cue rate discovery, loop scheduling, pitch application, BFX
    execution, and cue ownership remain separate;
  - ✅ **Nintendo Sound Tools BFX inspection — SHIPPED 2026-08-10.** These no-magic big-endian
    `fx_header_t` banks store signed default priorities, file-relative component offsets, opaque effect
    payloads, and an EOF-consuming u16 local-wave→PTR table. `N64SoundToolsFxBank` owns every byte and
    validates every local target against a complete PTR graph without requiring WBK audio. With no
    magic to trust, the carver emits `.bfx.n64` only when the complete asset set has exactly one fully
    parsed PTR and one full BFX match; missing, malformed, ambiguous, non-`.bin`, or colliding cases stay
    unchanged, and consumers continue to scan content rather than suffix.
    `n64-audio-fx-inspect <game.z64> -o effects.json` selects the unique structural BFX and PTR singletons;
    standalone BFX input requires explicit `--pointer`. The manifest records that binding basis because
    BFX contains no PTR identity. The schema-v3 follow-up retains the v2 nullable byte-zero binding—direct
    `81 <packed-local>` or the sole Spider-Man `95 <loop-count> 81 <packed-local>` wrapper—then resolves a
    nullable initial event only when the exact following grammar is present: `84 env[7] 9C pan A6 volume
    note<80 packed-length`. It exposes raw operands, the proven runtime pan half, `0x60` rest labeling, and
    finite versus `0x7FFF` indefinite length without inventing MIDI, duration, rate, pitch, or playback
    semantics. Continuation classification is separate and exact: direct remaining `80`, direct `80 E2`
    with only `E2` retained as uninterpreted-after-stop, or wrapper count `0xFF` plus `96 80` as infinite
    repeat. Wrong/truncated grammar, out-of-range bindings, and every other suffix remain nullable; neither
    resolver scans later bytes or changes structural BFX acceptance. Across 13,737 carved assets the
    predicate still finds exactly four candidates and zero false positives, pinning 1,680 components/effects,
    30,626 opaque bytes, and 1,608 mappings. All 1,680 initial bindings/events classify (1,339 finite-stop,
    340 indefinite-unreachable-stop, one infinite repeat) and cover all 1,608 local waves. The manifest
    preserves every raw component byte and reports `opaqueBeyondInitialEvent`; Neversoft cue ownership and
    per-wave rate remain unresolved, pitch/loop scheduling is not applied, and playback/decode/WAV output is
    not executed. This is Nintendo Sound Tools BFX, not the unrelated Codemasters WTC `.bfx` family
    documented elsewhere in this file;
  - ✅ **Strict N64 raw SFX cue inspection — SHIPPED 2026-08-10.** `N64SfxCueBank` consumes zero or
    more complete 16-byte big-endian records followed by the exact `FFFFFFFF` terminator, preserves every
    raw field/hash, and rejects nonzero record padding or trailing bytes. `n64-sfx-inspect <SFX|ROM> -o
    cues.json` uses one deterministic aggregate schema for a direct bank or all strict structural matches
    carved from a ROM. The archive carver now shares the same byte-only predicate, correcting two THPS2
    tables that the old semantic note-range heuristic named `.bin`; ROM inspection still scans every asset
    instead of treating suffixes as proof. The four-ROM scan covers 13,737 assets and pins 83 banks / 3,172 records (THPS1 0,
    THPS2 14/671, THPS3 14/572, Spider-Man 55/1,929), including the valid empty THPS1 aggregate.
    Alias-to-BFX/PTR ownership, rate/pitch application, loop scheduling, and playback remain unresolved;
  - ✅ **N64 direct/compressed animation — conservative binding slice shipped 2026-08-10, exact flat-map profile added 2026-08-11.**
    The reader consumes big-endian 0x2A tables plus 24-byte big-endian `SMatrix` records and mixed-endian
    0x2C tables/channel payloads. Each direct slot is bounded by the next pool offset, sized from playback
    frames and `tween+1`, copied only to that checked size, s16-swapped, and passed to the established PSX
    direct-matrix decoder. Successful opt-in animation normally binds each emitted corner by its global
    `G_MTX` joint when render placements are unique and the interpretation is proven by coincident
    addressing, an out-of-range `objectIndex + G_MTX`, or a hierarchical super's positional part order.
    The exact Spider-Man `map` payload instead binds `objectIndex + G_MTX` and uses vertex factor k=1;
    ordinary non-profile conversion and invalid/all-failed selections retain their static path. The GUI
    Animations pane routes exact selected slots, while
    `mesh --n64-animations` explicitly requests the full eligible bank. A four-ROM CorpusFact pins 155
    animated nonempty shells / 3,259 clips and admits all 155 / 3,259: 97 shells / 802 direct clips plus
    58 / 2,457 compressed clips. Spider-Man slot 007 `docock` supplies the positional-HIER oracle: its shell is
    field-identical to PSX, all 256 referenced vertices map `G_MTX m` to PSX positional mesh `m`, and all
    43 compressed clips match across 536,820 decoded s16 samples. Flat slot 108 `map` supplies the
    relative/k=1 oracle: its 1,776-byte shell (`2712A50E…BD9`), 41,552-byte bank (`F1439FD7…65A`), and
    render-bank id 215 must all match; its PC sibling is 32,536 bytes (`75EF75D6…56B0`), agrees on all
    12 objects and 812 distinct positions, and every placement uses `G_MTX 0`. Static and animated
    positions therefore remain identical while JOINTS_0 resolves to each placing object. All 802 direct slots
    decode within their owned ranges (798 exact and four one-frame-slack
    slots at Spider-Man 145/263 clips 43/50), and seven PSX/N64 Rosetta pairs match after s16 swapping
    across 585,144 payload bytes. Real global-binding oracles include the 110-joint, 33-placement THPS2
    `sk2def` direct shell and the nonzero-placement Spider-Man slot 225 compressed shell; both GLBs pass
    Khronos with zero issues. Preview uses the existing 30 fps PSX cadence, and direct tween endings use
    the established CycleAnim wrap, as explicit export policies; N64 runtime cadence and per-clip
    loop/clamp behavior remain unproven;
  - improve incomplete bundle naming only from proven trigger/content correspondences (418/594 as last counted; that figure predates the 2026-08-13 fail-closed `_L` guard, which deliberately returns slots to numeric — Spider-Man measured 179/261 on 2026-08-14 — so re-count before quoting it), never an arbitrary first-candidate guess. Spider-Man's literal `Jameson` and `DEM4_G` outsider loads now disambiguate the sole remaining matching content occurrences; the duplicated Mysterio/firering pairs stay numeric.

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
  - ✅ **Bare-`.cut` INTERMEDIATE animation inspection — SHIPPED 2026-08-10.** The 43 THUG authoring
    containers pair with 43 compiled `.cut.ps2` containers and 194/194 SKA members match by CUT stem
    plus TOC name checksum. `SkaIntermediateParser` consumes the version-2/3 little-endian full-float
    grammar exactly (embedded checksum/name/parent/flip skeleton, per-bone Q/T counts, 20-byte XYZW Q
    keys, and 16-byte XYZ T keys), pinning **4,588,265 Q + 6,079,925 T keys** and exact EOF across the
    corpus. The `ska` CLI emits schema-v1 `<stem>.ska.json` with raw frames/source quaternions and the
    engine-facing convention. This remains deliberately inspection-only: the embedded skeleton has no
    neutral-pose matrices, three v2 roots receive compiler-side prerotation, and some compiled
    translations wrap the signed-16 runtime range, so neither discovery nor `--ske`/`--skin` advertises
    an unproven glTF export. Four valid members omit bit29, so the supported family is described as
    INTERMEDIATE/full-float rather than universally flag-marked UNCOMPRESSED.
  - ✅ **PS2/Xbox CAS polygon-removal metadata inspection — SHIPPED 2026-08-11.**
    `CasPolyRemovalFile` accepts only explicitly typed `.cas.ps2` and `.cas.xbx`/Windows sidecars:
    little-endian version 2, `{version, removalMask, count}`, then `count × 8` bytes of
    `{mask, vertexReference}` on PS2 or `count × 12` bytes of `{mask, data0, data1}` on Xbox.
    The Xbox packed words expose the runtime-proven mesh load order and three vertex indices while
    retaining both raw words. Exact EOF, a nonnegative count, and the platform-selected stride are
    required. The `cas` CLI emits deterministic schema-v1 JSON and marks geometry application
    `notApplied`; it never infers a dialect from bare `.cas` bytes and never mutates a companion mesh.
    The loose-file oracle covers 8,134 PS2 files / 145,803 records and 4,942 Xbox/Windows files /
    44,106 records (13,076 files / 1,852,608 bytes / 189,909 records, zero failures). Its
    Sample-Builds-relative Windows paths are ordinal-sorted before slash normalization, then hashed as
    UTF-8 path + NUL + raw per-file SHA-256: `533B728E5099B292888F10EF0B10B35E92FFD4F07CF21B1EF8C9D6A998B5B7C8`;
    raw file bytes in that same order hash to
    `3FCDE1FB65DF4C1F0DC303F405767EC64281F3F5A1FF50EF673D5094DC04D019`. `CutArchive` now preserves
    the container platform suffix on CAS members; bare authoring CUTs stay bare. The retained CUT census
    pins 1,058 typed members / 561,520 bytes / 59,188 records, including 662 empty headers whose dialect
    is knowable only from the container suffix.
  - **Still open (not blockers):** applying CAS records to geometry remains unresolved because PS2
    needs the runtime DMA/ADC binding and Xbox needs companion mesh load-order/strip identity. THAW
    `.cas.ngc` uses a distinct big-endian `0x041000FE` envelope and is not accepted.
  - ✅ **Compiled PS2/Xbox WGT v1 mesh-scaling metadata inspection — SHIPPED 2026-08-11.** The retained
    THUG runtime reads a four-byte version, a signed vertex count, then exposes `3 × vertexCount`
    little-endian float weights followed by `3 × vertexCount` signed-byte bone indices to
    `SMeshScalingParameters`; the Xbox and PS2 mesh loaders consume those triples while loading the
    cutscene head. `CutsceneWeightMapFile` admits only explicit `.wgt.ps2`/`.wgt.xbx` version 1 with a
    nonnegative count, finite raw floats, and exact EOF `8 + 15 × vertexCount`. The `wgt` CLI preserves
    every raw triple in deterministic schema-v1 JSON and marks geometry application `notApplied`.
    Twelve loose files / 219,126 bytes / 14,602 vertices (eight unique payloads) form the accepted set;
    their ordinal Sample-Builds-relative path + NUL + raw per-file SHA-256 digest is
    `718F40AC62F4873ADF8BA77612568B1BFFD987C0D83EC0DBBE56B4FCCBF177AC`, and same-order raw bytes hash
    to `F08B803965E3C620BDBA34B5BDEF951960BC7586A26B8FEAF1E110BF4190B15E`. `CutArchive` now preserves
    the platform suffix on WGT members. The v1 CUT oracle pins 212 members in 52 containers /
    3,997,036 bytes / 266,356 vertices (132 PS2 plus 80 Xbox/Windows), and every payload SHA matches
    one of the eight loose v1 payloads.
  - **WGT limits (fail closed):** eight bare authoring files use `4 + 24 × vertexCount` without the
    retained compiler/consumer contract needed to claim their semantics. Four loose plus 40 CUT THUG2
    PS2 files use version 2 and exact `8 + 19 × vertexCount`; their extra leading `4 × vertexCount`
    region remains semantically unowned. Both dialects and `.wgt.ngc` are rejected. Geometry mutation
    remains separate because it needs caller-selected profile bone scales and an authoritative WGT ↔
    companion-skin vertex-order binding; the inspector never infers bone names or changes a mesh.
  - ✅ **`debug.log` texture-name side map — SHIPPED.** `ThawTextureNames.txt` carries 2,132
    compiled-texture checksum → original-art-name pairs harvested from the QTex bundles, and
    `ThawTexFile`/`NgcTexFile` use it before the general QBKey fallback. It remains deliberately
    separate from `QbKeyNames*.txt`: these identifiers are opaque build IDs, not CRC(name) pairs.
- ⚪ **Priority 3 — image formats**: `.tga` DONE 2026-07-11 — all 4 corpus TGAs verified standard
  (types 1/2 uncompressed, one 32-bit with real alpha); decoded via ImageSharp through the
  `Core/Formats/Rle/BitmapFile.cs` facade (`rle` CLI + Bitmap Converter tab), alpha preserved.
  Standard `.bmp` (3,535 files, all `BM`/BITMAPINFOHEADER) shipped in the same pass. Remaining:
  `.tim` (5 files) — standard PSX TIM headers, but they live in the multi-game demo-disc build
  (`Spider-Man (2000-2-4, PSX)`, not "Spider-Man PC" as the earlier census said) under third-party
  dirs (`DD/`, `WTC/` = TOCA) — out of scope as non-Neversoft content.
- ✅ **`.dff` — DONE 2026-08-07.** Was a routing-only gap; `.dff` now resolves through
  `MeshTypeDetector` alongside `.skn`. 477 files.
- ✅ **THPS2X `.ANIM` frontend timelines — SHIPPED 2026-08-10.** The old “Xbox-era skeletal
  animation” label was false: all 193 files live under `frontend/` and form UI timeline forests.
  `Thps2XFrontendAnimFile` parses the `Anm\0` v1 header and a deterministic recursive node grammar:
  bounded ASCII names, twelve raw base floats, one semantic-free u32, 42-byte timeline keys, nested
  nodes, and a closing screen/owner string. The uncertain u32 and u16 key fields remain raw rather
  than receiving invented meanings. Every file consumes exactly to EOF: 921 roots, 1,148 nodes,
  4,581 keys, maximum observed depth 1. `thps2x-anim` writes schema-v1 inspection JSON, preserving
  relative directories in batch mode so repeated basenames cannot overwrite. This is inspection,
  not skeletal export or a claim that the UI runtime has been reproduced.
- ✅ **`.pcm` — DONE 2026-08-07.** 2,752 files (1,376 identical on the Xbox and Windows discs).
  RIFF + Xbox ADPCM 0x0069, mono, nBlockAlign 36, wSamplesPerBlock 64, at 11025/22050/44100/48000.
  A block emits the header predictor as sample 0 then **63** nibbles — the 64th is padding;
  settled by diffing both readings against ffmpeg's `adpcm_ima_xbox` (bit-exact one way,
  mismatched the other). `Core/Formats/Audio/XboxImaAdpcm.cs` + `XboxPcmDecoder.cs`, on a new
  shared `Core/BinaryIO/RiffWaveReader.cs`.
- ✅ **`.snd` — DONE 2026-08-09.** 788 files, THUG2 Windows only. The decrypted retail
  executable exposes the complete decoder at VA `0x005F5A20`: low nibble first, canonical IMA
  tables, but the step index is updated **before** the current step lookup and the delta is
  `((step * magnitude) >> 2) + (step >> 3)`. Predictor and index start at zero and carry across
  the whole file. `nAvgBytesPerSec` is the decoded byte count, so the loader requests exactly
  `nAvgBytesPerSec / 2` samples and ignores the last high nibble for odd counts. The original x86
  routine and the clean-room implementation matched byte-for-byte on a stress vector; 350
  independently encoded PC/Xbox name pairs reach median windowed NCC 0.9906. Implemented by
  `Thug2PcSndCodec` / `Thug2PcSndDecoder`; full provenance is in
  `docs/formats/thug2-pc-snd.md`.
- ⚪ Not formats / no action: `.dep` (build path lists), `.chk` (checksum text), `.anr` (text
  anchor scripts), `.rec` replays, `.seq` ("Sequencer File" text on the DC proto), standard
  `.gif/.ogg/.jpg`, installer debris. `.zoo`/`.bfx`/`.ppv` = Codemasters WTC (see PPV entry).

---

## Done (for reference) ✅

- ✅ **Payload-bearing PS2 `.stex` standalone decode** — the earlier “raw blob needs external metadata” conclusion was false. Byte-zero owner blobs contain their texture records and decode through `ThawZoneTexFile.DecodeAllFromFile`; `FormatProbeTexture` and the Texture tab route them directly. `ThawArchiveTextureRegressionTests` pins two real nested `.stex` files by checksum, dimensions, and RGBA SHA-256. The 2026-08-09 corpus audit found all payload-bearing THAW/P8/THPG files recognizable; three 144-byte THAW owner stubs contain no texture records and correctly produce no output.
- ✅ **PSX animated-surface playback** — both previously tracked paths now ship:
  - UV wibble (2026-07-17): face bit 5 is UV scroll + per-vertex sine wibble, not an image flipbook; actual membership comes from tagged chunk 6. The exporter carries velocity/frequency/amplitude/phase with a frame-zero fallback, the viewer reproduces the native 64-sample table, and `.blend` exports build a timeline-driven UV shader. Spider-Man PC v6 correctly starts from widened face UVs in fixed 512-coordinate space and doubles only the scroll term; its legacy base-UV bytes are non-authoritative.
  - Colour pulse (2026-08-07, `a9d7c1a`; Blender follow-up 2026-08-10): frame zero remains a portable fallback; the GLB carries pre-transformed channel keys and the in-app viewer evaluates them on the shared 60 Hz timeline. A clock correction makes that timeline advance when either animation type is present instead of returning early with zero wibble meshes; the real February `l1a1_o.psx` pulse-only bank pins 6 channels, 15 pulsed primitives, 192 pulsed vertices, and zero wibble primitives. Direct `.blend` export now carries validated portable tables and byte-per-vertex POINT channel IDs into a shared Geometry Nodes evaluator that stores animated CORNER `Color`; malformed buffers/channels remain static, and additive/subtractive alpha, zero holds, accumulators, overbright keys, `fps_base`, mixed faces, and a 56-channel stress graph survive save/reopen in Blender 5.1. Blender native-time zero preserves the authored bake; later ticks use portable linear-output interpolation rather than claiming the viewer's packet-domain/nonlinear PS1-exact result.
- ✅ **THPG / Project 8 bare `.col` and `.skin` routing** — shipped 2026-08-07 (`21edfa5`). `MeshTypeDetector` recognizes bare `.col`, content-probes ambiguous `.skin`/`.mdl`, and routes `.dff`; the permissive Xbox `(1,1,1)` probe is intentionally last because many PS2-build scenes share that prefix. Routing tests pin both collision and scene cases. The underlying THPG/P8 `.col` files are ordinary version 10; the old `00 FF 00 FF` evidence was corrupt pre-offset-fix PAK extraction.
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
  build logs (text), Spider-Man `.tex` = hash manifests (text), `.psh` = C headers, and `.fam.*` =
  appearance config data. The old `.cas.*` grouping was incorrect: `.cas.ps2`/`.cas.xbx` are binary
  polygon-removal metadata (inspection now ships), while `.cas.ngc` is a separate unresolved envelope.
  (The 2026-07-07 claim that `.mpk.ngc` = padding stubs was wrong for 821 of
  them — they are apk companion data files; see the .apk.ngc entry above.)

## By design / won't-fix ⚪

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums. Engine analysis plus string extraction across 15 executables found 0 texture-name matches. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).
