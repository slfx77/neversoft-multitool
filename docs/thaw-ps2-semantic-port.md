# THAW PS2 Semantic Port

## Status

The current THAW PS2 shipping path stays on the existing heuristic mesh extractor in
`ThawPs2SkinFile`. This document locks the replacement plan and the internal seams for
the semantic-port replay layer. The first milestone scaffolding now exists in
`ThawPs2Replay.cs` and is exposed through `ThawPs2SkinFile.ReplayBatches(...)` for
trace-oriented tests and diagnostics.

## z_sm Worldzone Audit (2026-05-08)

This section audits the current THAW PS2 worldzone path against Santa Monica
`z_sm`. It is intentionally separate from the THAW skin replay plan below: the
worldzone converter already uses the `Ps2GeomFile` / `Ps2GeomVifVertexDecoder`
path, while skin replay is still being replaced.

Audit command:

```powershell
dotnet run --framework net10.0 --project src\NeversoftMultitool\NeversoftMultitool.csproj -- mesh "Sample\Builds\Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)\DATAP\worlds\worldzones\z_sm\z_sm.pak.ps2" --worldzone-time-of-day all --scale 0.01 --format blend --blender-helper "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" -o TestOutput\audit_z_sm_blend -v
```

THAW worldzones now always flow through the `MeshModelParser` / `ModelDocument`
pipeline. The old `--worldzone-debug-textures` and
`--worldzone-debug-leaf-colors` flags are intentionally rejected so they cannot
select the legacy worldzone exporter.

### File Coverage

Input `z_sm.pak.ps2` is 8,101,680 bytes. Its extracted entry payloads total
7,378,029 bytes; the remaining bytes are PAK container overhead.

| Region | Bytes | % of PAK | Current coverage |
| --- | ---: | ---: | --- |
| `.mdl` entries | 2,920,544 | 36.05% | Converter opens all 5 MDLs; 2 convert, 3 skip. |
| converted MDL entries | 2,912,656 | 35.95% | `00009C40.mdl` + `003A0780.mdl`, 55,230 `.blend` triangles. `003A0780.mdl` is logically extended by 2,704 bytes from the following COL prefix before parse. |
| `.tex` + `.stex` dictionaries | 3,192,144 | 39.40% | Texture catalog decodes them, plus sibling zone texture dictionaries. |
| `.A7DEA591` texture-owner blobs | 55,020 | 0.68% | Used by texture/material group matching. |
| `.col` collision | 690,446 | 8.52% | Collision itself is not part of this render/export path. The first `0xAB0` bytes of `00665640.col` are a level-MDL preamble overlap/continuation; the converter consumes the new `0xA90` bytes from that prefix for `003A0780.mdl`. |
| `.91E1028D` placement/unknown blobs | 222,544 | 2.75% | Not consumed for the level shell export. |
| `.ska` + `.ske` animation/skeleton | 263,192 | 3.25% | Not consumed for static worldzone shell export. |
| `.img`, `.shd`, misc unknown | 34,139 | 0.42% | Mostly outside the current worldzone geometry path. |

Worldzone ModelDocument conversion result for `z_sm.pak.ps2`:

| Item | Result |
| --- | ---: |
| MDLs in PAK | 5 |
| MDLs converted | 2 |
| MDLs skipped | 3 (`0004B780`, `0004CAA0`, `0009B5A0`) |
| converted placements | 2 |
| triangles written | 55,230 |
| ModelDocument materials | 762 |
| ModelDocument textures | 584 |
| `.blend` mesh objects | 3,027 |
| `.blend` images | 764 |
| billboard-pattern chunks | 150 parsed, suppressed in static `.blend` |

Skipped MDL details:

| MDL | Size | Standalone result | Why the worldzone export skips it |
| --- | ---: | --- | --- |
| `0004B780.mdl` | 2,192 | 0 triangles | Has a VIF/GIF setup-looking stream at `0x70C` and a `CDCDCDCD` sentinel at `0x6D4`, but no `MSCAL` / position-bearing mesh batch is decoded. |
| `0004CAA0.mdl` | 5,584 | 80 triangles | Contains small local-space object geometry, not positioned world shell geometry. Its valid DMA/VIF chunks are at `0x760` and `0x10A0`; decoded bounds are centered near origin, so the worldzone filter drops it as `IsLocalSpace`. |
| `0009B5A0.mdl` | 112 | unsupported / file too small | No VIF start and no preamble; the bytes are a tiny packed 16-bit sequence/list, not a renderable MDL under the current parser. |

