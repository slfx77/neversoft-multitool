# PS1 Character Animation Format — Diagnostic Findings

Snapshot of what the `psxanim` CLI command revealed about PS1 `.psx` character
animation data, derived from the THPS2 PSX prototype decompilation
(`\\wsl.localhost\Ubuntu\home\slfx77\thps2-psx-proto`) and validated against
sample files from THPS2 prototype, Spider-Man, and Apocalypse builds.

> **Per-game format variation:** The decompilation describes the **THPS2
> prototype** format. Spider-Man (later in 2000) and Apocalypse (1998) use
> related but **different** layouts for the per-anim entry table. The codec
> itself is unchanged across games.

## Confirmed: codec works on all PS1 Neversoft games

The `DecompressStream` port at
[PsxAnimDecompressor.cs](../../src/NeversoftMultitool/Core/Formats/Animation/PsxAnimDecompressor.cs)
produces clean, structured s16 output across all four samples we've tested:

- **THPS2 prototype `hawk2.psx`** anim 0 (1 frame × 19 bones × 6 channels):
  consumes 247 bytes of structured data; bone 0 decodes to
  `(Rx=-154.6°, Ry=-4.4°, Rz=-5.8°, T=(-0.32, -0.91, 0.13))` — a plausible
  bind pose.
- **Spider-Man `carnage.psx`** anim 1 (30 frames × 19 bones): consumes 1370 / 1378
  byte budget — 8-byte alignment leftover, exact match.
- Smooth frame-to-frame progressions, channel ranges consistent with rotations
  + translations.

## Confirmed: per-bone block layout

Each compressed-stream block (one per animation) holds:
```
[ bone 0:  ch0_stream  ch1_stream  ch2_stream  ch3_stream  ch4_stream  ch5_stream ]
[ bone 1:  …                                                                       ]
…
[ bone (N-1): …                                                                    ]
```

Each `chN_stream` is one independent `DecompressStream` invocation. Per bone,
the 6 streams are concatenated; the `src` cursor advances continuously through
all `numBones × 6 × streamLen` channel-frame samples for the animation.

## Confirmed: channel order

| Channel | Field | Meaning | Citation |
|---|---|---|---|
| 0 | `boneFrame[+0]` | Rotation around X (s16) | DECOMP.cpp:472, 523 |
| 1 | `boneFrame[+2]` | Rotation around Y | DECOMP.cpp:473, 523 |
| 2 | `boneFrame[+4]` | Rotation around Z | DECOMP.cpp:474, 523 |
| 3 | `boneFrame[+6]` | Translation X | DECOMP.cpp:524 |
| 4 | `boneFrame[+8]` | Translation Y | DECOMP.cpp:525 |
| 5 | `boneFrame[+0xA]` | Translation Z | DECOMP.cpp:532 |

Rotation matrix is built via `M3dMaths_RotMatrixYXZ(SVECTOR *euler, MATRIX *out)`.
The wrapper's angle convention is confirmed from assembly (`angle & 0x0fff`);
the exporter consumes the result through `Matrix4x4`/glTF's row-major convention
as `qy * qx * qz`, which is the transpose of the usual column-vector
`Ry · Rx · Rz` expansion.

## Confirmed: scales

- **Rotation**: PSY-Q angle units, low 12 bits = 360°. The decompiled
  `M3dMaths_RotMatrixYXZ__FP7SVECTORP6MATRIX` wrapper masks each X/Y/Z channel
  with `0x0fff`, shifts by two, and indexes the engine sin/cos table. Multiplier:
  `(s16 & 0x0fff) / 4096.0 * 360.0` for degrees, or
  `((s16 & 0x0fff) / 4096.0) * 2π` for radians. Earlier diagnostics inferred
  `65536` from Carnage's broad raw span, but that contradicts the runtime
  wrapper and over-damps Spider-Man's motion.
