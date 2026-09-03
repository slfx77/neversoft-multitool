# GBA rider models and animation: Claude handoff

Last audited: 2026-09-02

This document is the continuation point for **rider mesh and animation work only**.
It covers THPS3, THPS4, THUG, THUG2, American Sk8land, and the unfinished rider
side of Downhill Jam (DHJ). It is intentionally evidence-first: “unknown” means
that no reusable source model has been closed, even when a convincing software-
rendered rider is visible on screen.

## Scope guard and concurrent-work results

In scope:

- Locate and decode the THPS3-through-Sk8land rider/model containers.
- Prove their renderer, geometry, animation, and appearance bindings.
- Finish DHJ rider animation, packed normals, palette/ramp binding, and non-rider
  object models.
- Add tests and user-facing extraction only after each structure closes.

Out of scope:

- THPS2 rider work. It is already the feature-complete baseline used for parity.
- Any level-art, level-collision, entity, or course extraction.
- Exact later-cart art-to-collision registration. The visible-tile shear itself
  is fixed; only the still-unknown projection origin remains outside rider scope.
- DHJ course or course-collision reverse engineering. That independent level
  path is now implemented and is not a prerequisite for rider work.

Concurrent level work completed in this same uncommitted working tree:

- `GbaDhjCourse`, `GbaDhjCourseGeometryWriter`, and `gba-dhj-level` structurally
  locate all 11 DHJ course/texture pairs and export indexed visual GLBs plus the
  exact referenced collision polylines (with narrow viewer ribbons and a clearly
  labelled road-edge visualization). The generic `gba-level` route delegates to
  it for a `BXS` cartridge. This does not decode the 16-byte placed-object bank;
  locating non-rider object models remains item 7 below.
- `GbaLaterLevelArt` now follows the later engine's most-significant-nibble-first
  4bpp row expansion. Fresh THPS4, THUG, THUG2, and Sk8land renders no longer have
  the recurring 8-pixel sawtooth/shear, and corpus pixel hashes pin the ordering.
  This does not solve the separate approximate art/collision registration issue.

The working tree contained broad, intentional uncommitted GBA changes at audit
time. Run `git status --short` before editing and preserve unrelated work.

## Executive status

| Cart | Code | Source mesh | Animation | Appearance | Shipping route |
|---|---|---|---|---|---|
| THPS3 | `AT3E` | **SHIPPED**: 6-entry directory 0x08161CA4; rec0 = 139 verts / 243 faces, faces carry 64×64-page UVs | **SHIPPED**: 5,024 pose frames; clip table CLOSED as tick ranges into the remap after the bank (239 clips, 7 empty); deck translation proven | Texture page NOT located; materials diagnostic | `archive` carves `models/00_rider.chr.gba`; `mesh [--gba-animation(s)]`; Meshes & Characters + Animations pane |
| THPS4 | `AT6E` | Real-time 3D proven; `S3D` v6 model at 0x080C8550, not closed | Unknown | Unknown | None |
| THUG | `BTOE` | Unknown | Unknown | Unknown | None |
| THUG2 | `B2TE` | Unknown; an old loose header-like hit did not close | Unknown | Unknown | None |
| Sk8land | `BH9E` | Unknown | Unknown | Unknown | None |
| DHJ | `BXSE` | 24 closed 13-part rider variants | **all 94 clips** export as morph targets | Debug colours; real UVs export, texture page not located | `gba-dhj-model`, one pose or `--animate` |

Do not infer a shared rider format merely because THPS4 through Sk8land share
parts of their level-art/collision design. No rider-container equivalence has
been demonstrated.

## Pinned retail corpus

All exact offsets below refer to these files. The tests locate them by build name
through `TestPaths`; SHA-256 is included so another setup can establish that it
has the same oracle.

| Cart | Size | SHA-256 |
|---|---:|---|
| THPS3 USA/Europe | 8 MiB | `59ED9DC1FF97F5A96D72D1DCFFB14C84A2D68AEE6155FCCD88D497B962860064` |
| THPS4 USA/Europe | 8 MiB | `29D60B5B066D01963EC165333E8ED1F514A0BD697A9C6F06704024ECCD7FF139` |
| THUG USA/Europe | 8 MiB | `74C7AC7CFDB488C109AC931791B723FBE9FF16351EB8D6C48B40393A6EEB5860` |
| THUG2 USA/Europe | 8 MiB | `04742B4BEB1B777474860DB2692C00D669241CA46EDC8F7E816604BB32BD6304` |
| American Sk8land USA | 8 MiB | `5ABA14FFB5B7784D109D261EA04CCFD18F6ABE692DB005A5CB7D2EF5179A6031` |
| DHJ USA | 16 MiB | `8BBCE4C794057AB95F6F4747F25A09B0A49077C0B2BB9F2F183ACF7789359119` |