The main level MDL is `003A0780.mdl`. Its PAK table size is 2,903,776 bytes,
but the render preamble continues into the following `00665640.col` entry. The
logical render slice is therefore 2,906,480 bytes after adding the 2,704-byte
continuation. The level-MDL byte audit, using the same leaf flag and `K`
derivation as production code, shows:

| `003A0780.mdl` measurement | Value |
| --- | ---: |
| derived data base `K` | `0xA70` |
| PAK-table MDL size | 2,903,776 bytes |
| logical render-slice size | 2,906,480 bytes |
| preamble records | 4,016 |
| leaf records | 2,876 |
| valid leaf VIF chunks | 2,876 / 2,876 |
| leaf chunk byte union | 2,566,864 bytes |
| VIF payload byte union | 2,543,856 bytes |
| preamble + leaf chunk byte union | 2,888,144 bytes |
| preamble + leaf chunk coverage | 99.37% of the logical render slice |
| static VIF inventory | 3,058 position-bearing batches, 3,208 MSCALs |
| billboard-pattern chunks | 150 parsed, suppressed in static `.blend` |

The remaining 0.63% of the logical main MDL is 18,336 bytes that are not
converted from the authoritative preamble leaf list. These bytes are metadata or
small control gaps, not missed drawable VIF payload:

| Range / category | Bytes | What it appears to contain |
| --- | ---: | --- |
| `0x000000..0x000A70` | 2,672 | Pre-data-section header/setup bytes. No valid leaf DMA prologue was found here. |
| `0x000A70..0x0036B0` | 11,328 | Data-section root/skeleton metadata before the first referenced leaf chunk. Starts with root offset `0x002767D0`. |
| inter-leaf gaps | 4,288 | 28 small gaps between referenced leaf chunks. They look like count/offset control tables, not VIF vertex payloads. |
| `0x277240..0x277270` | 48 | Root-node prefix immediately before the preamble table. |

The earlier audit flagged `0x272C10..0x277240` as an unreferenced VIF chunk run:
33 valid DMA/VIF chunk-shaped records immediately before the root node, with 28
position/UV/color-bearing chunks. Investigation showed those chunks are not
orphaned. The following `00665640.col` entry starts with a preamble
overlap/continuation, and its records at COL offsets `0x10..0xA10` have
`Field40` values pointing back to `0x272C10..0x277150` inside `003A0780.mdl`.
One more continuation record is a non-leaf/terminator. The PAK table MDL end is
`0x00665660`, the COL entry starts at `0x00665640`, and actual collision-looking
data begins at COL `+0xAB0`.

After stitching that continuation, the converter exports the 33 additional
leaves. They add 308 triangles and 33 material rows: 28 NightOverlay glow rows
and 5 Base rows using glow texture `0x16D82D6A`. The glow/light-flare TEX0 states
are `0x2016B345DD30ABE0` for texture `0xC8E4B7A9` and
`0x2006B3859930ABC0` for texture `0x16D82D6A`.

For object MDLs like `00009C40.mdl`, the level-leaf audit only accounts for
2,752 / 8,880 bytes because that file uses the object-MDL scanner path; it still
converts to 106 triangles.

`z_sm_net.pak.ps2` is the network-play variant and is out of scope for this
non-net audit. The only relevant interaction is that the current texture catalog
scans sibling zone texture dictionaries; the `z_sm.pak.ps2` geometry export
uses the non-net texture entries before collapsing emitted rows into
ModelDocument materials.

### Material And Texture Coverage

The texture catalog decoded 590 unique textures from 5 zone texture entries:

- `z_sm.pak.ps2`: 728 catalog rows.
- `z_sm_net.pak.ps2`: 674 catalog rows.

For the `z_sm.pak.ps2` geometry export, all emitted mesh rows resolve against
`z_sm.pak.ps2` texture entries. The ModelDocument export collapses those into
762 Blender materials and 584 source texture records. The emitted rows cover
566 unique used texture checksums and no untextured material rows. Most
resolutions are exact material-group matches; destination-alpha synthesized
variants are still generated for C=Ad consumers.

Alpha classification for emitted `z_sm.pak.ps2` `.blend` objects:

| Alpha mode | Rows |
| --- | ---: |
| `OPAQUE` | 1,246 |
| `MASK` | 242 |
| `BLEND` | 1,539 |

Render-layer classification:

| Render layer | Rows |
| --- | ---: |
| `Base` | 2,873 |
| `NightOverlay` | 154 |

Callout leaves from the Blender comparison:

- `003A0780_world_leaf_01712` (powerline, texture `0xB8968FAE`) now remains
  `BLEND` / `BLENDED`; the Blender importer no longer promotes broad translucent
  surfaces to the dither/clip render path. The PC `z_sm` material using this
  texture (`0x36438118`) is a single-pass blended material, which supports the
  removal of the converter-side dither rewrite.
- `003A0780_world_leaf_00453` is one of the 150 Format-B billboard descriptors.
  These parse for audit counts but are suppressed from the PS2 static `.blend`
  until the runtime billboard transform/camera-facing path is modeled. The PC
  `z_sm` baseline also contains 150 billboard sectors/meshes, so suppression is
  a debugging quarantine, not proof that those effects are absent in-game.
- `003A0780_world_leaf_03005` is a broad standard source-alpha pass using the
  same exact-bbox texture as an earlier opaque sibling (`003A0780_world_leaf_03155`,
  texture `0x34623305`). The PC baseline uses `0x34623305` as one pass of a
  four-pass terrain material (`0xF1E1AD2C`, with `0xF2D86A26`, `0x19399741`,
  and `0xE6BE2F91`), so the PS2 standalone overlay is not semantically correct.
  Suppressing it makes the static export less misleading, but the real fix is
  multi-pass terrain/material reconstruction.

### PC Baseline

The PC build can now be used as an asset-level baseline for the non-net `z_sm`
worldzone. Its extracted large MDL entry starts with a small worldzone wrapper;
the normal THAW scene payload begins at `0xCC0`, where the material list
contains 951 rows and ends exactly at the `0xBABEFACE` sentinel at `0x44F10`.

Baseline command:

```powershell
dotnet run --framework net10.0 --project src\NeversoftMultitool\NeversoftMultitool.csproj -- mesh "Sample\Builds\Tony Hawk's American Wasteland (2006-2-6, PC - Final)\Installed\program files\Aspyr Media, Inc\THAW\Game\data\worlds\worldzones\Z_SM\z_sm.pak\007858E0.mdl" --tex "Sample\Builds\Tony Hawk's American Wasteland (2006-2-6, PC - Final)\Installed\program files\Aspyr Media, Inc\THAW\Game\data\worlds\worldzones\Z_SM\z_sm.pak\00319D60.tex" --scale 0.01 --format blend --blender-helper "C:\Program Files\Blender Foundation\Blender 5.1\blender.exe" -o TestOutput\audit_z_sm_pc_baseline_mesh_scaled_v2 -v
```

Current PC baseline result:

| Item | Result |
| --- | ---: |
| output | `TestOutput\audit_z_sm_pc_baseline_mesh_scaled_v2\007858E0.blend` |
| triangles | 48,455 |
| sectors | 1,126 |
| mesh primitives | 2,425 |
| materials in source scene | 951 |
| materials emitted to Blender | 923 |
| decoded PC textures | 577 |
| embedded images | 469 |
| billboard sectors/meshes | 150 |

PC material sanity after the 2026-05-10 baseline refresh:

| Check | Result |
| --- | ---: |
| Blender materials marked `Opaque` | 470 |
| Blender materials marked `Mask` | 289 |
| Blender materials marked `Blend` | 163 |
| Materials with at least one clamped texture axis | 326 |
| `WrapU=ClampToEdge` | 235 |
| `WrapV=ClampToEdge` | 323 |

The PC THAW material record stores per-pass texture addressing as packed
16-bit U/V fields where `0` is wrap and `1` is clamp. These are now parsed and
normalized before export. PC `.blend` import also trusts ModelDocument
`AlphaMode` instead of treating XBX/PC `sorted` or low `alphaCutoff` values as
proof of transparency, and it disables PS2-specific luminance-alpha synthesis
and vertex-alpha-driven transparency for `XbxScene` packages.

Every exported PC primitive has `POSITION`, `NORMAL`, `COLOR_0`, and
`TEXCOORD_0`. That makes the PC asset useful for verifying PS2 UVs, texture
checksums, material/pass intent, billboard placement, and reconstructed normals.
It is not a renderer-perfect baseline: PC and PS2 have different runtime
renderers, and PS2 GS behavior still needs capture-based validation.

### Current Semantic Mapping