- **Translation — RESOLVED (2026-07-06) via the hand-asm GTE walkthrough**
  (`thps2-psx-proto/docs/m3dasm_gte_walkthrough.md`, §6 fixed-point contract):
  - Reference unit: 1 world unit = the integer unit of camera `Position` and
    of `Item.Position >> 12` (item/object positions are i32 20.12 → `/4096`).
  - **s16 anim translation channels are world × 16 (12.4)** — the SAME unit
    as s16 model vertices. `Decomp_GetAnimTransform` copies them raw into
    `SMatrix.t` and preserves the ×16 scale through the whole hierarchy
    (child: `(parent.rot × anim_t) >> 12 + parent.t`; the 4.12 rotation
    cancels the `>>12`). The engine rebuilds every bone origin from anim data
    each frame — there is **no bind fallback**.
  - The only scale change in the render path is one `>>4`, applied to BOTH
    operands together: `M3dAsm_SetSuperTransforms` shifts `bone.t` and
    `M3dAsm_TransformAndOutcodeSuperVertices` shifts vertex XYZ. Therefore a
    converter must divide anim translations by the **same divisor as
    vertices** (`ScaleDivisor`, which already contains the ×16), emit them
    **absolute** (no frame-0/bind anchoring), and let the parent chain
    compose. Versus 20.12 bind positions: `s16 = fp12 >> 8`, so
    `anim_raw/ScaleDivisor` lands exactly in bind-offset units.
  - All engine shifts truncate toward −∞ (`sra`, MVMVA `sf=1`); no rounding
    anywhere. Rotation channels are model-space absolute; only translations
    chain through the hierarchy.
  - Historical note: the earlier `TranslationDivisorScale=16` default
    double-applied the `>>4` (it is already inside `ScaleDivisor = 2.25×16`)
    making translations 16× too small, and the bind-anchored delta emission
    had no engine counterpart. The "raw anim t vs obj.Position isn't a
    constant factor across bones" confusion came from comparing
    parent-relative anim values against absolute bind offsets without
    hierarchy composition.
  - Related mesh fact from the same walkthrough: the vertex `flags` halfword
    (4th s16 of each SVECTOR) is control data — bit 0 = stitch source
    (transformed AND copied to a downward-growing stitch buffer), bit 1 =
    stitch reference (NOT transformed; its first WORD is a byte distance back
    from the stitch-buffer top, not a position). The multitool currently
    decodes references as "second halfword = sequential attachment index",
    which may differ from the runtime scheme — open question whether disc
    data stores indices that loading converts, or offsets directly.

## Confirmed: codec sample-count formula

The C reconstruction at DECOMP.cpp:114 writes an extra "endpoint" sample per
outer iteration that doesn't match the formula
`segLength × numSegments + 1 + remainder = StreamLength`. Removing that extra
write is required for the formula to balance — the endpoint u16 is still READ
(advances srcIdx) and becomes the next outer iteration's `prev`, but it isn't
emitted as an output sample. This is a reconstruction artifact (~95% match
rate has 5% tolerance).

## Open: per-game entry table layout differs

The hierarchy block's per-anim entry table varies between games. The codec
runs identically on all of them — but mapping `logical animation index → entry`
requires the correct per-game layout.

### THPS2 release / Spider-Man PSX (Sept 2000) — full entry table

Per the decomp agent's analysis of DECOMP.cpp:484, the THPS2 release format has
a monolithic table at `hierData+4`, 8-byte stride, layout
`(u32 poolOffset, u32 frameCount)`. Spider-Man uses the same layout.

```
+0x00 : u32 numStreams                   (= number of animations, 44 for both samples)
+0x04 + i*8 : per-animation entry (8 bytes):
              +0x00: u32 poolOffset       (relative to pool start)
              +0x04: u32 frameCount       (= streamLen)
+0x04 + numStreams*8 : start of compressed stream pool (extends to ~EOF / next chunk)
```

The `RunAnim` u8 read at `animTable + animIdx*8 + 8` lands on entry[0]'s
+0x04 = frameCount's low byte, confirming the layout via runtime access
pattern.

**Crucially**: anim entries are **NOT** sorted by pool offset. animIdx 0 may
live at the very END of the pool (carnage anim 0 has `poolOffset = 102,296`
out of a ~127 KB trailing region). The "trailing 24 KB" of `(u8, u8)`
patterns I previously interpreted as a metadata table is actually just
**anim 0's compressed stream data** plus the streams of other late-indexed
animations — not a separate metadata block.

This explains the previously-mysterious carnage anim 0 "overflow": with the
old (incorrect) layout interpretation, anim 0's data was being read from a
pool location that didn't actually belong to it. The NEW interpretation
places anim 0 in the trailing region where the structured `(u8, u8)`
patterns are real codec headers from the start of bone 0's first channel.

Per-anim byte costs vary by encoding density — short anims (few frames,
many static bones) compress to ~33 bytes/bone, complex anims to ~100/bone.
The codec stops naturally at `streamLen` frames per channel; **byte budget
between consecutive entries is meaningless** because entries aren't sorted
by offset.

### ~~THPS2 PSX prototype — sparse table~~ RESOLVED (2026-07-06): the sparse layout never existed

The "PrototypeSparse" variant was an artifact of two stacked parser bugs and
has been removed:

1. The original hint (hawk2's "42 streams / 13,236 pool") came from the
   pre-chunk-tag parse reading 8 bytes early — those values were the chunk
   HEADER (tag 0x2A = 42, size 13,236). Fixed in the May 2026 anchor fix.
2. After the anchor fix, the classifier still demoted any v2 table with fewer
   than 5 valid entries to "sparse" (`MonolithicMinValidEntries` was
   calibrated against the mis-anchored data), recovering a chimera entry —
   entry 0's frame count decoded against entry 1's data offset. mj.psx
   (declares 2) parses perfectly as monolithic: entry 0 = (offset 0x14 —
   exactly `4 + 2×8`, the byte after the table — 80 frames), entry 1 =
   (offset 0xF58, 48 frames).

`RunAnim` (PERFECT-matched in the decomp) confirms there is only ONE table
layout: the engine indexes the same monolithic 8-byte entries directly at
runtime for every file, with no per-file variation and no load-time rebuild.
After accepting monolithic tables whose declared entries ALL validate, the
corpus classifies as 588 DirectMatrix + 227 Monolithic — matching the 227
files the chunk walk finds with 0x2C exactly — with **zero files recovering
fewer entries than declared**.

### Apocalypse PSX (1998) — v3 format

For croc and demon, the first animation parses correctly, but subsequent
entries report absurd frame counts (65,560, 131,106). The high u16 of each
"frame count" field is consistently `1` for v3 files. Likely interpretation:
**v3 packs `(u16 frameCount, u16 flags, u32 poolOffset)`** where flags carry
per-animation metadata. Confirming this needs an Apocalypse-specific
decompilation pass; the THPS2 PSX prototype source has no v3 code paths.

## Per-sample summary

| Sample | Era | numStreams | Pool size | Outcome |
|---|---|---|---|---|
| THPS2 proto `hawk2.psx` | v4 (March 2000) | 42 | 13,236 | mis-anchored read (42/13,236 = chunk header); actually a 1-entry v1 monolithic table |
| Spider-Man `carnage.psx` | v4 (Sept 2000) | 44 | 102,296 | full table; carnage anim 0 over-reads (slot overlap?) |
| Spider-Man `blackcat.psx` | v4 (Sept 2000) | 44 | 34,660 | full table; under-fills (anim slack) |
| Apocalypse `croc.psx` | v3 (1998) | 42 | 13,228 | v3 entry packing differs |
| Apocalypse `demon.psx` | v3 (1998) | 42 | 32,220 | same v3 packing |

## Resolved: chunk-tag-aware parsing (May 2026)

The original parser started reading at `meshBlockEnd` (the byte after the last
mesh block), which by happy accident landed 8 bytes BEFORE the actual anim
chunk data on every file we tested. The 8-byte offset meant we read the chunk
header (`tag:u32, size:u32`) as the first u32 ("numStreams") + first entry's
first u32 — which is why carnage's "44 streams" and hawk2's "PrototypeSparse
header (42, 13236)" all aligned: 44 = the 0x2C chunk tag, 13236 = the chunk
size, etc.

The fixed parser:

1. Walks the tagged-chunk chain from `data[4]` (engine's `metaTop`) looking for
   tag `0x2A` (v1, uncompressed direct-matrix SMatrix per bone per frame) or
   `0x2C` (v2, DecompressStream-compressed Euler+translation). Per
   `ProcessNewPSX` (SPOOL.cpp:884-928) the engine overwrites `pAnimFile` with
   each matching chunk, so the **last** match wins.
2. Reads the entry table from the chunk DATA (offset = chunk header + 8). All
   entries share the same 8-byte layout regardless of v1/v2:
   ```
   +0x00 u32 dataOffset (relative to chunk start)
   +0x04 u16 frameCount (total PLAYBACK frames)
   +0x06 u16 tweenFlag  (stored keyframe interval − 1; v1 only)
   ```
   Splitting `frameCount` into a u16 + u16 tween field is what unblocks
   Apocalypse v1 parsing — the previous u32 frameCount interpretation rejected
   entries with `(1 << 16) | frames` as "absurd" frame counts.
3. **Tween keyframe reduction (RESOLVED 2026-07-06, M3dUtils_InterpolateVectors
   PERFECT + both call sites):** when `tweenFlag != 0`, the v1 payload stores
   only keyframes every `interval = tweenFlag + 1` frames — both engine call
   sites pass `framesPerKey = tweenFlag + 1`, so even `tweenFlag = 1` means
   half the frames are stored. Stored record count =
   `(frameCount − 1) / interval + 1`. Playback lerps every s16 cell (all four
   SMatrix vec3 rows — 3 rotation rows AND the translation, component-wise,
   NOT slerp) between records `keyStart/interval` and `keyEnd/interval` with a
   truncating 1.12 factor `((frame − keyStart) << 12) / (keyEnd − keyStart)`
   via GTE GPL sf=1 (`a + ((b − a) × factor >> 12)`, truncation, no rounding).
   End-of-anim (keyEnd ≥ frameCount): cycle mode (loopMode 1) wraps toward
   keyframe 0 with denominator `frameCount − keyStart`; otherwise the window
   clamps back one interval and the factor EXTRAPOLATES past the last stored
   record (the converter uses this non-cycle branch — loop mode is runtime
   per-instance state a converter can't know, and baking the cycle wrap into
   one-shot clips would lurch them back toward the start pose).
   `tools/diagnostics/psx_anim_tween_survey.py`: 3,896 corpus entries carry
   tweenFlag > 1 and thousands more carry tweenFlag = 1 (nearly all of
   Apocalypse + the Spider-Man v1 family); v2 (0x2C) entries are always 0.
   Before this fix the converter read `frameCount` full records from
   keyframe-only payloads — e.g. bruce.psx anim 0 (40 frames, tween 2) stores
   14 records but 40 were read, so ~65% of every such anim was garbage.
4. For v1 (0x2A) the per-frame payload is `numBones × 24-byte SMatrix`
   (3×3 s16 rotation matrix + 3-vector s16 translation; PSY-Q 4096 = 1.0
   fixed-point). The decoder extracts YXZ Euler angles from each matrix and
   re-encodes into the same `short[boneCount, 6, frameCount]` channel layout
   the v2 path produces, so downstream consumers don't care which wire format
   ships in a given file.

### Survey of v1 (0x2A) presence

`tools/diagnostics/psx_anim_chunk_walk.py` reports **588 files** across every
sampled Neversoft PS1 build that use the v1 chunk tag — including all of
Apocalypse, both Spider-Man PSX prototypes, and ~150 character files across
THPS / THPS2 / THPS2X. v2 (0x2C) accounts for the remaining 227 files. The two
tags are distributed by build era: Apocalypse is overwhelmingly v1, Spider-Man
final is mostly v2, THPS2 final mixes both.

## Resolved: animation naming (May 2026, partial)

`Spool_FindAnim` (SPOOL.cpp:1692) walks an in-memory linked list of 0x45
animation packets looking for 8-char ASCII names. Cross-referencing with
`PreProcessAnimPacket` (psx_decompiled.c:59115) and dumping real 0x45 chunks
with `tools/diagnostics/psx_anim_packet_dump.py` confirms the on-disk layout:

```
+0x00 u32 count
For each entry:
  +0x00 char[8] name (NUL-padded)
  +0x08 u32 frameCount
  +0x0C frameCount × 8 bytes of frame data ({reserved:u32, meshIdx:u32}-pairs)
  next entry at +0x0C + frameCount*8
```

Sample names from `bits.psx` and `cost*.psx` files: `FIREBALL`, `EXPFIRE`,
`SHADOW`, `smoke`, `ribbon`, `xtri`, `webknot`, `cost99`, `costarm`,
`costbag`, `costblk`.

**However:** the 0x45 chunk is exclusive to *sprite / effect / costume*
PSX files. **Character files (carnage, hawk2, blackcat, mullen, etc.) do not
carry a 0x45 chunk at all.** Per `ProcessNewPSX` the chunk types found in a
character PSX are limited to `0x06`/`0x07`/`0x0A`/`0x2A`/`0x2C`/`"HIER"`/`"RGBs"`,
with the animation indices addressed numerically via `RunAnim(this, animIndex)`.
The text-based animation name → group index mapping the engine uses is built
at startup from per-game `*.psh` text files (`Create_LoadPSH`,
psx_decompiled.c around the `AnimGroupTable` references), which the shipped
disc does not contain.

Implication for the multitool: human-readable names like `walk`, `ollie`,
`grind` can't be recovered from a character `.psx` alone. The current
`anim_{i}` labeling stays. A future plan could parse 0x45 chunks for *sprite*
PSX exports if those formats become user-visible.

## How to re-run

```bash
dotnet build src/NeversoftMultitool/NeversoftMultitool.csproj --framework net10.0

dotnet run --project src/NeversoftMultitool --framework net10.0 -- psxanim \
  "Sample/Builds/Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)/CD/hawk2.psx" \
  --anim 0 --bone 0
```

- `--anim N` — which animation slot to dump
- `--bone N` — which bone within that animation to print frame-by-frame
- `-v` — full per-frame output for every bone (not just summary)