The local media build/file names are:

```text
Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)/Tony Hawk's Pro Skater 3 (USA, Europe).gba
Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)/Tony Hawk's Pro Skater 4 (USA, Europe).gba
Tony Hawk's Underground (2003-10-27, GBA - Final)/Tony Hawk's Underground (USA, Europe).gba
Tony Hawk's Underground 2 (2004-10-4, GBA - Final)/Tony Hawk's Underground 2 (USA, Europe).gba
Tony Hawk's American Sk8land (2005-10-18, GBA - Final)/Tony Hawk's American Sk8land (USA).gba
Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)/Tony Hawk's Downhill Jam (USA).gba
```

## What exists in the repository

Use these as the source of truth rather than reconstructing prior work from this
document alone.

- `src/NeversoftMultitool/Core/Formats/Gba/GbaSkaterModel.cs` is the solved
  THPS2 baseline and the negative locator used on all later carts.
- `src/NeversoftMultitool/Core/Formats/Mesh/Conversion/GbaModelGeometryWriter.cs`
  and `GbaAnimatedModelWriter.cs` show the existing static/morph-target output
  stack, but their assumptions are THPS2-specific.
- `src/NeversoftMultitool/Core/Formats/Gba/GbaDhjModel.cs` contains the closed
  DHJ rider and pose-directory readers.
- `src/NeversoftMultitool/Core/Formats/Mesh/Conversion/GbaDhjModelGeometryWriter.cs`
  assembles one DHJ pose and emits a `ModelDocument`, optionally carrying morph
  targets.
- `src/NeversoftMultitool/Core/Formats/Mesh/Conversion/GbaDhjAnimatedModelWriter.cs`
  turns one bounded pose clip into those morph targets and a weights track, and
  owns the stated frame-rate export policy.
- `src/NeversoftMultitool/CLI/GbaDhjModelCommand.cs` exposes the standalone
  `gba-dhj-model` command and its `--animate` opt-in; `Program.cs` registers it.
- `tests/NeversoftMultitool.Tests/Core/Formats/Gba/GbaDhjModelTests.cs` pins the
  DHJ corpus and pose assembly.
- `tests/NeversoftMultitool.Tests/Core/Formats/Gba/GbaDhjAnimationTests.cs` pins
  the animated export: target census and naming, per-corner reproduction of every
  pose record, the base-repeating record that adds no target, the unbounded-clip
  refusal, and the unchanged single-pose route.
- `tests/NeversoftMultitool.Tests/CLI/GbaDhjModelCommandTests.cs` pins both CLI
  routes and their selection errors.
- `tests/NeversoftMultitool.Tests/Core/Formats/Gba/GbaSkaterModelTests.cs`, test
  `LaterGamesDoNotClaimTheThps2MorphTargetComplex`, pins the later-cart negative.
- `tools/research/gba-3d/FINDINGS.md`, section “3D rider/model audit,” is the
  concise project-wide record.
- `tools/research/gba-3d/thps3_model_trace.lua` and
  `TestOutput/gba-thps3-model-trace.txt` retain the THPS3 output-path trace.
- `tools/research/gba-3d/dhj_runtime_trace.lua` and
  `TestOutput/gba-dhj-runtime-trace.txt` retain the DHJ consumer proof.
- `tools/research/gba-3d/later_idle_capture.lua`, `runtime_pointer_report.py`,
  `lz77_census.py`, and `dhj_geometry_probe.py` are retained probes.
- `TestOutput/gba-runtime-capture/` currently contains full state series only for
  `AT3E-auto`, `AT6E`, `AT6E-auto`, and `BXSE-auto`. There is no retained BTOE,
  B2TE, or BH9E state series in this checkout.

DHJ is not yet routed through the generic `.chr.gba`/Mesh Converter path. That
path, embedded animation discovery, carved character records, and the GUI are
still THPS2-only. DHJ currently works only through its standalone command.

## Evidence standard for a new rider parser

A visually plausible point cloud or one rasterized sprite is not closure. Before
claiming a THPS3-through-Sk8land format, require all of the following:

1. A live renderer consumer ties the on-screen rider to the proposed ROM/RAM
   banks.
2. Vertex record size, coordinate encoding, count/bounds, and bank end close.
3. Face/index record size and winding close; every index is in range and the
   highest referenced index tightly agrees with the vertex bank.
4. Part grouping or morph-frame organization is proven rather than inferred from
   a silhouette.