| Semantic | Current parser/export behavior | Audit status |
| --- | --- | --- |
| Position | VIF `V4_16` / `V4_32`; position scale is `1/16`. Level MDLs use identity placement because raw vertices are already in the Y-up glTF convention. Object MDLs use placement resolution when bones/trailers exist. | Good for level shell coverage; still needs GS camera-space comparison for exact runtime transform parity. |
| UV | VIF `V2_16` / `V2_32`, plus THAW worldzone `V3_16` at VU address `0x007` where the first two components are S/T. UV scale is `1/4096`; export flips V because decoded PNGs are top-down. | Covered. `003A0780.mdl` static inventory sees 3,049 UV-bearing batches, including 107 `V3_16` UV batches. |
| Normals | VIF `V3_16`, normalized by `1/32767`. Missing normals use a flat per-triangle fallback during ModelDocument strip emission; `.blend` exports no longer smooth those fallback normals across strips. `GltfNormalSmoother` may still replace/merge normals in GLB output. | Sparse in z_sm: only 8 batches in the main MDL inventory carry normal-looking data. Final PS2 normals are therefore mostly reconstructed. The PC baseline has normals on every primitive and is the best current asset-level comparison target. |
| Tangents | No tangent stream is parsed and no tangent attribute is emitted. | Missing. Any tangent-space normal/specular comparison will fail until tangents are derived or replayed from a known source. |
| Vertex color / lighting | VIF `V4_8` and `V3_8` colors are parsed. PS2 color range is treated as `0..128`, then clamped to glTF `0..1`; missing color defaults to 128. Worldzone exports do not apply synthetic sun/normal lighting by default. | Covered as parsed vertex color, not as a full PS2 lighting model. `Ps2WorldzoneLighting` exists as an explicit opt-in option record only. |
| Blend mode | Per-batch GS context is scanned from VIF/GIF setup and inherited across leaves: `TEX0`, `TEX1`, `MIPTBP1/2`, `CLAMP`, `ALPHA_1`, `TEST_1`, `TEXA`, `FRAME_1`. glTF/Blender alpha modes approximate source-alpha, fixed-alpha, additive/subtractive, alpha-test mask, and destination-alpha cases. Blender import no longer rewrites broad `BLEND` materials to the dither/clip path. | Good approximation, not GS-exact. PC material passes can validate intent for many textures, but destination-alpha behavior still needs PS2 GS capture correlation. |
| Texture path | PS2 data does not provide a normal filesystem texture path. The exporter resolves `TEX0` plus material group through zone texture dictionaries/owner blobs and emits generated PNG names/checksums in debug output. | Strong for current z_sm export: 566 unique used checksums, zero untextured material rows. Exact runtime TEX0 parity still needs GS correlation. |
| Coordinate system | Level worldzone vertices are currently treated as already Y-up/glTF-friendly and are emitted with identity root placement. `CoordinateScale` is a scalar only. | Visually plausible in overview render; not yet proven against a GS-derived view/projection/camera path. |

### GS Capture Comparison

Existing diagnostic tool:

```powershell
python tools\diagnostics\thaw_gsdump_parser.py "C:\Users\mmc99\Documents\PCSX2\snaps\Tony Hawk's American Wasteland [Collector's Edition]_SLUS-21295_20260507234210.gs" --gif
```

The current tool parses the PCSX2 dump container and GIF register stream. It is
useful for confirming runtime draw state, but it is still a packet/register
summarizer, not a mesh/raster comparator.

Sampled captures from `C:\Users\mmc99\Documents\PCSX2\snaps`:

| Capture | XYZ writes | Unique runtime TEX0 | Exact TEX0 overlap with `z_sm` export |
| --- | ---: | ---: | ---: |
| `20260507234210.gs` | 115,214 | 254 | 145 |
| `20260507234126.gs` | 112,779 | 243 | 142 |
| `20260507234026.gs` | 142,080 | 302 | 175 |
| `20260507233949.gs` | 86,632 | 177 | 92 |
| `20260507233934.gs` | 132,260 | 261 | 147 |

The preamble-continuation light/glow leaves match exact runtime texture states in
the captures. In the latest capture (`20260507234210.gs`), TEX0
`0x2016B345DD30ABE0` (`0xC8E4B7A9`) accounts for 5,902 XYZ writes and TEX0
`0x2006B3859930ABC0` (`0x16D82D6A`) accounts for 472 XYZ writes. Other sampled
captures also use those exact states, so the continuation data is active
in-game rendering data, not stale geometry.

