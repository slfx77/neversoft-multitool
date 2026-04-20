# THAW worldzone MDL — PCSX2 savestate findings (2026-04-20)

## Result summary

The engine loads `003B1940.mdl` (the 3.2 MB BH worldzone MDL, type
`0x7EA7357B`) into EE RAM at address **`0x0155FA00`**, **byte-for-byte
identical to the on-disk PAK-extracted bytes for ~96% of the file**. There is
no decompression, no format conversion, and no separate relocator buffer.

The call-chain mystery from `phase41x_summary.md` ("file[0] = 0xBFDD1111 —
clearly not an offset, so something must be transforming the data before
`FUN_001D4248` sees it") is resolved: the "garbage" is NOT garbage. The
first 4 bytes of a worldzone level MDL are simply not a CGeomNode
data-section offset. These files are laid out differently from the
`.geom.ps2` / `.mdl.ps2` GEOM-tree pattern the phase 1b/1c code walks.

## Byte-level diff: where the engine rewrites on load

| Region | Disk offset | Behaviour in EE RAM |
|---|---|---|
| File header / preamble VRAM block | `0x00000000` – `0x007A562` (~500 KB) | **Identical** |
| VIF batch region | `0x007A563` – `0x002A6F40` (~2.6 MB) | **Mostly identical**, with a tight pattern of 1-byte and 17-byte rewrites on a 4-byte grid (DMA chain pointer patching — the engine rewrites addresses embedded in VIF instructions so the chain can run in-place from EE RAM) |
| Preamble record table (5,649 records × 0x50 B) | `0x002A6F40` – EOF | **Identical byte positions** (world pos, bbox, sig, etc.) **EXCEPT record+0x3C flags and record+0x40/+0x48 pointers** |

Total: 3,094,149 / 3,232,880 bytes match (95.71%). 27,158 divergence runs,
mostly 1-17 byte patches on the 4-byte-aligned pattern that indicates DMA
tag / VIF address rewriting, not data restructuring.

## What the engine does to each preamble record

Using record 0 as an illustration:

```
              +0x00  +0x08  +0x10  +0x18          +0x20 (pos x,y,z)  +0x30 (bbox)  +0x3C  +0x40  +0x44  +0x48
Disk:  00*8   ff*4   8080   00ff  80961884 ff*4   (-14549.5, 154.86, 11079.49)     05     002C69A0  ffffffff  002A5FC0
EE:    00*8   00*4   8782   00ff  80961884 00*4   <same floats>                    15     01827390  00000000  018069B0
```

Changes, record-by-record:
1. Byte `+0x08..+0x0F` **pre-signature header**: sometimes rewritten (probably a parent / sibling index cache).
2. Byte `+0x1C..+0x1F`: a 4-byte slot right after the `0x4B189680` sig goes from `ffffffff` on disk → `00000000` in EE (reserved / relocator scratch).
3. Byte `+0x3C` flags: **`0x05` → `0x15`** on load. Bit 4 (`0x10`) set — most likely a "relocated / live" bit the engine uses so repeat loads don't re-relocate.
4. Byte `+0x40` and `+0x48`: **file-offset pointers on disk → EE-absolute pointers in RAM.**
   - Disk `+0x40 = 0x002C69A0` becomes EE `0x01827390`.
   - Delta = `0x002C7990`, which is `0xFF0` (= 51 × 0x50) beyond the corresponding
     disk offset from the base.
   - So the engine **inserts 51 extra records into the table** ahead of
     record-table position 0x002C69A0 / 0x002C7990 and rebases all pointers.
     This is why EE shows a 5,700-record run where disk has 5,649.

## Implications for our converter

1. **We already have the data.** No decompression / format conversion is
   needed. The bytes we extract from the PAK are the same bytes the engine
   acts on.
2. **The first 4 bytes of a 0x7EA7357B MDL are NOT a CGeomNode data-section
   offset.** The two-hop relocator pattern from `FUN_001D4248` (`file += *file;
   root = file + *file`) is for GEOM-tree files, a different format. The
   phase1b/1c code on `003B1940.mdl` would be invalid to apply.
3. **The CGeomNode-style tree, if it exists for worldzones, lives inside
   the preamble-record table** at `MDL + 0x2A6F40`. Each 0x50-byte record
   carries:
   - `+0x18`: magic `0x4B189680`
   - `+0x20`: 3 × f32 world position (verified against in-RAM state)
   - `+0x30`: 3 × f32 bbox half-extent
   - `+0x3C`: flags (bit 0 = leaf?, bit 2 = ?, bit 4 = "relocated", …)
   - `+0x40`: child / data file-offset (relocator rewrites these)
   - `+0x48`: sibling / next file-offset
4. **5,649 records vs 938 VIF batches**: records form a tree; batches are
   the leaves. We need to walk the tree using the `+0x40` / `+0x48`
   file-offsets and only emit geometry for leaf records. The leaf
   bit is probably in the `+0x3C` flags field — need to determine which
   bit.
5. **Batches in the VIF region are pre-positioned** in world space by the
   engine (matching our earlier Apr-17 finding that disk bytes already
   hold multi-sector pre-positioned geometry). So the placement path for
   0x7EA7357B files is: walk the record tree, match leaves to batches,
   but apply **no additional transform** — just the root-axis-swap if the
   engine's coordinate system differs from glTF's.

## Followups / what's been done

- **Leaf flag confirmed = bit 1 (`0x02`)**. Empirically: 3,977 of 5,649
  records have bit 1 set; all 3,977 have a non-zero `+0x40` file offset
  inside the VIF region `[0x604C, 0x2A6F40)`, and records without bit 1
  always have `+0x40 = 0xFFFFFFFF`. Wired up as `PreambleRecord.IsLeaf`
  in commit on 2026-04-20.
- **Implemented leaf-range-to-batch centre matching**. `Ps2GeomFile.TryGetLevelBatchCentres`
  was rewritten to sort leaves by `Field40` and binary-search the first
  leaf whose `Field40` falls inside each MSCAL batch's range; that leaf's
  `Centre` (verified world-space) becomes the batch placement. Replaces
  the old sequential sorted-record → batch mapping.
- **Converter result for z_bh.pak.ps2** after the fix: 3/9 MDLs → one
  combined `z_bh_worldzone.glb`, 10,989 triangles, 50 materials, 48
  textures, bbox 20,717 × 3,452 × 20,236 world units, 0 glTF validator
  errors or warnings.

## GS dump — ground-truth render stats (2026-04-20)

Captured two PCSX2 GS dumps from the same BH spawn with slightly different
camera angles (`Tony Hawk's American Wasteland ..._SLUS-21295_2026042003*.gs.xz`).
Parsed with `thaw_gsdump_parser.py --gif`.

Both dumps yield nearly identical totals in one frame:

| Dump | XYZ writes | TRIANGLE_STRIP | Unique TEX0 values | GIF tags |
|---|---|---|---|---|
| 034044 | 175,672 | 168,236 (95.8%) | 491 | 36,102 |
| 034156 | 178,632 | 170,548 (95.5%) | 491 | 36,634 |

Register-write mix is typical for a textured, coloured strip renderer:
RGBAQ and ST writes roughly equal XYZ writes, confirming every vertex
carries per-vertex colour + texcoord. Primary primitive is
`TRIANGLE_STRIP` (~96%); the remainder is `SPRITE` (billboards/UI) and
`TRIANGLE_FAN`.

**Our converter produces 10,989 tris (~32,967 vertex writes) across 3
MDLs — roughly 19% of the engine's per-frame vertex output and 10% of the
unique TEX0 values.** Translation: the engine draws ~3,977 strip primitives
per frame while our scanner extracts ~938 coarse batches, each of which
emits geometry for only one sub-chunk. The remaining ~80% of sub-chunks
are skipped because they don't match the `STCYCL + GIF-tag + STMOD +
UNPACK-V4_32` opening pattern our decoder requires for positions.

## Remaining caveat (batch subdivision)

Our VIF scanner (`FindRepeatedBatchSignatureRanges` /
`FindMscalBatchRanges`) only detects 938 coarse batch boundaries, but the
file contains 3,977 leaves / fine-grained sub-chunks (also: a raw sweep
finds 4,578 MSCAL-like tags in the VIF region, close to the leaf count).
This means our current per-batch primitives are ~4× too coarse — each
glTF mesh spans several leaves' worth of geometry under a single
placement.

Two plausible next steps:

1. **Subdivide batches by leaf `Field40` boundaries** — for each coarse
   batch, split at each leaf's `+0x40` file-offset. The bytes starting at
   `+0x40` aren't a clean VIF tag stream (first bytes look like packed
   vertex data, not STCYCL/UNPACK opcodes), so we'd probably need a
   smaller-granularity VIF decoder that tolerates partial / mid-chain
   entry points.
2. **Loosen the MSCAL scanner** — `FindMscalBatchRanges` currently walks
   via `VifNextCode`, skipping data that happens to share the MSCAL high
   byte. The real file has 4,578 MSCAL-family tags; if we can distinguish
   real MSCALs from false positives, we'd get one-batch-per-MSCAL which
   should align with the 3,977 leaves.

Either path involves non-trivial VIF-decoder work. The incremental
improvement already in place correctly places each coarse batch at
a plausible in-sector position, which is a substantial improvement over
the previous sequential-record heuristic.