5. Animation records bind to the same live model, with bounded clips and observed
   playback timing.
6. Palette/material/normal fields are traced to their consumers. Unknown fields
   must stay named unknown and must not be presented as RGB.
7. A content locator finds the complete corpus without depending solely on one
   retail offset, and negative tests reject sibling engines/lookalikes.
8. Corpus tests pin census, exact anchors/hash, representative decoded records,
   output topology/bounds, and invalid-selection behavior.

Only then add carving, generic Mesh/GUI routing, or broad cross-title reuse.

## THPS3 (`AT3E`): shipped 2026-09-02

`GbaThps3RiderModel` locates the six-record directory by shape and closes it
three ways; `GbaThps3RiderGeometryWriter` / `GbaThps3RiderAnimatedWriter` export
the rider exactly as the THPS2 skater is exported (morph targets, one clip per
file), and the carve, `mesh`, the Animations pane and `GbaRiderClips` route it.
What closed since the previous audit, in the order it mattered:

- The clip table is THPS2's grammar: `{u16 tickStart, u16 tickCount}` into a
  **tick→frame remap** that fills record 0's trailing region (`w2` up to
  record 1's mesh). Every one of its 8,507 entries is a pool frame, every clip
  addresses it in range, and the furthest tick aligned to 4 bytes is exactly the
  region — the earlier "entry 13 exceeds the bank" contradiction was the table
  read as frame ranges. Entries continue past `(0,0)` authored-empty clips: 239
  clips, 7 empty, holds of two ticks per frame throughout.
- The 12-byte face record is `{v0,v1,v2,0; u0,v0,u1,v1,u2,v2; material; flag}`:
  the library's textured rasterizer packs `(v & 0x3F) << 6 | u` and reads a
  **64×64 8bpp page** from `r3`, the bytes are 6.2 fixed-point texels, and the
  flat rasterizer stores `r3 + material` instead. The page pointer is passed by
  the caller and is NOT in the live render descriptor; no 4 KB RAM or ROM window
  holds the sprite's palette-index set, and the eight 4,096-byte LZ77 streams
  are sprites. Locating the page is the remaining appearance item.
- Frame header bytes 4–6 are the deck translation, proven against the retained
  capture (the EWRAM deck copy equals the stored deck plus those bytes on all
  24 vertices at frame 686). Bytes 0–2 are not the AABB centre and bytes 8–10
  are ignored by that copy, so both stay undecoded.

The evidence trail below is retained as written before the container closed.

### Proven negative: it is not the THPS2 model complex

`GbaSkaterModel.TryLocate` returns null on THPS3. This is stronger than a magic-
number miss. The THPS2 locator closes the engine identity

```text
frameStride == 4 + sum(ceil(vertexCount/4) * 12)
                  + sum(ceil(normalCount/2) * 4)
```

together with an in-ROM face pointer, per-subobject face-index validation, clip
remap, and roster structure. A stride/subobject-generalized scan (1 through 20
subobjects) found **zero** structurally valid AT3E headers. The conclusion is
“different format,” not “no 3D rider.”

### Exact live output anchor

The retained attract-mode gameplay capture shows the rider as:

- OAM object 13.
- 64 x 64, 8bpp.
- OBJ tile 86, hence destination `0x06010000 + 86 * 32 = 0x06010AC0`.

`thps3_model_trace.lua` watches that destination. During frames 6400–6700, the
same upload appears each rendered frame with:

```text
PC/R15 = 0x030046B4
LR/R14 = 0x080205DD
R0     = 0x06010AC0       destination
R1     = 0x020194C8       EWRAM raster buffer
R2     = 0x000001C0
R3     = 0x00000003
```

The static caller around `0x080205C8` loads source/destination from descriptor
fields `+0x08/+0x0C` and calls the transfer at `0x080205D8` (ROM routine near
`0x08049AE8`, executing from IWRAM in this run). This closes only the final
raster upload. `0x020194C8` is software-rendered pixel output, not source model
geometry.

The next useful trace is a narrow write watch on the active EWRAM output buffer,
then a backward walk from its producer to ROM reads. Re-resolve the buffer from
the descriptor rather than assuming `0x020194C8` survives other scenes/builds.

### Rejected false lead: the large EWRAM pointer lattices are level-art rows

At frame 8400:

- `0x02012CC4` contains 169 ROM pointers beginning `0x08380E08`, with successive
  row streams commonly separated by `0x16A`.
- `0x020161E4` contains 169 companion pointers beginning `0x0838C520` (many rows
  share the same empty-stream pointer).

Those values sit under THPS3 level-record 0 at ROM file offset `0x0B1450`:

```text
plane 0 = 0x08380B60; first stream = 0x08380E08
plane 1 = 0x0838C278; first stream = 0x0838C520
0x2A8 = 4 + 169 * 4       (header plus row-offset table)
```

At frame 10800, after the attract demo changes level:

- `0x02017A1C` contains 127 pointers beginning `0x084ACD94`, often separated by
  `0x1B2`.
- `0x0201B514` contains the 127-pointer companion beginning `0x084BA6E4`.

These land under level-record 2 (`0x0B1530`):

```text
plane 0 = 0x084ACB94; first stream = 0x084ACD94
plane 1 = 0x084BA4E4; first stream = 0x084BA6E4
0x200 = 4 + 127 * 4
```

The exact row-table identity and level-dependent relocation reject the earlier
“169 morph frames plus a shared face bank” hypothesis. Do not return to these
runs as rider candidates.

### Rejected/low-value static leads

- THPS3 has 159 LZ streams that decode to 884 bytes and 46 that decode to 904
  bytes. Sampled outputs use small tile/map-index domains (roughly 0–27 and
  0–23), not signed XYZ-like records. Repetition by decoded size is not a model
  locator.
- An EWRAM chain around `0x020000E4` led to a player-relative collision scalar,
  not a geometry bank.
- The 64 x 64 OBJ tile is useful as a renderer oracle, but extracting that tile
  would only preserve one camera/animation raster and is not a 3D model export.

### Next THPS3 experiment

1. Clone `thps3_model_trace.lua` and watch the first write to the currently
   resolved EWRAM raster buffer, not only the VRAM copy.
2. Capture registers at the buffer producer, then watch its ROM reads over one
   frame. Separate persistent model/face reads from animation- or level-varying
   reads.
3. Repeat with at least two animations, two riders/outfits if selectable, and two
   levels. A model bank should follow rider choice but not level choice.
4. Once a candidate bank appears, derive its record grammar from the consumer
   before writing a broad scanner.
5. Add a negative test against THPS2 and at least one later cart even if the
   THPS3 reader is game-code gated.

## THPS4 (`AT6E`): immediate runtime anchor

There is no model parser or renderer trace yet, but the retained capture removes
the first discovery step. In gameplay:

```text
frame 8400:  OAM object 0, x=94,  y=54, 64x64, 8bpp, tile 77
frame 12000: OAM object 0, x=103, y=64, 64x64, 8bpp, tile 77
```

The exact OBJ destination is `0x06010000 + 77 * 32 = 0x060109A0`. At frame 12000,
OAM object 1 begins at tile 205; `205 - 77 = 128` 32-byte slots, exactly the 4096
bytes occupied by the 64 x 64 8bpp rider. This independently supports the tile
calculation. A direct 64 x 64 8bpp decode from `0x060109A0` in the frame-8400
VRAM dump produces a coherent centred nonzero bound (`x=17..47`, `y=16..51`);
aligning the odd OAM tile down instead pulls unrelated pixels into `(0,0)`.

Next action: parameterize/copy the THPS3 VRAM-write probe for `0x060109A0`, log
the final upload source and caller, then walk backward to the raster producer.
Do not assume the THPS3 caller addresses or source-buffer layout carry over.

## THUG, THUG2, and Sk8land: reset-to-evidence state

There are no retained gameplay RAM/OAM captures for `BTOE`, `B2TE`, or `BH9E`
in this checkout, and there is no model parser for any of them. Generate those
captures before doing more static pattern searches.

THUG2 once produced a loose THPS2-header-like byte sequence, but it failed the
full header/face/clip/remap/roster closure and its exact offset was not retained.
Treat it as rejected noise. If it matters, rediscover it only after a live
consumer points into the same region.

For each cart:

1. Run `later_idle_capture.lua` with autoplay and choose a gameplay frame.
2. Parse the OAM dump to find the visible rider object, dimensions, bpp, and tile.
3. Compute/watch its OBJ VRAM destination and capture the final upload.
4. Walk backward to the buffer producer and model/animation ROM reads.
5. Compare different rider/outfit/animation/level states before proposing a
   common format with another cart.

## DHJ (`BXSE`): what is closed

DHJ is a separate Visual Impact engine. Its rider banks are not directly usable
as static meshes: vertices are authored in 13 independent rigid-part spaces and
must be transformed by a pose before the global-index faces form a rider.

### Model record

`GbaDhjModel.FindModels` content-scans the following exact structure, gated to
`BXSE` and closed by counts, face indices, the tight highest index, and sentinel:

```text
+0x00  u16 marker = 128; u16 groupCount = 13
+0x04  u16 vertexCount[13]
+0x1E  unknown model metadata through +0x43
+0x44  u16 faceCount[13]
+0x5E  unknown model metadata through +0x83
+0x84  Vertex[sum counts]  // s16 x,y,z; two stored normal bytes (currently u16)
        Face[sum counts]    // u8 v0,v1,v2,shadeCode
        u32 0x01234567
```

Face indices address the complete transformed vertex array, not a face group's
local vertex run. The census is 24 variants:

```text
vertices: 125 119 128 141 121 125 135 128 124 142 141 145
          141 145 134 138 139 143 128 138 132 142 146 148
faces:    102 104 100 104 102 102 100 104 100 124 104 104
          104 104 112 112 108 108 110 110 112 112 116 116
```

Key file offsets are model 0 header `0xEABA20`, model 19 header `0xEB7A18`, and
model 23 header `0xEBA430`.

Live gameplay model 19 closes as:

```text
header       0x00EB7A18
vertex bank  0x00EB7A9C / bus 0x08EB7A9C, 138 vertices
face bank    0x00EB7EEC / bus 0x08EB7EEC, 110 faces
sentinel     0x00EB80A4
end          0x00EB80A8
geometry SHA-256 (vertex bank through sentinel):
4316D4D75169EDAEFADA8630F6C591114E357066B29C913BF7528F651AE0F553
```

Its per-part vertex counts are
`6,9,9,7,8,10,8,10,8,8,4,4,47`; face counts are
`4,7,6,7,7,6,5,12,4,4,6,6,36`. The first vertex is
`(51,10,1,0x1B1F)`, the first face is `(2,1,0,0x20)` in group 0, and the last
face is `(0x88,0x59,0x5A,0x87)` in group 12.

### Pose directory and transform

The unique pose directory is structurally found at file offset `0xE71808`:

```text
u32 0
u32 frameStride = 0x50
u32 tableOffset = 0x10
u16 groupCount = 13
u16 clipCount = 94
u32 relativeClipOffsets[94]  // relative to header + 4
```

One pose frame is `u16 header` followed by 13 records of:

```text
s8 tx, s8 ty, u8 tz, u8 angleX, u8 angleY, u8 angleZ
```

Angles are 256 steps per turn. Bounded clips occupy `N * 0x50 + 4`; the final
four bytes vary by clip and are retained as an undecoded playback/control value.
For example, clip 79 is `0xEA4520`, 12 frames, trailer `0x10`; clip 90 is
`0xEA99FC`, 26 frames, trailer `0x1A`.

Pose 79/frame 0 starts with these exact records:

```text
part 0:  tx=0,   ty=0,  tz=9,   ax=0,  ay=1,   az=192
part 1:  tx=-10, ty=-18,tz=29,  ax=11, ay=1,   az=37
part 12: tx=-8,  ty=-1, tz=145, ax=15, ay=250, az=42
```

`GbaDhjModelGeometryWriter.ApplyPose` directly translates the engine routine
copied from ROM `0x080009DC` to IWRAM `0x030045BC`: X rotation, the handed Y
stage (Z flips at zero Y angle), Z rotation, then `(tx,ty,-tz)`. Export converts
to `(x,-z,-y) * 2`. The game uses a 512-scale integer sine table; the writer uses
floating-point trig, so model-space assembly is faithful but not bit-exact.

### Runtime proof

The retained gameplay descriptor begins at EWRAM `0x02036B88`:

```text
+0x08  current pose pointer  (watched at 0x02036B90)
+0x24  face pointer          (watched at 0x02036BAC)
+0x28  vertex pointer        (watched at 0x02036BB0)
```

At frame 4558, ROM code `0x0801B14C..0x0801B17E` loads model 19 and observes
face count `0x6E`, group count `0x0D`, and sentinel address `0x08EB80A4`.
At frame 4560:

- `0x0801B082` enters the rider render path with the transform routine at
  `0x030045BC`.
- `0x030045EC` reads pose data at `0x08EA99FE` (clip 90 frame data starts two
  bytes after the frame header).
- `0x03004694` reads vertex bank `0x08EB7A9C`, with model index `0x19` in R8.
- `0x0300480C` consumes face bank `0x08EB7EEC`; its byte indices are multiplied
  by the transformed-vertex stride. The byte-identical ROM face consumer is at
  `0x08000C0C`.

Subsequent logged pose reads advance by `0x50` at frames 4562, 4564, 4567, and so
on. The observed 2–3 video-frame cadence proves that “one pose record per 60 Hz
tick” would be an unjustified exporter assumption.

### Animated export (shipped)