The latest capture is almost entirely VU1 output: 10,573 Path1New transfer
packets, 23,019 GIF tags, and 115,214 XYZ writes. Runtime primitives are mostly
triangle strips (`108,726` XYZ writes), with sprites and triangle fans also
present. This confirms the in-game frame contains data outside a static
worldzone GLB comparison: HUD, skater, particles/effects, dynamic props,
camera/view state, and runtime GS state.

Visual status:

- The exported `z_sm_worldzone.glb` overview render contains the expected Santa
  Monica shell: beach/ocean slab, boardwalk/pier, buildings, cliffs, city
  skyline, trees, and night overlays.
- The PCSX2 screenshot shows a camera-local nighttime street/building view with
  HUD, skater, dynamic lighting, projected shadows, fog/darkness, and UI
  overlays. A visual mismatch here is expected without camera extraction and GS
  replay.
- The current GLB path can show whether geometry/textures are generally present,
  but it cannot prove per-pixel parity with the game.

Next GS work needed for a real comparison:

1. Extend `thaw_gsdump_parser.py` to emit ordered GS vertex events, not just
   register counts: current `PRIM`, `RGBAQ`, `ST`/`UV`, `XYZF2`/`XYZ2`, `ADC`,
   `TEX0`, `ALPHA`, `TEST`, `CLAMP`, `FRAME`, `ZBUF`, `XYOFFSET`, `SCISSOR`.
2. Decode runtime strips/fans/sprites from those events into a frame-local mesh
   stream.
3. Correlate GS `TEX0` fields to `ZoneTextureCatalog` entries by VRAM page,
   CLUT, dimensions, and material group. Exact full-value TEX0 matching is too
   strict; the latest capture only overlaps 145 / 254 runtime TEX0 values with
   the export.
4. Add capture-to-parser matching by texture state, bounding box, vertex count,
   and draw order so we can identify which parser leaves are missing or wrong.
5. Optional but likely required for pixel parity: rasterize enough GS behavior
   for alpha test, destination alpha, depth test, fog, scissor, XY offset, and
   framebuffer alpha writes.

Bottom line: the current parser is reading almost all of the main `z_sm` level
MDL's addressed geometry bytes and resolves the exported material texture set
well. The largest semantic gaps versus the actual in-game render are GS-exact
blend/depth/destination-alpha behavior, sparse or reconstructed normals, no
tangents, no real runtime lighting pass, and no camera/state-aligned GS replay
for the captures.

## Locked Decisions

- Keep `Ps2SceneGltfWriter` as the first downstream consumer.
- Replace topology reconstruction upstream of glTF export, not export semantics first.
- Port only the needed PCSX2 behavior into repo-native C#.
- Do not embed PCSX2, shell out to PCSX2, or add native dependencies.
- Scope replay to the THAW PS2 skin path only:
  - VIF stepping and `vif_next_code` behavior
  - `STCYCL`, `BASE`, `OFFSET`, `ITOP`, `STMOD`, `STMASK`
  - `UNPACK` addressing, `FLG`, `USN`, and CL/WL write behavior
  - double-buffer state: `TOPS`, `TOP`, `DBF`
  - `MSCAL` and `MSCNT` save-and-flip behavior with `XTOP`-visible batch starts
  - the subset of VU1 output behavior needed to explain kicked strip data
  - `XGKICK` and GIF packet interpretation for vertex emission
  - GS queue semantics for strip reconstruction, specifically `XYZ` and `ADC`
- Explicitly exclude rasterization, texture sampling, full GS emulation, and unrelated
  PS2 scene formats.

## Internal Seams

The replay implementation is split into four internal seams. These are the units that
later milestones should expand instead of adding more parser heuristics.

- `VifReplayState`
  - Owns VIF register state and double-buffer state per command.
  - Tracks `CL`, `WL`, `BASE`, `OFFSET`, `TOPS`, `TOP`, `DBF`, `ITOP`, `STMOD`,
    and `STMASK`.
- `Vu1BatchSnapshot`
  - Captures replay-state facts at each `MSCAL` or `MSCNT` boundary.
  - Holds `XTOP`, pre/post `TOPS`, written-memory span, decoded parser tag, and the
    set of `UNPACK` commands that produced the batch.
- `GifKickPacket` and `GsVertexEvent`
  - `GifKickPacket` is the replay-side packet summary that already exists.
  - `GsVertexEvent` is the next seam to add when replay moves from batch snapshots to
    deterministic strip emission.
  - It should carry the ordered GS queue events, including `ADC/no-kick`.
