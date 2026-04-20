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

## Followups

- Determine which bit in `+0x3C` flags identifies a leaf. Candidates in
  order of likelihood: bit 0 (`0x01`), bit 1 (`0x02`), bit 2 (`0x04`).
  Record 0 on disk has `0x05` (bits 0+2), EE has `0x15` (bits 0+2+4) — bit
  4 is the relocated marker, so leaf flag is probably bit 0 or bit 2.
- Implement a record-tree walker that starts at record[0] (likely root),
  follows `+0x40` to the first child, and uses `+0x48` as sibling pointer
  to enumerate the tree. File-offset format (both are file-relative byte
  offsets as shown by the disk→EE rebase).
- Match tree leaves to VIF batches: the record count (5,649) is much
  larger than the batch count (938), suggesting many internal nodes per
  leaf. A leaf's `+0x40` probably points to the VIF batch start (values
  like `0x002C69A0` fall inside the VIF region at `0x604C – 0x2A6F40`).
- The 938 batches being pre-positioned means `Ps2MdlPlacementResolver`
  should return identity placements for 0x7EA7357B MDLs and just use the
  record tree to select which batches to emit, not how to move them.