`gba-dhj-model --animate` exports a whole bounded pose clip as glTF **morph
targets**, mirroring the THPS2 cart's `GbaAnimatedModelWriter`:
`GbaDhjAnimatedModelWriter.TryBuild` poses the rider at every record of the clip
through the same `GbaDhjModelGeometryWriter.ApplyPose` the single-pose route
uses, takes each record's vertex delta from the clip's own frame 0, dedupes
targets by POSE rather than by frame, and drops a record whose pose IS the base
(an all-zero target is dropped on write and would shift every later target's
index). The clip's records become a one-hot weights track.

Morph targets over fully posed vertices were chosen deliberately over a
one-joint-per-part skin: they sidestep the handedness trap recorded below, and
they preserve the faces that connect vertices from different rigid parts.

The rate is an explicit **export policy**, stated as such in the writer's doc
comment. The retained trace advances the pose pointer every 2–3 video frames, so
one record per 59.7275 Hz tick would be unjustified; the exporter therefore emits
one key per pose RECORD at 30 records/second — the fastest cadence actually
observed — which makes an exported clip an upper bound on playback speed rather
than an invented number.

`TryBuild` returns null for an out-of-range clip and for the unbounded final
clip, and the command refuses rather than falling back to a single pose; it also
refuses an explicit `--frame` combined with `--animate`, since an animated clip
is always based on its own frame 0.

Measured on model 19: clip 79 → 12 keys / 11 targets, clip 18 → 24 keys / 22
targets (its frame 1 repeats frame 0 and contributes none), clip 90 → 26 keys /
25 targets whose per-target displacement ramps smoothly 2 → 41 → 0 units. Every
export is Khronos-clean (0 errors, 0 warnings; the 13 infos are `TEXCOORD_0` on
the untextured debug materials). Pinned by
`tests/.../Core/Formats/Gba/GbaDhjAnimationTests.cs` and the command tests.

### Current export limitations

`gba-dhj-model` defaults to clip 79/frame 0 and can export one or all 24 variants
at any bounded clip/frame, or a whole bounded clip with `--animate`. It emits all
faces, one unlit diagnostic material per authored face group, and flat triangle
normals (the animated route substitutes per-vertex normals, which glTF morphing
requires so a delta resolves to one base vertex).

It does **not**:

- Prove playback timing: the exported rate is a policy, the per-clip `u32`
  trailer is undecoded, and loop/transition behaviour is unknown.
- Decode or use the stored normal bytes.
- Use `shadeCode` at all; group colour is not game colour.
- Resolve outfit/variant palette or ramp bindings.
- Prove which pose clips/variants belong together beyond the live example.
- Bound the final pose clip.
- Locate non-rider object models.
- Integrate with generic Mesh/GUI/embedded-animation discovery.

## DHJ remaining experiments

### 1. Animation playback semantics

The morph-target export above ships; what remains is closing playback semantics
so the exported rate stops being a policy:

- Trace the animation-update code that writes/advances descriptor `+0x08` and
  identify timer, loop, and transition fields around `0x02036B88`.
- Relate the small u32 trailer after each bounded clip to observed duration,
  cadence, or control behavior. It is not always equal to frame count.
- Prove clip-to-action and model/variant binding through multiple live actions.

There is a strong but deliberately unshipped final-clip boundary lead in the
pinned US ROM: clip 93 starts at `0xEAB218`; 25 consecutive `0x50` records end
exactly at ASCII resource header `JBOG` at `0xEAB9E8`. The shipping parser leaves
its count as `-1` because this boundary has not been made structural/corpus-safe.
Confirm the `JBOG` owner/length or trace clip 93 playback before changing that.

A compact one-joint-per-part skin remains possible because vertex group
membership is known, and would shrink the exports considerably, but do not
convert the stored Euler bytes naively: the engine Y-stage reflection and the
export-space reflection cancel only after the complete matrix transform. Derive a
proper export-space rotation matrix, verify determinant/handedness, then
decompose to a quaternion. The shipped morph-target export is the oracle to check
any such skin against — it reproduces every record exactly.

### 2. Stored normals

Watch reads at the first live stored normal (`0x08EB7AA2`, vertex `+6`) and follow
the value into lighting/shading. Also watch adjacent vertices so a wide load does
not evade an exact-byte watchpoint.

Current storage observations are constraints, not a decode:

- Model 19 has 76 distinct u16 pairs; its two bytes happen to stay in `0..31`.
- Across all 24 models, byte `+6` reaches 61 and byte `+7` reaches 63.
- Therefore a hard-coded 5/5/5 interpretation is already contradicted by the
  corpus. Two quantized angular components remain only a hypothesis.