- `ThawReplayMeshExtractor`
  - Planned replacement for the current `c2/c3`-driven topology reconstruction path.
  - Its input is replayed kick and vertex events, not parser heuristics.
  - Its output remains `Ps2Mesh` and `Ps2Vertex` so the existing writer can stay in place.

## Corpus And Proving Set

The rollout and validation corpus is all THAW PS2 skins. The fixed proving set is:

- `skater_lasek`
- `skater_hawk`
- `acc_backpack01`
- `body_f_torso`
- `pro_vallely_head`
- `sec_jimbo_xen`

`skater_lasek` stays first because it exposes both missing faces and incorrect
connections.

## Current Heuristic Inventory

This is the replacement map for the current parser behavior.

| Current behavior | Classification | Replay outcome |
| --- | --- | --- |
| `FLUSH+DIRECT` setup detection | True format fact | Survives as format decoding |
| pre-first-`FLUSH` preamble batches | True format fact | Survives as format decoding |
| `NLOOP` output-window bounding | Inferred VU1 behavior | Disappears into replayed kick packets |
| setup-local slot buffers | Inferred VU1 behavior | Disappears into replayed VU1 state |
| constrained second-pair copies | Unsupported guess | Removed when replay emits real kick events |
| address-gap fallback splitting | Unsupported guess | Validation-only fallback during rollout, then removed from THAW path |
| oversized-triangle restart filter | Exporter workaround | Validation-only diagnostic after replay is complete |
| odd-slot parity tracking | GS strip fact | Survives, but becomes replay-derived instead of heuristic |

## Architecture

`ThawPs2SkinFile` should converge on a thin orchestration role:

1. Parse the THAW container and entry tables.
2. Find setup ranges, including the preamble range before the first setup boundary.
3. Hand VIF chains to the replay layer.
4. Convert replayed kick output into `Ps2Scene`.

The current writer stays unchanged except for metadata it already supports, such as
strip parity or restart markers sourced from replay.

Diagnostics stay available, but they remain separate from shipping parser code. The
Python simulator and scratch tools are truth-discovery inputs, not production
dependencies.

## Rollout

### Milestone 1

Trace-parity scaffolding for `skater_lasek`.

- Land replay-state types and `ReplayBatches(...)`.
- Verify VIF register transitions and `XTOP` snapshots against the Python reference.
- Do not switch the production parser.

Stop/go gate:

- replay-state trace parity first
- no production THAW parser regression

### Milestone 2

Replay-driven mesh extraction for the fixed proving set.

- Add ordered GS queue events.
- Add `ThawReplayMeshExtractor`.
- Compare replay-driven strips against the current heuristic output and PC meshes.

Stop/go gate:

- batch-level replay parity
- visible topology improvement on the proving set

### Milestone 3

THAW-wide rollout across all skins.

- Switch THAW skin parsing from heuristic reconstruction to replay extraction.
- Keep THAW-wide zero-failure conversion and glTF validation as hard gates.

Stop/go gate:

- replay parity holds across the proving set
- THAW-wide conversion remains zero-failure
- glTF validation remains clean

### Milestone 4

Delete obsolete heuristic branches.

- Remove setup-local slot-buffer reconstruction.
- Remove constrained second-pair copy logic from the THAW shipping path.
- Keep only replay-backed metadata and validation diagnostics.

## Acceptance Checks

### Replay Parity

- VIF register transitions match the reference trace for proving files.
- `XTOP` snapshots match at every batch boundary on proving files.
- Kick-packet counts and `ADC/no-kick` placement match the replay reference.
- Double-buffer phase changes and batch boundaries are identical.

### Mesh Parity

- Compare exported non-degenerate triangle counts against PC companions.
- Keep unique unordered triangle-set comparison, but do not treat it as the only metric.
- Keep `skater_lasek` as the first visual review file.

### Regression Gates

- Existing THAW parser tests stay in place until replay fully replaces the heuristic path.
- THAW-wide zero-failure conversion remains required.
- glTF validation remains required.
- “Count matches but topology is still wrong” is detected by:
  - unique unordered triangle-set comparison
  - proving-set visual review
  - replay-event parity against the reference trace

## Implementation Notes

- The replay layer is intentionally additive until Milestone 3.
- The shipping parser should not depend on replay-only interpretations until replay
  parity is strong enough to replace the current topology reconstruction path.
- The Python simulator and scratch harnesses should be treated as reference or analysis
  tools only; the production target remains repo-native C#.