Compare any candidate decode against area-weighted geometric normals in each
part's local space and against the live renderer consumer. Pin several known
values and a corpus domain test before replacing recomputed normals.

### 3. Palette/ramp and `shadeCode`

Watch the first live face shade byte (`0x08EB7EEF`) through the rasterizer and
watch OBJ-palette writes while changing rider/outfit. Diff captures of the live
descriptor, model-header unknown regions (`+0x1E..+0x43`, `+0x5E..+0x83`), and
palette RAM.

Model 19 uses 17 shade codes (`0x20`, then a subset of `0x80..0x9F`). Across all
24 variants, 57 codes occur, from `0x00` through `0xE1`. This is not an RGB byte.
Require a consumer-proven mapping from face code plus variant/outfit state to a
palette/ramp colour before replacing debug materials.

### 4. Non-rider models

`0x01234567` also occurs in unrelated track/resource data. The shipping locator
therefore intentionally requires marker 128, 13 groups, valid count-derived
banks, tight indices, and `BXSE`.

For a non-rider object, first obtain a live vertex/face pointer from its renderer.
Then run `dhj_geometry_probe.py --offset <sentinel>` to see whether the broader
grouped grammar closes. Add a separate format/profile if group count, marker, or
metadata differs; do not weaken the rider locator globally because one sentinel
produces a plausible cloud.

## Reproduction commands

Set local ROM variables in PowerShell (adjust `$media` if needed):

```powershell
$media = 'C:\Users\mmc99\Desktop\Games\TCRF\Spider-Man Research\Media'
$thps3 = Join-Path $media "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)\Tony Hawk's Pro Skater 3 (USA, Europe).gba"
$thps4 = Join-Path $media "Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)\Tony Hawk's Pro Skater 4 (USA, Europe).gba"
$thug = Join-Path $media "Tony Hawk's Underground (2003-10-27, GBA - Final)\Tony Hawk's Underground (USA, Europe).gba"
$thug2 = Join-Path $media "Tony Hawk's Underground 2 (2004-10-4, GBA - Final)\Tony Hawk's Underground 2 (USA, Europe).gba"
$sk8 = Join-Path $media "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)\Tony Hawk's American Sk8land (USA).gba"
$dhj = Join-Path $media "Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)\Tony Hawk's Downhill Jam (USA).gba"
```

Build and run only the current DHJ rider/model tests:

```powershell
dotnet build .\tests\NeversoftMultitool.Tests\NeversoftMultitool.Tests.csproj
$tests = '.\tests\NeversoftMultitool.Tests\bin\Debug\net10.0\NeversoftMultitool.Tests.exe'
& $tests --filter-class NeversoftMultitool.Tests.Core.Formats.Gba.GbaDhjModelTests --explicit on --output Detailed
& $tests --filter-class NeversoftMultitool.Tests.CLI.GbaDhjModelCommandTests --explicit on --output Detailed
```

Export the runtime-verified DHJ rider/pose:

```powershell
dotnet run --project .\src\NeversoftMultitool\NeversoftMultitool.csproj `
  --framework net10.0 -- `
  gba-dhj-model $dhj --index 19 --clip 79 --frame 0 `
  -o .\TestOutput\gba-dhj-model-check -v
```

Export the same clip as morph-target animation (12 keys, 11 targets):

```powershell
dotnet run --project .\src\NeversoftMultitool\NeversoftMultitool.csproj `
  --framework net10.0 -- `
  gba-dhj-model $dhj --index 19 --clip 79 --animate `
  -o .\TestOutput\gba-dhj-model-anim -v
```

Reconfirm the live model-19 container from its sentinel:

```powershell
python .\tools\research\gba-3d\dhj_geometry_probe.py $dhj --offset 0xEB80A4 --limit 20
```

Expected single result:

```text
sentinels=1
0xEB80A4: header=0xEB7A18 marker=128 groups=13 vertices=138@0xEB7A9C faces=110@0xEB7EEC prelude=0x84 candidates=1
```

Run the retained BizHawk 2.6.3 probes (mGBA core, Lua 5.1):

Use new timestamped output names: both trace scripts open their destination with
`"w"`, so pointing them at the retained evidence files would truncate those files.
Only `rider` mode in `dhj_runtime_trace.lua` is current for this handoff; its old
`course` mode predates the shipped course parser and should not guide model work.

```powershell
$bizhawk = '.\tools\vendor\bizhawk\EmuHawk.exe'
$traceTag = Get-Date -Format 'yyyyMMdd-HHmmss'

$env:NM_GBA_THPS3_MODEL_TRACE = "TestOutput/gba-thps3-model-trace-$traceTag.txt"
$env:NM_GBA_THPS3_MODEL_TRACE_FIRST_FRAME = '6400'
$env:NM_GBA_THPS3_MODEL_TRACE_LAST_FRAME = '6700'
& $bizhawk $thps3 "--lua=$((Resolve-Path .\tools\research\gba-3d\thps3_model_trace.lua).Path)"

$env:NM_GBA_DHJ_TRACE_MODE = 'rider'
$env:NM_GBA_DHJ_TRACE = "TestOutput/gba-dhj-runtime-trace-$traceTag.txt"
$env:NM_GBA_DHJ_TRACE_LAST_FRAME = '5200'
& $bizhawk $dhj "--lua=$((Resolve-Path .\tools\research\gba-3d\dhj_runtime_trace.lua).Path)"
```

Capture a missing later cart:

```powershell
$captureTag = Get-Date -Format 'yyyyMMdd-HHmmss'
$env:NM_GBA_CAPTURE_ROOT = "TestOutput/gba-runtime-capture-$captureTag"
$env:NM_GBA_CAPTURE_AUTOPLAY = '1'
& $bizhawk $thug "--lua=$((Resolve-Path .\tools\research\gba-3d\later_idle_capture.lua).Path)"
```

The capture script writes screenshots every 300 frames and VRAM, palette, OAM,
IWRAM, and EWRAM every 1200 frames through frame 12000. Choose a gameplay frame
from the screenshots before treating its RAM pointers as rider state. A unique
capture root matters because the script otherwise reuses fixed
`<game-code>-auto/frame_*` names.

Report aligned ROM pointers from an EWRAM or IWRAM dump. These two examples use
the already retained AT3E/AT6E captures; the preceding THUG command writes BTOE
under its newly timestamped capture root instead:

```powershell
python .\tools\research\gba-3d\runtime_pointer_report.py `
  .\TestOutput\gba-runtime-capture\AT3E-auto\frame_008400_ewram.bin `
  --rom-size 0x800000 --limit 20

python .\tools\research\gba-3d\runtime_pointer_report.py `
  .\TestOutput\gba-runtime-capture\AT6E-auto\frame_008400_iwram.bin `
  --ram-base 0x03000000 --rom-size 0x800000 --limit 20
```

Reproduce the THPS3 LZ census:

```powershell
python .\tools\research\gba-3d\lz77_census.py $thps3
```

BizHawk 2.6.3 caveats are recorded in `tools/vendor/bizhawk/README.md`:

- Lua is 5.1; use the `bit` library, not Lua 5.3 bitwise syntax.
- mGBA DMA write callbacks may supply nil address/value arguments. Register
  predicates were intentionally load-bearing in the THPS3 trace.
- Do not leave a high-churn framebuffer-wide watch active; use one destination
  byte/word, capture a bounded burst, then unregister or stop.

## Recommended work order

1. **THPS4 renderer walk:** start at proven OBJ destination `0x060109A0`; turn
   the retained capture into the first source-bank anchor outside DHJ. THPS4
   shares THPS3's library, so expect the same directory/mesh/face grammar.
2. **THPS3 texture page:** find the caller that hands the bucket walk its `r3`
   (the textured page base) — the rider's caller is `0x0802951A` — and bind the
   64×64 page plus the OBJ palette; then replace the diagnostic materials.
3. **Capture and anchor THUG/THUG2/Sk8land:** do not static-scan blind, and do not
   assume one shared VV rider format before consumer evidence.
4. **Close one VV-era format end to end:** source mesh, faces, pose/animation,
   appearance binding, content locator, tests, then export integration.
5. **Finish DHJ animation timing:** the morph-target tracks ship; what is left is
   the playback rule behind them — trace the descriptor's pose advance, decode the
   per-clip trailer, and keep final clip 93 unbounded until its boundary is proven.
6. **Finish DHJ normals and appearance:** trace stored-normal and shade consumers;
   diagnostic group colours must remain visibly labeled until then.
7. **Find DHJ non-rider objects from the closed course bank first:**
   `GbaDhjCourse.CourseInfo` already bounds `ObjectCount` 16-byte records at
   `ObjectDataOffset` (`header +0x0C/+0x1C`), ending exactly at the first road
   edge. Course 0 has 138 records at ROM `0x009E5F84`. Correlate those records
   with live placed objects and renderer reads before falling back to a broad
   sentinel probe.

The completion bar is parity in capability, not reuse of THPS2's byte layout:
viewable topology, bounded/playable animation, real appearance binding, corpus-
pinned extraction, and a supported application route for each format that the
evidence shows to be distinct.
