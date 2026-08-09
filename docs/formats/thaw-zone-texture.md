# THAW PS2 Zone .tex File Format

Reverse-engineered from `z_ho.pak/0009BF70.tex` (Hollywood zone, 3,266,032 bytes)
via Ghidra decompilation + binary analysis.

> **Status: SOLVED (Phase 336).** The decoder now produces pixel-accurate output
> matching the PC build's `.tex.wpc` ground truth for both PSMT4 and PSMT8 zone
> textures across multiple zones (z_ho, z_at, z_bh). The implementation lives in
> [`ThawZoneTexOwnerBlobDecoder.cs`](../../src/NeversoftMultitool/Core/Formats/Texture/Ps2Scene/ZoneTex/ThawZoneTexOwnerBlobDecoder.cs).
> See "Phase 336" at the bottom of this document for the complete pipeline.

Important distinction:

- The layout described in the first sections of this file is the **current
  extracted public zone `.tex` file** in this repo.
- The runtime owner blob consumed by `FUN_001e9ac0(...)` is a different
  structure: a fixed `0x10` header, followed by `0x50` primary records,
  `0x40` secondary prepared-source entries, and then relocatable data.
- So the extracted public `.tex` file is **not** a direct byte-for-byte dump of
  the owner blob that `FUN_001e9ac0(...)` expects.

## File Layout

The entire file is loaded into PS2 EE RAM as a single contiguous blob.
The packed pixel/CLUT data, record table, and DMA chain share the same address space.

```
Offset      Size        Content
──────────  ──────────  ────────────────────────────────────────
0x0000      10 bytes    File header
0x000A      variable    Packed pixel/CLUT data START
0x16D0      63,360      Record table (990 × 0x40 bytes)
0x10E50     ~267,920    DMA upload chain (upload blocks)
0x525E0     variable    Packed pixel/CLUT data continues
...         ...         (data_offset addresses span all regions)
0x2CC06A    ---         Packed data END (0x0A + max extent)
0x31D5F0    ---         EOF
```

**Key insight**: The packed data, record table, and DMA chain physically overlap
in the file. The build tool arranges pixel data into all available space, including
within the record table and DMA chain regions. At runtime, the record table is
parsed first, then the DMA chain is sent to GIF — after which those bytes are
effectively "free" for the DMA controller to read as pixel data.

## File Header (10 bytes)

```
+0x00  u32  checksum     File/zone texture checksum (QbKey hash)
+0x04  u32  public_tag   Often 0xABBAE5E0 in Hollywood; not the owner-blob header field
+0x08  u16  unknown      Purpose TBD (0xD4B2 in test file)
```

Do not interpret this 10-byte public header as the decompiled owner-blob header
read by `FUN_001e9ac0(...)`. The owner-blob loader expects a different `0x10`
header shape entirely; the shared `0xABBAE5E0` byte pattern in Hollywood is
therefore not enough to identify the public extracted file as a direct owner
blob dump.

**IMPORTANT: The sentinel is NOT universal.** Only z_ho.pak has `0xABBAE5E0` at +0x04.
Other zone .tex files (z_ho_net, z_sm) have different header bytes. The file format
(record table + DMA chain) is identical across all files; only the header content varies.

## Packed Data Base

**PACKED_BASE = 0x0A** (immediately after the 10-byte header).

All `data_offset` values in records are relative to this base. The DMA chain
confirms this: every DMA REF address = `0xEB000000 + PACKED_BASE + data_offset`.
The `0xEB000000` is the runtime RAM load address (a placeholder/tag that gets
relocated when the DMA chain is processed).

Verified: `REF_ADDR - cumul_off = 0xEB00000A` for ALL 990 records (constant).

### Cross-File Verification

PACKED_BASE = 0x0A verified across all 3 extracted zone .tex files via DMA REF matching:

| File      | Size       | Sentinel    | Records | DMA REFs | PACKED_BASE |
|-----------|------------|-------------|---------|----------|-------------|
| z_ho      | 3,266,032  | 0xABBAE5E0  | 990     | 138      | 0x0A        |
| z_ho_net  | 3,115,120  | (none)      | 933     | 138      | 0x0A        |
| z_sm      | 3,153,456  | (none)      | 720     | 125      | 0x0A        |

### Record Table Discovery Algorithm

Works for all files regardless of sentinel presence:
1. Find DMA chain start: scan for `0x10000006` (CNT QWC=6) + verify next tag
2. Walk backwards from DMA start: each 0x40-byte block with valid TEX0 is a record
3. Count header at `records_start - 0x10` contains `count - 1`

## Record Table

**Location**: Immediately after PACKED_BASE + first texture data.
**Start**: 0x16D0 (= PACKED_BASE + 990_records × 0x40... no, computed from file structure)
**Count**: 990 valid records.
**Record size**: 0x40 (64 bytes).

Records are NOT sorted by data_offset — the packed data is scattered. 853 unique
data blocks serve 990 records (137 records share data with others via identical
data_offset values).

### Record Structure (0x40 bytes)

```
Offset  Type  Name              Description
──────  ────  ────────────────  ─────────────────────────────────────────────
+0x00   u32   checksum          Texture checksum (QbKey hash of texture name)
+0x04   u32   group_checksum    Owner/group checksum (~20 distinct values)
+0x08   u32   mip_count         Number of mip levels (0-4; 940 have 0)
+0x0C   u32   layout_mode       Flags/mode (e.g. 0x00040005, 0x02000001)
+0x10   u64   tex0              GS TEX0 register value (packed)
+0x18   u32   mip_field_1       Mip-related (0 for non-mipped textures)
+0x1C   u32   mip_field_2       Mip-related (0 for non-mipped textures)
+0x20   u32   reserved_1        Almost always 0 (1 exception)
+0x24   u32   reserved_2        Always 0
+0x28   u32   cumul_off         = data_offset + pal_bytes (pixel data start)
+0x2C   u32   data_size         Pixel data bytes (main + mips, NO CLUT)
+0x30   u32   data_offset       Offset from PACKED_BASE to CLUT start
+0x34   u32   pal_bytes         CLUT size in bytes (separate from data_size)
+0x38   u32   upload_off        Offset from DMA chain start to pixel IMAGE tag
+0x3C   u32   pixel_qwc_shifted Main-level pixel QWC << 16
```

### TEX0 Register Layout (u64 at +0x10)

```
Bits   Field  Description
─────  ─────  ──────────────────────────
0-13   TBP0   Texture base pointer (VRAM page)
14-19  TBW    Texture buffer width (in 64-pixel units)
20-25  PSM    Pixel storage mode (0x00=CT32, 0x02=CT16, 0x13=T8, 0x14=T4)
26-29  TW     Texture width exponent (width = 1 << TW)
30-33  TH     Texture height exponent (height = 1 << TH)
34-34  TCC    Texture color component (0=RGB, 1=RGBA)
35-36  TFX    Texture function
37-50  CBP    CLUT base pointer (VRAM page)
51-54  CPSM   CLUT pixel storage mode (0x00=CT32, 0x02=CT16)
55-55  CSM    CLUT storage mode (0=CSM1, 1=CSM2)
56-60  CSA    CLUT entry offset
61-63  CLD    CLUT buffer load control
```

### Data Layout Per Texture

Each texture's data block in the packed section:

```
PACKED_BASE + data_offset:                    CLUT data (pal_bytes bytes)
PACKED_BASE + data_offset + pal_bytes:        Main-level pixel data
PACKED_BASE + data_offset + pal_bytes + ...:  Mip level data (if mip_count > 0)
```

Total data for one texture = `pal_bytes + data_size`.

These offsets are still important metadata. Current decompilation indicates the
runtime keeps CPU-side prepared image buffers and decodes paletted textures from
those source pointers (`FUN_0019cd48`), while the DMA stream remains useful as a
fallback / cross-check path and for GS-state reconstruction.

### Palette Sizes

| PSM   | CPSM  | pal_bytes | Entries | Notes                    |
|-------|-------|-----------|---------|--------------------------|
| PSMT4 | CT16  | 32        | 16      | 16 colors × 2 bytes     |
| PSMT4 | CT32  | 64        | 16      | 16 colors × 4 bytes     |
| PSMT8 | CT16  | 512       | 256     | 256 colors × 2 bytes    |
| PSMT8 | CT32  | 1024      | 256     | 256 colors × 4 bytes    |
| CT32  | —     | 0         | —       | No palette (direct RGBA) |
| CT16  | —     | 0         | —       | No palette (direct RGB)  |

### pixel_qwc_shifted (+0x3C)

`field_3c = (main_pixel_bytes / 16) << 16`

Verified: 0/990 mismatches. This encodes the DMA quadword count for the
main texture level's pixel transfer, shifted left by 16.

## DMA Upload Chain

**Start**: 0x10E50 (immediately after record table).
**End**: ~0x525E0.

### Standard Upload Block (128 bytes = 8 QWs)

Each texture has 1-2 upload blocks (CLUT + pixels), plus additional blocks
for mip levels.

```
QW0: DMA CNT tag    — QWC=6, ID=1 (send next 6 QWs inline to GIF)
QW1: GIF A+D tag    — NLOOP=4, NREG=1, REGS=0x0E (A+D register)
QW2: BITBLTBUF      — DBP, DBW, DPSM (destination format for GS VRAM)
QW3: TRXPOS         — Source/dest position (usually 0,0)
QW4: TRXREG         — Transfer width × height
QW5: TRXDIR         — Direction = 0 (host → local upload)
QW6: GIF IMAGE tag  — NLOOP=N (QWs of pixel data to transfer)
QW7: DMA REF tag    — QWC=N, ADDR=0xEB... (relocated runtime address)
```

### Upload Sequence Per Texture

1. **CLUT upload** (if pal_bytes > 0):
   - BITBLTBUF: DBP ≈ CBP from TEX0, DPSM = CPSM
   - TRXREG: small rectangle (e.g. 8×2 for 32-byte CT16 CLUT)
   - REF ADDR = 0xEB00000A + data_offset

2. **Pixel upload** (at upload_off from DMA chain start):
   - BITBLTBUF: DBP = TBP0 from TEX0, DPSM = PSMCT32 or PSMCT16
   - TRXREG: upload rectangle (e.g. 64×32 for 128×128 PSMT4-as-CT32)
   - REF ADDR = 0xEB00000A + cumul_off (= data_offset + pal_bytes)

3. **Mip uploads** (if mip_count > 0): additional blocks before CLUT block

### DMA REF Address Format

All REF addresses use the pattern: `0xEB000000 + file_offset_from_byte_0x0A`.

The `0xEB` prefix is a relocation marker. At runtime, the zone loader replaces
these with actual EE RAM addresses pointing into the loaded file blob.

### SIGNAL Blocks

18 of ~2088 upload sequences have a 176-byte stride (extra SIGNAL register
block with reg 0x3F between them). These likely synchronize GS processing
between texture upload batches.

## PSM Distribution (Hollywood Zone)

| PSM    | Count | Notes                    |
|--------|-------|--------------------------|
| PSMT4  | 962   | 4-bit indexed (dominant)  |
| CT32   | 1     | Direct 32-bit RGBA       |
| PSMT8  | 27    | 8-bit indexed            |

## Decoding Notes

### Decompiled Runtime Behavior

`FUN_0019cd48` is a linear, bottom-up pixel decoder that expands already-prepared
indexed image data through a CLUT. It reads pixel and CLUT pointers from the
texture object (`+0x14` and `+0x18`), does not contain `Conv4to32` /
`Conv4to16` logic, and does not read back texels from GS VRAM.

`FUN_001cfb58` patches and relocates blob-owned render/setup structures, but the
current decompilation does not show it transforming packed pixel payload bytes
in place before upload.

### Phase 6 Source-Binding Findings

Targeted decompilation in
`historical analysis artifact phase6_source_binding.c`
adds a critical clarification:

- `FUN_001e6658` does not derive prepared source pointers from the zone record
  table. It relocates explicit relative offsets already stored in the runtime
  image object:
  - `+0x14` = pixel source pointer
  - `+0x18` = CLUT source pointer
  - `+0x40` / `+0x44` = upload/setup pointers used to build TEX0 and GS state
- `FUN_001e7348` is a separate in-place row-flip helper for the prepared pixel
  buffer.
- `FUN_001e7638` builds TEX0 from the relocated upload/setup pointers; it does
  not resolve the prepared source layout.

Implication: the current public "header data slot" decoder is still a heuristic
reconstruction from record metadata. It is not yet parsing the same object
fields that the game actually decodes through. A safe fix needs the zone loader
path that materializes those per-object `+0x14` / `+0x18` offsets, rather than
further trial-and-error reinterpretation of `data_offset` or `cumul_off`.

### Phase 7 Owner-Bridge Findings

Targeted decompilation in
`historical analysis artifact phase7_owner_bridge.c`
clarifies the wrapper layer above the generic owner parser:

- `FUN_001a0480` is only a bridge that chooses between:
  - `FUN_001e9fa8(blob_path_or_name, out_size_ptr)` for the "construct from
    external blob" path
  - `FUN_001e9fe0(...)` for the empty/raw-memory path
- `FUN_001e9fa8` immediately calls `FUN_001216f0(...)`, then passes the
  returned pointer to `FUN_001e9ac0(...)`.
- `FUN_001a0368` iterates the owner's primary `0x40` records and instantiates
  wrapper image objects from those already-relocated record entries. It does not
  derive alternate CPU-side source bindings from zone record metadata.

Implication: there is no extra zone-specific decode stage between the wrapper
object and `FUN_001e9ac0`. The unresolved question moved one layer lower to the
blob returned by `FUN_001216f0`.

### Phase 8 File-Source Findings

Targeted decompilation in
`historical analysis artifact phase8_file_source.c` and
`historical analysis artifact phase8b_file_read.c`
shows that `FUN_001216f0` loads a complete blob by path/hash. It does not
select an inner payload offset based on zone-specific metadata:

- `FUN_0025e288` looks up a cached resident blob by path hash and returns a
  pointer plus size.
- Failing that, `FUN_001231b8` / `FUN_00123220` query the file size and then
  read the file into the destination buffer through the generic file object.
- `FUN_00123220` passes the file handle and destination pointer straight to
  `FUN_00121d18`; no zone-specific pointer adjustment happens there.
- The cache helper `FUN_0025d150` only converts a cached relative pointer into
  an absolute address inside the cached blob.

Implication: the pointer handed to `FUN_001e9ac0` is the whole cached/read blob,
not a late-selected interior payload. That makes it much less likely that the
runtime is silently skipping a hidden prefix before owner parsing. If the
extracted `.tex` bytes still do not match the generic owner header at byte 0,
the remaining possibilities are a different outer container path before
extraction, or that the runtime object backing `FUN_0019cd48` is being sourced
from a different asset than the public `.tex` file bytes we have been decoding.

### Companion `.geom` Finding

The post-load helper in `FUN_00157318` is now much clearer:

- `FUN_00157040` builds a companion path from the format string at `0x004af0c8`:
  `levels\\%s\\%s%s.geom.%s`
- the third substitution is either `"_net"` or empty
- the final substitution is the platform suffix returned by `FUN_00157f60`
- `FUN_00157318` then calls:
  - `FUN_0016ad60("levels\\...\\.tex", ...)` to load the texture-owner blob
  - `FUN_00198060(owner_wrapper, "levels\\...\\.geom.%s")`

`FUN_00198060` loads that `.geom` companion through `FUN_00120b20(...)` and
passes it to `FUN_001d4248(...)`, which in turn calls `FUN_001cfb58(...)`.

Implication: the `FUN_001cfb58` path is a geometry/material companion fixup
stage, not a second texture-source decode stage. That further weakens the
hypothesis that `FUN_001cfb58` is where the zone `.tex` packed bytes are being
converted into the CPU-side `+0x14` / `+0x18` source buffers consumed by
`FUN_0019cd48`.

### Phase 9-12 Wrapper / Binding Findings

The strongest new lead is no longer the file loader. It is the runtime binding
path used after zone owner records already exist:

- `FUN_001a0368` creates lightweight `0x14` image wrappers for zone textures.
  For these wrappers:
  - `flags |= 0x2`
  - `wrapper + 0x10 = zone record pointer`
- The wrapper getters decompiled in
  `historical analysis artifact phase9_zone_wrapper_methods.c`
  prove that `flags & 0x2` is a real "record-backed" mode:
  - `FUN_0019f870` falls back to `FUN_001ea2e8(record)` for pixel format
  - `FUN_0019fa98` falls back to `FUN_001ea350(record)` for CLUT format
  - `FUN_0019fb60` falls back to `FUN_001ea3a8(record)` for mip count
- `FUN_0019cd48` still decodes only from a loaded image object's `+0x14/+0x18`
  pointers. It does not decode directly from the record-backed wrapper mode.

That means there must be a later binding step that turns checksum/group-based
references into resolved zone record pointers or related runtime objects.

Targeted decompilation in
`historical analysis artifact phase10_record_lookup.c`,
`historical analysis artifact phase11_lookup_caller.c`, and
`historical analysis artifact phase12_binding_chain.c`
shows part of that rebinding chain:

- `FUN_001ea0b8(texture_checksum, group_checksum, owner)` searches owner records
  and their `0x40` child entries and returns the matching child-entry pointer.
- `FUN_001db060(entry)` takes a `(checksum, group_checksum)` pair stored at
  `entry + 0x20` / `entry + 0x2c`, resolves it through `FUN_001ea0b8`, and
  overwrites `entry + 0x20` with the resolved pointer.
- `FUN_001e5368(...)` processes an entire table of such entries and calls
  `FUN_001db060` on each one before continuing with later setup.

Implication: this wrapper/binding path is still useful, but it is not itself the
pixel-source decode path. It demonstrates an explicit late binding step from
checksums to resolved zone record pointers, which explains how runtime texture
references are attached to zone records. The missing piece is still the
transition from those resolved record-backed references to the concrete prepared
source object whose `+0x14/+0x18` pointers `FUN_0019cd48` actually consumes.

### Phase 13-17 Binding Object Findings

Targeted decompilation in
`historical analysis artifact phase13_binding_object.c`,
`historical analysis artifact phase15_binding_consumers.c`,
`historical analysis artifact phase16_handoff_consumers.c`, and
`historical analysis artifact phase17_binding_builders.c`
clarifies what the `FUN_001e5368(...)` object actually is and what uses it:

- `FUN_001e5628(...)` is a blob-loading wrapper around `FUN_001e5368(...)`.
  After `FUN_001216f0(...)` returns a blob pointer, it stores that blob pointer
  at `binding + 0x24`.
- `FUN_001a4280(...)` and `FUN_001a43a0(...)` are the only direct constructors
  of this path. They construct objects with vtable `DAT_004b4e80` and choose
  between:
  - direct `FUN_001d4248(...)` loading into `owner + 0xC0`, or
  - binding-object mode via `FUN_001e5698(...)` / `FUN_001e5628(...)`, stored at
    `owner + 0xC4`
- A direct string dump at `0x004b4e68` shows that this class appends
  `".geom.%s"` in its file-path constructor. That confirms the `DAT_004b4e80`
  family is on the companion `.geom` side, not a hidden prepared-pixel source
  loader for the public zone `.tex` blob.
- In that vtable family, `FUN_001a4530(...)` is the destructor for the
  binding-object mode. If `owner + 0xC0 == 0`, it frees `owner + 0xC4` through
  `FUN_001e56b8(...)`.
- `FUN_001e56b8(...)` removes the binding object from the global
  `DAT_005af6b0` list, unhooks per-record association nodes from render/setup
  chains, returns pooled 12-byte helper nodes through `FUN_001e5c48(...)`, and
  frees the binding object itself.

The object layout implied by `FUN_001e5368(...)` is now roughly:

- `+0x18` = pooled helper node from `DAT_0049ad24`
- `+0x1C` = count of `0x40` entries
- `+0x20` = pointer to the `0x40` entry table
- `+0x24` = backing blob pointer when created through `FUN_001e5628(...)`
- `+0x30` = pointer to a `0x50`-stride table
- `+0x34` = next pointer in global `DAT_005af6b0` list
- `+0x38` = optional handoff table copied into the owner

The important consumers are not pixel decoders:

- `FUN_001a4218(owner, table, blob_base)` copies the optional `binding + 0x38`
  table into `owner + 0xE0` / `owner + 0xE4` and rebases each pointer by the
  blob base.
- `FUN_001a45e8(owner, mask)` iterates those copied entries as `8`-byte
  `[mask, pointer]` pairs and sets `*pointer |= 0x8000` on each matching
  `ushort`. This is a deferred flag-patch table, not image source data.
- `FUN_001db0f0(...)` allocates an association node, chains it off the matched
  primary zone record at `record + 0x34`, and patches a `0x50`-stride table so
  each entry's `+0x08` field points at the resolved `0x40` entry instead of the
  original key.
- `FUN_001db778(...)` is only a pointer relocation helper for that `0x50`-stride
  table (`entry + 0x0C += blob_base`).

This is the key conclusion from the new decompilation: the `FUN_001e5368(...)`
object is a render/material binding runtime owned by the `DAT_004b4e80` class.
It explains late checksum-to-record rebinding, per-record setup chains, and
deferred state/flag patching. It does **not** expose or construct the concrete
prepared pixel/CLUT source object consumed by `FUN_0019cd48(...)`.

### Phase 23-24 Owner / Vtable Findings

Targeted decompilation in
`historical analysis artifact phase23_owner_variants.c`,
`historical analysis artifact phase24_owner_slots.c`, and
`historical analysis artifact vtable_004b4170.txt`
changes how the owner/image relationship should be interpreted:

- `DAT_004b41c8` is **not** a separate unrelated vtable. It is the interior
  tail of `DAT_004b4170`:
  - `0x004b41c8 = 0x004b4170 + 0x58`
  - the table alternates `[this_adjust, function]` pairs
  - image-wrapper instances use the sub-vtable pointer at `0x004b41c8`
  - owner instances use the full table at `0x004b4170`
- The full owner table already exposes the raw image load/decode methods in its
  inherited tail:
  - `+0x84 -> FUN_0019c4c8`
  - `+0x8C -> FUN_0019c538`
  - `+0x94 -> FUN_0019c5b0`
  - `+0xBC -> FUN_0019cd48`
- The owner-only front of the table contains constructors/factories for child
  image wrappers:
  - `+0x14 -> FUN_001a0540` -> allocate wrapper -> `FUN_00164b90`
  - `+0x1C -> FUN_001a05c0` -> allocate wrapper -> `FUN_00164c78`
  - `+0x24 -> FUN_001a06c8` -> allocate wrapper -> `FUN_00164cb0`
  - `+0x34 -> FUN_001a0780` -> release a child image object through its own
    destructor slot

The cached owner constructors are now clearer too:

- `FUN_0016ab20` -> `FUN_001a09c0` -> `FUN_001a0188` creates/caches an empty
  owner object by checksum key.
- `FUN_0016ac20` -> `FUN_001a0918` -> `FUN_001a01d8` creates/caches an owner
  object keyed by `param_1 + param_5`.
- `FUN_0016ad60` -> `FUN_001a0890` -> `FUN_001a0280` is the previously known
  file/blob-backed owner constructor path.

One caller of that middle path is now identified:

- `FUN_0025b550(...)` obtains a key/checksum through a callback at `object+0x18`
  slot `+0x24`, calls `FUN_0016ac20(...)` with additional parameters from
  `object->0x0C + 0x90..0x9C`, then stores the resulting owner through another
  callback at slot `+0x64`.

Implication: the "zone owner" and "image wrapper" paths are not disjoint class
families. They are the same inheritance tree, with `DAT_004b41c8` representing
the inherited image-interface sub-vtable inside the larger owner class. This
means the missing bridge is less likely to be a totally separate object type.
The more likely remaining question is which zone-specific call path actually
invokes the inherited image load/decode slots on this owner family, or how the
record-backed mode transitions into that inherited raw-image mode before
`FUN_0019cd48(...)` runs.

### Phase 25-26 Image I/O Findings

Targeted decompilation in
`historical analysis artifact phase25_generic_image_io.c`,
`historical analysis artifact phase26_vtable_fallbacks.c`, and
`historical analysis artifact vtable_004b41c8_ext.txt`
clarifies the higher-level image read/write API around the sub-vtable at
`DAT_004b41c8`:

- `FUN_00169018(obj, dst_rgba, x, y, w, h)` is a generic RGBA read helper.
  - It first asks the object for a direct RGBA buffer through slot `+0x1C`.
  - If that buffer exists, it copies rows directly.
  - If not, it falls back to sub-vtable slot `+0x134 -> FUN_0019def0(...)`.
- `FUN_00169208(obj, src_rgba, x, y, w, h)` is the symmetric write helper.
  - If a direct RGBA buffer exists, it copies rows directly.
  - Otherwise it falls back to slot `+0x13C -> FUN_0019e100(...)`.

The newly dumped sub-vtable tail is:

- `+0x114 -> FUN_0019fcc0`
- `+0x11C -> FUN_0019e700`
- `+0x124 -> FUN_0019eb68`
- `+0x12C -> FUN_0019f0d0`
- `+0x134 -> FUN_0019def0`
- `+0x13C -> FUN_0019e100`
- `+0x144 -> FUN_0019e390`
- `+0x14C -> FUN_0019e640`

What those fallbacks actually do:

- `FUN_0019def0(...)` reads a rectangular region from paletted source data into
  RGBA32 output.
  - it uses `inner + 0x14` as the pixel-index buffer
  - it uses `inner + 0x18` as the CLUT
  - for `PSMT8` it applies the CSM1 table `DAT_005ad180`
- `FUN_0019e100(...)` writes RGBA32 data back into paletted source data.
  - it quantizes each RGBA color through `FUN_0019d440(...)`
  - it writes indices back into `inner + 0x14`
- `FUN_0019e390(...)` and `FUN_0019e640(...)` are region/single-pixel variants
  of that same paletted write path.
- `FUN_0019e700(...)`, `FUN_0019eb68(...)`, and `FUN_0019f0d0(...)` operate on
  palette data and palette-space transforms for the same paletted raw-image
  mode.

The important negative result is that these functions still do **not** support
record-backed zone wrappers directly:

- like `FUN_0019cd48(...)`, they resolve the inner object only through raw-image
  mode (`flags & 1`) or decoded-buffer mode (`flags & 4`)
- they do not use the record-backed fallback (`flags & 2`) that appears in
  metadata getters such as `FUN_0019f870(...)`, `FUN_0019fa98(...)`, and
  `FUN_0019fb60(...)`

Implication: even the generic image read/write API assumes the zone texture has
already been promoted from the lightweight record-backed wrapper into a real
raw-image wrapper before any pixel access occurs. That makes the missing
promotion/factory call more central, not less. The remaining decomp target is
still the zone-specific caller that invokes the inherited child-image factory
slots (`+0x2C/+0x34/+0x3C` on `DAT_004b41c8`, or the corresponding owner-side
factory entries on `DAT_004b4170`) to produce the raw-image object consumed by
all of these pixel APIs.

### Phase 27-29: Owner Slot + Raw-Constructor Results

The next decomp passes closed off most of the remaining "hidden promotion"
theory:

- `FUN_0016a010(...)` is just owner hash insertion.
  - it hashes the child by checksum
  - inserts it into the owner child table
  - immediately dispatches owner vtable slot `+0x3C`
- The previously unresolved owner-side entries around that slot are not real
  logic:
  - `0x001a0778` is `jr ra; move v0, zero` (return `0`)
  - `0x001a07b8` is `jr ra; nop`
  - `0x001a07c0` is `jr ra; move v0, zero`
- The analogous image-side "NO FUNCTION FOUND" slots around `0x00169738` /
  `0x00169740` are also trivial `jr ra` stubs.

That means the owner insertion hook is **not** a hidden promotion path.

The raw-image constructor graph also turned out to be closed:

- `FUN_001e5cc8(...)` is only called by `FUN_0019c4c8(...)`
- `FUN_001e5dc8(...)` is only called by `FUN_0019c538(...)`
- `FUN_001e5f98(...)` is only called by `FUN_0019c5b0(...)`
- `FUN_001e6030(...)` is only reached through `FUN_0019bbd8(...)` clone/copy
  helpers

No analyzed zone-specific caller feeds record-backed wrappers into those
constructors.

Combined with the earlier findings:

- record-backed wrappers created by `FUN_001a0368(...)` only expose metadata
  fallback through functions like `FUN_0019f870(...)`, `FUN_0019fa98(...)`, and
  `FUN_0019fb60(...)`
- the actual pixel APIs (`FUN_0019cd48(...)`, `FUN_00169018(...)`,
  `FUN_00169208(...)`, `FUN_0019def0(...)`, `FUN_0019e100(...)`) still require
  a concrete raw-image object

Current interpretation:

- there is no discovered lazy CPU-side promotion from a zone record wrapper into
  the raw-image object family
- the zone runtime path is therefore more likely to stay in GS/upload/setup
  structures rather than ever entering the generic CPU paletted-image decode
  path used by standalone `.img.ps2` images

Practical consequence for the decoder effort:

- decomp evidence now points away from "find the missing wrapper promotion call"
- the more promising remaining targets are the GS/upload state path:
  - `FUN_001cfb58(...)` consumers and the structures it patches
  - the render/setup object path behind `FUN_001d4248(...)`
  - any place where zone records are resolved to `TEX0` / upload state without
    first materializing a raw-image object

### PSMT4 Upload as PSMCT32

PSMT4 textures are uploaded to GS VRAM using PSMCT32 format (the GIF IMAGE
transfer uses DPSM=PSMCT32). The raw nibble-packed pixel data is written into
VRAM in PSMCT32 page layout, but the GS reads it as PSMT4 during rendering.

For tooling, the practical decode strategy is:
1. Prefer the prepared source-slot path when file-backed header data is available.
2. Use upload replay into simulated GS VRAM as fallback when the source-slot path
   cannot resolve an entry or when only uploads are available.

### CSM1 CLUT Swizzle

For PSMT8 textures, the CLUT uses CSM1 mode. The CSM1 lookup table
(DAT_005ad180, 256 bytes) is in BSS and runtime-initialized. The standard
PS2 CSM1 swizzle pattern must be applied to remap 8-bit indices to correct
palette entries.

### PSMCT32 (No Palette) Edge Case

For PSMCT32 textures (PSM=0x00), `data_offset=0` and `pal_bytes=0`, but `cumul_off`
points directly to the pixel data. The formula `cumul_off = data_offset + pal_bytes`
does NOT hold for these records (0 + 0 ≠ actual cumul_off). Always use `cumul_off`
for pixel data location when reasoning about blob layout, even though the public
decoder should prefer the source-slot path when file-backed data is available.

### Shared Data Blocks

137 records share data with other records (95 unique data_offsets used by
multiple records). These are typically duplicate textures used in different
contexts within the zone (different group_checksum but same visual data).

### Phase 33-36: GS Draw Dispatch Classification

The `FUN_001d0588(...)` GS/runtime branch now splits more clearly:

- `FUN_001d09e8(...)` is a piece-draw submission path, not a texture decode path.
  - It is only called from `FUN_001d0588(...)`.
  - It performs frustum / clip-style rejection and calls `FUN_001cfa30(...)`
    and `FUN_001d2c60(...)` for additional visibility rejection.
  - It chooses a child piece list, then dispatches either:
    - `FUN_001d1f58(...)` for the normal piece path
    - `FUN_001d10a0(...)` when the stream is already in the alternate buffered
      mode (`param_3 & 0x10`)
- `FUN_001d10a0(...)` and `FUN_001d1f58(...)` are the real piece emitters.
  - Both build packet data into `DAT_005ad058`.
  - Both share the same optional state helpers:
    - `FUN_001d6138(...)`
    - `FUN_001d6618(...)`
    - `FUN_001d6b50(...)`
  - Both enqueue draw payloads and optional extra words rather than decoding
    CPU-side texture data.
- `FUN_001d2c60(...)` is a generic visibility / bucket rejection helper.
  - It is used by `FUN_001d09e8(...)`, `FUN_001d1f58(...)`,
    `FUN_001d2ee0(...)`, and `FUN_001e1be0(...)`.
  - It updates global per-bucket counters, which fits render culling rather than
    texture preparation.

`FUN_001d3388(...)` is a separate recursive state/material submission branch:

- It is called from `FUN_001d0588(...)` when the object has the `0x20` bit set.
- If the node is not a leaf (`flags & 2` clear), it only recurses through child
  nodes.
- On leaf nodes (`flags & 2` set), it:
  - optionally runs `FUN_001d6138(...)`
  - optionally runs `FUN_001d6618(...)`
  - optionally runs `FUN_001d6b50(...)`
  - emits a compact packet using the already-selected stream
- `FUN_001d6138(...)` writes interpolated colors into multiple destination
  pointers from a timeline/keyframe table. This is render-state animation.
- `FUN_001d6618(...)` picks a time-varying material entry and queues slot
  updates through `FUN_001ba4f0(...)`.
  - `FUN_001ba4f0(...)` just appends `(material, slot, value)` triplets to a
    small global queue.
  - The updated slots are `6`, `0x34`, and `0x36`, which is material/register
    state, not pixel data transformation.
- `FUN_001d6b50(...)` computes animated UV offsets and writes them into
  `DAT_00550fb0/00550fb2`, later emitted as extra packet words.

Implication:

- This branch is mixed render packet building, but still not the missing source
  data decode.
- `FUN_001d09e8(...)` is geometry/piece submission with visibility and stream
  setup.
- `FUN_001d3388(...)` is recursive material/state submission with animated
  color/material/UV helpers.
- Neither path materializes a CPU-side raw image object or transforms zone blob
  pixel bytes into the `+0x14/+0x18` raw-image buffers used by
  `FUN_0019cd48(...)`.

### GS Packet Helper Classification

The surrounding packet helpers are also now classified:

- `FUN_001c90e8(stream)` is a generic stream/template copier.
  - It flushes back to neutral state if `DAT_005ad380 != 0`.
  - It copies `(*stream + 2)` qwords from the template stream into the active
    packet buffer at `DAT_005ad058`.
  - It returns the starting packet pointer it just copied into.
- `FUN_001c9b80(packet)` resets the default transform packet state.
  - It reloads the baseline matrices/vectors from `_DAT_005510a0..._005510d0`
    and derived constants into `PTR_DAT_0049aaf8`.
  - It writes transformed matrix blocks to `packet + 0x20/+0x30/+0x40/+0x50`.
- `FUN_001c99e0(packet, tag)` is just `FUN_001c9a20(packet, tag, 0)`.
- `FUN_001c9fd0(packet, state)` is `FUN_001c9a20(packet, 8, 0)` plus:
  - `packet[1] = state`
  - `FUN_001c9658(packet)`

Current interpretation:

- These helpers are generic stream/matrix/state setup, not pixel decode.
- They support the GS submission path used by `FUN_001d0588(...)`,
  `FUN_001d09e8(...)`, `FUN_001d10a0(...)`, and `FUN_001d1f58(...)`.
- This still reinforces the same conclusion: the analyzed runtime branch
  consumes already-built GS render/setup state rather than materializing the
  CPU-side `+0x14/+0x18` raw-image buffers used by `FUN_0019cd48(...)`.

### Phase 37-40: Packet State + Material Slot Tables

The common packet-state helpers and the queued material-update path are now
clearer:

- `FUN_001c9a20(packet, set_bits, clear_bits)` is a packet layout/state mutator.
  - It updates the packet's mode byte.
  - It recomputes the packet size with `FUN_001c9aa8(...)`.
  - If the size changes, it advances `DAT_005ad058`, updates the stored size
    byte, and rebuilds the packet through `FUN_001c9b30(...)`.
- `FUN_001c9a00(packet, clear_bits)` is just `FUN_001c9a20(packet, 0,
  clear_bits)`.
- `FUN_001c9658(packet)` builds per-packet transformed vectors / matrices into
  offsets `+0x90..+0xf0`. This is view/state preparation, not texture decode.
- `FUN_001c9e20(packet, state_obj)` enables packet bit `4` and writes animated
  per-packet parameters at offsets like `+0x60..+0x84`. This looks like
  per-draw effect state, not raw texture payload handling.
- `FUN_001ca018(packet, short_a, short_b)` enables another packet mode and
  writes transformed vectors at `+0x60..+0x90`. Again, render-state setup.

The queued material-slot updates from `FUN_001ba4f0(...)` are consumed here:

- `FUN_001ba540()` iterates the queued `(material, slot, value)` triplets and
  applies each one through `FUN_001ba5b8(...)`, then resets `DAT_0049979c = 0`.
- `FUN_001c4040()` is the sole caller of `FUN_001ba540(...)` in the analyzed
  code. It is a frame/render maintenance path that runs a cluster of render
  update functions, then flushes queued material-slot changes once.
- `FUN_001ba5b8(material_blob, slot_id, value64)` scans the material blob until
  it finds a packet block whose opcode family matches `0x68`.
  - Inside that block it treats the contents as a counted table of repeating
    entries:
    - `value_lo`
    - `value_hi`
    - `slot_id`
  - When the requested `slot_id` matches, it overwrites the 64-bit value in
    place.
- `FUN_001c3c10(...)` is the generic packet walker used by `FUN_001ba5b8(...)`
  to skip variable-sized packet blocks until that `0x68` block is found.

Current interpretation:

- Zone/runtime materials are carrying prebuilt packet blobs with embedded,
  slot-indexed 64-bit parameter tables.
- The animated updates from functions like `FUN_001d6618(...)` are patching
  those prebuilt tables in place at render time.
- This still is not CPU-side texture decode, but it is the first concrete path
  showing how runtime material/GS state is represented in loaded zone objects.

Practical consequence:

- If the public decoder eventually needs real per-material `TEX0` / related GS
  state from the zone runtime rather than from the simpler upload/header path,
  the next likely extraction target is this prebuilt material packet format:
  identify which slot IDs correspond to texture registers and how
  `FUN_001cfb58(...)` rebases or installs those blobs into the loaded object.

### Phase 41: Register-Level Interpretation

The packet emitter helpers provide a strong register-level anchor:

- `FUN_001c3648(kind, reg_value_ptr, page_a, page_b)` is a narrow GS register
  bit-packer.
  - `kind == 6` updates the low 14 bits of `u64[0]` and, when `page_b >= 0`,
    also writes a 14-bit field into `u64[1] << 5`.
  - `kind == 0x16` writes a 14-bit field into `u64[1] << 5`.
  - `kind == 0x50` writes a 14-bit field into the low bits of `u64[1]`.
- The already-established `FUN_001e7638(...)` usage confirms this is patching
  GS texture-page fields in prebuilt register values.
  - In practice, the `0x50` case is being used to patch the page field in the
    TEX0-style payload stored at `obj+0x40` / `obj+0x44`.

This makes the queued slot IDs much more meaningful:

- The runtime animation path `FUN_001d6618(...)` queues updates for slots:
  - `6`
  - `0x34`
  - `0x36`
- Inference from the slot numbers and the GS packet model:
  - `6` matches `TEX0_1`
  - `0x34` matches `MIPTBP1_1`
  - `0x36` matches `MIPTBP2_1`

This is an inference from the decompiled slot IDs lining up with standard GS
register numbering, but it is a strong one.

The `+0x44` install path in `FUN_001cfb58(...)` is also sharper now:

- `FUN_001d3d20(obj)` is trivial, but the important correction is that it
  returns `*(obj + 0x2c)`, not `obj + 0x2c`.
- `FUN_001cfb58(...)` uses that returned structure as a lookup table root:
  - `*(root + 0x3c)` = count
  - `*(root + 0x40)` = table base
- When the object's `+0x44` blob is present and marked with the sign bit,
  `FUN_001cfb58(...)`:
  - rebases the blob and its internal pointer at `+0x10`
  - walks each referenced keyframe/value entry
  - replaces each keyframe's integer texture-state ID with a canonical pointer
    into the table at `*(FUN_001d3d20(obj) + 0x40)`
  - stores the first resolved entry pointer at blob `+0x18`
  - calls `FUN_001d7830(obj, 1)`, which just marks the `+0x44` blob dirty

Current interpretation:

- The loaded zone object already owns a canonical texture-state table rooted at
  `*(obj + 0x2c)`, with entries at `root + 0x40`.
- The `+0x44` blob is not raw pixel data or a GS packet stream. It is a small
  selector/keyframe object that is patched to point at canonical `0x40` entry
  records.
- The animated runtime path then selects one resolved `0x40` entry and patches
  GS register values inside prebuilt material blobs using slot IDs that appear
  to be actual GS texture register numbers.

Practical next target:

- Decompile the writer/allocation path for the canonical `0x40` entries at
  `root + 0x40`, especially the fields at `+0x28/+0x30`.
- The table is now the most promising runtime source not only for authoritative
  `TEX0` / mip register state, but also for the prepared CPU-side texture
  payloads that the generic image path appears to consume later.

### Phase 43: Canonical `0x40` Entry Layout

Cross-reading
`historical analysis artifact phase3b_image_load_methods.c`,
`historical analysis artifact image_accessors_004b41c8.c`,
`historical analysis artifact image_extra_004b41c8.c`, and
`historical analysis artifact phase34_submit_helpers.c`
sharpens the canonical secondary-entry structure considerably:

- `entry + 0x10` is authoritative `TEX0`.
  - `FUN_001ea2a8(...)` and `FUN_001ea2c8(...)` derive `1 << TW` and
    `1 << TH` from it.
  - `FUN_001ea2e8(...)` derives pixel depth / storage mode from it.
  - `FUN_001ea350(...)` derives CLUT format from it.
- `entry + 0x08` is mip-count-minus-one.
  - `FUN_001ea3a8(entry)` returns `*(char *)(entry + 8) + 1`.
- `entry + 0x0c` is a per-mip flag word.
  - `FUN_001ea3b8(...)` scans bit patterns there to decide whether any mip
    level needs special copy/transform handling.
- `entry + 0x18` and `entry + 0x20` are additional 64-bit texture-state
  payloads, not CPU-side pointers.
  - `FUN_001d6618(...)` queues `entry + 0x10`, `entry + 0x18`, and
    `entry + 0x20` into material slots `6`, `0x34`, and `0x36`.
  - Strong behavioral inference:
    - slot `6` = `TEX0_1`
    - slot `0x34` = `MIPTBP1_1`
    - slot `0x36` = `MIPTBP2_1`
- `entry + 0x28` is an entry-owned pixel / mip buffer pointer.
  - `FUN_001ea488(...)` uses the entry's `TEX0`, mip count, and flag word to
    copy or transform mip payloads into that buffer.
- `entry + 0x30` is an entry-owned CLUT / auxiliary buffer pointer used by the
  record-backed reconcile path.
  - `FUN_0019c620(...)` uses `entry + 0x30` as the destination for palette
    copy/conversion after populating the entry's pixel buffer.

This is the first decomp-backed view showing that one canonical `0x40` entry
appears to carry both:

- GS texture-state qwords at `+0x10/+0x18/+0x20`
- CPU-side prepared payload pointers at `+0x28/+0x30`

That makes the unresolved allocation / fill path for `entry + 0x28` and
`entry + 0x30` the highest-value next target, because these entry-owned buffers
are clearly part of the runtime prepared-image path even though the raw
extracted public file still does not map onto the owner blob directly.

### Phase 44: PSMT4 Mip Transform Branch

Targeted decompilation in
`historical analysis artifact phase44_mip_transform_helpers.c`
clarifies the special per-mip transform path reached from `FUN_001ea488(...)`
when the canonical `0x40` entry carries per-level transform flags in
`entry + 0x0c`.

The control split in `FUN_001ea488(...)` is:

- no special flag for this mip level:
  - raw `memcpy` from the caller-provided source buffer into `entry + 0x28`
- `0x02000000 << level` set:
  - `PSMT4` (`bpp == 4`) -> `FUN_001c8218(...)`
  - `PSMT8` (`bpp == 8`) -> `FUN_001c8678(...)`
- `0x00040000 << level` set:
  - `PSMT4` only -> `FUN_001c8b28(...)`

Structurally, the helpers are not generic decode functions. They are layout /
repack stages applied to already-prepared mip payload bytes:

- `FUN_001c8218(table_base, w, h, dst, src)`
  - validates power-of-two dimensions up to `0x400`
  - gathers source data into large stack-resident scratch blocks in `0x40`
    byte row chunks
  - runs a unique inner shuffle helper:
    - `FUN_001c7438(table_base, 0x80, 0x80, scratch)`
  - scatters the shuffled result back out in `0x100` byte spans
  - finishes with a unique finalizer:
    - `FUN_001c81b8(table_base, w, h)`
  - interpretation: a PSMT4-specific tile/block reorder path operating on
    `128 x 128` macroblocks

- `FUN_001c8678(table_base, w, h, dst, src)`
  - mirrors the same structure for the `PSMT8` branch
  - uses:
    - `FUN_001c7af8(table_base, 0x80, 0x40, scratch)`
    - `FUN_001c8ac8(table_base, w, h)`
  - interpretation: the 8-bit analogue of the same transform family, operating
    on `128 x 64` macroblocks

- `FUN_001c8b28(table_base, w, h, dst, src)`
  - validates power-of-two dimensions
  - computes both:
    - full-size `PSMT4` geometry via `FUN_001c69a8(..., 0x14)`
    - half-size `PSMCT16` geometry via `FUN_001c69a8(..., 2)`
  - compares page counts via `FUN_001c6a48(...)`
  - allocates a global staging buffer:
    - `DAT_0054a580`
    - `DAT_004997d0`
  - writes the source through:
    - `FUN_001c6fa0(...)`
  - reads it back through:
    - `FUN_001c6ca8(...)`
  - finishes with:
    - `FUN_001c8ac8(table_base, w, h)`
  - interpretation: an alternate PSMT4 repack route that goes through a shared
    staging/page-mapping path rather than the stack-local tile shuffler used by
    `FUN_001c8218(...)`

Practical conclusion:

- `FUN_001c8218(...)` and `FUN_001c8b28(...)` are two distinct structural
  transforms for flagged `PSMT4` mip levels.
- `FUN_001c8218(...)` is the "stack scratch + tile shuffle" branch.
- `FUN_001c8b28(...)` is the "global staging + page-mapping" branch.
- Both are post-source layout transforms, not the missing high-level zone blob
  unpacker.

Best next decomp targets:

1. `FUN_001c7438`
   - unique inner shuffle for the `FUN_001c8218(...)` PSMT4 branch
2. `FUN_001c81b8`
   - unique finalizer for the same branch
3. `FUN_001c6fa0`
   - source-side staging writer for `FUN_001c8b28(...)`
4. `FUN_001c6ca8`
   - destination-side staging reader for `FUN_001c8b28(...)`
5. `FUN_001c8ac8`
   - common finalizer shared by `FUN_001c8678(...)` and `FUN_001c8b28(...)`
6. `FUN_001c7af8`
   - lower-priority 8-bit analogue, useful as a simpler comparison case once
     the PSMT4-unique leafs are known

### Phase 45: Transform Subhelpers

Targeted decompilation in
`historical analysis artifact phase45_transform_subhelpers.c`
closed most of that helper list.

- `FUN_001c7438(table_base, w, h, src, dst)`
  - walks `128 x 128` PSMT4 macroblocks
  - uses:
    - `DAT_0049a5d8` as the source-page block order
    - `DAT_0049a658` .. `DAT_0049a674` as the output block placement tables
  - calls `FUN_001c71f0(...)` on each `0x80`-byte working block
  - interpretation: page/block walker around a smaller inner permutation kernel

- `FUN_001c7af8(table_base, w, h, src, dst)`
  - mirrors the same structure for `128 x 64` PSMT8 macroblocks
  - uses:
    - `DAT_0049a6d8` as the source-page block order
    - `DAT_0049a758` .. `DAT_0049a774` as the output block placement tables
  - calls `FUN_001c7368(...)` on each `0x40`-byte working block

- `FUN_001c81b8(table_base, w, h)`
  - is not a transform
  - it is a sparse `10 x 10` dimension-eligibility table lookup rooted at
    `DAT_0049a7d8`

- `FUN_001c8ac8(table_base, w, h)`
  - is likewise only a sparse `10 x 10` lookup rooted at `DAT_0049a968`

- `FUN_001c6fa0(...)`
  - writes 4-bit source data into the global staging buffer `DAT_0054a580`
  - uses `DAT_00499958`, `DAT_004999d8`, and `DAT_00499dd8` to choose byte
    lane, nibble lane, and destination offset inside the staged page layout

- `FUN_001c6ca8(...)`
  - reads back `PSMCT16` words from the same staging buffer
  - uses `DAT_004997d8`, `DAT_00499858`, and `DAT_004998d8`

Practical conclusion:

- `FUN_001c8218(...)` and `FUN_001c8678(...)` are page/block walkers around
  fixed inner permutation kernels.
- `FUN_001c8b28(...)` is an explicit table-driven staging/repack path, not an
  opaque special case.
- The remaining unknown was narrowed to the inner kernels plus the static data
  tables they consume.

### Phase 46: Inner Block Kernels

Targeted decompilation in
`historical analysis artifact phase46_block_kernels.c`
shows the core block transforms are simple lookup-table permutations.

- `FUN_001c71f0(...)`
  - operates on `0x40`-byte PSMT4 source slices
  - reads 4 source nibbles at a time from offsets in `DAT_00499fd8`
  - packs them into 2 output bytes
  - there is no hidden arithmetic swizzle beyond table lookup + nibble packing

- `FUN_001c7368(...)`
  - operates on `0x40`-byte PSMT8 source slices
  - reads 4 source bytes at a time from offsets in `DAT_0049a3d8`
  - writes them directly into the output block

This is the strongest decomp result so far for the prepared-source path:
the flagged per-mip transforms are not mysterious runtime decode logic, they
are fixed static permutations driven by ELF-resident tables.

### Phase 47: Extracted Static Transform Tables

The relevant static tables were recovered from the ELF with the reusable
[`DumpMemoryRange.java`](../../tools/reverse-engineering/ghidra/DumpMemoryRange.java)
helper. Their durable values and roles are summarized below.

Important recovered table families:

- `DAT_00499fd8`
  - 1024-byte `PSMT4` block LUT used by `FUN_001c71f0(...)`
- `DAT_0049a3d8`
  - 512-byte `PSMT8` block LUT used by `FUN_001c7368(...)`
- `DAT_0049a5d8`
  - 32-entry PSMT4 page block order used by `FUN_001c7438(...)`
- `DAT_0049a658`
  - 32-entry PSMCT32 output block order for the same walker
- `DAT_0049a6d8`
  - 32-entry PSMT8 page block order used by `FUN_001c7af8(...)`
- `DAT_0049a758`
  - 32-entry PSMCT32 output block order for the 8-bit walker
- `DAT_0049a7d8`
  - sparse `10 x 10` finalizer / eligibility table for `FUN_001c81b8(...)`
- `DAT_0049a968`
  - sparse `10 x 10` finalizer / eligibility table for `FUN_001c8ac8(...)`
- `DAT_004997d8`, `DAT_00499858`, `DAT_004998d8`,
  `DAT_00499958`, `DAT_004999d8`, `DAT_00499dd8`
  - alternate PSMT4 staging/repack tables used by
    `FUN_001c6fa0(...)` / `FUN_001c6ca8(...)`

Two practical comparisons now stand out:

- The extracted `PSMT4` family matches the standard Conv4-to-32 tables:
  - `DAT_0049a5d8` matches the `PSMT4` page block order
  - `DAT_0049a658` matches the `PSMCT32` destination block order
  - `DAT_00499fd8` matches the `PSMT4` block LUT
  - `DAT_0049a7d8` decodes to the same sparse `10 x 10` eligibility matrix as
    the current `CanConv4to32Table`

- The extracted `PSMT8` family partly matches and partly diverges:
  - `DAT_0049a6d8`, `DAT_0049a758`, and `DAT_0049a3d8` match the standard
    Conv8-to-32 page/block layout family
  - but `DAT_0049a968` is **not** the same sparse `10 x 10` matrix as the
    current shared Conv4/Conv8 eligibility table

Practical implication for the decoder:

- The standard Conv4-to-32 tables in the current code appear decomp-faithful.
- The current Conv8-to-32 eligibility gate is likely wrong if it reuses the
  Conv4 matrix.
- More importantly for the widespread 4-bit corruption, the current decoder's
  heuristic `layout_mode` transforms are still not the same thing as the
  decompiled `FUN_001c8b28(...)` staging path.

The highest-value implementation target is now much narrower:

1. Replace the guessed `layout_mode`-specific PSMT4 block/tile heuristics with
   an exact port of the `FUN_001c8b28(...)` staging-table path for the
   `0x00040000 << level` branch.
2. Audit the `PSMT8` conversion gate against `DAT_0049a968` instead of sharing
   the Conv4 eligibility matrix.

### Phase 48: Current Decoder Comparison

Comparing the extracted Phase 47 tables against the current local decoder code
sharpens what is and is not still suspect.

- The standard Conv4-to-32 implementation is largely decomp-faithful:
  - the local `PSMT4` page block order matches `DAT_0049a5d8`
  - the local destination block order matches `DAT_0049a658`
  - the local `PSMT4` block LUT matches `DAT_00499fd8`
  - the local sparse Conv4 eligibility table matches `DAT_0049a7d8`

- The standard Conv8-to-32 implementation is only partly faithful:
  - the local `PSMT8` page/block tables match `DAT_0049a6d8`,
    `DAT_0049a758`, and `DAT_0049a3d8`
  - but the local Conv8 eligibility gate still reuses the Conv4 matrix, while
    the decompiled `FUN_001c8ac8(...)` finalizer table is `DAT_0049a968`

- The current zone-header layout heuristics are still ahead of the decomp:
  - the prepared-source path we have decompiled so far branches on:
    - `0x02000000 << level` -> `FUN_001c8218(...)` / `FUN_001c8678(...)`
    - `0x00040000 << level` -> `FUN_001c8b28(...)`
  - there is still no direct decomp evidence in that path that the low
    `layout_mode` bits (`...01`, `...05`) drive the current custom block/tile
    heuristics

Practical implication:

- The remaining decoder risk is now less about the standard Conv4 tables and
  more about:
  - using the wrong Conv8 eligibility matrix
  - applying heuristic low-bit layout transforms that are not yet backed by a
    decompiled runtime consumer
  - not yet porting the exact decompiled branch behavior for the flagged
    `PSMT4` prepared-source paths

### Phase 49: Conv4-to-16 Alignment and Low-Bit Negative Result

Further comparison against the current local decoder code closes one important
branch and weakens another.

#### 1. `FUN_001c8b28(...)` aligns with the existing local Conv4-to-16 family

Cross-reading
`historical analysis artifact phase45_transform_subhelpers.c`,
`historical analysis artifact phase47_transform_tables.txt`, and
`src/NeversoftMultitool/Core/Formats/Psx/Ps2TexSwizzleVramMappingBuilder.cs`
shows that the alternate flagged `PSMT4` branch is not a new transform family.

The local Conv4-to-16 builder already matches the decompiled table families:

- `Block16` matches `DAT_004997d8`
- `ColumnWord16` matches `DAT_00499858`
- `ColumnHalf16` matches `DAT_004998d8`
- `Block4` matches `DAT_00499958`
- `ColumnWord4` matches `DAT_004999d8`
- `ColumnByte4` matches `DAT_00499dd8`
- `CanConv4to16Table` matches `DAT_0049a968`

That means:

- `FUN_001c6fa0(...)` / `FUN_001c6ca8(...)` are structurally the same
  Conv4-to-16 staging/readback family already represented in local code
- `FUN_001c8ac8(...)` is consistent with the local Conv4-to-16 size gate
- `FUN_001c8b28(...)` is much closer to the existing
  `UnswizzlePsmt4WithUploadDpsm(..., PSMCT16)` path than to the custom
  header-layout heuristics

This materially weakens the earlier hypothesis that the missing fix is
"implement the alternate flagged `PSMT4` branch from scratch." The core table
family for that branch already exists locally.

#### 2. The standard page/block swizzle builders are mostly correct

Comparing
`src/NeversoftMultitool/Core/Formats/Psx/Ps2TexSwizzlePageMappingBuilder.cs`
against the Phase 47 table dump shows:

- the local `PSMT4` and `PSMT8` page/block LUTs match the decompiled tables
- the local Conv4-to-32 size gate matches `DAT_0049a7d8`
- the local Conv8-to-32 size gate still does **not** match exactly, because it
  reuses the Conv4 matrix instead of the decompiled `DAT_0049a968` table

So the swizzle/page-mapping core is mostly decomp-faithful already.

#### 3. The low `layout_mode` bits still have no decomp-backed consumer here

Searching the existing canonical-entry image-load path did **not** reveal a
decompiled consumer that interprets the low bits of `entry + 0x0c` to
distinguish `0x02000001` from `0x02000005`.

In the decompiled prepared-source path:

- `FUN_001ea3b8(...)` only scans the shifted per-mip flag families
- `FUN_001ea488(...)` only dispatches on:
  - `0x02000000 << mip`
  - `0x00040000 << mip`

No direct `& 1`, `& 4`, `== 1`, or `== 5` style interpretation of the low
layout bits has been found in that path so far.

Practical implication:

- The current heuristic transforms in
  `src/NeversoftMultitool/Core/Formats/Ps2Scene/ThawZoneTexHeaderLayoutSupport.cs`
  remain the least decomp-backed part of the decoder.
- The next code fix should likely target those heuristics first, rather than
  replacing the already-matching Conv4/Conv4-to-16 table families.

### Phase 50: The Canonical 0x40 Entry Is Serialized and Wrapped Directly

Existing decomp now shows that the canonical prepared-source entry is already
serialized in the loaded owner blob and exposed directly through record-backed
wrappers.

Key evidence:

- `FUN_001e9ac0(...)` rebases each secondary 0x40 entry in-place during owner
  load.
  - If `entry + 0x2C != 0`, it adds the blob's mip-data base to `entry + 0x28`.
  - If `entry + 0x34 != 0`, it adds the same base to `entry + 0x30`.
  - It always rebases `entry + 0x38` and then calls `FUN_001e9eb8(...)` on that
    same entry.
- `FUN_001a0368(...)` iterates the owner's secondary table, allocates one
  wrapper image per 0x40 entry, sets `wrapper.flags |= 2`, stores the entry
  pointer at `wrapper + 0x10`, and precomputes wrapper width/height from
  `FUN_001ea2a8(entry)` / `FUN_001ea2c8(entry)`.

That means the record-backed path is:

1. `.tex` blob is parsed by `FUN_001e9ac0(...)`
2. secondary 0x40 entries are rebased in-place
3. `FUN_001a0368(...)` wraps those rebased entries directly
4. consumers like `FUN_001ea2e8(...)`, `FUN_001ea350(...)`,
   `FUN_001ea3a8(...)`, `FUN_001ea3b8(...)`, and `FUN_001ea488(...)` operate
   on that same rebased 0x40 entry

This materially weakens the earlier "missing writer" / "late promotion"
hypothesis for `entry + 0x28/+0x30`. Those prepared-source pointers already
exist in the serialized owner blob; runtime load is rebasing them, not
inventing them later.

Practical implication:

- The strongest decomp-backed decoder direction is now to parse these canonical
  0x40 entries from the owner blob path itself, rather than infer prepared
  source layout from primary-record metadata or low-bit post-unswizzle
  heuristics.

### Phase 51: Two Different 0x40 Entry Families

Another old ambiguity is now resolved: the owner blob's prepared-source `0x40`
entries are **not** the same `0x40` entries later owned by the `.geom` binding
runtime.

Owner-side prepared-source `0x40` entries:

- loaded and rebased by `FUN_001e9ac0(...)`
- reached from each primary record through `primary + 0x40`
- wrapped directly by `FUN_001a0368(...)`
- consumed by `FUN_001ea2a8(...)`, `FUN_001ea2c8(...)`, `FUN_001ea2e8(...)`,
  `FUN_001ea350(...)`, `FUN_001ea3a8(...)`, `FUN_001ea3b8(...)`,
  `FUN_001ea488(...)`, and `FUN_0019c620(...)`

`.geom` binding-runtime `0x40` entries:

- allocated as part of the separate `FUN_001e5368(...)` object
- stored at `binding + 0x20`, with count at `binding + 0x1C`
- resolved by `FUN_001db060(...)`, which treats `entry + 0x20/+0x2C` as an
  unresolved `(texture_checksum, group_checksum)` pair
- rebound through `FUN_001ea0b8(...)`, which searches owner primary records and
  their child `0x40` entries, then overwrites `binding_entry + 0x20` with the
  matching owner secondary-entry pointer

This means the earlier shorthand:

- "`FUN_001d3d20(obj) + 0x40` is the canonical texture table"

was too strong. `FUN_001d3d20(obj) + 0x40` belongs to the `.geom` binding
runtime and references owner secondary entries indirectly; it is not the same
table that `FUN_001a0368(...)` wraps or that `FUN_001ea488(...)` consumes.

Practical implication:

- Decoder work should stay focused on the owner blob's rebased secondary `0x40`
  entries from `FUN_001e9ac0(...)`, not on the separate `.geom` binding table
  behind `FUN_001d3d20(...)`.

### Phase 52: The Extracted Public `.tex` File Does Not Match the Owner Blob Directly

The decompiled owner blob layout from `FUN_001e9ac0(...)` and the extracted
public `.tex` file currently parsed in this repo are now clearly different
formats, not just different interpretations of the same bytes.

Strongest evidence:

- `FUN_001e9ac0(...)` expects a fixed `0x10` header:
  - `+0x00` = `u16` secondary-count hint
  - `+0x02` = `u16` primary count
  - `+0x04` = data-section relocation offset
  - `+0x08` = secondary-table relocation offset
  - `+0x0C` = mip-table relocation offset
- After that header, `FUN_001e9ac0(...)` expects `primary_count * 0x50` bytes
  of primary records, then `secondary_count * 0x40` bytes of prepared-source
  entries.
- The current public parser does something fundamentally different:
  - `ThawZoneTexCoreDecoder.IsZoneTex(...)` does **not** read a fixed owner
    header; it discovers the format by scanning for the first GIF upload block
    and then walking backward across `0x40` records.
  - `ThawZoneTexHeaderParser.ParseHeaderEntries(...)` starts at offset `0x40`
    and parses flat `0x40` records until the first GIF block.
  - `ThawZoneTexFile.TryGetHeaderDataLayout(...)` then infers a trailing packed
    data section heuristically from `DataOffset + PaletteBytes + DataSize`.
- The sample extracted file bytes also do not fit the owner-blob header even at
  the first 16 bytes:
  - `0009BF70.tex @ 0x0000` = `C9 7B 87 09 E0 E5 BA AB B2 D4 54 3F 00 66 3F 78`
  - interpreted as the owner header, that would imply `primary_count = 0x0987`
    and huge nonsense relocation offsets, not the known Hollywood `990`
    textures.
- Skipping the first 10-byte public header does not rescue the match; the bytes
  at `0x000A` still do not form the decompiled owner-blob header.

Conclusion:

- The extracted public zone `.tex` file in this repo is **not** a direct dump of
  the owner blob consumed by `FUN_001e9ac0(...)`.
- This is more than a tiny outer wrapper mismatch. The body organization differs
  too: the repo file is organized around a discovered flat `0x40` record table
  plus GIF upload chain, while the owner blob is organized around a fixed `0x10`
  header with `0x50` primaries and `0x40` secondaries.

Practical implication:

- The remaining decoder work should not assume that parsing the public
  `data_offset/cumul_off/upload_off` records is equivalent to parsing the owner
  blob that the game code dereferences.
- The next high-value target is the build/extraction boundary between these two
  representations: either the packer path that produces the extracted public
  file, or the runtime path that turns this public representation into the owner
  blob later consumed by `FUN_001e9ac0(...)`.

### Phase 53: FUN_001e9eb8 Rebases Auxiliary Blocks, Not the CPU Mip Stream

The owner-side `0x40` entry layout is now sharper around the entry-owned
pixel/aux buffers and the nested table behind `FUN_001e9eb8(...)`.

What the CPU-side per-mip copy/repack path actually consumes:

- `FUN_001ea488(...)` uses:
  - `entry + 0x08` as `mip_count_minus_one`
  - `entry + 0x0C` as the per-mip transform/layout family bitfield
  - `entry + 0x10` as authoritative `TEX0` for width/height/bpp
  - `entry + 0x28` as the destination pixel/mip buffer
- It then walks mip levels by computed payload size:
  - `bytes = (width >> mip) * (height >> mip) * bpp / 8`
  - and copies/transforms from the caller-provided source buffer into
    `entry + 0x28` based on the high flag families in `entry + 0x0C`

What `FUN_001e9eb8(...)` does instead:

- It never touches `entry + 0x28`
- It takes `entry + 0x38` as a pointer to a chained `0x80`-stride table
- If `entry + 0x30 == 0`, it starts at `entry + 0x38 + 0x10` and processes
  `mip_count_minus_one + 1` blocks
- If `entry + 0x30 != 0`, it starts at `entry + 0x38 - 0x70` and processes
  `mip_count_minus_one + 2` blocks
- For each `0x80` block it only rebases `block + 0x04`:
  - `block_ptr = mip_table_base + (block_ptr & 0xFFFFF0)`
  - the low nibble is preserved as embedded small flags/alignment bits

Practical implication:

- `entry + 0x28` is an important decomp-backed runtime pixel/mip buffer, but
  the direct-copy arm of `FUN_001ea488(...)` does not prove it is the original
  serialized source of mip payload bytes.
- `entry + 0x38` is a different auxiliary table family that still matters at
  runtime, but `FUN_001e9eb8(...)` alone does not make it the CPU decode source.
- `entry + 0x30` is also now better interpreted as "presence of an extra
  prepared auxiliary/palette block" than as a selector for the main mip stream,
  because its only observable effect here is to add one leading `0x80` block to
  the rebasing loop.

### Phase 54: Raw Header Bytes Confirm the Public/Owner Mismatch

This is a narrower confirmation of Phase 52 using the actual extracted-file
header bytes from `TestOutput/z_ho_extract/z_ho.pak/0009BF70.tex`.

Observed extracted-file start (`0009BF70.tex`, first 16 bytes):

- `+0x00..0x03 = 0x09877BC9`
- `+0x04..0x07 = 0xABBAE5E0`
- `+0x08..0x09 = 0xD4B2`

That matches the older public/extracted interpretation:

- `u32 checksum`
- `i32 sentinel`
- `u16 unknown`

But `FUN_001e9ac0(...)` still decompiles as reading:

- `u16 secondary_count_hint`
- `u16 primary_count`
- `u32 secondary_count`
- `u32 base_a_offset`
- `u32 base_b_offset`

and then immediately using those fields to derive:

- `owner.primary_array = blob + 0x10`
- `owner.secondary_array = blob + 0x10 + primary_count * 0x50`
- in-place rebasing of secondary `0x40` entries

Those two starts are not compatible for the Hollywood sample:

- interpreting `0009BF70.tex` literally as the owner-blob header would give
  `primary_count = 0x0987`, which does not match the known 990-record public
  table
- the next dword would be `0xABBAE5E0`, which looks like the old sentinel, not
  a plausible positive owner-blob relocation offset
- the bytes immediately after `+0x0A` also continue to look like the old
  packed-data view, not a `0x10`-byte owner header followed by `0x50` primaries

Combined with the earlier file-source decomp:

- `FUN_00157318(...)` still loads `levels\\...\\.tex` through
  `FUN_0016ad60(...)` / `FUN_001e9fa8(...)`
- `FUN_001e9fa8(...)` still passes the loaded/cached blob straight to
  `FUN_001e9ac0(...)`
- re-reading `FUN_001216f0(...)` directly still shows:
  - cache-hit path: copy `size` bytes from the cached blob pointer to the
    destination buffer with `FUN_00472ff4(...)`
  - file-read path: read `size` bytes into the destination buffer with
    `FUN_00121d48(...)` / `FUN_00123220(...)`
  - no extra returned offset or inner-payload selection before control reaches
    `FUN_001e9ac0(...)`

Practical implication:

- the owner-side `0x50` / `0x40` layout is still the best model for the
  runtime object that record-backed wrappers decode from
- but it should no longer be conflated with the raw extracted public file
  layout
- the highest-value next decomp target is now the missing bridge between those
  two layers:
  - an outer wrapper/translation step before `FUN_001e9ac0(...)`, or
  - a cache/archive path that returns a transformed inner blob rather than the
    literal extracted bytes on disk, or
  - a Ghidra misread in the very first `FUN_001e9ac0(...)` header interpretation

### Phase 55: The Loader Boundary Still Shows Whole-Blob Reads, Not Inner-Payload Selection

Targeted decompilation in
`historical analysis artifact phase55_loader_boundary.c`
narrows the loader-side ambiguity further.

What the owner `.tex` path actually does:

- `FUN_001e9fa8(...)` still calls `FUN_001216f0(...)` and then passes the
  returned pointer straight to `FUN_001e9ac0(...)`
- `FUN_001216f0(...)` has three visible cases:
  - cache-hit path:
    - `FUN_0025e288(...)` returns a cached blob pointer plus size
    - if no destination was supplied, `FUN_001216f0(...)` allocates one with
      `FUN_0011dd28(size)`
    - it then copies exactly `size` bytes with `FUN_00472ff4(dest, cached, size)`
  - whole-file path:
    - `FUN_001231b8(...)` returns a file size
    - `FUN_00123220(...)` then loads that file into the caller-supplied
      destination buffer
  - streaming path:
    - `FUN_00122068(...)` creates a file object
    - `FUN_00121cf0(...)` queries the file size
    - `FUN_00121d48(file_obj, dest, 1, size)` performs the read into the
      caller-supplied destination buffer

What the tiny loader helpers turned out to be:

- `FUN_0011dd28(...)` is just an allocator wrapper around `FUN_0011c4b0(...)`
- `FUN_0011de18(...)` is just the corresponding free wrapper around
  `FUN_0011c998(...)`
- `FUN_00121d48(...)`, `FUN_00121d18(...)`, and `FUN_00121f30(...)` are thin
  file-object dispatch wrappers that select mode/state and then call a file
  backend vtable slot (`+0x8C`, `+0x84`, `+0x3C`)

Negative result that matters:

- none of the caller-visible `.tex` owner-path helpers above select an inner
  payload pointer before `FUN_001e9ac0(...)`
- the decompiled control flow still shows the returned pointer as:
  - the destination buffer allocated by `FUN_001216f0(...)`, or
  - the caller-supplied buffer if one was passed in
- in other words, the visible owner-path loader still looks like "read/copy the
  whole blob, then call `FUN_001e9ac0(...)` on that blob"

Important nuance:

- `FUN_00120b20(...)` is a different cached-file helper used in other paths
  such as the `.geom` side; on cache hits it returns a pointer inside a cache
  object after applying a small aligned offset
- but the owner `.tex` path analyzed here goes through `FUN_001216f0(...)`, not
  `FUN_00120b20(...)`

Practical implication:

- the "hidden inner payload selected by `FUN_001216f0(...)`" theory is now much
  weaker
- the two stronger remaining explanations are:
  - the extracted public `.tex` file in this repo is not the same runtime input
    that the game loads, despite the matching filename/path convention, or
  - the current `FUN_001e9ac0(...)` header interpretation is still wrong in a
    way that survives the current decomp

### Phase 56: `FUN_00121d48` and `FUN_0011dde8` Are Thin Wrappers, Not a Format Bridge

Targeted instruction dumping tightened the loader boundary again:

- `FUN_00121d48(...)` is only a tiny state-setting wrapper around a file-object
  virtual call:
  - it writes `3` to `file_handle + 0x14`
  - loads the backend object from `file_handle + 0x44`
  - dispatches through the backend vfunc at `+0x8C`
  - preserves the caller-supplied destination pointer and size registers
- `FUN_0011dde8(size, ptr)` is also tiny:
  - it forwards `(heap, size, ptr, 0)` into `FUN_0011c8c8(...)`
  - there is no sign of returned-pointer rebasing or hidden payload selection

Practical implication:

- the already-suspected "maybe the loader helpers shift the returned blob base"
  branch is now much weaker
- the remaining candidates are even narrower:
  - the underlying file-backend vfunc reached by `FUN_00121d48(...)`
  - a cache path below `FUN_0025e288(...)`
  - or a bad high-level interpretation of `FUN_001e9ac0(...)` itself

### Phase 57: The Cache Returns an Interior Pointer, but the Payload Is Stored Verbatim

The cache branch is now sharper enough to separate "interior pointer" from
"transformed payload."

What the cache reader does:

- `FUN_0025e288(...)` walks a resident cache arena and finds a `0x20`-stride
  record whose fields match the requested key(s)
- `FUN_0025d150(record)` then returns:
  - `record + record[1]` in the simple case
  - or an equivalent rebased interior pointer in the alternate-memory case
- so yes: the pointer returned to `FUN_001216f0(...)` on cache hits is an
  interior payload pointer, not the base of the cache record itself

What the cache writer does:

- `FUN_0025e110(...)` writes those same `0x20` records
- it fills:
  - `record[0] = key/type`
  - `record[2] = payload_size`
  - `record[3] = secondary id/hash`
  - `record[1] = computed payload offset inside the arena`
- then copies the producer bytes directly with:
  - `FUN_00472ff4(record + record[1], producer_ptr, payload_size)`
- then writes a sentinel record (`0xB524565F`) at the next `0x20` slot
- the direct `.tex` producer path in `FUN_00140ab8(...)` (`"%s.tex.%s"`)
  now makes that concrete:
  - it calls `FUN_001216f0(...)` to load the bytes into a temporary buffer
  - then immediately calls `FUN_0025e110(..., loaded_ptr, loaded_size)`
  - then frees that temporary buffer with `FUN_0011c998(...)`
- so at least for this obvious `.tex` producer, the cache payload is the exact
  byte stream returned by `FUN_001216f0(...)`, not a second-stage inner payload
  selected during cache insertion

What that means for the boundary problem:

- the cache absolutely does wrap payloads in its own tiny arena/record format
- but the cache itself still stores the producer payload bytes verbatim behind
  that wrapper
- so the interior pointer returned by `FUN_0025d150(...)` does **not** by
  itself explain the public-file vs owner-blob layout mismatch

Practical implication:

- the remaining stronger candidates are now:
  - the file-backend vfuncs reached from `FUN_00121d18(...)` /
    `FUN_00121d48(...)`
  - a higher-level producer that hands already-transformed bytes to
    `FUN_0025e110(...)`
  - or a surviving misread in the `FUN_001e9ac0(...)` header/structure model

### Phase 58: The Loader Uses a Real Pooled File-Object Class

The file backend is no longer hypothetical. The allocator / constructor chain is
now concrete:

- `FUN_00122010(...)` zeroes `DAT_00498688` and calls `FUN_00124f88(...)`
- `FUN_00124f88(...)` allocates or resets **16 pooled file objects** stored in
  `DAT_005ac4c8`
  - each object is `0x15c` bytes
  - new objects are allocated with `FUN_0011c4b0(..., 0x15c, 1, 0)`
  - each one is constructed through `FUN_001239f8(...)`
- `FUN_001239f8(obj)` is the real file-object constructor:
  - it calls `FUN_00121b48(...)`
  - sets `obj + 0x44 = &DAT_004aaec8`
  - sets `obj + 0x48 = -1`
  - clears `obj + 0x14c/+0x150/+0x154/+0x158`
- `FUN_00121b80(obj)` is the reset path used when reusing pooled objects:
  - clears generic state fields
  - resets defaults such as `obj + 0x20 = 1`, `obj + 0x28 = 0x10000`,
    `obj + 0x2c = 100`
  - then dispatches to vtable slot `+0x2c`

This matters because the loader wrappers are not dispatching into some hidden
opaque handle. They are operating on a concrete class whose backend vtable is
known.

### Phase 59: Pool Allocation / Release Is Just a Free-List

The allocator behavior is also clear now:

- `FUN_00122358(...)` pops an object from the `DAT_005ac4c8` free-list
  - if all 16 objects are in use, it forces progress by calling the object's
    vfunc at `+0x0c`, then retries
  - once it finds a reusable object, it resets it with `FUN_00121b80(...)`
- `FUN_00122598(obj)` returns an object to the same 16-slot free-list
- `FUN_00121c38(obj)` is only the "busy?" predicate:
  - it returns `obj + 0x40 > 0`

So the pool layer is not hiding any extra payload boundary either. It is just a
reusable object cache for backend file requests.

### Phase 60: The Cache-Backed File Subsystem Still Does Not Reveal an Inner-Payload Bridge

The backing-file subsystem itself is now much sharper:

- `FUN_00123888(path)` resolves the cache/index base and sets:
  - `DAT_004986fc = 1`
  - `DAT_004986f8 = base pointer / base offset`
- `FUN_001234e8(path)` builds the indexed-file backend when
  `DAT_004985bc != 0`
  - it creates `DAT_00498700 = FUN_00125560(path, 0, 0, 0)`
  - it opens the backing handle `DAT_00498708`
- `FUN_00123830(...)` tears that backend down:
  - frees `DAT_00498700`
  - closes `DAT_00498708`

This means the "indexed/cached file mode" is real, but it is still a file
subsystem over a backing archive or index. It is not yet evidence of a hidden
owner-blob reformatter.

### Phase 61: The Open Path Caches Size / Offset, It Does Not Strip a Nested Owner Blob

The backend open method is `FUN_001245a0(...)`, which is the vtable method at
`DAT_004aaec8 + 0x34`.

What it does:

- direct-file mode (`DAT_004985bc == 0`)
  - normalizes the path
  - may prefix `host:` when needed
  - stores a path-tail pointer when useful
- indexed/cache mode (`DAT_004985bc != 0`)
  - looks up the file via `FUN_00125298(path, DAT_00498700)`
  - on hit, sets:
    - `obj + 0x30 = record[1] & 0x7fffffff`
    - local base = `DAT_004986f8 + (record[0] & 0x1fffff)`
- then builds a backend request through `FUN_00124d98(...)`

What it does **not** do:

- it does not parse a second inner header
- it does not select an owner-sub-blob from inside the returned file bytes
- it does not rewrite the caller-visible blob before the later read path

So even at the real open method, the evidence still points to "open file /
cache entry, remember size and base offset, then issue read requests."

### Phase 62: `DAT_004aaec8` Is the Actual File-Backend Vtable

Dumping the pointer table at `0x004aaec8` resolves the loader-side wrappers to
real methods:

- `+0x0c -> FUN_00121c48(...)`
- `+0x24 -> FUN_00123bc0(...)`
- `+0x2c -> 0x00123fd0` (tiny reset stub)
- `+0x34 -> FUN_001245a0(...)` open
- `+0x3c -> FUN_001248b0(...)` close
- `+0x84 -> FUN_001241d0(...)` whole-file read wrapper
- `+0x8c -> FUN_00124258(...)` actual read request
- `+0xb4 -> 0x00121fe8` (tiny size-query stub)

`FUN_00123a48(...)` also re-installs `obj + 0x44 = &DAT_004aaec8`, which
confirms this table is the canonical backend for these pooled objects.

### Phase 63: The Critical Backend Vfuncs Are Queue / Read Helpers, Not Format Bridges

The most important backend methods now have clear behavior:

- vfunc `+0x0c` = `FUN_00121c48(obj)`
  - repeatedly calls `FUN_00122748(...)`
  - loops until `obj + 0x40 == 0`
  - returns `*obj`
  - this is a "pump the request queue until the object is idle" helper
- vfunc `+0xb4` = stub at `0x00121fe8`
  - loops while `obj + 0x30 < 0`
  - then returns `obj + 0x30`
  - this is decisive: the size query just returns the cached size field; it is
    **not** a hidden parsing or wrapper-stripping step
- vfunc `+0x84` = `FUN_001241d0(obj, dest)`
  - if partial-read state exists, it resets that state
  - calls the size-query vfunc at `+0xb4`
  - then calls vfunc `+0x8c` with `(dest, 1, size)`
  - so this is only a whole-file convenience wrapper
- vfunc `+0x8c` = `FUN_00124258(obj, dest, count, elem_size)`
  - stores the destination and requested byte count
  - allocates an alignment/scratch buffer when needed
  - enqueues opcode `3` via `FUN_00124d98(...)`
  - if synchronous mode is enabled, waits via the `+0x0c` helper
- vfunc `+0x3c` = `FUN_001248b0(obj)`
  - enqueues opcode `2`
  - clears `obj + 0x48`
  - optionally waits synchronously

The small non-function slots are also not hiding much:

- `0x00123fd0` clears the read/scratch fields and sets `obj + 0x48 = -1`
- `0x00123ff0`, `0x00123ff8`, `0x00124000`, and `0x00124468` are just
  `return 0` stubs
- `0x001241c8` is just `jr ra; nop`

### Phase 64: The File-Backend Vfunc Path No Longer Looks Like the Missing Public-File -> Owner-Blob Bridge

Taken together, the new file-object and vtable work changes the loader-side
conclusion materially:

- the loader wrappers now resolve to a concrete file-object class
- the backend open path caches a size and a base offset, but does not expose
  any owner-sub-blob selection
- the size-query slot returns the cached `obj + 0x30` field directly
- the whole-file read slot just asks for that size and forwards to the actual
  read-request method
- the actual read-request method is alignment / queue handling, not a format
  conversion layer

So the "maybe the hidden translation lives in the file backend vfuncs" theory
is now much weaker.

The stronger remaining candidates are now:

- a higher-level producer that already gives transformed bytes to
  `FUN_001216f0(...)` / `FUN_0025e110(...)`
- or a remaining high-level misread of what `FUN_001e9ac0(...)` expects and how
  its header / first indirections actually work

### Phase 65: The Direct `FUN_001e9ac0(...)` Callers Still Pass the Raw Blob Base

The two direct wrappers around `FUN_001e9ac0(...)` are now concrete enough to
remove one more caller-side ambiguity:

- `FUN_001e9fa8(path, out_size_ptr)`
  - calls `FUN_001216f0(path, out_size_ptr, 0, 0)`
  - stores the returned pointer in `s0`
  - then does `move a0, s0; jal FUN_001e9ac0`
  - after construction, stores that same raw blob pointer at `owner + 8`
- `FUN_001e9fe0(blob_ptr, ...)`
  - is the in-memory variant
  - it calls `FUN_001e9ac0(a0)` directly with the caller-supplied pointer still
    in `a0`
  - then stores `0` at `owner + 8`

So the direct caller evidence is now strong: `FUN_001e9ac0(...)` is still being
given the raw blob base, not an already-offset inner pointer.

### Phase 66: The `FUN_001e9ac0(...)` Header Math Still Matches the Raw Instructions

Re-checking the entry block at the instruction level did **not** expose a
decompiler scaling mistake.

What the first loads actually do:

- `lhu v1, 0x0(s0)` -> first `u16`
- `lhu v0, 0x2(s0)` -> second `u16`
- `lw  a1, 0x4(s0)` -> third field at `+0x04`
- `lw  v0, 0x8(s0)` -> fourth field at `+0x08`
- `lw  v1, 0x0c(s0)` -> fifth field at `+0x0c`

What the layout arithmetic actually does:

- `a0 = s0 + 0x10`
- `mult v1, v0, 0x50`
- `a2 = a0 + primary_count * 0x50`
- if `secondary_count > 0`, secondary array size is `secondary_count * 0x40`
- the two base offsets are still:
  - `s3 = blob + *(u32 *)(blob + 0x08)`
  - `s0 = blob + *(u32 *)(blob + 0x0c)`

So the current high-level model remains instruction-backed:

- fixed `0x10` owner header
- `primary_count * 0x50` bytes of primary records
- `secondary_count * 0x40` bytes of secondary entries

This does **not** prove the interpretation is complete, but it does make the
"maybe Ghidra only mis-scaled the pointer math" branch much weaker.

### Phase 67: The Only Visible `FUN_0025e110(...)` Producer Is Still the Plain `%s.tex.%s` Path

Re-checking the decompiled coverage still shows just one visible producer path
writing `.tex` payloads into the cache:

- `FUN_00140ab8(...)`
  - loads bytes with `FUN_001216f0(...)`
  - immediately writes them with `FUN_0025e110(..., loaded_ptr, loaded_size)`
  - then frees the temporary buffer

Within the current decompiled set, there is still no second visible caller to
`FUN_0025e110(...)` that would suggest a special owner-blob-preparation writer.

That makes the "higher-level producer already transformed the bytes before the
cache saw them" theory weaker as well, at least in the currently traced path.

### Phase 68: The Next Best Target Is Now the `FUN_001e9ac0(...)` Interpretation Itself

At this point the negative evidence is stacking up on the loader side:

- the direct callers pass the raw blob base
- the cache stores producer bytes verbatim
- the file backend is a concrete queued file-object class, not a hidden format
  bridge
- the `FUN_001e9ac0(...)` entry-block arithmetic still matches the disassembly

So the next strongest target is no longer "keep digging lower in the backend."
It is:

- re-check the semantic interpretation of the first `FUN_001e9ac0(...)` header
  fields and rebasing bases (`+0x08` / `+0x0c`)
- or prove that the extracted public `.tex` file in this repo is not the same
  byte stream that the runtime owner path actually consumes

### Phase 65: The Earliest `FUN_001e9ac0(...)` Header Interpretation Was Still Wrong

Re-reading `FUN_001e9ac0(...)` after the concrete file-backend work exposed a
real contradiction in the current notes.

What the first `0x10` bytes actually do in `FUN_001e9ac0(...)`:

- `+0x00` (`u16`) is copied to `DAT_005af6bc`
- `+0x02` (`u16`) is the primary-record count
- `+0x04` (`u32`) is **not** a relocation base
  - it is used as the count of `0x40` secondary entries
  - `puVar2[5]` is only set when this value is positive
  - the loader then advances the secondary-array tail by `secondary_count * 0x40`
- `+0x08` (`u32`) is the first relocation base
  - primary `+0x10/+0x14` are rebased from `blob + this_base`
  - every secondary `+0x38` is also rebased from `blob + this_base`
- `+0x0C` (`u32`) is the second relocation base
  - secondary `+0x28/+0x30` are rebased from `blob + this_base`
  - `FUN_001e9eb8(...)` receives this same base and rebases nested `0x80`
    entries from it

So `FUN_001e9ac0(...)` is using **two relocation bases, not three**, and the
header dword at `+0x04` is a count, not an offset.

That also corrects the primary-record interpretation:

- primary `+0x10/+0x14` were previously described as rebased from a
  `data_section_offset` at header `+0x04`
- the code actually rebases them from the header `+0x08` base

And it corrects the owner layout too:

- `FUN_001e9fa8(...)` calls `FUN_001216f0(...)`, passes the returned blob
  directly to `FUN_001e9ac0(...)`, then writes `owner + 0x08 = loaded_blob_ptr`
- `FUN_001ea008(...)` later frees `owner + 0x08` when non-zero
- so owner `+0x08` is not just a permanently-unused reserved field in the
  normal file-backed path; it is the retained raw blob pointer

What this changes:

- the surviving "maybe the file backend hides a translation" theory is weaker
  than before
- the stronger local problem is now the owner-parser model itself: we had the
  earliest header fields partially mislabeled
- the extracted public-file mismatch still remains real, but the precise owner
  header we should be comparing against is now:
  - `u16 unknown/global`
  - `u16 primary_count`
  - `u32 secondary_count`
  - `u32 base_a_offset`
  - `u32 base_b_offset`

Practical implication:

- the next highest-value decomp target is still the first-indirection / entry
  interpretation around `FUN_001e9ac0(...)`, not the backend file vfuncs
- if there is a remaining bridge, it is now more likely to be a high-level
  parser-model error than a hidden file-read transformation

### Phase 69: The Concrete File-Handle Backend Still Does Not Look Like a Blob Translator

The retained conclusions from phases 57–66 make the file backend behind
`FUN_00121d48(...)`, `FUN_00121d18(...)`, `FUN_00121f30(...)`, and
`FUN_00121cf0(...)` materially clearer.

Important constructor correction:

- `FUN_00121b48(...)` is only a base ctor. It installs:
  - `file_handle + 0x44 = &DAT_004aa948`
- `FUN_001239f8(...)`, the actual ctor used by the `0x15C`-byte pooled handles
  allocated in `FUN_00124f88(...)`, immediately overwrites that with:
  - `file_handle + 0x44 = &DAT_004aaec8`
- so the real backend object used by pooled file handles is `DAT_004aaec8`, not
  the base descriptor at `DAT_004aa948`

What the base descriptor turned out to be:

- `DAT_004aa948` is mostly a stub table
- its `+0x3C`, `+0x84`, and `+0x8C` entries are tiny `jr ra ; move v0, zero`
  stubs
- its `+0xB4` entry just returns `handle + 0x30`

What the real pooled-handle descriptor looks like:

- `DAT_004aaec8` has zero subobject offsets at `+0x08/+0x20/+0x30/+0x38/+0x80/
  +0x88/+0xB0`
- that means the backend methods operate directly on the file handle base
- the relevant slots are:
  - `+0x0C -> FUN_00121c48(...)`
  - `+0x24 -> FUN_00123bc0(...)`
  - `+0x34 -> FUN_001245a0(...)`
  - `+0x3C -> FUN_001248b0(...)`
  - `+0x84 -> FUN_001241d0(...)`
  - `+0x8C -> FUN_00124258(...)`
  - `+0xB4 -> 0x00121fe8`

What those concrete methods actually do:

- `FUN_001245a0(...)` (`+0x34`, open/setup):
  - either normalizes a host path or resolves a cache/archive entry through
    `FUN_00125298(...)`
  - stores size-ish metadata in `handle + 0x30`
  - records request state in handle fields
  - emits an async work item through `FUN_00124d98(...)`
  - there is no payload parsing or byte rewriting here
- `FUN_001248b0(...)` (`+0x3C`, close/release):
  - stages a small request, clears `handle + 0x48`, and emits it through
    `FUN_00124d98(...)`
  - no payload access
- `FUN_001241d0(...)` (`+0x84`, full-read helper):
  - rewinds via `FUN_00121d78(...)` if needed
  - queries the size with `FUN_00121cf0(...)`
  - then calls the `+0x8C` slot with `(dest, 1, size)`
- `FUN_00124258(...)` (`+0x8C`, ranged read):
  - stores destination pointer and byte count in handle fields
  - optionally allocates an aligned staging buffer
  - emits the read request through `FUN_00124d98(...)`
  - no wrapper parsing, header stripping, or content translation appears here
- `0x00121fe8` (`+0xB4`, size query):
  - busy-waits until `handle + 0x30 >= 0`
  - returns `handle + 0x30`

What the shared dispatch path does:

- `FUN_00124d98(...)` builds a `0x70`-byte work item, assigns an operation id,
  optionally records metadata via `FUN_00123c78(...)`, and hands the request to
  `FUN_00454f88(...)`
- `FUN_00123c78(...)` only records per-operation bookkeeping into a 16-entry
  ring in the handle object
- `FUN_00122748(...)` pumps completed work items:
  - calls the per-item callback
  - then calls the descriptor completion hook at `+0x24`
- `FUN_00123bc0(...)` just wraps `FUN_00121e68(...)`
- `FUN_00121e68(...)` only decrements outstanding-op counts and clears
  `handle + 0x14` back to idle when the count reaches zero

Practical implication:

- the concrete backend object behind `FUN_00121ea0(...)` / `FUN_00121d48(...)`
  no longer looks like a plausible outer-wrapper translator
- this layer resolves a path or cache/archive entry, tracks size/offset/state,
  and queues async open/read/close work
- there is still room for lower-level raw I/O below the queued callback path,
  but in the decompiled backend object itself there is no sign of:
  - selecting an inner payload header before returning bytes to
    `FUN_001216f0(...)`
  - stripping an outer wrapper
  - transforming the read byte stream into a different blob layout
- this makes the "file backend object secretly rewrites the `.tex` blob before
  `FUN_001216f0(...)` sees it" theory much weaker

### Phase 70: The Owner Header Model Needed One More Correction

The phase 67 owner disassembly/xref pass, combined with the earlier record
lookup and bit-1 owner trace, tightens the `FUN_001e9ac0(...)` header
interpretation as follows.

Corrected top-level owner-blob header:

- `+0x00`: unknown `u16`
  - copied to `DAT_005af6bc`
  - current xrefs only show that write; no read-side consumer is currently
    known
- `+0x02`: primary-count `u16`
- `+0x04`: secondary-entry count `u32`
  - this was still too loosely described before
  - the raw instructions show it feeding the `0x40`-stride secondary region
    sizing/allocation path
- `+0x08`: base A
  - used to rebase primary `+0x10/+0x14`
  - used to rebase secondary `+0x38`
- `+0x0C`: base B
  - used to rebase secondary `+0x28/+0x30`
  - used by the nested `0x80` rebasing path

This means the top-level header math is no longer the strongest weak point.
The remaining uncertainty is deeper:

- the semantic meaning of the first `u16`
- the primary/secondary entry field meanings
- the mismatch between this owner-blob model and the public extracted `.tex`
  file bytes we are currently feeding the tool

### Phase 71: The Cache and Indexed-File Path Still Shows No Blob Translation

Re-checking the phase 8 cache/indexed-file analysis against the zone-TEX
consumer and bit-1 owner trace keeps the same conclusion:

- `FUN_0025e288(...)` is still just a cache lookup returning
  `FUN_0025d150(record)` plus the stored payload size
- `FUN_0025d150(...)` only rebases to the payload start inside the cache
  record; there is no visible decompression or inner-wrapper selection there
- `FUN_0025e110(...)` still copies producer bytes verbatim into the cache
  record payload area
- the only visible `.tex` producer into `FUN_0025e110(...)` is still
  `FUN_00140ab8(...)`, which passes the direct pointer/size returned by
  `FUN_001216f0(...)`
- `FUN_001e9fa8(...)` still passes that raw blob base straight into
  `FUN_001e9ac0(...)`

So the cache/indexed-file branch remains weak as an explanation for the public
file vs owner-blob mismatch.

### Phase 72: The Read Path's Visible Byte Rewriting Is Alignment Staging, Not Format Translation

The retained phase 65–70 backend-vfunc, executor-hook, and descriptor analysis
makes the file-read completion path clearer.

Important read-path behavior:

- `FUN_00124258(...)` is the ranged-read helper
  - it stores destination/count in the handle
  - if alignment requirements are awkward, it allocates a staging buffer and
    records both the original destination and aligned transfer parameters
  - it emits opcode `3` through `FUN_00124d98(...)`
- `FUN_00123c78(...)` records extra metadata for opcode `3`
  - specifically the descriptor `+0x14/+0x18` values
- `FUN_00123a70(...)` is the important completion/update hook
  - for opcodes `2` and `3`, if the read used a staging buffer and succeeded,
    it copies bytes from the aligned scratch buffer back to the caller's real
    destination
  - it then frees the temporary buffer bookkeeping when appropriate

This is the only concrete low-level byte rewriting currently visible in the
file backend path, and it is transport/alignment handling, not blob
translation. That again argues against a hidden backend-side transformation of
the `.tex` format before `FUN_001e9ac0(...)`.

Current direction:

- the highest-value target is still the first-indirection / entry semantics in
  `FUN_001e9ac0(...)` and its primary/secondary tables
- if there is still a bridge between the public file and the owner blob, it now
  looks more likely to be a parser-model error or a higher-level packaging
  misunderstanding than a hidden cache/backend rewrite

### Phase 73: The Owner Tables Now Have Concrete Checksum and State/Payload Split Fields

Tracing the record lookup, constructor family, image accessors/load methods,
and submit helpers adds the first concrete field labels inside the primary and
secondary tables.

Primary `0x50` record:

- `primary + 0x08` is the group/material checksum
  - `FUN_001ea0b8(texture_checksum, group_checksum, owner)` first matches this
    field before it scans the linked `0x40` entries
- `primary + 0x3C` is the linked secondary-entry count for that bucket
- `primary + 0x40` is the rebased pointer to the first linked `0x40` entry
- `primary + 0x00`, `+0x04`, and `+0x4C` are definitely reused as the runtime
  linked-list next / sort-key / prev fields once `FUN_001c4840(...)` runs

Secondary `0x40` entry:

- `secondary + 0x00` is the texture checksum
  - `FUN_001ea0b8(...)` matches it against the requested texture checksum
  - `FUN_001a0368(...)` uses the same field to dedupe wrapper creation
- `secondary + 0x10` is authoritative `TEX0`
  - `FUN_001ea2a8`, `FUN_001ea2c8`, `FUN_001ea2e8`, and `FUN_001ea350` all
    decode width, height, PSM, and CLUT format directly from it
- `secondary + 0x18` and `+0x20` are authoritative GS texture-state qwords
  - `FUN_001d6618(...)` queues them to material slots `0x34` and `0x36`
  - the material queue/apply path patches those qwords into the packet blob
- `secondary + 0x28` and `+0x30` are the entry-owned prepared CPU buffers
  - `FUN_001ea488(...)` writes per-mip prepared pixel data into `+0x28`
  - `FUN_0019c620(...)` uses `+0x28/+0x30` as the destination pair when
    reconciling from a concrete raw-image object
- `secondary + 0x38` is the nested `0x80` table pointer consumed by
  `FUN_001e9eb8(...)`

What remains unresolved:

- the exact meaning of the rebased primary `+0x10/+0x14` pointers
- the still-murky methods that touch `secondary + 0x18/+0x20` outside the
  material-state path
- the public-file vs owner-blob mismatch at the outer format boundary

That makes the next best target narrower again: the remaining unknown primary
fields and any consumers of the secondary-entry buffer/state setters, not the
backend loader path.

### Phase 74: The Unresolved CLUT-Side Slot Block Is Mostly Decoded from Raw Instructions

Raw instruction and vtable analysis makes the slot family around
`0xEC/0xF4/0xFC/0x104/0x10C` clearer.

Useful decoded behavior:

- slot `0xEC` at `0x0019f938` is a pointer getter
  - in raw/direct mode it returns `raw_image + 0x18`
  - in record-backed mode it falls back to `secondary + 0x30`
  - so `secondary + 0x30` is now the concrete record-backed analogue of the
    raw image's aux/CLUT buffer pointer
- slot `0xF4` at `FUN_0019f9e0(...)` is a setter, but only for raw/decode mode
  - it copies into `raw_image + 0x18` for `raw_image + 0x20` bytes
  - it has no record-backed fallback
  - so it does **not** prove that `secondary + 0x18/+0x20` are CPU payload
    fields
- slot `0xFC` at `FUN_0019fa98(...)` is the CLUT format getter
- slot `0x104` at `FUN_0019fb60(...)` is the mip-count getter
- slot `0x10C` at `0x0019fc28` is a mode/transform bit getter
  - in raw/direct mode it returns `((raw_image + 0x08) >> 10) & 1`
  - in record-backed mode it returns `((secondary + 0x0C) >> 2) & 1`

This removes the earlier false tension around `FUN_0019f9e0(...)`:

- the record-backed aux/CLUT-side pointer is still best modeled as
  `secondary + 0x30`
- the evidence for `secondary + 0x18/+0x20` now remains primarily the animated
  material-state path, not the wrapper setter/getter family

Current best next target:

- still find a concrete consumer for rebased primary `+0x10/+0x14`
- then re-check whether the animated material path's selected entry really is
  the same canonical owner `0x40` entry, or a sibling structure with a similar
  leading layout

### Phase 74: The Record-Backed Aux/CLUT Getter Maps Raw `+0x18` to Secondary `+0x30`

One more useful correction came from the raw unresolved-slot and vtable-method
analysis.

The tiny unresolved method at `0x0019f938` (vtable slot `+0xEC`) behaves as:

- direct/raw-image mode:
  - return `raw_image + 0x18`
- record-backed mode:
  - return `secondary_entry + 0x30`

That is important because raw-image `+0x18` is the relocatable auxiliary/CLUT
buffer field in the concrete raw-image object. So this slot strongly supports:

- `secondary + 0x30` is the record-backed analogue of the raw-image
  auxiliary/CLUT buffer pointer
- `secondary + 0x18` is not the obvious record-backed aux-buffer pointer

This resolves the earlier tension around `FUN_0019f9e0(...)`:

- the decompiler makes `FUN_0019f9e0(...)` look like a flat write into
  `entry + 0x18`
- but the neighboring getter split shows the record-backed aux path is actually
  centered on `secondary + 0x30`
- so `FUN_0019f9e0(...)` should not be treated as evidence that
  `secondary + 0x18` is CPU-side payload storage

What still remains unresolved:

- a clean decomp of the setter partner around `FUN_0019f9e0(...)`
- concrete consumers of the rebased primary `+0x10/+0x14` fields

So the next high-value decomp target is now:

- the unresolved slot family around `0x0019f938` / `0x0019f9e0`
- plus any owner-primary walkers that consume the base-A rebased primary
  pointers directly

### Phase 75: The `0xFC` Config-Node Family Was Real, but It Is Not `FUN_001d6618(...)`

The targeted decomp in phase76_controller_lookup.c
corrected an important misread from the previous pass.

What is now direct:

- `FUN_00118658(...)` is only a seeded checksum extender. It lowercases letters
  and normalizes `/` to `\\`; it does **not** resolve pointers.
- The nearby string block in phase76_string_block.txt
  confirms the suffixes:
  - `_params`
  - `_begin`
  - `_end`
  - `_subtitles`
- `FUN_002d78a8(...)` is a thin ctor for a `0xFC` object. It installs vtable
  `DAT_004da460` and zeroes `+0xE8/+0xEC/+0xF0/+0xF4/+0xF8`.
- `FUN_002d9e10(...)` allocates and links those `0xFC` nodes.
- `FUN_002144e8(...)` then populates that node family with derived keys:
  - `node + 0x44 = hash(config_name + "_begin")`
  - `node + 0x4C = hash(config_name + "_end")`
  - `_params` and `_subtitles` are similarly hashed, then resolved through
    `FUN_002a7940(...)` / `FUN_002a7980(...)`

So the previous equation
`FUN_002d9e10/FUN_002144e8 node == FUN_001d6618 param object`
was wrong. The shared `+0x44` offset was coincidental.

### Phase 76: `_params` / `_subtitles` Resolve Through a Generic Checksum Table

The new targeted decomp in phase76_param_resolvers.c
and phase76_param_consumers.c
shows:

- `FUN_002a7280(hash)` looks up a generic table bucket at
  `DAT_0049d0f4[(hash & 0xFFF)]` and walks a chain at `entry + 0x10` until
  `entry + 0x04 == hash`
- `FUN_002a72c8(...)` is a thin wrapper that skips `'\r'`-typed indirections
- `FUN_002a7940(...)` returns `entry + 0x0C` only when the resolved node type
  byte at `+0x02` is `0x0A`
- `FUN_002a7980(...)` returns `entry + 0x0C` only when that type byte is
  `0x0C`
- `FUN_002af6e0(...)` is a generic element fetch from the returned object/list

That makes the `0xFC` node path look like generic config/event/timeline data,
not texture-owner data.

This also means:

- the `0xFC` node family is no longer evidence for or against the owner
  secondary `0x40` entry layout
- `FUN_001d6618(...)` remains unresolved, but it must be traced through its own
  param-object family, not through `FUN_002d9e10(...)` / `FUN_002144e8(...)`

The separate `0x572370` runtime table is still distinct from the owner
secondary entry:

- `FUN_00262aa0(...)` allocates a `count * 0xF0` table
- `FUN_00262958(...)` uses table-entry `+0x18` as a linked-list head
- so that `0xF0` table still does not cleanly alias the owner secondary `0x40`
  entry

The primary-pointer branch stayed negative:

- no current decomp artifact shows a confirmed downstream dereference of rebased
  primary `+0x10/+0x14`
- the known owner-table walkers still only rely on `primary + 0x08`, `+0x34`,
  `+0x3C`, and `+0x40`

So the next best target is now:

- return to the actual `FUN_001d6618(...)` param-object family without using the
  `0xFC` config-node path as a bridge, or
- shift back to the owner blob itself, especially the still-unexplained public
  `.tex` vs owner-blob boundary

### Phase 77: The Visible Owner-Loader Chain Still Passes the Raw Base Through

Re-reading the direct entry wrappers keeps the loader-side result negative:

- `FUN_001e9fa8(...)` calls `FUN_001216f0(...)`, then passes the returned base
  straight into `FUN_001e9ac0(...)`, and only afterward stores that raw base at
  `owner + 0x08`
- `FUN_001e9fe0(...)` calls `FUN_001e9ac0(...)` directly on the caller-provided
  base and sets `owner + 0x08 = 0`
- `FUN_001216f0(...)` still resolves to:
  - cache hit -> raw `FUN_00472ff4(...)` copy of cached bytes
  - direct file size/read -> whole-buffer allocation and copy
  - no visible inner-payload offset adjustment before returning the base pointer
- `FUN_0025e110(...)` also still just caches the raw byte buffer produced by
  `FUN_001216f0(...)`

So the currently visible runtime chain is still:

`public path -> FUN_001216f0(...) -> raw byte buffer -> FUN_001e9ac0(...)`

with no proven transformation in between.

That keeps the contradiction intact:

- the extracted public `0009BF70.tex` still begins with the older 10-byte public
  header pattern
- but the decompiled owner loader still interprets its input as the owner blob
  beginning with `u16/u16/u32/u32/u32`

The best next target remains whichever can actually break that contradiction:

- a higher-level producer path that hands different bytes into
  `FUN_001216f0(...)` / `FUN_0025e110(...)`, or
- a concrete reason the current high-level interpretation of `FUN_001e9ac0(...)`
  is still wrong despite the instruction-level consistency

### Phase 78: The Producer/Cache Side Also Preserves Raw Bytes

The higher-level producer path is now effectively closed as a translation
candidate.

What is direct from the visible decomp:

- `FUN_00140ab8(...)` always follows the same pattern:
  - build a path
  - `FUN_001216f0(..., out_size, 0, 0)`
  - `FUN_0025e110(..., loaded_ptr, out_size)`
  - free the temporary `loaded_ptr`
- `FUN_0025e110(...)` copies the producer buffer verbatim into cache storage via
  `FUN_00472ff4(record_payload, producer_ptr, payload_size)`
- `FUN_0025e288(...)` looks up that cache record and returns
  `FUN_0025d150(record)` plus the stored size
- `FUN_0025d150(...)` only computes the cached payload start inside the cache
  metadata wrapper; it does not transform payload bytes

The sibling image path is the same:

- `FUN_001428a8(...)` is an `images\\%s.img.%s` loader, not a `.tex` owner
  loader
- it still preserves bytes exactly by `FUN_00472ff4(param_1 + 0x40, loaded_ptr,
  size)`

Package mode also still looks raw:

- `FUN_001245a0(...)` resolves a package descriptor through `FUN_00125298(...)`
- the descriptor is then used as what decompiles cleanly to a raw offset/size
  pair:
  - `offset = DAT_004986f8 + (desc[0] & 0x1fffff)`
  - `size = desc[1] & 0x7fffffff`
- no visible wrapper stripping or inner-payload selection happens there either

So both of the visible upstream layers now say the same thing:

- producer side preserves raw bytes
- cache side preserves raw bytes
- backend/package open side preserves raw bytes

That makes the remaining contradiction sharper, not weaker:

- either the extracted public `0009BF70.tex` is not the same byte stream the
  runtime owner path consumes despite the visible load chain, or
- the current semantic model of `FUN_001e9ac0(...)` is still wrong in some
  specific but important way

The next best target is therefore narrower again:

- either the package/archive producer above `FUN_001245a0(...)` / `FUN_00125298(...)`
  that could prove the extracted file is not the same stream, or
- a more adversarial re-read of `FUN_001e9ac0(...)` at the instruction level,
  especially any possibility that the initial pointer is already post-header or
  that one of the top-level fields is being semantically misnamed

### Phase 80: Primary `+0x10/+0x14` Still Have No Confirmed Runtime Consumer

I ran a dedicated owner-primary pass specifically for the base-A-rebased primary
`0x50` fields at `+0x10` and `+0x14`.

Artifacts:
- `phase75_primary_field_scan.txt`
- `phase76_primary_consumers.c`
- `phase79_owner_helper_region.c`
- helper scripts:
  - `FindPrimaryFieldConsumers.java`
  - `FindPrimaryListConsumers.java`
  - `FindPrimaryRegConsumers.java`

Concrete result:
- `FUN_001e9ac0(...)` is still the only confirmed owner-primary function that
  touches both `primary + 0x10` and `primary + 0x14`; it only rebases them with
  base A during owner construction.
- The first targeted scan in `phase75_primary_field_scan.txt` produced six
  candidates. After decompiling them in `phase76_primary_consumers.c`, every
  non-loader hit turned out to be unrelated cache/init code:
  - `FUN_00120b20`
  - `FUN_00137898`
  - `FUN_0016b628`
  - `FUN_00197540`
  - `FUN_002bc320`
- The already-traced owner-specific consumers still do not use those fields:
  - `FUN_001ea0b8(...)` uses primary `+0x08`, `+0x3C`, and `+0x40`
  - `FUN_001db0f0(...)` uses primary `+0x08` and `+0x34`
  - record-backed wrapper getters (`FUN_0019f870`, `FUN_0019fa98`,
    `FUN_0019fb60`, `0x0019f938`, `0x0019fc28`) route through secondary/TEX0
    state, not primary `+0x10/+0x14`
  - `FUN_001ea488(...)` consumes secondary `+0x28` as the prepared-source
    destination buffer
- The owner-helper region in `phase79_owner_helper_region.c` only contains
  secondary-entry helpers:
  - `FUN_001ea3a8(...)` mip-count getter
  - `FUN_001ea3b8(...)` transform-flag test
  - `FUN_001ea488(...)` prepared-source copy/transform into `secondary + 0x28`
  There is no neighboring getter or copier for primary `+0x10/+0x14`.

Current conclusion:
- In the currently traced zone texture runtime, primary `+0x10/+0x14` are not
  the known pixel pointer / CLUT pointer pair.
- They are also not the known GS setup / TEX0 / mip-state blob path.
- As of this pass, they remain opaque base-A-rebased pointers with no confirmed
  downstream consumer in the decompiled owner/runtime path.

### Phase 81: The Package Branch Now Looks Like Index Lookup Plus Raw Data-File Reads

The package/index path is sharper now and still does not look like the missing
public-file to owner-blob translation stage.

What `FUN_001234e8(...)` and `FUN_00125560(...)` now show:

- `FUN_001234e8(path)` sets up indexed-file mode by:
  - loading `DAT_00498700 = FUN_00125560(path, 0, 0, 0)`
  - opening a separate backing handle into `DAT_00498708`
- `FUN_00125560(...)` behaves like an index-table loader, not a payload
  decoder:
  - it reads an index/control file wholesale into memory
  - then rebases only index-internal pointers such as:
    - `base + 0x08` -> root table pointer
    - chained descriptor/list pointers through `entry + 0x08/+0x14/+0x20/...`
  - it does not walk or rewrite package payload bytes
- `FUN_00125298(path, DAT_00498700)` is then just a hashed lookup into that
  in-memory index object:
  - `FUN_00125160(...)` normalizes the path and hashes directory + filename
  - `FUN_00125298(...)` returns the matching descriptor record
- `FUN_001245a0(...)` package mode still consumes that descriptor as a raw
  location record:
  - `offset = DAT_004986f8 + (desc[0] & 0x1fffff)`
  - `size = desc[1] & 0x7fffffff`

So the package branch now looks like:

`index object in DAT_00498700 -> descriptor lookup -> raw offset/size into backing data file`

not:

`descriptor lookup -> hidden subfile translator -> owner blob`

That makes the package/index branch a weaker explanation for the public
`.tex` vs owner-blob mismatch than before.

### Phase 82: The Top-Level `FUN_001e9ac0(...)` Header Reads Are Now Instruction-Level Strong

The adversarial re-read of the opening instructions still favors the current
owner-header model.

Direct from `disasm_1e9ac0_long.txt`:

- `lhu v1, 0x0(s0)` -> first `u16`
- `lhu v0, 0x2(s0)` -> second `u16`
- `lw a1, 0x4(s0)` -> `u32` count / secondary-count field
- `lw v0, 0x8(s0)` -> base-A offset
- `lw v1, 0xc(s0)` -> base-B offset
- `addiu a0, s0, 0x10` -> first primary record starts at `blob + 0x10`
- `mul-like product using second_u16 * 0x50` -> primary array span
- `addu s3, s0, v0` -> base A is `blob + *(u32*)(blob + 0x08)`
- `addu s0, s0, v1` -> base B is `blob + *(u32*)(blob + 0x0c)`

Combined with the later rebasing loop, that still supports:

- top-level header = `u16/u16/u32/u32/u32`
- primary stride = `0x50` bytes
- secondary stride = `0x40` bytes
- no direct evidence here that the caller hands `FUN_001e9ac0(...)` a
  post-header pointer

So the remaining contradiction is now better framed as:

- the visible loader/cache/package chain still looks raw end-to-end, and
- the opening `FUN_001e9ac0(...)` header math still looks materially correct

which pushes the next likely break point either above the extracted public-file
boundary itself, or into a still-unidentified semantic mismatch deeper than the
top-level header arithmetic.

Practical implication:

- unless new disassembly contradicts these loads, another pass trying to
  reinterpret the first `0x10` bytes of `FUN_001e9ac0(...)` is now lower value
  than tracing where the runtime loader stream can diverge from the extracted
  public file bytes

### Phase 83: The Local Repo Extractor Writes the Chosen PAK Slice Verbatim

I checked the local C# extraction path that produces the extracted Hollywood
zone `.tex` files used by tests and analyzer runs.

What the repo actually does:

- CLI extraction goes straight through `PakArchive.GetFileList(...)` and
  `PakArchive.ExtractFiles(...)`
- the analyzer loaders also go straight through `PakArchive.ExtractFiles(...)`,
  then select `0009BF70.tex` by filename and read those bytes back verbatim

The important behavior in `PakArchive.ExtractFiles(...)` is simple:

- once an `ArchiveEntry` has been chosen, extraction is just:
  - choose `pakData` vs `pabData`
  - `Array.Copy(sourceData, entry.Offset, fileData, 0, entry.Size)`
  - write `fileData` to disk
- for `z_ho.pak.ps2`, there is no companion `z_ho.pab.ps2`, so the Hollywood
  zone extractor always copies directly from the `.pak` file

That means the local repo does **not**:

- prepend or strip a public `.tex` wrapper during extraction
- apply any post-slice transform before `0009BF70.tex` is written
- reinterpret the extracted bytes before the zone-TEX decoder sees them

So the local extractor is compatible with the runtime's **raw-slice** behavior
once the correct `(offset, size)` pair has been selected.

The remaining repo-side risk is earlier than the copy:

- `PakArchive` does not implement the runtime `FUN_00125298(...)` package-index
  format directly
- instead it reconstructs PAK entries heuristically by:
  - scanning for `QbKey("last")`
  - walking backward through `0x20` / `0xC0` entries
  - inferring table-wide alternate field order from the `0x10` flag

So if the extracted `0009BF70.tex` bytes are wrong, the most plausible local
repo cause is now:

- the PAK table parser selecting the wrong entry start / size / field order

not:

- any later transformation in the extractor or analyzer path.

### Phase 85: Runtime PAK Loading Still Looks Structurally Different from `PakArchive`

The runtime-side package family is now strong enough that I would stop treating
the repo `PakArchive` path as presumptively equivalent.

What the decomp supports:

- indexed-package mode is gated by `DAT_004985bc != 0`
- `FUN_001234e8(...)` then sets up two separate resources:
  - `DAT_00498700 = FUN_00125560(path, 0, 0, 0)` -> in-memory index/control
    blob
  - `DAT_00498708` -> separate backing data-file handle
- `FUN_00125560(...)` reads and rebases an index/control file in memory
- `FUN_00125298(...)` performs a hashed lookup into that rebased index and
  returns a fixed `0x0c` descriptor
- `FUN_001245a0(...)` uses that descriptor only as raw slice metadata

So the runtime path currently looks like:

`logical path -> hashed index/control blob -> 0x0c descriptor -> raw slice in separate backing data file`

That is structurally different from the local repo path:

`single .pak file -> sentinel scan for QbKey("last") -> backward-walk 0x20/0xC0 entries -> inferred offset/size`

This does not prove the repo extractor is wrong, but it does mean the repo
`PakArchive` path is still unproven for these zone packages at the format-family
level.

### Phase 86: The Sample `z_ho.pak.ps2` Still Has a Classic Sentinel Table, But With Extra Flag Variants

I did a narrow byte-level read of the local sample archive:

- `Sample/Builds/.../PAK/z_ho.pak.ps2`
- size `0x008290F0` / `8,556,688` bytes
- no companion `z_ho.pab.ps2`

Concrete archive-shape result:

- the file does contain a classic `QbKey("last") = 0xB524565F` sentinel
  at `0x000014A0`
- stepping the front table using `hasFilename = (flags & 0x20) != 0` produces a
  coherent mixed `0x20` / `0xC0` entry layout that lands cleanly near that
  sentinel

But the flag values in the real table are broader than the repo parser allows:

- full embedded-name entries appear with:
  - `0x20`
  - `0x22`
- compact entries appear with:
  - `0x00`
  - `0x02`

Concrete examples from the sample:

- `0x00000000` -> type `A7F505C4`, flags `0x22`,
  `"worlds\\worldzones\\z_ho\\z_ho_sfx_dat_ps2.qb.ps2"`
- `0x00000EA0` -> type `8BFA5E8E`, flags `0x02`
- `0x00000EC0` -> type `7EA7357B`, flags `0x02`
- `0x00000F00` -> type `49875607`, flags `0x22`,
  `"worlds\\worldzones\\z_ho\\z_ho_sfx.qb.ps2"`
- `0x000013E0` -> final full entry before the sentinel, flags `0x20`

That matters because the local `PakArchive` validator currently only accepts:

- `0x00`
- `0x10`
- `0x20`
- `0x30`

So the sample archive now looks like:

- same general sentinel-table family as classic Neversoft PAKs
- but with an extra `0x02` flag bit in real THAW PS2 tables

Implication:

- the repo parser is probably not dealing with a completely different archive
  family for `z_ho.pak.ps2`
- but its flag validation is still too strict, which can break backward table
  discovery and therefore entry selection

### Phase 84: The Primary `+0x34` Field Was Still Stale in the Notes

One owner-structure assumption in the running notes was still too specific.

The correction:

- the conditional `+0x28/+0x30/+0x38` rebasing near the top of
  `FUN_001e9ac0(...)` is walking `owner.secondary_array` at `0x40` stride, not
  the primary array
- the later primary loop only:
  - rebases `primary.+0x10/+0x14` with base A
  - stores the owner at `primary.+0x38`
  - clears `primary.+0x34`
  - rewrites `primary.+0x40`
  - inserts the record with `FUN_001c4840(...)`
- later, `FUN_001db0f0(...)` uses that same `primary.+0x34` as an intrusive
  list head

So the older primary-field naming around `+0x28/+0x2C/+0x30/+0x34` as
`mip_*` / `clut_present` state is not decomp-backed enough and should be
treated as unresolved owner-primary state instead.

This does not weaken the top-level owner-header model, but it does remove one
stale interpretation from the primary-record description.

### Phase 87: The `DATAP.HED/WAD` vs Flat `PAK\*.pak.ps2` Contradiction Is Resolved

The higher-level archive picture is now concrete rather than inferred.

Local sample files:

- `Sample/Builds/.../Archives/WAD/DATAP.HED`
- `Sample/Builds/.../Archives/WAD/DATAP.WAD`
- `Sample/Builds/.../PAK/z_ho.pak.ps2`
- `Sample/Builds/.../PAK/z_ho_net.pak.ps2`

The important result:

- `DATAP.HED` is THAW plaintext `offset(u32), size(u32), name(null-term)`
- for this file family, the offset is sector-based:
  - `wad_byte_offset = (raw_offset & 0x00FFFFFF) * 0x800`
- when interpreted that way, the WAD-resident payloads are byte-identical to
  the flat sample `PAK\*.pak.ps2` files

Concrete Hollywood examples:

- `\worlds\worldzones\z_ho\z_ho.pak.ps2`
  - HED record at `0x47854`
  - raw offset `0x00052560`
  - size `0x00829090`
  - WAD byte offset `0x292B0000`
  - the `DATAP.WAD` slice at `0x292B0000` for `0x829090` bytes is byte-equal
    to `PAK/z_ho.pak.ps2`
- `\worlds\worldzones\z_ho\z_ho_net.pak.ps2`
  - raw offset `0x00042FE0`
  - size `0x0074B660`
  - WAD byte offset `0x217F0000`
  - byte-equal to `PAK/z_ho_net.pak.ps2`
- `\sounds\pak\z_ho_sfx.pak.ps2`
  - raw offset `0x000335C0`
  - size `0x000B32C0`
  - WAD byte offset `0x19AE0000`
  - byte-equal to `PAK/z_ho_sfx.pak.ps2`

So the earlier contradiction was self-inflicted:

- comparing the raw `DATAP.HED` offset `0x00052560` directly against `DATAP.WAD`
  bytes was wrong
- the correct THAW interpretation is sector-based, and once applied, the
  top-level `WAD -> PAK` embedding is exact

Implication:

- the sample supports a real two-layer archive model:
  - outer `DATAP.WAD` / `DATAP.HED`
  - inner zone `*.pak.ps2`
  - inner zone assets like `0009BF70.tex`
- the runtime `.HED/.HDP/.WAD` package-family evidence and the flat extracted
  `PAK` sample files are no longer in conflict
- the remaining decoder problem is back inside the inner zone payload path, not
  at the outer archive boundary

### Phase 88: The `PakArchive` Gap Is Still Real, But It Is an Inner-Layer Issue

The inner `z_ho.pak.ps2` archive still shows the THAW-specific flag widening
already noted above:

- real flags include `0x00`, `0x02`, `0x20`, and `0x22`
- the local `PakArchive` validator still only accepts
  `0x00`, `0x10`, `0x20`, and `0x30`

That still matters for the inner layer because it can truncate backward table
discovery to the later strict-valid region.

But it no longer explains the outer archive contradiction, because:

- the flat `PAK\*.pak.ps2` files are now proven to be exact slices of
  `DATAP.WAD`
- `WadArchive`'s THAW sector-based model was the correct interpretation for the
  top-level container

So the package conclusions are now:

- outer `WAD/HED` layer: structurally understood
- inner `PAK` layer: same general sentinel-table family, but still with a THAW
  PS2 parser gap around the extra `0x02` flag bit

### Phase 89: THAW PS2 `PAK` Flags Behave Like Additive Bits, Not a Tiny Enum

I widened the sample from just `z_ho.pak.ps2` to a representative PS2 THAW
subset:

- `z_*.pak.ps2`
- `cap*.pak.ps2`
- `cas*.pak.ps2`
- `global*.pak.ps2`
- `levelselect*.pak.ps2`
- `storyselect*.pak.ps2`

Across `263` such files, the important result is that the flag field behaves
like an additive bitfield family, not a strict four-value enum.

Observed flag values in this subset:

- `0x00`
- `0x01`
- `0x02`
- `0x08`
- `0x10`
- `0x20`
- `0x21`
- `0x22`
- `0x28`
- `0x30`

The distribution by file family is especially clean:

- ordinary zone/world PAKs:
  - `0x00`, `0x02`, `0x22`
  - and in some simpler cases `0x20`
- create-a-park shell files:
  - `0x00`, `0x10`, `0x30`
- create-a-park sky files:
  - `0x01`, `0x21`
- create-a-park asset packs:
  - `0x00`, `0x08`, `0x28`
- plain/global/CAS-style packs:
  - usually just `0x00` and/or `0x20`

So the structurally useful interpretation is now:

- `0x20` = entry has an embedded filename and therefore uses the `0xC0`
  full-entry size
- the lower bits like `0x01`, `0x02`, `0x08`, and `0x10` appear to be
  independent family/behavior bits layered on top of the same basic table
  shape

That means the current local validator is too strict in two ways:

- it should not treat valid flags as only
  `0x00`, `0x10`, `0x20`, `0x30`
- it should not implicitly collapse “full entry” into exact-flag `0x20`
  when real THAW PS2 tables also use `0x21`, `0x22`, `0x28`, and `0x30`

### Phase 90: The `0x10` Bit Is Real on PS2 THAW, But It Still Does Not Prove Swapped `size/offset`

The sample subset finally produced real PS2 THAW files with the `0x10` bit:

- `cap_shell1.pak.ps2`
- `cap_shell1_net.pak.ps2`
- ...
- `cap_shell6.pak.ps2`
- `cap_shell6_net.pak.ps2`

But the important correction is that these files do **not** support the current
`0x10 => swapped field order` assumption.

Concrete example from `cap_shell1.pak.ps2`:

- entry `0x20`
  - type `0x8BFA5E8E` (`.tex`)
  - flags `0x10`
  - `+0x04 = 0x00000990`
  - `+0x08 = 0x00041BF0`
- entry `0x40`
  - type `0x7EA7357B`
  - flags `0x10`
  - `+0x04 = 0x00042560`
  - `+0x08 = 0x0007DF80`
- entry `0x60`
  - type `0x72A6D78C` (`.col`)
  - flags `0x10`
  - `+0x04 = 0x000C04C0`
  - `+0x08 = 0x0002901A`
- entry `0x80`
  - type `0x49875607`
  - flags `0x30`
  - `+0x04 = 0x000E94C0`
  - `+0x08 = 0x00005DE8`

Using the normal interpretation:

- `offset = +0x04`
- `size = +0x08`

those entries form a coherent near-contiguous payload chain:

- `.qb` at `0x170` size `0x83C`
- `.tex` at `0x990` size `0x41BF0`
- next payload at `0x42560`
- next payload at `0xC04C0`
- named payload near EOF at `0xE94C0`

Using the swapped interpretation:

- `offset = +0x08`
- `size = +0x04`

the same records become implausible or obviously wrong, especially the
`0x30` named entry at `0x80`, which would become:

- offset `0x5DE8`
- size `0xE94C0`

That would overlap most of the archive and is not a credible table
interpretation.

So for the current PS2 THAW sample set:

- `0x10` is a real flag bit
- but it is **not** decomp-backed or sample-backed evidence that PS2 THAW PAKs
  swap `offset` and `size`

The local `PakArchive` `UsesAltFieldOrder(flags)` assumption should therefore
be treated as unproven at best, and likely wrong for this PS2 THAW family.

### Phase 91: Compatibility Boundary for THAW PAK Support

The next useful question was not “what does THAW do?” but “what can change
without breaking the current repo support surface?”

The current local support boundary is narrower than the `PakArchive.cs`
summary comment suggests.

Relevant code/tests:

- `src/NeversoftMultitool/Core/Formats/Archives/PakArchive.cs`
  still documents:
  - exact valid flags `0x00/0x10/0x20/0x30`
  - `0x10 => swapped size/offset`
- `tests/NeversoftMultitool.Tests/Core/Formats/Archives/PakArchiveTests.cs`
  only covers the THAW PS2 corpus
  - `qb.pak.ps2` must parse as an archive
  - `cap_shell2.pak.ps2` must parse as an archive
  - `cap_assets_fast_particle_data.pak.ps2` must classify as raw
  - `qb.pak.ps2` must return `241` entries
  - `cap_shell1.pak.ps2` must return `5` entries and the expected names
  - a broad THAW corpus pass must avoid exceptions and maintain large archive/raw counts
- `src/NeversoftMultitool/Core/RecursiveUnpacker.cs`
  uses `PakArchive.IsPakArchive(...)` to decide whether `.pak` files are
  extractable archives or raw payloads, so classification regressions matter
  outside the unit tests too
- the retained
  [`DecodeProvenanceCommand`](../../tools/validation/thaw-zone-texture/Commands/DecodeProvenanceCommand.cs)
  and [`ContentSearchCommand`](../../tools/validation/thaw-zone-texture/Commands/ContentSearchCommand.cs)
  consume `PakArchive` entries directly, so THAW zone decoding research remains
  downstream of `PakArchive` correctness

Important limitation:

- the current tests do **not** prove that `cap_shell*.pak.ps2` extraction uses
  the correct `offset/size` interpretation
- they only prove that shell archives are recognized and that filenames/counts
  are plausible
- the extraction correctness test is on `qb.pak.ps2`, not on the `0x10` shell
  family

Cross-build sample reads tighten the compatibility picture:

- THAW PS2:
  - additive low-bit flags are real
  - `0x20` still behaves like the structural “full entry / embedded filename” bit
  - `0x10` is real, but the sampled shell tables still prefer normal
    `offset = +0x04`, `size = +0x08`
- TH Project 8 PS2:
  - sampled `center_lechand01_main.pak.ps2` still shows the conservative
    `0x00/0x20` table pattern
  - that means the old compact/full structural model still exists in later PS2
    titles
- TH Proving Ground PS2:
  - sampled `a_bcity_sky.pak.ps2` uses `0x01` and `0x21`
  - so additive low-bit flags are not THAW-only; they persist into a later PS2 family

That leads to a safer high-level support direction:

- treat `0x20` as the structural bit that selects compact `0x20` vs full `0xC0`
  entry size
- stop treating the entire flag field as a tiny exact-value enum
- do **not** keep a global “any `0x10` seen in the table means all entries swap
  `offset` and `size`” rule unless a real sample family proves it
- preserve the raw-vs-archive distinction, because `RecursiveUnpacker` and the
  THAW tests depend on that behavior

Open compatibility gap before code changes:

- the repo does not currently have non-THAW `PakArchive` tests
- THP8/THPG samples should be treated as validation targets, but not yet as
  guaranteed supported behavior
- if `PakArchive` is changed, the first new tests should pin:
  - THAW `qb.pak.ps2` extraction as today
  - THAW `cap_shell1.pak.ps2` extraction with a size/offset correctness check
  - raw `cap_assets_fast_particle_data.pak.ps2` classification
  - at least one THP8 `0x00/0x20` pack and one THPG `0x01/0x21` pack as
    smoke coverage

### Phase 92: Current CLI Behavior Confirms the Compatibility Split

A direct `dotnet run --framework net10.0 --project src/NeversoftMultitool -- archive ...`
probe is useful here because it exercises the same `PakArchive.IsPakArchive(...)`
and `PakArchive.ExtractFiles(...)` path that users actually hit.

Current observed behavior:

- THAW shell family
  - `cap_shell1.pak.ps2`
  - current CLI result: `PAK archive detected`, `Found 5 files`
- TH Project 8 PS2
  - `center_lechand01_main.pak.ps2`
  - current CLI result: `PAK archive detected`, `Found 30 files`
- TH Proving Ground PS2 mission/zone families
  - `m_classic_fdr.pak.ps2`
  - current CLI result: `PAK archive detected`, `Found 6 files`
  - `z_bedroom.pak.ps2`
  - current CLI result: `PAK archive detected`, `Found 7 files`
- TH Proving Ground PS2 sky family
  - `a_bcity_sky.pak.ps2`
  - current CLI result: `PAK raw data file`

That confirms the practical compatibility boundary:

- the current parser is already working for:
  - THAW PS2 archive families covered by the tests
  - THP8 PS2 `0x00/0x20`-style packs
  - at least some THPG PS2 non-sky packs
- the current parser is **not** broad enough for additive-flag families like
  the THPG PS2 sky packs, where the first table entries use `0x01/0x21`

So the safest future support strategy is:

- preserve the already-working compact/full-entry families
- broaden flag acceptance in a way that still keeps `0x20` as the structural
  full-entry bit
- avoid a parser rewrite that would destabilize the existing THAW/THP8/THPG
  non-sky success cases just to handle one new additive-flag family

### Phase 93: Repo-Wide Compatibility Contracts Are Broader Than Parsing

A focused repo-usage pass makes the compatibility target more concrete.

`PakArchive` changes can break consumers even if entry parsing itself becomes
more correct, because the repo depends on three separate contracts:

1. classification
   - `PakArchive.IsPakArchive(...)` drives format probing, recursive unpacking,
     CLI archive selection, and raw-data rejection
   - `RecursiveUnpacker` is also string-coupled to the literal label
     `PAK (raw)`
2. naming/path shape
   - extracted files are rooted under `Path.GetFileNameWithoutExtension(pakPath)`
   - embedded paths are normalized
   - unnamed entries use the current `QbKey`/hex fallback naming strategy
   - multiple tests/tools hard-code names like `0009BF70.tex` and
     `003B9540.mdl`
3. extraction/list coupling
   - `GetFileList(...)` and `ExtractFiles(...)` are treated as a matched pair by
     the CLI and tests
   - count/order drift can therefore show up as subtle progress/reporting or
     downstream lookup regressions even when parsing still “works”

That means a safe THAW PAK implementation should try to change the minimum
semantic surface:

- widen recognition/parsing only where the table structure is actually proven
- keep the current output naming/root conventions unless there is a separate
  deliberate migration
- preserve current raw-vs-archive behavior for known THAW samples
- add tests before parser changes for:
  - shell-family extraction correctness, not just entry count/path names
  - `RecursiveUnpacker` `.pak` classification behavior
  - at least one additive-flag family currently rejected as raw

This is enough for a high-confidence implementation plan:

- the likely safe code change is not “THAW special mode”
- it is a narrower structural parser correction that:
  - keeps `0x20` as the entry-shape bit
  - stops treating low flag values as an exact enum
  - does not globally swap `offset/size` just because `0x10` is present
  - leaves output naming/classification contracts intact unless tests are
    explicitly updated

### Phase 94: The Remaining Zone Decode Gap Is the Record-Backed Reconcile Step

The newest raw-image and vtable tracing tightens the next decoder target.

What is now solid:

- `FUN_0019c620(...)` is the key reconcile step between a concrete raw-image
  object and the record-backed zone entry:
  - it compares wrapper compatibility through:
    - `+0xE4` = base texel format / bits-per-pixel
    - `+0x104` = mip-count compatibility
    - `+0x10C` = one-bit mode gate
  - it later uses `+0xFC` as the CLUT-format getter to decide whether palette
    bytes can be copied directly or must be converted between CT16 and CT32
- in record-backed mode, the meaningful prepared-source analogue is:
  - `secondary + 0x28` = prepared pixel/mip buffer
  - `secondary + 0x30` = prepared CLUT / aux buffer
- `FUN_001ea488(...)` is the pixel-side worker that fills `secondary + 0x28`
  from a source raw-image pixel pointer
  - no high transform bits for the mip -> direct copy
  - `0x02000000 << mip` -> `FUN_001c8218(...)` / `FUN_001c8678(...)`
  - `0x00040000 << mip` -> `FUN_001c8b28(...)`

The practical implication for the current decoder is important:

- the current file-backed prepared-source path still decodes public slot bytes
  directly
- that bypasses the only confirmed runtime path here that can:
  - reconcile pixels into the record-backed `+0x28` buffer, and
  - convert CLUT bytes into the record-backed `+0x30` buffer when source and
    destination CLUT formats differ

So the remaining bug is no longer best framed as “find another swizzle table.”
It is better framed as:

- either build the record-backed `+0x28/+0x30` analogue in C# by porting the
  `FUN_0019c620(...)` / `FUN_001ea488(...)` behavior closely enough, or
- find the still-missing source-object path that proves the public slot bytes
  are not the raw-image source consumed by that reconcile step

One caution from the constructor pass:

- the raw-image family does not expose a single universal `+0x10/+0x28`
  meaning
  - `FUN_001e61b8(...)` binds `+0x18/+0x14` into caller-owned memory and leaves
    `+0x10/+0x28` zero
  - `FUN_001e6338(...)` allocates an owned prepared-source block into `+0x10`
    and stores its size in `+0x28`
- but the actual decode-relevant payload contract remains stable:
  `+0x18/+0x14/+0x20/+0x1C`

That weakens the “hidden third object shape” theory and strengthens the
record-backed reconcile theory.

### Phase 95: Reconcile Source Is a Standard Raw Image, but the Caller Is Still Hidden

The next pass tightened both sides of the reconcile model.

What is now solid:

- `FUN_0019c620(...)` does **not** accept a pure record-backed source wrapper.
  Its source-resolution logic only handles:
  - `flags & 1` -> direct raw-image backing at `wrapper + 0x10`
  - `flags & 4` -> indirect/decode wrapper whose first slot points at a
    concrete raw image
  It then immediately dereferences the source payload at
  `raw_image + 0x14/+0x18/+0x1C/+0x20`.
- So the source side of reconcile is the standard raw-image family, not another
  hidden object format.
- The viable source producers are:
  - `FUN_0019c4c8 -> FUN_001e5cc8(...)`
  - `FUN_0019c538 -> FUN_001e5dc8(...)`
  - `FUN_0019c5b0 -> FUN_001e5f98(...) -> FUN_001e6338(...)`
  - `FUN_0019bbd8(...) -> FUN_001e6030(...)` clone/copy path
- The destination-side split in `FUN_0019c620(...)` is now clean:
  - non-record-backed destination -> copy into destination raw-image
    `+0x14/+0x18`
  - record-backed destination -> populate `secondary + 0x28/+0x30`
- The inherited slot identity is confirmed:
  - owner `DAT_004b4170 + 0x9C`
  - image sub-vtable `DAT_004b41c8 + 0x44`
  both resolve to `FUN_0019c620(...)`.

What is still missing:

- there are still no direct xrefs to the slot address `004B420C`
- `FUN_001a07c8(...)` is only the owner clone slot and just allocates a wrapper
  then calls `FUN_0019bbd8(...)`
- `FUN_0019bbd8(...)` is only a clone/copy constructor and never calls
  `FUN_0019c620(...)`

That means the unresolved gap is now very specific:

- the missing call site is probably a generic owner/image copy or assignment API
  that dispatches through inherited vtable slots, not another zone-specific
  loader or another hidden pixel-object family.

So the best next target is no longer “what is the source object?” That part is
now narrow enough. The best next target is the higher-level generic caller that
chooses one of the raw-image constructors/clones above and then invokes the
inherited reconcile slot.

### Phase 96: Owner Child-Hash Branch Is Only Table Maintenance

The owner child-hook branch is now closed much more cleanly.

New concrete decomp:

- `FUN_0016a718(...)` only removes a child entry from the owner-side hash table
  by checksum and decrements the count.
- `FUN_0016a7f8(...)` only initializes or adopts the owner-side child hash
  table:
  - allocates `bucket_count * 0x0C` bytes when needed
  - zeroes each bucket via `FUN_0016a7d0(...)`
  - stores the bucket base in `table + 0x04`
- `FUN_0016a7d0(...)` is just the bucket-zero helper
- `FUN_0016a5e8(...)` frees chained overflow nodes and clears the buckets

That means the surrounding owner helpers are exactly what they look like:

- `FUN_0016a220(...)` = allocate/size owner child hash table
- `FUN_0016a010(...)` = insert child by checksum, then notify owner container
- `FUN_00169988(...)` = walk existing children and remove missing ones by
  checksum through `FUN_0016a718(...)`

So this branch does **not** build concrete raw-image wrappers and does **not**
invoke the inherited reconcile slot.

The owner-container callback rooted at `DAT_004af8b8` is also now effectively
closed:

- `DAT_004af8b8 + 0x3C -> 0x0016A590`
- raw instruction dump shows `0x0016A590` is just `jr ra; nop`
- the sibling slots in that same tiny vtable block are similarly trivial stubs

So the `FUN_0016a010(...) -> owner-container +0x3C` path is not the missing
bridge into `FUN_0019c620(...)`.

This removes the last realistic chance that the owner child-table management
branch was secretly escalating into image reconcile.

### Phase 97: Generic RGBA Callers Exist, but Reconcile Caller Is Still Missing

One new xref pass gives a better pivot on the generic image side.

Direct xrefs now confirm:

- `FUN_0019c620(...)` still has no direct callers in the current coverage
- `FUN_0019fcc0(...)` also has no direct callers in the current coverage
- the generic RGBA block APIs do have a concrete caller family:
  - `FUN_00165f00(...)`
  - `FUN_00166690(...)`
  - `FUN_00166a88(...)`
  - `FUN_00166d28(...)`

Those functions are therefore better next targets than the owner child-table
branch, because they are proven higher-level generic image operators that sit
above `FUN_00169018(...)` / `FUN_00169208(...)`.

The important boundary remains unchanged:

- visible generic image operators exist
- visible owner child-table maintenance exists
- neither currently exposes the higher-level caller that dispatches through the
  inherited reconcile slot `owner + 0x9C` / `image + 0x44`

So the best next decomp target is now the caller family
`FUN_00165f00/00166690/00166a88/00166d28`, or another generic image-assignment
layer adjacent to them, not more work on owner child bookkeeping.

### Phase 98: The First Generic RGBA Caller Family Is Also a Dead End

The newly decompiled generic RGBA callers do not lead to reconcile either.

What they actually are:

- `FUN_00165f00(...)`:
  - snapshots a rectangular RGBA region with `FUN_00169018(...)`
  - writes it back with `FUN_00169208(...)`
  - optionally walks the affected region and calls `FUN_00169648(...)` per
    pixel
- `FUN_00166690(...)`:
  - reads a region to RGBA
  - splits/warps it through temporary RGBA buffers with
    `FUN_00168260(...)`, `FUN_001685f0(...)`, `FUN_00168cf0(...)`,
    `FUN_00168428(...)`
  - writes the result back through `FUN_00169208(...)`
- `FUN_00166a88(...)`:
  - reads a strip to RGBA
  - runs it through `FUN_001685f0(...)` / `FUN_00168428(...)`
  - writes it back
- `FUN_00166d28(...)`:
  - reads a strip to RGBA
  - processes it through `FUN_001685f0(...)` / `FUN_00168428(...)`
  - optionally fills the temporary region with a solid color before writing back

So this whole family is built on the visible RGBA block APIs
`FUN_00169018(...)` / `FUN_00169208(...)`, not on the hidden reconcile slot.

The shared direct caller `FUN_00141ba0(...)` confirms the family semantics even
more strongly:

- it uses globals like `DAT_0053dc90/94/...` as image-space bounds
- it calls `FUN_00165f00(...)`, `FUN_00166a88(...)`, and `FUN_00166d28(...)`
  to crop/pad/fill around a bounding region
- it passes a caller-provided solid color through to the fill-capable helper

That means this branch is generic image border/padding work, not the missing
runtime path into `FUN_0019c620(...)`.

So the practical narrowing is:

- owner child-table branch: closed
- first generic RGBA caller family: closed
- inherited reconcile slot `FUN_0019c620(...)`: still has no direct callers in
  the current coverage

The next best target is therefore no longer this RGBA helper family. It should
be a different higher-level image/object assignment family adjacent to, but not
inside, these generic RGBA operations.

### Phase 99: `FUN_001e6818` Is Not the Missing Record-to-Raw Bridge

The `FUN_001e6818(...)` branch is now closed as a reconcile/source-selection
candidate.

What it actually does:

- it is only called by the standard raw-image constructors
  `FUN_001e61b8(...)` and `FUN_001e6338(...)`
- it fills the normal raw-image source fields:
  - `raw + 0x14`
  - `raw + 0x18`
  - `raw + 0x1C`
  - `raw + 0x20`
- its `raw + 0x30` write is a packed descriptor/setup payload, not another
  pixel-source pointer

That means this family is just building the already-known standard raw-image
shape consumed by `FUN_0019cd48(...)`, `FUN_00169018(...)`, and the raw-source
arm of `FUN_0019c620(...)`.

What it does **not** do:

- it does not consume record-backed `secondary + 0x28/+0x30`
- it does not bridge record-backed entries into raw-image `+0x14/+0x18`
- it does not expose a hidden source-selection step before
  `FUN_0019c620(...)`

So the unresolved bridge still sits in wrapper/object code above the standard
raw-image constructors, not in `FUN_001e6818(...)` itself.

### Phase 100: Transform-Bit Handling Still Only Sees the High Families

The transform-bit path is also tighter now.

`FUN_001ea3b8(...)` and `FUN_001ea488(...)` still only treat
`secondary + 0x0C` as a per-mip high-family bitfield:

- `0x02000000 << mip`
- `0x00040000 << mip`

and otherwise fall back to straight copy.

There is still no decomp-backed consumer in this path for the low layout bits
that would distinguish values like:

- `0x02000001`
- `0x02000005`
- `0x00040007`

The source selection in `FUN_0019c620(...)` happens **before**
`FUN_001ea488(...)`, through wrapper flags and wrapper payload shape, not by
decoding low bits from `secondary + 0x0C`.

So this pass adds more negative evidence against the current low-bit heuristic
layout layer being a faithful runtime model.

### Phase 101: The Image Slot Band Now Has a Concrete Wrapper Family

The biggest structural gain this pass is that the “mysterious isolated image
API surface” is no longer isolated.

New indirect-call scan:

- `DAT_004b41c8 + 0x44 -> FUN_0019c620(...)`
- `DAT_004b41c8 + 0x4C -> FUN_0019cca0(...)`
- `DAT_004b41c8 + 0x6C -> FUN_0019d060(...)`
- `DAT_004b41c8 + 0xBC -> FUN_0019cd48(...)`
- `DAT_004b41c8 + 0xF4 -> FUN_0019f9e0(...)`
- `DAT_004b41c8 + 0x114 -> FUN_0019fcc0(...)`
- owner-side equivalents also exist, including `DAT_004b4170 + 0x9C`

The thin free-function wrapper band is now explicit:

- `FUN_00164ce0(...)` -> image slot `+0x44`
- `FUN_00164d08(...)` -> image slot `+0x4C`
- `FUN_00164d58(...)` -> image slot `+0x6C`
- `FUN_00167e90(...)` -> image slot `+0x9C`
- `FUN_00167f08(...)` -> image slot `+0xBC`
- `FUN_00168000(...)` -> image slot `+0xF4`
- `FUN_00168058(...)` -> image slot `+0x114`

There is also a small adapter/object family above that:

- `FUN_0016c670(...)`
- `FUN_0016c8e8(...)`
- `FUN_0016cae0(...)`
- `FUN_0016d398(...)`
- `FUN_0016d410(...)`
- `FUN_0016d4b0(...)`
- `FUN_0016d550(...)`

Those adapters prove the slot band is part of a real generic object/image API,
not dead decomp surface. In particular, the lack of direct xrefs to
`FUN_0019c620(...)` is now explained: the runtime often reaches it through
wrapper-forwarders like `FUN_00167e90(...)` / `FUN_0016d4b0(...)`, not by
calling the slot target directly.

What still remains unresolved:

- the currently exposed callers into `+0x9C` / `+0xBC` look like generic
  object/image pipelines, not an obviously zone-specific prepared-source path
- `FUN_0019c620(...)` is therefore no longer “unreachable”, but the specific
  zone/runtime family that uses it for THAW zone TEX is still not singled out

The best next decomp target is now a larger caller that touches multiple image
slots from the same object. `FUN_0018a988(...)` is the strongest current
candidate because the indirect-call scan shows it dispatching through:

- `+0x4C`
- `+0x6C`
- `+0x9C`
- `+0xBC`
- `+0x114`

from a single image-bearing object at `s0 + 0x1F8`.

### Phase 102: Direct Wrapper Callers Are Live, But Still Generic

The next direct-caller pass closed the immediate wrapper-xref branch without
finding a zone-specific prepare/reconcile path.

Artifacts:

- `phase111_wrapper_direct_callers.c`
- `run_phase111_wrapper_direct_callers.sh`

The strongest concrete caller families now look like this:

- `FUN_0014adc0(...)` and `FUN_0015ab18(...)` are bounds/stat aggregation over
  child image objects. They repeatedly use `FUN_0016d4b0(...)` (`+0x9C`) to
  read per-child bounds and combine them into aggregate min/max extents.
- `FUN_0015bc78(...)`, `FUN_0015bd38(...)`, `FUN_0015bb00(...)`,
  `FUN_0015bb90(...)`, and `FUN_00170840(...)` are thin wrapper/iterator
  helpers around that same child-image family. They do not look zone-related.
- `FUN_002feed8(...)` is a serializer/export path. It:
  - gets an image object by checksum
  - writes a header or prefix via `FUN_00167ee0(...)`
  - gets payload offset via `FUN_00167f08(...)`
  - gets payload pointer via `FUN_00167e90(...)`
  - gets payload size via `FUN_00167eb8(...)`
  - copies the payload into a stack buffer and emits it through
    `FUN_00142960(...)`
- `FUN_002f3860(...)` is the most important concrete live path for this API
  surface. It:
  - builds image objects
  - applies generic image edits (`FUN_00165f00(...)`, rotate/scale/flags)
  - copies metadata through `FUN_00167fd8(...) -> FUN_00168000(...)`
  - repeatedly attaches images through `FUN_00168058(...)`
  - finally calls `FUN_00164ce0(...)` once on a newly created `0x80 x 0x80`
    image
- `FUN_001402b8(...)`, `FUN_002e4068(...)`, `FUN_00141ac0(...)`, and
  `FUN_00142080(...)` match the same generic family: create image objects from
  config/checksum records, optionally attach one image to another with
  `FUN_00168058(...)`, then finalize/export with `FUN_00164d58(...)`.

This gives one useful positive result and one useful negative result:

- positive: `FUN_0019c620(...)` is definitely live at runtime through
  `FUN_00164ce0(...)`
- negative: every currently exposed caller into this wrapper band still looks
  like generic image serialization, composition, or export rather than the THAW
  zone owner/blob path

So this branch is no longer a mystery, but it is still not the missing zone TEX
prepare/reconcile path.

The best next targets are now the still-unresolved indirect `+0x44` sites from
the broader slot scan, especially:

- `FUN_0016f760(...)`
- `FUN_0016fae8(...)`
- `FUN_00170728(...)`
- `FUN_00172f78(...)`
- `FUN_0018dba8(...)`

Those are the best remaining candidates for a non-generic caller into
`FUN_0019c620(...)`.

### Phase 103: The Broad `+0x44` Scan Is Mostly False Positives

The next decomp passes closed most of the remaining `+0x44` branch as unrelated
object-family reuse.

Artifacts:

- `phase112_indirect_reconcile_sites.c`
- `phase112_xrefs.txt`
- `phase113_indirect_reconcile_callers.c`
- `phase114_wrapper_shape_44_sites.c`
- `phase115_fun_1a50d8.c`
- `run_phase112_indirect_reconcile_sites.sh`
- `run_phase113_indirect_reconcile_callers.sh`
- `run_phase114_wrapper_shape_44_sites.sh`

The strongest correction is structural:

- `+0x44` is not unique to the real image-wrapper family
- most of the unresolved `phase107` hits were on completely different object
  layouts, especially vtable loads from `object + 0x18`, `object + 0x14`,
  `object + 0x0C`, or `object + 0x90`

What the `object + 0x18` branch actually is:

- `FUN_0016f760(...)`, `FUN_0016fae8(...)`, `FUN_00170728(...)`, and
  `FUN_00172f78(...)` are child-list color/state propagation helpers
- they pass packed RGBA words such as `0xFFRRGGBB` or `0x81808080` into the
  `+0x44` call
- `FUN_00172f78(...)` is a percentage/range-driven color sweep across entries
- `FUN_0018dba8(...)` is just a small status/query helper in that same
  non-image family

Their callers confirm the same classification:

- `FUN_0021bbd0(...)` and `FUN_0021bca8(...)` parse color components from data
  records and push them into those helpers
- `FUN_0013f9e8(...)` and `FUN_002f04d0(...)` are config-driven setup/update
  paths that also drive `FUN_0016fae8(...)`
- `FUN_00172d98(...)` and `FUN_00172ca8(...)` are global progress/update paths
  over those child lists
- `FUN_00210928(...)` is a larger config/state routine that still uses the same
  color-propagation helper family rather than the zone texture owner/blob path

The tighter “wrapper-shape” pass also stayed negative:

- `FUN_0015aab0(...)`, `FUN_0015bd88(...)`, `FUN_0020e028(...)`,
  `FUN_0020e310(...)`, `FUN_0023bcc8(...)`, `FUN_002bada8(...)`, and
  `FUN_001a50d8(...)` all use `+0x44`, but they do not expose the
  `FUN_00164ce0(...) -> FUN_0019c620(...)` prepared-source reconcile path
- several are pointer/status gates or state resets
- `FUN_0020e028(...)` / `FUN_0020e310(...)` copy larger ready-made object data
  blocks after `+0x44` / `+0x74` existence checks, which is still not the zone
  image-reconcile path

So the conclusion from this whole branch is:

- broad offset-based hunting for `+0x44` is now low-signal
- the real image-wrapper `+0x44` path is still best represented by the exact
  wrapper function `FUN_00164ce0(...)`
- the newly exposed indirect callers are mostly unrelated object/state systems,
  not the THAW zone texture prepare/reconcile path

That means the next decomp target should stop following raw `+0x44` matches and
instead go back to one of two tighter directions:

- exact wrapper-family callers around the real image-wrapper object shape, or
- the zone owner/wrapper construction path itself, now that the broad slot scan
  has mostly been exhausted as noise

### Phase 104: The `0x0049D59C` Branch Is a Scene-Command Registry, Not a Hidden Decoder Bridge

The new owner-load branch is now classified much more cleanly.

Artifacts:

- `phase120_owner_setup_bridge.c`
- `phase121_owner_setup_table_xrefs.txt`
- `phase122_owner_setup_table_ptrs.txt`
- `phase123_owner_setup_table_mem.txt`
- `phase124_scene_command_family.c`
- `phase125_scene_command_xrefs.txt`
- `phase126_shared_hook_tail.c`
- `phase127_shared_hook_tail_xrefs.txt`
- `run_phase120_owner_setup_bridge.sh`
- `run_phase124_scene_command_family.sh`
- `run_phase126_shared_hook_tail.sh`

The important positive result:

- `0x0049D59C` is not a standalone hidden callback slot
- it is the function half of a flat `string_ptr, function_ptr` command table
- the local slice is explicitly:
  - `UnloadAllLevelGeometry -> FUN_00290018(...)`
  - `QuickReload -> FUN_00290420(...)`
  - `UnloadScene -> FUN_00290388(...)`
  - `LoadScene -> FUN_00290038(...)`
  - `LoadCollision -> FUN_00290298(...)`
  - `AddScene -> FUN_00290208(...)`
  - `AddCollision -> FUN_00290308(...)`
  - `ToggleAddScenes -> FUN_001580B8(...)`

The memory dump at `0x004CFA18` also tightens the `LoadScene` path:

- `"levels\\%s\\%s%s.tex"`
- suffix choice is `""`, `"_net"`, or `"_sky"`

So `FUN_00290038(...)` is a real scene/level owner-load command, not an odd
one-off wrapper.

What `LoadScene` actually does:

- parses scene/config flags
- formats `levels\\%s\\%s%s.tex`
- loads the `.tex` owner with `FUN_0016ad60(...)`
- then calls the shared hook `FUN_00157118(...)`

The shared hook is now explicit:

- `FUN_00157118(...)` calls `FUN_00157040(...)` to build the companion path
- opens a scoped load context with `FUN_0011df18(...)`
- calls `FUN_00197ff0(...)`
- closes the scope with `FUN_0011df20(...)`
- then calls `FUN_00157248(...)`

The tail of that path is also now classified:

- `FUN_00197ff0(...)` is just a tiny generic helper:
  - `FUN_00120b20(...)`
  - then `FUN_00197eb0(...)`
- `FUN_00157248(...)` stamps the returned object with scene checksum/path state
  and links a small node into the global scene list rooted at `DAT_0053E2D0 /
  DAT_0053E2E0`

So the `LoadScene` branch is not secretly building the prepared pixel/CLUT
source object or doing zone-texture-specific decode work. It is front-end
scene/owner setup plus companion object install.

### Phase 105: `QuickReload` Reuses Existing Scene Owner State, But Still Feeds the Same Shared Hook

`FUN_00157528(...)` is not a fresh file/path loader.

It:

- gets an existing owner-like object from `FUN_00157610(...)`
- reads `owner + 0x08`
- reads bit 1 from `owner + 0x0C`
- tears down or detaches through `FUN_00157450(owner, 1)`
- then calls:
  - `FUN_00157118(param_1, owner->8, !bit1, bit1, 0, 0)`

`FUN_00290420(...)` is just the table entry that drives that path after checking
for the scene checksum argument.

That matters because it closes the earlier ambiguity:

- `FUN_00290038(...)` is the real fresh `LoadScene` path
- `FUN_00157528(...)` is a reload/reuse path hanging off `QuickReload`
- both converge on the same scene/companion install hook
- neither one exposed a hidden byte transformation or a late promotion step
  into the raw-image reconcile family

This is a useful negative result for the decoder investigation:

- the scene-command branch around `LoadScene` / `QuickReload` does not explain
  the remaining zone TEX decode mismatch
- it reinforces the earlier conclusion that the visible loader path is still
  loading the public `.tex` owner blob directly and then installing companion
  scene objects around it

So this whole branch should now be considered mostly closed for decoder
purposes. The next best target is back on the owner/image side or the exact
record-backed reconcile/source-selection path, not more work on the scene
command table.

### Phase 106: `FUN_0018A988(...)` Still Looks Like the Best Live Wrapper-API Lead

After closing the scene-command branch, I checked the strongest remaining
multislot wrapper candidate again.

Artifacts:

- `phase128_fun_18a988_xrefs.txt`
- `phase129_multislot_owner_caller.c`
- `phase130_multislot_owner_caller_xrefs.txt`
- `run_phase129_multislot_owner_caller.sh`

The useful new fact is narrow but concrete:

- `FUN_0018A988(...)` currently has one direct caller in coverage:
  - `FUN_00345EB8(...)`

That caller is small:

- enters the standard scoped context with `FUN_0011ca28(...)`
- calls `FUN_00345F20(...)`
- calls `FUN_0014DBB8(DAT_00498850)`
- then calls `FUN_0018A988(DAT_00498878)`
- exits the scope with `FUN_0011ca50(...)`

So the multislot wrapper branch is still live, but the exposed caller is still
too thin to classify. The next useful target there is now:

- `FUN_00345F20(...)`
- the owner/identity of `DAT_00498878`
- or the table/dispatcher around `FUN_00345A70(...)`

This does not yet beat the earlier scene-command closure in value, but it is
now the best exact live caller on the wrapper-API side.

### Phase 107: The `DAT_00498878` Multislot Branch Is Another Non-Decoder System

The next wrapper-API branch is now also mostly closed.

Artifacts:

- `phase131_global_object_family.c`
- `phase132_global_object_family_xrefs.txt`
- `phase133_vtable_base_init.c`
- `phase134_vtable_4b2550.txt`
- `phase135_multislot_vtable_methods.c`
- `phase131_live_wrapper_branch.c`
- `phase132_multislot_base_ctor.c`
- `run_phase131_global_object_family.sh`

The key structural result:

- `DAT_00498878` is allocated once during `FUN_001978A8(...)`
- the allocation is `0x200` bytes
- the block is passed to `FUN_0018CEA8(...)`
- `FUN_0018CEA8(...)` only:
  - runs `FUN_0018A888(...)`
  - stores vtable `DAT_004B2550` at `+0x1F8`

So `DAT_00498878` is a preallocated global object, not something loaded from
zone/scene file data.

The base ctor also makes the object shape much clearer:

- `FUN_0018A888(...)` initializes five paired embedded subrecord families
- it stores base vtable/state at `+0x1F8`
- `FUN_0018A988(...)` then walks five structured config blocks:
  - `+0x10`
  - `+0xB0`
  - `+0x100`
  - `+0x160`
  - `+0x1B0`
- and dispatches through that vtable to apply the parsed values

The vtable itself closes the classification:

- `FUN_0018CFA8(...)` scales normalized coordinates to:
  - `640 x 448` when `DAT_004985D0 == 1`
  - otherwise `512 x 512`
- then calls `FUN_001C2CC0(..., DAT_004997A4)`
- sibling methods call:
  - `FUN_001C2CE8(DAT_004997A4)`
  - `FUN_001C2D08(DAT_004997A4, ...)`
  - `FUN_001C2D18(DAT_004997A4)`
  - `FUN_001C2D58(DAT_004997A4, ...)`

That is strong evidence for a generic display/UI/render-control style object,
not raw-image decode or zone texture state.

The surrounding live branch is also generic:

- `FUN_00345A70(...)` allocates a `0x24` callback record and stores
  `FUN_00345EB8(...)` into it
- `FUN_00345EB8(...)` calls:
  - `FUN_00345F20(...)`
  - `FUN_0014DBB8(DAT_00498850)`
  - `FUN_0018A988(DAT_00498878)`
- `FUN_00345F20(...)` only does global engine/state work around:
  - `DAT_0049C3F0`
  - `DAT_004A0D04`
  - `DAT_004A0CE8`
  - `DAT_0049AD70`

So this branch is now another useful negative result:

- it is live
- it uses the same broad wrapper/vtable style
- but it is still not in the zone texture decode family

That means the strongest remaining target is no longer the generic multislot
wrapper API. The next best target is back to the real record-backed
reconcile/source-selection problem:

- the zone-side caller that truly bridges into `FUN_0019C620(...)`
- or the exact upstream source path feeding `FUN_001EA488(...)`

### Phase 108: A Named Target/Source Reconcile Chain Is Finally Visible

This pass produced the strongest concrete source-selection bridge so far.

Artifacts:

- `phase136_owner_adjacent_reconcile_callers.c`
- `phase137_owner_adjacent_reconcile_xrefs.txt`
- `phase138_reconcile_immediate_callers.c`
- `phase139_reconcile_immediate_xrefs.txt`
- `phase140_fun_13ff18_xrefs.txt`
- `phase141_fun_13ff18_callers.c`
- `phase142_manager_branch.c`
- `phase143_manager_branch_xrefs.txt`
- `phase144_lookup_helper.c`
- `phase145_lookup_helper_tables.txt`
- `run_phase136_owner_adjacent_reconcile_callers.sh`

The key structural chain is now:

- `FUN_0013FF18(...)`
- `FUN_0016F3C0(...)`
- `FUN_0016A2D0(...)`
- `FUN_00164CE0(...)`
- `FUN_0019C620(...)`
- `FUN_001EA488(...)`

Why that matters:

- `FUN_0013FF18(...)` lives in the same decomp slice as the visible `%s.tex.%s`
  loader path
- it repeatedly:
  - queries keyed entries from a config object via `FUN_0013E130(...)`
  - loads/caches a `.tex` payload through `FUN_00140AB8(...)`
  - then immediately calls `FUN_0016F3C0(DAT_00498800, ...)`

So this is no longer just a generic image-builder chain. It is a named
target/source application path built directly around loaded `.tex` payloads.

What the middle of the chain does:

- `FUN_0016F3C0(...)` normalizes two names, iterates a child list under
  `param_1 + 0x10`, filters by entry id or wildcard, and only on matching
  entries calls `FUN_0016A2D0(...)`
- `FUN_0016A2D0(...)` is the clearest reconcile adapter seen so far:
  - finds an existing destination with `FUN_0016A100(...)`
  - builds a temporary source image with `FUN_00169B98(...)`
  - immediately reconciles the pair through `FUN_00164CE0(...)`
  - releases the temporary source through `FUN_00169FA0(...)`

That is the first decomp-backed path here that looks like:

- resolve destination by symbolic name
- resolve/build source by symbolic name
- apply runtime reconcile into the destination

The lookup side also now makes sense:

- `FUN_0016A4A8(...)` is just a hash-table lookup in an owner/manager
- `FUN_0016A100(...)` tries transformed checksum variants using the table:
  - `1`
  - `2`
  - `3`
  - `4`
  - `6`
  - `0x0C`
  - `8`
  - `0x18`
- then falls back to a stripped-name variant path

So `FUN_0016A100(...)` looks like name-to-destination resolution within one
owner/manager, not raw offset math.

The manager side is also visible now:

- `FUN_0016E758(...)` allocates/records list entries under `DAT_00498800`
- in one arm it loads an asset from path via `FUN_0016AEC0(...)` and
  `FUN_00157AA8(...)`
- in the other arm it resolves existing entries through the broader manager
  layer
- `FUN_0013EF48(...)` and `FUN_0013FF18(...)` both feed this same manager
  after calling `FUN_00140AB8(...)`

This does **not** prove that the THAW zone decoder bug is solved by this branch.
But it is the first branch that plausibly explains a missing runtime
source-selection step:

- the game may be reconciling pre-existing destination wrappers with temporary
  source images selected by symbolic names from loaded `.tex` content
- that is much closer to the observed “public slot bytes alone are not enough”
  problem than the earlier generic image/export or scene-command branches were

The best next target is now narrower than before:

- classify `DAT_00498800` and the keyed config object behind `DAT_00498808`
- or classify the sibling chain through `FUN_0013EF48(...)` / `FUN_0016E758(...)`
  to determine whether this is still generic alias/replacement machinery or the
  exact zone runtime source-selection system

### Phase 109: `DAT_00498800` Looks Like a Broader Named-Asset Manager

One useful caution from the next pass: the `DAT_00498800` branch is broader than
just reconcile.

Artifacts:

- `phase146_manager_siblings.c`

The sibling helpers confirm that the same manager/list family also does generic
named state updates:

- `FUN_0016FAE8(...)`:
  - resolves one or more named entries from the same manager/list
  - computes a packed `0xFFRRGGBB` color
  - applies it either directly through the child object's `+0x44` method or
    through `FUN_0016D3D0(...)`
- `FUN_0016FE88(...)`:
  - resolves the same named entries
  - either calls `FUN_0016D370(...)` directly
  - or applies a default `0x80808080` packed value through
    `FUN_0016D3D0(...)`

So `DAT_00498800` is not a narrow “zone decode bridge object.” It is a broader
named-asset/named-child manager that supports at least:

- adding/loading entries (`FUN_0016E758(...)`)
- symbolic target/source reconcile (`FUN_0016F3C0(...) -> FUN_0016A2D0(...)`)
- symbolic direct reconcile (`FUN_0016F558(...) -> FUN_0016A378(...)`)
- symbolic color/default-state updates (`FUN_0016FAE8(...)`, `FUN_0016FE88(...)`)

That does not weaken the reconcile finding. It just sharpens the interpretation:

- the chain through `FUN_0013FF18(...) -> FUN_0016F3C0(...) -> FUN_0016A2D0(...)`
  is still the strongest live source-selection bridge in current coverage
- but it is likely one feature of a larger named-asset manager, not a
  dedicated zone-only runtime

So the next best target is still the same, but with better framing:

- classify the keyed config object behind `DAT_00498808`
- or identify the higher-level subsystem that uses `DAT_00498800` for these
  named target/source operations, then decide whether the zone TEX runtime is
  one client of that subsystem or something else entirely

### Phase 110: `DAT_00498808` Is a Temporary Keyed-Config Interpreter Around `DAT_00498800`

The next pass closes most of that question.

Artifacts:

- `phase148_manager_setup_band.c`
- `phase147_manager_globals_xrefs.txt`
- `phase142_manager_branch.c`
- `phase146_manager_siblings.c`
- `phase144_lookup_helper.c`
- `phase145_lookup_helper_tables.txt`

What `FUN_0013E4D0(...)` actually does:

- stores the caller-supplied manager/object pointers into globals:
  - `DAT_00498800 = param_3`
  - `DAT_00498804 = param_4`
  - `DAT_0049880C = param_5`
- allocates a `0x7c` object and constructs it through `FUN_0013DBC8(...)`
- stores that object in `DAT_00498808`
- calls `FUN_0013DD98(...)` and then `FUN_0013DC80(DAT_00498808, param_2)` to
  load/initialize it from keyed data
- invokes `FUN_002BA9D8(param_6, 0, param_1, 0, 0, 0)`
- then tears the whole temporary setup back down and clears all four globals

So `DAT_00498808` is not a persistent zone owner. It is a temporary keyed
config/dispatch object that is installed just long enough for one higher-level
callback/script-style execution.

What `FUN_0013E5E8(...)` is:

- a large hash-dispatch method on that `DAT_00498808` object
- for many hashes it simply forwards to methods out of its local vtable at
  `param_1 + offset`
- for the decoder-relevant hashes it reaches back into the globals and drives
  the named manager:
  - `FUN_0013EF48(...)` -> `FUN_0016E758(DAT_00498800, ...)`
  - `FUN_0013F9E8(...)` -> `FUN_0016FAE8(...)` / `FUN_0016FE88(...)`
  - `FUN_0013FF18(...)` (from earlier phases) -> `FUN_0016F3C0(DAT_00498800, ...)`
  - it also does keyed lookups through `FUN_0013E130(...)`, `FUN_0013E180(...)`,
    and `FUN_0013E3F8(...)`
- one branch (`0x50F1285B`) directly multiplies a 4-float vector from
  `DAT_00498800 + 0x20` and pushes it through `FUN_001708B0(DAT_00498800, ...)`
- another branch (`0xC0BC8271`) routes through `DAT_00498804` via
  `FUN_0013BA80(...)`

That makes the roles much sharper:

- `DAT_00498800`: active named-asset/named-child manager target for the current
  config execution
- `DAT_00498808`: temporary keyed-config interpreter object for that execution
- `DAT_00498804`: secondary callback/helper object used by at least one config
  opcode
- `DAT_0049880C`: additional transient context pointer; still not fully typed,
  but only lives for the duration of the same wrapper call

The nearby clients in the `0x0013Exxx-0x00140xxx` band confirm that this is
broader than a zone-specific loader and broader than a narrow UI-only helper:

- `FUN_0013EF20(...)` / `FUN_00140990(...)` clear or reset the active manager
- `FUN_0013EF48(...)` builds named entries under `DAT_00498800`
- `FUN_0013F9E8(...)` applies symbolic color/default-state changes across named
  entries
- `FUN_001409B8(...)` runs a general keyed traversal over data found through
  `DAT_00498808`
- earlier clients like `FUN_001402B8(...)` show the same manager family being
  used for generic image/config assembly paths, not zone-only runtime state

So the strongest interpretation now is:

- this band is a generic keyed-config + named-asset/image manager subsystem
- zone texture reconcile/source-selection is one capability exercised through
  it, not the identity of the subsystem itself

The most decoder-relevant implication is:

- `DAT_00498800` / `DAT_00498808` are not the missing zone-only runtime object
- if the zone path uses this subsystem, it likely enters through a specific
  higher-level `FUN_0013E4D0(...)` setup call and config payload, not through a
  dedicated zone-exclusive manager family

### Command Table Slice Around `0x0049d59c`

The data at `0x0049d59c` is not an isolated callback slot. The dump in
`phase120_49d59c_range.txt` shows a flat `string_ptr, function_ptr` table with
8-byte entries, and `0x0049d59c` is just the function half of the
`"LoadScene" -> FUN_00290038(...)` entry.

The surrounding entries make the family clear:

- `AddTemporaryProfile -> FUN_002fc540(...)`
- `RememberTemporaryAppearance -> FUN_002fc578(...)`
- `RestoreTemporaryAppearance -> FUN_002fc698(...)`
- `SyncPlayer2Profile -> FUN_002fc780(...)`
- `UnloadAllLevelGeometry -> FUN_00290018(...)`
- `QuickReload -> FUN_00290420(...)`
- `UnloadScene -> FUN_00290388(...)`
- `LoadScene -> FUN_00290038(...)`
- `LoadCollision -> FUN_00290298(...)`
- `AddScene -> FUN_00290208(...)`
- `AddCollision -> FUN_00290308(...)`
- `ToggleAddScenes -> FUN_001580b8(...)`
- `LoadNodeArray / ReLoadNodeArray / ParseNodeArray / NodeArrayBusy / ...`
- later gameplay/script entries such as
  `SkaterLastScoreLandedGreaterThan`, `AnySkaterTotalScoreAtLeast`,
  `EliminateLastPlacePlayer`, and `AccumulateScores`

So this is a broader script/command registry table, not a zone-only structure.
The slice containing `LoadScene` is specifically a level/scene setup cluster
inside that larger command family.

The neighboring decomp confirms the semantics:

- `FUN_00290018(...)` (`UnloadAllLevelGeometry`) just calls `FUN_00157720(...)`
- `FUN_00290420(...)` (`QuickReload`) calls `FUN_00157528(...)`
- `FUN_00290388(...)` (`UnloadScene`) resolves the active scene owner via
  `FUN_00157610(...)` / `FUN_00157638(...)`, tears it down with
  `FUN_00157450(...)`, and frees associated owner state via `FUN_0016af38(...)`
- `FUN_00290208(...)` (`AddScene`) parses scene/collision name arguments and
  calls `FUN_00157318(...)`, the `levels\\%s\\%s.tex` / companion `.geom`
  loader path already tied to zone/scene loading
- `FUN_00290298(...)` (`LoadCollision`) and `FUN_00290308(...)`
  (`AddCollision`) route through `FUN_00157610(...)` and collision helpers
  `FUN_00159920(...)` / `FUN_00159be0(...)`
- `FUN_00290038(...)` (`LoadScene`) is a higher-level wrapper over the same
  owner setup bridge:
  - formats the owner path from the scene name plus a mode suffix
  - loads through `FUN_0016ad60(...)`
  - sets script flags in a small temporary parameter object
  - calls `FUN_00157118(...)`, which in turn runs `FUN_00157040(...)`,
    `FUN_00197ff0(...)`, and `FUN_00157248(...)`

The important takeaway for the zone-TEX investigation is:

- this table is a generic script-command dispatch registry
- the `LoadScene` / `AddScene` cluster in it is absolutely level/scene related
- but `0x0049d59c` itself is not a hidden zone-texture-specific registry; it is
  one entry inside a larger command table

### `FUN_00345a70` / `FUN_00345eb8` Dispatch Family

The `FUN_00345eb8(...) -> FUN_0018a988(...)` path is not owned by the scene
command table. It sits inside a separate bootstrap-created singleton family.

The object shape is now clear from `phase131_dispatch_root_345a70.c`,
`phase133_dispatch_family_root.c`, `phase134_dispatch_family_data.txt`, and
`phase137_dispatch_family_methods.c`:

- `FUN_003459a0(...)` is the sole constructor caller currently exposed; it
  lazily allocates a singleton at `DAT_004a1090` and bumps a refcount in
  `DAT_004a1094`
- `FUN_00345a70(...)` initializes that singleton with a small vtable-like table
  at `DAT_004fc5e0`
- it also allocates a `0x24` callback node, installs `FUN_00345eb8(...)` at
  node `+0x1c`, stores the owning singleton at node `+0x20`, and gives the
  node its own small dispatch table at `DAT_004fc608`
- `FUN_00345a00(...)` is the matching refcounted release path
- `FUN_00345af0(...)`, `FUN_00345b58(...)`, and `FUN_00345bb0(...)` are methods
  in the singleton’s local vtable-like family
- `FUN_00346018(...)` is the callback-node dispatch stub; it simply calls the
  function pointer stored at node `+0x1c`, which is `FUN_00345eb8(...)` here

That means `FUN_00345eb8(...)` is just the queued callback body for this
singleton, not a broader table-dispatch root. Its work is:

- `FUN_00345f20(owner, owner->1c)` for cleanup/reset work
- `FUN_0014dbb8(DAT_00498850)`
- `FUN_0018a988(DAT_00498878)`

The owning family looks generic runtime/state-management, not scene or zone
loading:

- `FUN_002e51a8(...)` is the sole exposed caller into `FUN_003459a0(...)`, and
  that function is the large global bootstrap loop that instantiates many
  unrelated managers/subsystems
- in that bootstrap sequence, `FUN_003459a0(1)` sits beside many other global
  manager constructors (`FUN_00352368`, `FUN_0033a240`, `FUN_00348c88`,
  `FUN_0035b100`, `FUN_0030b6e8`, `FUN_003ce818`, etc.)
- `FUN_002e0ad8(...)`, `FUN_002ee0a8(...)`, `FUN_002ee0e8(...)`, and
  `FUN_0030e470(...)` only toggle or query this singleton through
  `FUN_00345bd0(...)`; none route through the `LoadScene` / `AddScene` family

`FUN_00345bd0(...)` itself is a global mode toggle, not a loader:

- it flips the singleton state bit at owner `+0x1c`
- iterates the global `DAT_0049ad70` list and calls one of two vtable slots on
  every entry depending on the mode
- updates several other global managers (`DAT_0049af10`, `DAT_00498850`,
  `DAT_00498880`, `DAT_0049cf58`, `DAT_004a0d04`)

So the decoder-relevant conclusion is:

- the sole `FUN_00345eb8(...) -> FUN_0018a988(...)` branch belongs to a generic
  bootstrap/runtime-state subsystem
- it does not currently tie back into the scene/zone loading path
- `FUN_0018a988(...)` is being used here as one callback step inside that
  generic subsystem, not as evidence of a zone-owner transition

### Scoped Config Interpreter Around `DAT_00498808`

The next passes narrow `DAT_00498800` / `DAT_00498808` substantially:
they are not a dedicated zone subsystem, but a scoped config-execution layer
that can drive the named asset/image manager for one scripted/configured task.

The setup wrapper is `FUN_0013e4d0(...)`, from
`phase148_manager_init_band.c`:

- `DAT_00498800 = param_3`
- `DAT_00498804 = param_4`
- `DAT_0049880c = param_5`
- allocate `0x7c`
- construct it with `FUN_0013dbc8(...)`
- store it in `DAT_00498808`
- initialize it from keyed source data with `FUN_0013dc80(DAT_00498808, param_2)`
- run `FUN_002ba9d8(param_6, 0, param_1, 0, 0, 0)`
- then clear all four globals and destroy the temporary object

The temporary object shape is now clearer from
`phase150_config_object_band.c` and `phase152_config_helpers.c`:

- `+0x08 = &DAT_004acf98`
- `+0x6c` = keyed hash/object table
- `+0x74` = optional cache/store handle
- `+0x78` = secondary mode/state field

Helper roles:

- `FUN_0013dbc8(...)` constructs the `0x7c` scoped object
- `FUN_0013dd98(...)` resets its keyed table and seeds a root/default entry
- `FUN_0013dc80(...)` copies another same-shape scoped object into it,
  including the optional `+0x74/+0x78` state
- `FUN_0013e130(obj, key)`:
  - lookup `key` in `obj + 0x6c`
  - extract the stored checksum/id with `FUN_0013d8c0(...)`
  - resolve that to a child entry with `FUN_00140f08(...)`
- `FUN_0013e180(obj, key)` returns the raw stored table entry
- `FUN_0013e3f8(obj)` returns `obj + 0x74`
- `FUN_0013e470(obj)` returns `obj + 0x78`
- `FUN_0013e400(obj)` allocates the optional `0x45a0` store object used at
  `obj + 0x74`

The important `.tex` boundary correction is in `FUN_00140ab8(...)`:

- it only runs when `*(param_1 + 0x74) != 0`
- it loads file bytes through `FUN_001216f0(...)`
- then writes them into that local store with `FUN_0025e110(...)`
- the stored key is derived from the input name/path and extension variant

So the scoped `DAT_00498808` object is not just reading symbolic config. It can
also build a local hashed store of `.tex` payload bytes for the duration of the
config execution.

`FUN_0013e5e8(...)` is the main opcode/hash-dispatch method on that temporary
object. It has zero direct callers because it is reached through the object's
vtable/dispatch family, not ordinary call xrefs. The decoder-relevant opcodes
in the current decompile are:

- `FUN_0013ff18(...)`:
  - query keyed entries from `DAT_00498808`
  - load/cache `.tex` through `FUN_00140ab8(...)`
  - then immediately call `FUN_0016f3c0(DAT_00498800, ...)`
- `FUN_0013ef48(...)`:
  - builds named entries through `FUN_0016e758(DAT_00498800, ...)`
- `FUN_0013f9e8(...)`:
  - applies symbolic color/default-state changes through
    `FUN_0016fae8(...)` / `FUN_0016fe88(...)`
- one opcode routes through `DAT_00498804` via `FUN_0013ba80(...)`

That keeps the earlier named reconcile bridge intact:

- `FUN_0013ff18(...) -> FUN_0016f3c0(...) -> FUN_0016a2d0(...) ->
  FUN_00164ce0(...) -> FUN_0019c620(...) -> FUN_001ea488(...)`

The new direct-caller result is that `FUN_0013e4d0(...)` itself only has two
visible callers in current coverage, from `phase154_fun_13e4d0_callers.c` and
`phase153_fun_13e4d0_xrefs.txt`:

- `FUN_0021a820(...)`
- `FUN_002f0c80(...)`

`FUN_002f0c80(...)` is table/callback driven, not a normal direct code path:

- it walks a broader external/config table
- builds temporary scoped config objects with `FUN_0013dbc8(...)`
- seeds them from external data through `FUN_0013de08(...)`
- builds the callback/argument object at `FUN_0013e478(...)`
- runs `FUN_0013e4d0(..., 0x53e82a70)`

`FUN_0021a820(...)` is another scoped-config client:

- it packages arguments with `FUN_0013e478(...)` / `FUN_00140ab0(...)`
- runs `FUN_0013e4d0(...)`
- then reads a result back out of the active manager via `FUN_00170a98(...)`
  and related follow-up helpers

The important conclusion is:

- `DAT_00498800` / `DAT_00498808` are still not the missing zone-only manager
- they are a generic scoped config interpreter that can load/cache `.tex`
  payloads and drive named image/asset reconcile operations
- if the zone runtime uses this layer, the real high-value target is now the
  specific config payload / callback route that invokes `FUN_0013e4d0(...)`,
  not the manager family itself

### Scoped Config Helper Clarifications

The next small helper pass removes a few remaining ambiguities.

From `phase156a_fun_13de08.c` and `phase156b_fun_13dc10_13e478_140ab0.c`:

- `FUN_0013de08(dst, src, flag)` does **not** synthesize a new config format
  for the scoped interpreter
  - it resets the destination via `FUN_0013dd98(...)`
  - clones a keyed table from `src` into `dst + 0x6c` with `FUN_002ac8f0(...)`
  - optionally calls `FUN_0013d8f0(...)`
- `FUN_0013dc10(...)` is the destructor/reset path for the same `0x7c` object
  family
  - releases the optional `+0x74` store handle
  - tears down the keyed table at `+0x6c`
- `FUN_0013e478(...)` constructs the **argument/callback object** passed as
  `param_1` into `FUN_0013e4d0(...)`
  - `+0x6c = param_2`
  - `+0x70 = param_3`
  - `+0x74 = 0`, later filled by `FUN_00140ab0(...)`
  - vtable `DAT_004ad150`
- `FUN_00140ab0(obj, value)` is just `obj + 0x74 = value`

So the `FUN_002f0c80(...)` branch is feeding `FUN_0013e4d0(...)` with:

- a cloned pre-authored keyed config object via `FUN_0013de08(...)`
- a separate small argument/callback object via `FUN_0013e478(...)`

That means this path is still config-driven execution, not raw structure
construction.

### `0x0049dd00` Table Naming Correction

The `0x0049dd00` dump is an alternating `string_ptr, function_ptr` command
table. The pair mapping from `phase157_table_49dd2c_dump.txt` and
`phase158_table_49dd2c_strings.txt` corrects one earlier shorthand:

- `0x0049dd20`:
  - string `0x004e0bf8` = `"PreloadModels"`
  - function `0x002f08b8`
- `0x0049dd28`:
  - string `0x004e0c08` = `"PreloadPedestrians"`
  - function `0x002f0c80`

So `FUN_002f0c80(...)` is the `PreloadPedestrians` command, not
`PreloadModels`. The surrounding entries also make the whole table slice
unambiguously generic gameplay/preload scripting:

- `ToggleMetricItem`
- `ToggleMemMetrics`
- `PreAllocTextureSplat`
- `KillTextureSplats`
- `PreloadModels`
- `PreloadPedestrians`
- `PreselectRandomPedestrians`
- `FlushPedsFromHeap`
- `SpawnPed`
- `ShouldSpawnPed`
- `GetNumPedLifeObjects`
- `KillAllPedLifeObjects`
- `KillAllPedMissionObjects`
- `CreatePedMissionObjects`
- `ReplaceCarTextures`
- `LoadSound`
- `IsSoundLoaded`
- `PlaySound`
- `StopSound`
- `StopAllSounds`

That closes the `FUN_002f0c80(...)` branch as non-decoder. It is a generic
pedestrian preload/script command that happens to reuse the same scoped config
interpreter family.

So the only still-live `FUN_0013e4d0(...)` caller branch is `FUN_0021a820(...)`.

### What `FUN_0021a820(...)` Actually Adds

The next helper pass on `FUN_0021a820(...)` does not tie it cleanly to zone
loading, but it does make the branch less generic.

From `phase159_fun_21a820_helpers.c`:

- `FUN_001ff1e0(...)` is just a keyed child/property lookup over the source
  object at `param_1 + 0x0c`
- one of the two looked-up keys in `FUN_0021a820(...)` resolves locally through
  the repo's `QbKeyNames.txt` dictionary:
  - `0x8a897dd2 = shadow`
- after running the scoped config interpreter, `FUN_0021a820(...)`:
  - optionally calls `FUN_0021d828(...)`
  - reads a cached 4-word result from the active manager with `FUN_00170a98(...)`
  - optionally runs `FUN_0021b098(...)`

Those helpers classify as follows:

- `FUN_00170a98(manager)`:
  - memoizes the manager's slot `+0x6c` result into `manager + 0x40..0x4c`
- `FUN_0021d828(obj)`:
  - runs a vtable refresh/reset on the object itself
  - if `obj + 0x210` is non-null and `FUN_0016f1e0(...)` says the manager has a
    linked object, calls `FUN_0021b8e0(...)`
- `FUN_0021b8e0(obj)`:
  - forwards `FUN_0016f1e0(*(obj + 0x210))` into `FUN_001640c8(...)`
- `FUN_0021b098(obj)`:
  - lazily looks up the child keyed by `0x222756d5` from `obj + 0x0c`
  - then attaches one of its products to the manager at `obj + 0x210` if
    missing

The manager-side helpers reached here are still generic pointer/link wrappers:

- `FUN_0016f1e0(manager)` -> `manager + 0x30`
- `FUN_0016ed30(manager)` / `FUN_0016ece0(manager, obj)` inspect/attach a
  linked object under the manager's internal chain
- `FUN_0021dd08(x)` -> `x + 0x270`
- `FUN_001fdf60(x)` just forwards into `FUN_00207758(DAT_0049af44, x)`

So the current state of the last live branch is:

- `FUN_0021a820(...)` is no longer “anonymous generic logic”
- it is pulling at least one render-oriented child (`shadow`) out of its local
  object tree, running the scoped config interpreter, then refreshing/caching
  object-manager state
- but it is still not yet proven to be the THAW zone texture path

That makes the next best target narrower still:

- identify what object family owns `FUN_0021a820(...)`, or
- resolve the remaining unresolved child key `0x222756d5`, because that child is
  lazily attached back into the manager after the scoped config run

### Phase 172: Callback Vtable Band Classification

The callback-object vtable at `DAT_004ad150` is now materially better
classified from `phase170_vtable_4ad150_dump.txt` and
`phase172_callback_vtable_band.c`.

`FUN_0013e5e8(...)` is still the central hash/opcode dispatcher, but the newly
decompiled sibling methods show that most of the unresolved band is generic
manager/config transform logic, not a hidden zone-only bridge.

Concrete dispatcher mapping now visible in `FUN_0013e5e8(...)`:

- `0x0ecf0248` -> vtable `+0x9c` -> `FUN_0013f400(...)`
- `0x2aafa114` -> vtable `+0xa4` -> `FUN_0013f428(...)`
- `0x21aec583` -> vtable `+0xc4` -> `FUN_0013f588(...)`
- `0x59188f0c` -> vtable `+0x54` -> `FUN_0013f320(...)`
- `0x53da94f1` -> vtable `+0x5c` -> `FUN_0013ef48(...)`
- `0x681a03af` -> vtable `+0x6c` -> `FUN_0013ef20(...)`
- `0xffffffffaa42a104` -> vtable `+0x8c` -> `FUN_0013ff18(...)`
- `0xffffffffaee6e915` -> vtable `+0x74` -> `FUN_0013f9e8(...)`
- `0xffffffffcefe8478` -> vtable `+0xd4` -> `FUN_00140990(...)`
- `0xffffffffb1c39a54` -> vtable `+0xe4` -> `FUN_001409b8(...)`
- `0xffffffffdd9c297e` is a two-step path:
  - first vtable `+0x94` -> `FUN_001402b8(...)`
  - then falls through to vtable `+0xac` -> `FUN_0013f9c0(...)`

The newly classified methods themselves:

- `FUN_0013f400(...)` is just `FUN_00170480(DAT_00498800)`.
- `FUN_0013f428(...)`:
  - reads a cached 4-word result from `FUN_00170a98(DAT_00498800)`
  - if manager flag bit 2 in `DAT_00498800 + 0x50` is set, scales by the
    largest component of `DAT_00498800 + 0x20..0x2c`
  - then calls `FUN_00170af8(DAT_00498800)`
- `FUN_0013f530(...)`:
  - looks up key `0x812684ef` in the scoped config table at `DAT_00498808 + 0x6c`
  - forwards the resolved value to `DAT_00498804` via `FUN_0013ba80(...)`
- `FUN_0013f588(...)`:
  - resolves either the current root keyed entry or a keyed child under
    `DAT_00498808`
  - reads a scale/triple set from hashes `0x5a96985d`, `0x7323e97c`,
    `0x0424d9ea`, and `0xffffffff9d2d8850`
  - iterates a list resolved by `param_2`
  - fetches per-entry data through `FUN_0013bcc8(...)`
  - applies scaled values through `FUN_0013bd08(...)`
  - skips entries found in global list `0x20d9ac2f`
- `FUN_0013f9c0(...)` is just `FUN_0016efa8(DAT_00498800)`.
- `FUN_0013fc30(...)` is just `FUN_0016fa58(DAT_00498800)`.
- `FUN_0013fc58(...)`:
  - resolves a keyed config entry from `DAT_00498808`
  - reads a mode gate `0xffffffff8602f6ee`
  - loads floats / angle data from `0xffffffffcf6aa087`, `0x5663f13d`,
    `0x266932c8`, and `0x1256b6c6`
  - builds a transform matrix through `FUN_00110c88(...)`
  - finishes by calling `FUN_0016f9b8(DAT_00498800)`

Two `DAT_004ad150` entries are still unresolved in the current function
database:

- `+0x0b4 -> 0x0013f4d8` (`data/code`, no function found)
- `+0x0cc -> 0x0013f810` (`data/code`, no function found)

The useful decoder conclusion is:

- the callback-object band is now mostly classified as generic manager/config
  mutation, reset, traversal, and transform logic
- the clearly decoder-adjacent callback paths are still the same narrow subset:
  - `FUN_0013ef48(...)` named entry build
  - `FUN_0013ff18(...)` named source/destination reconcile
  - `FUN_001402b8(...)` image/config assembly path
- so the next high-value target is no longer “what do these callback methods
  do?” in general
- it is which higher-level config payloads or callback hashes actually select
  the `FUN_0013ff18(...)` / `FUN_001402b8(...)` path in zone runtime, as opposed
  to these more generic manager-transform branches

### Phase 173: Callback Band Looks More Like Generic Body/Part Config Than Zone-Specific Runtime

I reran the callback-band decomp cleanly through an isolated copied project with
`run_phase172_callback_methods.sh`, producing `phase172_callback_methods.c`.
That pass materially tightened the interpretation of the `DAT_004ad150`
callback object.

Concrete new points:

- local QBKey/hash sweeps across `QbKeyNames*.txt` plus the generated QBKey
  candidate corpus
  produced two concrete name resolutions used directly in this band:
  - `0x2457f44d = body`
  - `0xffffffffeb307e68 = shoes`
- `FUN_001405b0(...)` is now much more suggestive:
  - it starts from either `0x650fab6d` or `0x0fc85bae`
  - then propagates the same triple/default fields into sibling keyed entries
    under `0x7e54e2a2`, `0x0571da24`, `0xffffffffeb307e68` (`shoes`), and
    `0x2457f44d` (`body`)
  - that looks more like generic body-part/config value propagation than
    anything texture-owner-specific
- `FUN_0013f530(...)` is the confirmed `DAT_00498804` callback bridge:
  - it looks up `0x812684ef` in the scoped config table
  - then forwards the resolved value to `DAT_00498804` through
    `FUN_0013ba80(...)`
- `FUN_0013f588(...)` is a list/scalar propagation helper:
  - it resolves either the active keyed node or a keyed child under
    `DAT_00498808`
  - reads `0x5a96985d`, `0x7323e97c`, `0x0424d9ea`, and `0xffffffff9d2d8850`
  - iterates a list resolved by `param_2`
  - uses `FUN_0013bcc8(...)` / `FUN_0013bd08(...)`
  - skips entries found in global list `0x20d9ac2f`
- `FUN_0013fc58(...)` is a matrix/transform builder:
  - it gates on `0xffffffff8602f6ee`
  - reads floats from `0xffffffffcf6aa087`, `0x5663f13d`, `0x266932c8`, and
    `0x1256b6c6`
  - builds matrix state through `FUN_00110c88(...)`
  - finishes with `FUN_0016f9b8(DAT_00498800)`
- the two unresolved callback slots remain unresolved even in the isolated pass:
  - `+0x0b4 -> 0x0013f4d8`
  - `+0x0cc -> 0x0013f810`
  - both still show as `data/code`, not normal functions in current coverage

The important decoder conclusion is now sharper:

- the callback band is still real and still live
- but most of the newly resolved methods look like generic body-part,
  transform, traversal, or helper-callback scripting
- the only clearly decoder-adjacent methods remain:
  - `FUN_0013ef48(...)`
  - `FUN_0013ff18(...)`
  - `FUN_001402b8(...)`
- of those, `FUN_0013ff18(...)` is still the only one that directly hits the
  confirmed reconcile chain:
  - `FUN_0016f3c0(...) -> FUN_0016a2d0(...) -> FUN_00164ce0(...) -> FUN_0019c620(...)`

So the next best target is no longer broad callback-method classification. It
is the higher-level config payload or caller path that selects the
`0xffffffffaa42a104` / `0xffffffffdd9c297e` callback routes in zone runtime,
because those are still the only concrete scripted entries into the
image/reconcile-capable part of this subsystem.

### Phase 174: Full `DAT_004ad150` Callback Band Still Looks Generic

I reran the callback-object vtable band through a clean read-only headless pass
using `run_phase172_callback_vtable_band.sh`, which produced:

- `phase172_callback_vtable_band.c`
- `phase173_callback_vtable_xrefs.txt`

That pass materially improved coverage of the `DAT_004ad150` method slice while
also reinforcing the same high-level conclusion: most of the unresolved methods
in this band are generic manager/config operations, not a hidden zone-only
decoder bridge.

Concrete results from `phase172_callback_vtable_band.c`:

- `FUN_0013f400(...)` is just `FUN_00170480(DAT_00498800)`
- `FUN_0013f428(...)` is a cached-result/scale/finalize path:
  - `FUN_00170a98(DAT_00498800)`
  - optional scalar selection from `DAT_00498800 + 0x20/+0x24/+0x28/+0x2c`
  - `FUN_00170af8(DAT_00498800)`
- `FUN_0013f530(...)` looks up key `0xffffffff812684ef` from
  `DAT_00498808 + 0x6c` and forwards it through
  `FUN_0013ba80(DAT_00498804, ...)`
- `FUN_0013f588(...)` is a keyed list/scalar propagation helper:
  - resolves a keyed node from `DAT_00498808`
  - reads `0x5a96985d`, `0x7323e97c`, `0x0424d9ea`, and `0xffffffff9d2d8850`
  - iterates list `param_2`
  - uses `FUN_0013bcc8(...)` / `FUN_0013bd08(...)`
- `FUN_0013f9c0(...)` is just `FUN_0016efa8(DAT_00498800)`
- `FUN_0013fc30(...)` is just `FUN_0016fa58(DAT_00498800)`
- `FUN_0013fc58(...)` is a keyed transform/matrix build ending in
  `FUN_0016f9b8(DAT_00498800)`

Two `DAT_004ad150` entries are still unresolved in current coverage:

- `+0x0b4 -> 0x0013f4d8` (`NO FUNCTION FOUND`)
- `+0x0cc -> 0x0013f810` (`NO FUNCTION FOUND`)

The dispatch table in `FUN_0013e5e8(...)` is also sharper now:

- hash `0xffffffffaa42a104` dispatches through vtable `+0x8c`, which is
  `FUN_0013ff18(...)`
- hash `0xffffffffdd9c297e` first dispatches through vtable `+0x94`, which is
  `FUN_001402b8(...)`, then falls through to the common `+0x0ac` tail, which is
  `FUN_0013f9c0(...)`
- the clearly decoder-adjacent callback subset remains:
  - `FUN_0013ef48(...)`
  - `FUN_0013ff18(...)`
  - `FUN_001402b8(...)`

The xref side stayed narrow:

- `FUN_0013e4d0(...)` still has only three direct callers in current coverage:
  - `FUN_0021a820(...)`
  - `FUN_0021a820(...)` alternate path
  - `FUN_002f0c80(...)`
- there are still no direct xrefs to the callback-method bodies because they are
  reached through `DAT_004ad150`

So the best next target stays narrow as well:

- stop broad callback-band archaeology
- instead trace which higher-level config payloads or callback-hash routes
  actually select `FUN_0013ff18(...)` or `FUN_001402b8(...)` in the real zone
  runtime
- if that cannot be tied back to the zone owner path, this whole callback/config
  branch should be closed as generic runtime infrastructure rather than the
  decoder source of truth

### Phase 175-183: The Inner Callback Hashes Come From Interpreted Config Bytecode

The next passes finally exposed the execution core under `FUN_002ba9d8(...)`.
Artifacts:

- `phase176_callback_dispatch_root.c`
- `phase177_callback_dispatch_root_xrefs.txt`
- `phase178_callback_executor_core.c`
- `phase179_callback_executor_core_xrefs.txt`
- `phase180_callback_invoke_path.c`
- `phase181_callback_invoke_xrefs.txt`
- `phase182_callback_symbol_dispatch.c`
- `phase183_callback_symbol_dispatch_xrefs.txt`
- rerunnable scripts:
  - `run_phase176_callback_dispatch_root.sh`
  - `run_phase178_callback_executor_core.sh`
  - `run_phase180_callback_invoke_path.sh`
  - `run_phase182_callback_symbol_dispatch.sh`

The important structural result is:

- `FUN_002ba9d8(...)` is only a thin global dispatch wrapper
- in mode `7`, it builds an execution context with `FUN_002b81e8(...)`,
  initializes it through `FUN_002b8a58(...) -> FUN_002b87b0(...)`, then steps it
  with repeated calls to `FUN_002ba2c8(...)`
- `FUN_002ba2c8(...)` is a generic bytecode/node walker over `ctx + 0x14`
- `FUN_002b95d8(...)` is the parser/branch resolver used for the default opcode
  path

The decisive handoff is in `phase182_callback_symbol_dispatch.c`:

- `FUN_002b94f0(...)` extracts a symbol/hash from the current stream, including:
  - inline literal hash opcodes
  - keyed lookups through `FUN_002ae260(...)` on the active data object
- `FUN_002b9438(...)` is the actual callback-object invoke path:
  - it resolves the current callback object/context from `ctx + 0x1c` or
    fallback `ctx + 0x18`
  - then calls the callback object's vtable `+0x1c` slot:
    - `(**(code **)(iVar1 + 0x1c))( ..., parsed_hash, callback_context, exec_ctx )`
  - for the scoped config object family, that `+0x1c` slot is
    `FUN_0013e5e8(...)`
- `FUN_002b94b0(...)` is the alternate direct code-pointer dispatch path
- `FUN_002b93d8(...)` routes another dispatch form through `FUN_002b9318(...)`

That settles the key question from the last few passes:

- the inner hashes like `0xffffffffaa42a104` and `0xffffffffdd9c297e` are not
  being invented by a higher-level caller
- they are coming straight from the interpreted config bytecode/data stream that
  `FUN_002ba2c8(...)` and `FUN_002b95d8(...)` walk
- `FUN_0013e5e8(...)` is therefore the callback-object method that receives
  those parsed hashes from generic executor machinery, not from a zone-specific
  direct callsite

This narrows the next target again:

- broad callback-method archaeology is now done
- broad `FUN_002ba9d8(...)` caller archaeology is also much less valuable
- the remaining useful target is the specific config payload/data object passed
  as `param_2` into `FUN_0013e4d0(...)` from `FUN_0021a820(...)`, because that
  payload is what actually contains the bytecode entries that produce the
  `0xffffffffaa42a104` / `0xffffffffdd9c297e` callback hashes

So if this branch continues, it should focus on the data/payload side of
`FUN_0021a820(...)`, not on more generic executor internals.

### Phase 184-188: The Remaining Callback Band Resolves To Editable Appearance Commands

The next outward pass classified the `0x002191xx` / `0x002948xx` /
`0x002949xx` branch well enough to stop treating it as a likely zone-texture
runtime path.

Artifacts:

- `phase184_219x_family.c`
- `phase185_219x_family_xrefs.txt`
- `phase186_219_caller_band.c`
- `phase187_219_caller_band_xrefs.txt`
- `phase188_table_49fea0_ptrs.txt`
- rerunnable scripts:
  - `run_phase184_219x_family.sh`
  - `run_phase186_219_caller_band.sh`
  - `run_phase188_editable_list_table.sh`

The useful direct classifications are:

- `FUN_00219118(...)` is only a thin typed wrapper:
  - `FUN_002a7940(param_1, 1)`
  - `FUN_002191c8(...)`
- `FUN_00219638(...)` is a list-selection helper:
  - it counts candidate items
  - picks one by weighted/random index
  - copies keyed fields from the selected element into another object through
    `FUN_002ad3c8(...)`
  - returns a selected keyed value via `FUN_00140f08(...)` and
    `FUN_002ae030(..., 0xffffffff90436bd9, ...)`
- `FUN_00294900(...)` is not a direct engine-side texture path:
  - it gets an executor/context via `FUN_002b8578(param_2)`
  - resolves key `0x62ba3f6a` from `param_1`
  - then calls `FUN_00219ad0(...)`
- `FUN_0021b098(...)` is a post-apply child attach/cache helper around key
  `0x222756d5`, not a zone owner/blob parser

The decisive classification came from the nearby command table dump at
`0x0049fe80` in `phase188_table_49fea0_ptrs.txt`:

- `0x0049fe90 -> "CreateRandomAppearance" -> FUN_00294848`
- `0x0049fe98 -> "AddEditableList" -> FUN_002948A0`
- `0x0049fea0 -> "RemoveEditableList" -> FUN_002948D0`
- `0x0049fea8 -> "ForEachInEditableList" -> FUN_00294900`
- `0x0049feb0 -> "SelectFrom" -> FUN_00294960`

That is strong enough to classify this branch as editable appearance /
pedestrian-content scripting rather than the THAW zone texture owner runtime.
It also fits the surrounding command table from `phase157_table_49dd2c_dump.txt`
and `phase158_table_49dd2c_strings.txt`, which already exposed:

- `PreloadPedestrians`
- `PreselectRandomPedestrians`
- `SpawnPed`
- `ReplaceCarTextures`

So the callback/config branch should now be treated as closed for decoder work:

- it does contain one real path into `FUN_0019c620(...)`
- but that path lives inside generic appearance/config content scripting, not a
  proven zone `.tex` owner load/use path
- the next best target should pivot back to the zone owner/blob side, not
  continue further down the editable-list callback family

### Phase 189-191: The Owner-Primary Field Scans Still Collapse To Old False Positives

After closing the callback/appearance branch, the next pass reran the dedicated
owner-primary heuristics to see whether a cleaner owner-side target would emerge.

Artifacts:

- `phase189_primary_field_consumers.txt`
- `phase190_primary_list_consumers.txt`
- `phase191_primary_reg_consumers.txt`
- rerunnable script:
  - `run_phase189_owner_primary_scans.sh`

The headline result is negative but useful: the scans still converge on the same
small candidate set as the older Phase 75-78 work, and the already-decompiled
`phase76_primary_consumers.c` still classifies those as non-decoder paths.

The repeated candidates are:

- `FUN_00120b20(...)`
- `FUN_00137898(...)`
- `FUN_0016b628(...)`
- `FUN_00197540(...)`
- `FUN_002bc320(...)`

Their current classifications remain:

- `FUN_00120b20(...)` is a cached load/object retrieval path, not a direct
  owner-primary consumer
- `FUN_00137898(...)` and `FUN_00197540(...)` build/populate `0x14`-stride
  keyed-entry tables from config data
- `FUN_0016b628(...)` is a static table/setup initializer
- `FUN_002bc320(...)` is a bulk reset/zero path over a larger manager object

The broader list/reg scans in `phase190_primary_list_consumers.txt` and
`phase191_primary_reg_consumers.txt` also produced many obvious stack-frame
false positives from generic functions that merely happened to use `+0x08`,
`+0x10`, `+0x14`, `+0x40`, etc. in unrelated structs.

So this branch should also be treated as effectively closed for now:

- it did not expose a new real downstream dereference of rebased primary
  `+0x10/+0x14`
- it did not identify a better owner-side decode bridge than the ones already
  known
- the owner/blob work should therefore pivot to other unresolved owner fields or
  to the public-file vs owner-blob boundary, not keep rerunning broad primary
  field scans

### Phase 193-197: The Newly Opened `.pak` Builder Branch Is Sky/Cutscene, Not Zone

The next outward pass tried to use runtime `.pak` path builders as a way to
break the remaining extracted-file vs owner-blob contradiction.

Artifacts:

- `phase193_backend_path_templates.txt`
- `phase194_pak_string_xrefs.txt`
- `phase195_pak_path_builders.c`
- `phase196_pak_path_builders_xrefs.txt`
- `phase197_type_constant_hits.txt`
- rerunnable scripts:
  - `run_phase195_pak_path_builders.sh`
  - `run_phase197_type_constant_search.sh`

The main classification result is negative but useful:

- `FUN_00127990(...)` is not a zone/world `.pak` loader
  - it formats through `PTR_s_skies__00498770`
  - the actual format string is `"%s%s/%s.pak.%s"`
  - its caller `FUN_001275c0(...)` walks the sky table at `0x53c340`
- `FUN_0020f1c8(...)` and `FUN_00211440(...)` are cutscene pack builders/loaders
  - they use `"cutscenes/%s/%s.pak"` and `"cutscenes/%s/%s.pak.%s"`
  - their visible callers sit in the cutscene/object branch, not in the zone
    owner path
- the `.pak` cache writer hit from `0xB524565F` is still only `FUN_0025e110(...)`
  - it copies loaded bytes into cache storage
  - then writes a `QbKey("last")` sentinel at the next `0x20` slot
  - that is cache packing, not runtime archive parsing

So the newly opened `.pak` string branch should be treated as closed for the
zone decoder. It exposes sky, cutscene, and other subsystem pack loaders, but
not the missing zone-world runtime path.

### Phase 193-197: The Sample Distribution Layer Still Does Not Match The Logical Scene Path

The sample-side package evidence got sharper while checking that branch.

From the shipped sample tree:

- the only archive/index files under the PS2 sample build are:
  - `Archives/WAD/DATAP.HED`
  - `Archives/WAD/DATAP.WAD`
  - `Archives/WAD/MUSICP.HED`
  - `Archives/WAD/MUSICP.WAD`
  - `Archives/WAD/STREAMP.HED`
  - `Archives/WAD/STREAMP.WAD`
  - plus `BIN/streamp*.hdp`
- there is no visible data-side `P.HED/HDP` pair analogous to what the runtime
  file backend formats in `phase193_backend_path_templates.txt`

From direct string inspection of `DATAP.HED`:

- it contains `\worlds\worldzones\z_ho\z_ho.pak.ps2`
- it contains `\worlds\worldzones\z_ho\z_ho_net.pak.ps2`
- it contains `\sounds\pak\z_ho_sfx.pak.ps2`
- it contains direct named assets like
  `\models\peds\ped_boone\ped_boone.tex.ps2`
- it does **not** contain `levels\z_ho\z_ho.tex`
- it does **not** contain `z_ho.tex.ps2`

That keeps the core contradiction intact:

- the scene loader still formats logical names like `levels\%s\%s%s.tex`
- but the shipped outer package layer visibly names the zone as a second-layer
  `worldzones/.../*.pak.ps2` container instead

### Phase 193-197: The Inner `z_ho.pak.ps2` Really Looks Like A Mixed Named/Compact Archive

Direct inspection of the inner sample archive helped narrow the zone case:

- the opening `0xC0` entries in `Sample/Builds/.../PAK/z_ho.pak.ps2` are full
  named entries with flags `0x22`
- those names are things like:
  - `worlds\worldzones\z_ho\z_ho_sfx_dat_ps2.qb.ps2`
  - `worlds\worldzones\z_ho\z_ho_sfx.qb.ps2`
  - `worlds\worldzones\z_ho\z_ho.qb.ps2`
  - `worlds\worldzones\z_ho\z_ho_scripts.qb.ps2`
- the same archive also contains compact unnamed entries, including the known
  extracted zone texture payload `0009BF70.tex`
- direct string scans over the whole file did not expose any `.tex.ps2` name
  inside `z_ho.pak.ps2`

That is strong evidence that the zone `.tex` is being stored as a compact
hashed inner entry, while many `.qb` assets in the same archive still use full
named entries. So `0009BF70.tex` is best understood as a repo-side extraction
name for an unnamed compact inner entry, not as the runtime logical path.

### Phase 197: Direct `.tex` / `.pak` / `.qb` Type Hashes Are Not Present As Obvious Runtime Constants

I also ran a direct ELF search for the common Neversoft type hashes:

- `0x8BFA5E8E` = `QbKey(".tex")`
- `0x6C217288` = `QbKey(".pak")`
- `0xA7F505C4` = `QbKey(".qb")`

`phase197_type_constant_hits.txt` came back with zero mapped-memory hits for
all three values.

That does not prove the runtime never reasons about these types, but it does
make one specific theory weaker:

- there is no obvious direct type-constant dispatch in the currently loaded ELF
  image for `.tex`, `.pak`, or `.qb`

So the next best target is no longer "find another outer `.pak` path-builder"
and not "grep for obvious `.tex` type constants." The more promising direction
is now the actual inner archive lookup/parser or the hash-based resolver that
maps a logical scene request like `levels\z_ho\z_ho.tex` onto a compact unnamed
entry inside the second-layer zone pack.

### Phase 198: The Owner Constructor Chain Still Has No Hidden Path Remap

I revisited the concrete owner constructor band to rule out one more likely
bridge:

- `FUN_0016ad60(...)`
- `FUN_001a0890(...)`
- `FUN_001a0280(...)`
- `FUN_001a0480(...)`
- `FUN_001e9fa8(...)`

The result is still strongly negative:

- `FUN_0016ad60(...)` only:
  - normalizes/copies the path into a stack buffer
  - hashes the path and adds the variant `param_3`
  - looks up an existing owner object by that hash
  - otherwise allocates a new owner through `FUN_001a0890(...)`
- `FUN_001a0890(...)` only allocates `0x18` bytes and forwards into
  `FUN_001a0280(...)`
- `FUN_001a0280(...)` only forwards into `FUN_001a0480(...)`
- `FUN_001a0480(...)` only chooses between:
  - `FUN_001e9fa8(path, out_size_ptr)` when a path is supplied, or
  - `FUN_001e9fe0(blob_ptr, size)` when a caller already has bytes
  then optionally builds wrapper/hash tables via `FUN_0016a220(...)` and
  `FUN_001a0368(...)`
- `FUN_001e9fa8(...)` still just:
  - calls `FUN_001216f0(path, out_size_ptr, 0, 0)`
  - passes the returned base straight into `FUN_001e9ac0(...)`

So there is still no visible second lookup, `.pak` indirection, or path remap
between the logical scene name and the owner-blob parser. The constructor chain
itself is now low-value as a search target.

### Phase 198: The Generic Named-Image Reconcile Path Still Looks Callback-Scoped

The named-image reconcile chain remains real:

- `FUN_0013ff18(...) -> FUN_0016f3c0(...) -> FUN_0016a2d0(...) ->
  FUN_00164ce0(...) -> FUN_0019c620(...)`

But the concrete mechanics still look generic rather than zone-specific:

- `FUN_0016f3c0(...)` normalizes two symbolic names and iterates a registered
  list of mappings
- `FUN_0016a2d0(...)`:
  - resolves a destination by symbolic name via `FUN_0016a100(...)`
  - constructs a temporary source image via `FUN_00169b98(...)`
  - reconciles the pair with `FUN_00164ce0(...)`
  - releases the temporary source with `FUN_00169fa0(...)`
- `FUN_00140ab8(...)` is still the only visible producer into
  `FUN_0025e110(...)`

Direct xref output is still telling:

- `FUN_0013ff18(...)` has no direct callers in the current decomp coverage
- its only exposed use remains through the callback vtable/config band already
  classified as appearance/content scripting

So this path is still decoder-relevant as a *generic* prepared-image reconcile
mechanism, but it is not yet a proven client of the zone scene/owner load path.

### Phase 198: The `levels\...\.tex` Logical Path Exists In Code, Not In The Shipped Sample Data

The strongest packaging-side clarification this pass came from a full-tree ASCII
search over the shipped PS2 sample build.

What is present:

- the EXE contains the logical path templates:
  - `levels\%s\%s%s.tex`
  - `levels\%s\%s%s.geom.%s`
- `DATAP.HED` contains:
  - `\worlds\worldzones\z_ho\z_ho.pak.ps2`
  - `\worlds\worldzones\z_ho\z_hoped.pak.ps2`
  - `\worlds\worldzones\z_ho\z_ho_net.pak.ps2`
  - many other `worldzones/.../*.pak.ps2`
  - direct named assets like `\models\peds\ped_boone\ped_boone.tex.ps2`

What is *not* present anywhere in the shipped sample data tree:

- `levels\z_ho\z_ho.tex`
- any `levels\...\*.tex` asset path in `DATAP.HED`
- any `levels\...\*.tex` string in the sample `PAK` files

That matters because it makes the remaining contradiction sharper and more
concrete:

- the logical `levels\...\.tex` path is definitely a runtime interface used by
  the EXE
- but the shipped sample data layer still exposes the zone as a second-layer
  `worldzones/.../*.pak.ps2` container instead

So the next best target is no longer the owner constructor chain and not the
generic callback-image reconcile band. It is now the *distribution/index
translation* boundary itself:

- either the runtime package index seen by `FUN_001216f0(...)` is already
  flattened in a way the shipped sample files do not expose
- or there is still an unresolved inner-archive/hash resolver below the current
  string/path-level coverage

### Phase 199: The Original Unpacked Build Exposes A Binary Package-Index Layer, But Still No `levels\z_ho\z_ho.tex`

Re-checking the original unpacked THAW PS2 build in the configured research
cache made the packaging boundary sharper again.

The top-level package files are exactly:

- `DATAP.HED`
- `DATAP.WAD`
- `DATAPD.HDP`
- `DATAPF.HDP`

and the extracted tree under `Extracted\DATAP\...` already contains explicit
zone files such as:

- `Extracted\DATAP\worlds\worldzones\z_ho\z_ho.pak.ps2`
- `Extracted\DATAP\worlds\worldzones\z_ho\z_ho_net.pak.ps2`
- `Extracted\DATAP\worlds\worldzones\z_ho\z_hoped.pak.ps2`

while a direct search still does **not** expose a parallel flattened
`levels\z_ho\z_ho.tex` asset path in the shipped data layer.

The new important structural result is that `DATAPD.HDP` / `DATAPF.HDP` are not
noise or filename truncation. They form a binary index layer alongside the
plaintext `DATAP.HED`:

- both files begin with the same entry count: `0x11F2` (`4594`)
- `DATAPF.HDP` size is exactly:
  - `0x10 + 4594 * 0x0C = 0xD768`
  - so it is a flat `12`-byte-record table after a `16`-byte header
- `DATAPD.HDP` size is exactly:
  - `0x10 + 984 * 0x0C + 4594 * 0x0C = 0x10588`
  - so it splits cleanly into:
    - a `16`-byte header
    - `984` `12`-byte index records from `0x10` to `0x2E2F`
    - `4594` `12`-byte records from `0x2E30` to EOF

The trailing `DATAPD.HDP` record block at `0x2E30` is the key bridge to the
known plaintext package data. Its early entries mirror `DATAP.HED` offset/size
pairs directly:

- `DATAP.HED` first entries:
  - `\anims\standardkeyq.bin -> offset 0x00000000, size 0x00000800`
  - `\anims\standardkeyt.bin -> offset 0x00000001, size 0x00000800`
  - `\cag_replaceable.ini -> offset 0x00000002, size 0x000001F4`
  - `\customparks\custom1.prk -> offset 0x00000003, size 0x00003AAF`
- `DATAPD.HDP + 0x2E30` begins with matching triplets:
  - `(0x00000000, 0x00000800, 0x6C2B3E96)`
  - `(0x00000001, 0x00000800, 0xA4CBB1E6)`
  - `(0x00000002, 0x000001F4, 0x9A714839)`
  - later entries continue with the same `custom*.prk` offset/size sequence

So the `DATAPD.HDP` tail is best understood as a hashed companion table over the
same logical file set exposed by `DATAP.HED`, even though the third word still
does not yet match the currently assumed `Crc32Neversoft(name)` normalization.

The front `984` records in `DATAPD.HDP` then act as an in-file index into that
hashed tail table:

- the third word in the front records points at aligned entry starts inside the
  `0x2E30` table:
  - `0x2E30`
  - `0x2E48`
  - `0x2E78`
  - `0x2EE4`
  - `0x2EF0`
  - `0x2EFC`
- those offsets land exactly on consecutive `12`-byte records in the hashed tail
- the early front records look like bucket descriptors over that tail table:
  - `(count=2, hash=0x182F13EC, ptr=0x2E30)`
  - `(count=4, hash=0xFFFFFFFF, ptr=0x2E48)`
  - `(count=9, hash=0x07B6EE79, ptr=0x2E78)`
  - then many single-entry runs follow
- the final front record is an explicit sentinel:
  - `(count=0xFFFFFFFF, hash=0x00000000, ptr=0x00000000)`

So the working interpretation is now:

- `DATAPD.HDP` = hashed bucket/index layer over the trailing `4594` hashed file
  records

with the caveat that the exact meaning of the middle hash field is still
unresolved.

The negative result is also useful:

- `DATAPD.HDP` and `DATAPF.HDP` do not expose readable ASCII
  `levels\...`/`z_ho` strings in direct scans
- `DATAP.HED` still names Hollywood only through
  `\worlds\worldzones\z_ho\z_ho.pak.ps2` and related sibling pack paths

So this original unpacked build does **not** dissolve the old contradiction. It
sharpens it:

- the shipped/original data layer clearly contains both:
  - plaintext `DATAP.HED` path names
  - binary `DATAPD.HDP` / `DATAPF.HDP` package indexes
- but that visible data still stops at the `worldzones/.../*.pak.ps2` layer
- there is still no direct evidence in the shipped data files of a logical
  `levels\z_ho\z_ho.tex` asset entry

That pushes the next best target more specifically toward the runtime binary
package-index users, not the owner constructors:

- understand what `DATAPD.HDP`'s third-word hash actually is
- identify how `DATAPF.HDP` maps onto the same `4594`-entry logical file set
- then trace the runtime code that consumes those HDP tables to see whether the
  `levels\...\.tex` logical path is resolved through an additional hashed alias
  layer beyond the shipped file names

### Phase 200: `FUN_00125160(...)` / `FUN_00125298(...)` Match The `DATAPD.HDP` Structure Almost Exactly

Re-reading the existing package-index decomp against the concrete `DATAPD.HDP`
bytes was the first strong runtime/data alignment in this branch.

`FUN_00125160(path, out_dir_hash, out_file_hash)` now reads cleanly as:

- normalize `/` -> `\`
- split at the last slash
- hash the directory portion into `out_dir_hash`
- hash the leaf filename portion into `out_file_hash`

Then `FUN_00125298(path, index_object)` does:

- call `FUN_00125160(...)` to get `(dir_hash, file_hash)`
- if `index_object + 8` is non-null, treat it as a front lookup table where each
  record is:
  - `+0x00 = count`
  - `+0x04 = dir_hash`
  - `+0x08 = pointer to a run of 12-byte tail records`
- match `dir_hash` against the front table
- then scan `count` tail records and compare `file_hash` against each tail
  record's third word at `+0x08`
- return the matching tail record pointer

That is an almost direct semantic match for the observed `DATAPD.HDP` layout:

- front records at `DATAPD.HDP + 0x10`:
  - `(count, dir_hash, ptr_to_tail_run)`
- tail records at `DATAPD.HDP + 0x2E30`:
  - `(offset, size, file_hash)`

Concrete examples:

- front:
  - `(2, 0x182F13EC, 0x2E30)`
  - `(4, 0xFFFFFFFF, 0x2E48)`
  - `(9, 0x07B6EE79, 0x2E78)`
- tail at those targets:
  - `0x2E30 -> (0x00000000, 0x00000800, 0x6C2B3E96)`
  - `0x2E48 -> (0x00000002, 0x000001F4, 0x9A714839)` and following records
  - `0x2E78 -> (0x00000003, 0x00003AAF, 0x2ABA32CB)` and following records

So the current working interpretation is no longer speculative:

- `DATAPD.HDP` is almost certainly the runtime-side hashed directory/file lookup
  structure consumed by `FUN_00125298(...)`

The unresolved part is narrower now:

- the exact hash function behind `FUN_002BB6A0(...)` still has not been matched
  to the observed `file_hash` values
- `DATAPF.HDP` is still structurally real but not yet semantically tied to the
  same lookup path

But the bigger packaging question is now materially clearer:

- the runtime binary index layer can already resolve shipped package names from
  `DATAPD.HDP`
- and those shipped names are still things like
  `\worlds\worldzones\z_ho\z_ho.pak.ps2`
- not a visible shipped `levels\z_ho\z_ho.tex`

So if `levels\%s\%s%s.tex` is still the logical runtime API, the unresolved
translation layer is now more likely *above* or *alongside* the `DATAPD.HDP`
path, not inside the owner constructor chain and not inside `FUN_00125298(...)`
itself.

### Phase 201: The `DATAPD.HDP` Hashes Are Normalized `QbKeyLower`-Style CRCs

The earlier "unresolved third-word hash" point is now closed.

`FUN_002BB6A0(...)` is just a thin wrapper around `FUN_001185C8(...)`, and
`FUN_001185C8(...)` decompiles as the standard reflected CRC-table path with:

- initial value `0xFFFFFFFF`
- ASCII `A-Z` lowercased before hashing
- `/` normalized to `\`
- no final XOR

So for practical purposes it is a normalized `QbKey.HashLower(...)`-style hash.

That lines up directly with the concrete `DATAPD.HDP` bytes:

- the front-table directory hashes are hashes of the directory portion with a
  leading slash stripped
  - `anims -> 0x182F13EC`
  - `customparks -> 0x07B6EE79`
- the tail-table third word is the hash of the leaf filename only
  - `standardkeyq.bin -> 0x6C2B3E96`
  - `standardkeyt.bin -> 0xA4CBB1E6`
  - `cag_replaceable.ini -> 0x9A714839`
  - `custom1.prk -> 0x2ABA32CB`

So the working `DATAPD.HDP` model is now strong rather than speculative:

- top-level header:
  - `+0x00 = 0x11F2` total tail/file-record count
  - `+0x04 = 0x2E30` tail-table start
  - `+0x08 = 0x10` front-table start
- front table at `+0x10`:
  - `(count, dir_hash, ptr_to_tail_run)`
- tail table at `+0x2E30`:
  - `(offset, size, leaf_hash)`

### Phase 202: `FUN_001234E8(...)` Does Not Switch To An Alternate Package Root

The alternate `FUN_001234E8(...)` caller branch also closed cleanly.

The literal at `0x004DE300` is:

- `"DATA"`

and the alternate literal at `0x004D0970` also begins with:

- `"DATA"`

with only one xref:

- `FUN_00291EE0(...)`

So the branch in `FUN_00291EE0(...)` that conditionally calls
`FUN_001234E8(0x004D0970)` is **not** selecting a different package namespace.
It is still feeding the same pack stem into the package backend.

That fits the `FUN_001234E8(...)` string templates much better than the earlier
"alternate root" theory:

- `cdrom0:\%s%sP%s.HDP;1`
- `cdrom0:\%s%sP.WAD;1`
- `host:%shostP%s.HDP`
- `host:%sP%s.HDP`

So the meaningful interpretation is:

- `FUN_001234E8("DATA")` sets up the `DATAP*` package backend
- the later `0x004D0970` call is mode/setup noise around the same `"DATA"` stem,
  not a hidden `levels`-side namespace

That closes one more false lead on the package boundary.

### Phase 203: `DATAPF.HDP` Is An Alternate HDP Index Over The Same `...P.WAD`

The remaining `DATAPF.HDP` question is now materially narrower.

The key bridge is the second caller of `FUN_00125560(...)`:

- `FUN_001234E8(...)` calls `FUN_00125560(path, 0, 0, 0)`
- `FUN_002A5950(...)` calls `FUN_00125560(param_2, 0, param_3, param_4)`

Inside `FUN_00125560(...)`, the last argument selects the `D/F` suffix used by
the `...P%s.HDP` path templates:

- `param_4 == 0` -> `"D"`
- `param_4 != 0` -> `"F"`

That branch is exercised explicitly by the two wrapper helpers:

- `FUN_002A5868(...) -> FUN_002A5950(..., ..., ..., 0)` -> `...PD.HDP`
- `FUN_002A58A0(...) -> FUN_002A5950(..., ..., ..., 1)` -> `...PF.HDP`

The backing data-file side is separate. `FUN_002A5950(...)` then calls
`FUN_002A5618(...)`, which constructs and opens:

- `\%s%sP.WAD;1`
- or `host:%sP.WAD`

So the important structural result is:

- `DATAPD.HDP` and `DATAPF.HDP` are not paired with separate WADs
- they are alternate HDP indexes over the same `...P.WAD` backing file

The `D/F` split is also not zone-specific runtime. The immediate wrappers are
gated by generic mode/state helpers:

- `FUN_002998A0(...)` -> `DAT_0049C478` loaded-state gate
- `FUN_002998C8(...)` -> `DAT_0049C47C` loaded-state gate
- both collapse to `true` when `DAT_004985B4 == 2`

So the current safest interpretation is:

- `DATAPD.HDP` is the main hashed directory/file index already matched to
  `FUN_00125298(...)`
- `DATAPF.HDP` is a second HDP index family used by a broader subsystem through
  `FUN_002A58A0(...)`, but it still targets the same `...P.WAD`

That means the "wrong outer package family" theory is now weaker again. The
package layer looks increasingly coherent; the unresolved translation is more
likely above these HDP/WAD selectors than inside them.

### Phase 214: The `PD/PF` Wrapper Branch Is Audio-Only

The later `PD/PF` wrapper pass is now closed as non-zone infrastructure.

The command table rooted at `0x0049DE80` resolves as an audio command band with
entries such as:

- `SetRandomMode`
- `SetMusicLooping`
- `LoadMusicHeader`
- `LoadStreamHeader`
- `AddMusicTrack`
- `PlayStream`
- `StopStream`
- `StopAudioStreams`

The relevant wrapper entries are:

- `LoadMusicHeader -> FUN_00285510 -> FUN_0029D3F0 -> FUN_002A5868`
- `LoadStreamHeader -> FUN_00285580 -> FUN_0029D430 -> FUN_002A58A0`

and those wrappers only:

- extract an object/value from script/config through `FUN_002AE858(...)`
- bracket the call with a render/engine lock
- forward into the already-known `PD/PF` loader pair

So this branch belongs to the audio header/stream subsystem, not the zone
texture owner path around:

- `FUN_00290038(...)`
- `FUN_00157318(...)`
- `FUN_0016AD60(...)`

The adjacent `FUN_00299A90(...)` family also ended up low-value here. Its
recovered body searches a runtime table rooted at `DAT_00567700 / DAT_00567710`
for a keyed entry whose state via `FUN_0029D480(...)` is not `1`, and the
caller-side `0/1` mode split does not map cleanly onto the unresolved scene
translation problem.

So the exposed `PD/PF` loader path is now best understood as:

- audio-side header/index loading over `...PD.HDP` and `...PF.HDP`
- not the missing bridge between logical scene `.tex` requests and shipped zone
  `worldzones/.../*.pak.ps2` data

### Phase 215: `FUN_002A79F0(...)` Is Generic Config Lookup, Not Path Translation

The `FUN_002A79F0(...)` / `0x62DF9442` branch is also closed as a false lead.

`FUN_002A79F0(...)` is just a thin wrapper over the typed config/QB lookup
family around `FUN_002A77A0(...)`, `FUN_002A72C8(...)`, `FUN_002A7940(...)`,
and `FUN_002A7980(...)`. It reads values out of the current config node; it is
not a package/path resolver.

One concrete reuse site is the zone sound loader path, where the same family is
used for keys such as:

- `zone_sfx_size_%s`
- `zone_size_warning_%s`
- `%s_sfx_addresses_%s`
- `Sounds\\pak\\%s_sfx.pak.%s`

So `0x62DF9442` is best interpreted as another config/QB symbol, not a hidden
package alias or path-hash translator. That makes this branch low-value for the
`levels` vs `worldzones` contradiction.

### Phase 216: `DATAPD.HDP` Contains The Physical `worldzones` Pack Names, Not The Logical `levels` Names

The strongest new data-side result is a direct hash check against the shipped
HDP files using the now-confirmed `FUN_001185C8(...)` normalized hash model.

For `DATAPD.HDP`:

- physical directory hash present:
  - `worlds\\worldzones\\z_ho -> 0x81C3DA33`
- logical scene-style directory hashes absent:
  - `levels\\z_ho -> 0xCDC25E7F` **missing**
  - `levels\\z_ho\\z_ho -> 0x5287727B` **missing**

Leaf-name checks are even sharper:

- logical scene-side names absent:
  - `z_ho.tex -> 0xD8542756` **missing**
  - `z_ho_net.tex -> 0xB65E034F` **missing**
  - `z_ho_sky.tex -> 0x72CD5B80` **missing**
  - `z_ho.geom.ps2 -> 0xFD7E1868` **missing**
  - `0009BF70.tex -> 0xC5BF9FDE` **missing**
- shipped physical pack names present:
  - `z_ho.pak.ps2 -> 0x15FC8212`
  - `z_ho_net.pak.ps2 -> 0x574CD0EE`
  - `z_hoped.pak.ps2 -> 0x0BB262E8`

The concrete `worlds\\worldzones\\z_ho` bucket in `DATAPD.HDP` is:

- front record:
  - `front[950] = (count=3, dir_hash=0x81C3DA33, ptr=0x101C8)`
- tail run:
  - `(off=0x42FE0, size=0x74B660, leaf_hash=0x574CD0EE)` -> `z_ho_net.pak.ps2`
  - `(off=0x52250, size=0x186DF0, leaf_hash=0x0BB262E8)` -> `z_hoped.pak.ps2`
  - `(off=0x52560, size=0x829090, leaf_hash=0x15FC8212)` -> `z_ho.pak.ps2`

So `DATAPD.HDP` is not silently carrying the logical `levels\\...\\.tex` scene
names in hashed form. It is indexing the shipped physical `worldzones` pack
names.

### Phase 217: `DATAPF.HDP` Also Misses The Logical `levels` / `.tex` Hashes

The same candidate-hash check against `DATAPF.HDP` closes the last obvious
package-index fallback:

- it also misses:
  - `levels\\z_ho`
  - `levels\\z_ho\\z_ho`
  - `z_ho.tex`
  - `z_ho_net.tex`
  - `z_ho_sky.tex`
  - `z_ho.geom.ps2`
- but it does contain the physical pack leaves:
  - `z_ho.pak.ps2`
  - `z_ho_net.pak.ps2`
  - `z_hoped.pak.ps2`

So neither `DATAPD.HDP` nor `DATAPF.HDP` contains the logical `levels`-side
scene names in hashed form. That is the strongest evidence so far that the
remaining translation layer sits **above** the HDP/WAD backend rather than
inside it.

At this point the remaining contradiction is narrower and more concrete:

- the scene-side runtime still formats logical requests like
  `levels\\%s\\%s%s.tex`
- the shipped package indexes expose only physical `worldzones/.../*.pak.ps2`
  entries, even in hashed form

So the best next target is back on the scene/owner side:

- the higher-level producer that maps logical scene requests onto physical pack
  names before `FUN_001216F0(...)` / `FUN_00125298(...)` run
- not more work inside the `PD/PF` archive split and not more work inside the
  generic config lookup family

### Phase 204: The `PD/PF` Wrapper Branch Is Music/Stream Header Loading, Not Zone Runtime

The next caller pass closed another promising-looking but ultimately unrelated
branch.

The wrapper xrefs are very tight:

- `FUN_0029D3F0(...)` is only called by `FUN_00285510(...)`
- `FUN_0029D430(...)` is only called by `FUN_00285580(...)`

Those two functions do the same basic thing:

- resolve an object/key through `FUN_002AE858(...)`
- enter a guarded runtime section with `FUN_0011CA28(...)`
- call the `PD` or `PF` wrapper
- leave through `FUN_0011CA50(...)`

The important classification came from the surrounding dispatch table at
`0x0049DE60`. It is a flat string/function command table, and the relevant
entries are:

- `0x004E0F68 -> "LoadMusicHeader" -> FUN_00285510(...)`
- `0x004E0F78 -> "LoadStreamHeader" -> FUN_00285580(...)`

The nearby table entries confirm the subsystem:

- `SkipMusicTrack`
- `PauseMusic`
- `StopMusic`
- `SetMusicMode`
- `SetRandomMode`
- `SetMusicLooping`
- `AddMusicTrack`
- `ChangeTrackState`
- `GetCurrentTrack`
- `PlayStream`
- `StreamExists`
- `AddStream`
- `RemoveStream`
- `StopStream`

So the `FUN_0029D3F0(...)` / `FUN_0029D430(...)` branch is not the missing
`levels -> worldzones` resolver for zone textures. It belongs to the audio
header/stream system.

The deeper helper `FUN_00299A90(...)` also fits that reading better than the
earlier package-index theory. Its recovered body does **not** use the apparent
caller-side `mode 0/1` argument in any meaningful way. Instead it scans the
runtime table rooted at `DAT_00567700` / `DAT_00567710` for an entry keyed by
`param_1` whose backing state via `FUN_0029D480(...)` is not `1`.

Its immediate callers are all table-management helpers over that same runtime
band:

- `FUN_00299CB0(...)`
- `FUN_00299EE0(...)`
- `FUN_00299F48(...)`
- `FUN_0029B1A0(...)`
- `FUN_0029B1F8(...)`
- `FUN_0029B5F0(...)`
- `FUN_0029B730(...)`
- `FUN_0029D5C8(...)`

Those functions:

- remove keyed entries from the runtime table
- map keys to table indices
- attach or detach per-entry payload lists
- query whether an object references one of the registered header entries

So the safest current interpretation is:

- `FUN_002A5868(...)` / `FUN_002A58A0(...)` still do load `...PD.HDP` /
  `...PF.HDP` over the shared `...P.WAD`
- but the exposed caller branch using them here is the music/stream header
  subsystem, not the zone scene/texture owner loader
- and the apparent `FUN_00299A90(..., 0/1)` mode split is not the `D/F` split;
  it is either optimized away or irrelevant at the recovered body level

That makes this whole audio-side `PD/PF` wrapper branch low-value for the zone
texture contradiction. The better remaining targets are back on:

- the scene/owner loader path around `FUN_0016AD60(...)`
- or some higher-level logical-path producer that turns scene requests like
  `levels\\...\\.tex` into shipped package paths before the owner loader ever
  sees them

### Phase 204: The `PD/PF` Wrapper Branch Is Audio Streaming, Not Zone/Scene Loading

The next pass above `FUN_002A5868(...)` / `FUN_002A58A0(...)` closes that whole
branch as non-zone runtime.

The immediate callers are:

- `FUN_0029D3F0(...) -> FUN_002A5868(...)`
- `FUN_0029D430(...) -> FUN_002A58A0(...)`

and their direct wrappers are:

- `FUN_00285510(...)`
- `FUN_00285580(...)`

Those wrappers do not build scene or zone-owner paths. They simply:

- pull an object pointer out of the config/script payload with `FUN_002AE858(...)`
- enter a small context guard with `FUN_0011CA28(...)`
- forward into the `PD/PF` loader wrappers
- then restore context with `FUN_0011CA50(...)`

The decisive classification comes from the dispatch table at `0x0049DE80`:

- `"SetRandomMode" -> FUN_00285818`
- `"SetMusicLooping" -> FUN_00285850`
- `"LoadMusicHeader" -> FUN_00285510`
- `"LoadStreamHeader" -> FUN_00285580`
- `"AddMusicTrack" -> FUN_002855F0`
- `"ChangeTrackState" -> FUN_00285670`
- `"GetCurrentTrack" -> FUN_00285890`
- `"TrackEnabled" -> FUN_002856D0`
- `"MusicIsPaused" -> FUN_00285768`
- `"ClearMusicTrackList" -> FUN_00285788`
- `"PlayStream" -> FUN_00284368`
- `"StreamExists" -> FUN_00284538`
- `"AddStream" -> FUN_00284588`
- `"RemoveStream" -> FUN_00284600`
- `"StopStream" -> FUN_00284678`
- `"StopAudioStreams" -> FUN_002846C0`

So the actual branch is:

- audio/music command table
- `LoadMusicHeader` / `LoadStreamHeader`
- `FUN_0029D3F0` / `FUN_0029D430`
- `FUN_002A5868` / `FUN_002A58A0`
- `FUN_002A5950`
- `FUN_00125560(..., param_4 = 0/1)` choosing `...PD.HDP` vs `...PF.HDP`
- `FUN_002A5618(...)` opening the matching `...P.WAD`

That is a coherent audio-stream/header loader family. It does **not** overlap
with the zone/scene owner path around:

- `FUN_00290038(...)`
- `FUN_00157318(...)`
- `FUN_0016AD60(...)`
- `FUN_001E9FA8(...)`

So the `PD/PF` split is no longer a promising lead for the unresolved
`levels\...\.tex` vs `worldzones\...\*.pak.ps2` contradiction. It belongs to
audio stream/music header loading instead.

### Phase 218: The Inner Hollywood Pack Confirms Named Zone QB Assets Beside The Anonymous Zone TEX

Using the current THAW PS2 `PAK` parser, extracting shipped:

- `Extracted/DATAP/worlds/worldzones/z_ho/z_ho.pak.ps2`

produces a coherent 90-file archive with both:

- named zone-authored QB assets:
  - `worlds/worldzones/z_ho/z_ho.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_scripts.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_peds.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_sfx.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_sfx_scripts.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_level_particle_data.qb.ps2`
  - `worlds/worldzones/z_ho/z_ho_level_fast_particle_data.qb.ps2`
- and the anonymous compact zone texture payload:
  - `0009BF70.tex`

That materially strengthens the earlier format split:

- the shipped inner worldzone pack really does contain the authored Hollywood
  QB payloads under physical `worlds/worldzones/...` names
- while the zone texture payload still sits beside them as an unnamed compact
  `.tex` entry

So the unresolved problem is no longer “does the inner pack really contain the
zone-authored data?” It does.

### Phase 219: `FUN_00263608(...)` Is A Real Higher-Level Physical `worldZones` Resolver, But One Caller Is Still Sky

The string-backed helper:

- `FUN_00263608(...)`

is now decompiled cleanly enough to classify its behavior. It:

- reads a candidate list from config via `FUN_002AE030(..., 0x993AAEA0, ...)`
- resolves each candidate name through the current scoped object table
- formats:
  - `worlds/worldZones/%s/%s.%s.%s`
- probes the candidate path through `FUN_00122068(...)` / `FUN_00121CF0(...)`
- keeps the best match
- writes the selected candidate back into the source config object through:
  - `FUN_002AD340(..., 0x7A354E97, chosen_entry)`

That is direct code-side evidence that THAW PS2 has a higher-level
logical-to-physical resolver above `DATAPD/F.HDP`.

However, the first exposed caller is still a closed branch:

- `FUN_001275C0(...)`

That caller walks the runtime table at `0x53C340`, loads physical paths under
`PTR_s_skies__00498770`, and remains the sky branch, not the scene/zone owner
path.

So `FUN_00263608(...)` is important as a mechanism, but not every visible user
of it is relevant to the zone TEX contradiction.

### Phase 220: The `0x572370` Branch Is A Better Zone-Side Lead Than The Earlier Scene/Backend Paths

The second live user of that resolver is materially better:

- `FUN_002E8328(...)`

This function is a `"zone"` string-ref path that:

- reads a config list via `FUN_002AEA18(..., 0x2E7D5EE7, ...)`
- builds the runtime table at `0x572370` with `FUN_00262AA0(...)`
- backfills missing physical path sizes through `FUN_002E6E08(...)` and
  `FUN_00122068(...)`
- normalizes/layouts the table via:
  - `FUN_002626C0(...)`
  - `FUN_00263570(...)`
  - `FUN_00263928(...)`
- then associates per-zone authored QB/config payloads through
  `FUN_00262958(...)`

The activation/update side around it is:

- `FUN_002E8048(...)`
- `FUN_002E7C48(...)`
- `FUN_002995B8(...)`

`FUN_002E8048(...)` looks up a zone entry in the `0x572370` table from a config
hash (`0x5F143FF8`) and then toggles or refreshes that zone through
`FUN_002E7C48(...)`. `FUN_002995B8(...)` trims the current zone name from
`entry + 0x74`, constructs additional keyed names and payload handles from it,
and loads/activates zone runtime state. This is substantially closer to a real
global zone-map / zone-runtime family than the earlier audio, sky, callback,
or owner-constructor false leads.

What this does **not** prove yet:

- an explicit direct call from `FUN_00157318(...)` / `FUN_00290038(...)` into
  this `0x572370` family
- a literal shipped-data alias string mapping `levels\\z_ho` onto
  `worlds\\worldzones\\z_ho\\z_ho.pak.ps2`

But it does change the best next target. The highest-value live branch is now:

- the zone-map / zone-runtime family around:
  - `FUN_002E8328(...)`
  - `FUN_002E8048(...)`
  - `FUN_002E7C48(...)`
  - `FUN_002995B8(...)`

rather than the already-closed HDP/WAD backend or the sky/audio path-builder
branches.

### Phase 221: Shipped Data Still Exposes Physical `worldzones` Naming, But Not A Literal `levels -> worldzones` Alias Table

Cross-checking shipped content still gives the same negative result on explicit
string aliases:

- `DATAP.HED` names the physical Hollywood packs:
  - `\\worlds\\worldzones\\z_ho\\z_ho.pak.ps2`
  - `\\worlds\\worldzones\\z_ho\\z_ho_net.pak.ps2`
  - `\\worlds\\worldzones\\z_ho\\z_hoped.pak.ps2`
- the extracted Hollywood folder contains only those three physical packs
- `dbg.pak.ps2` exposes many physical `worlds\\worldzones\\z_ho\\...` debug
  names and also script symbols such as:
  - `LoadScene`
  - `load_z_ho`
- but broad shipped-content scans still do **not** expose a literal
  `levels\\z_ho -> worlds\\worldzones\\z_ho\\z_ho.pak.ps2` mapping table

So the current best synthesis is:

- physical `worldzones/.../*.pak.ps2` naming is clearly data-driven
- the global zone/runtime code clearly has a higher-level zone resolver
- but the exact logical scene-name to physical pack-path translation is still
  happening through runtime config/QB structures rather than an obvious flat
  shipped alias table

### Phase 222-234: The Zone Command Layer Now Resolves Cleanly Into Physical Folder State, Current-Zone State, And Pending-Load State

The command table at `0x0049FDB8` is now concrete rather than inferred:

- `SetPakZoneFolder -> FUN_002E7FA0(...)`
- `CreateZoneMap -> FUN_002E8328(...)`
- `StartPakLoad -> FUN_002E8048(...)`
- `SetCurrentZone -> FUN_002E86B0(...)`
- `GetCurrentZoneName -> FUN_002E8730(...)`
- `FinishPendingZoneLoads -> FUN_002E88A8(...)`
- `SetSaveZoneNameToCurrent -> FUN_002E8800(...)`
- `GetSaveZoneName -> FUN_002E8878(...)`
- `ZoneLoaded -> FUN_002E8A60(...)`
- `ZoneLoadedAndParsed -> FUN_002EB8A0(...)`
- `LoadPedPak -> FUN_002E8930(...)`

The important structural result is that this layer splits into three different
state families:

1. Physical folder root:

- `FUN_002E7FA0(...)` (`SetPakZoneFolder`) pulls an unnamed/raw argument via
  `FUN_002AE858(param_1, 0, ...)` and stores it into the global buffer at
  `0x49D3E8`.
- `FUN_002E6E08(...)`, used during `CreateZoneMap`, later combines that global
  folder root with the zone entry name at `entry + 0x74` through:
  - `%s%s/%s.%s.%s`
  - `%s%s/%s%s.%s.%s`
- together with the nearby string block:
  - `_net`
  - `pak`
  - `worlds/worldZones/%s/%s.%s.%s`

This finally pins down the physical-pack side of the runtime: the zone system
is not inventing archive paths ad hoc. It is using a configurable global folder
root plus the authored zone entry name to build paths like:

- `worlds/worldZones/z_ho/z_ho.pak.ps2`
- `worlds/worldZones/z_ho/z_ho_net.pak.ps2`

2. Current-zone selection:

- `FUN_002E8048(...)` (`StartPakLoad`) looks up a zone entry in `0x572370`
  using config hash `0x5F143FF8`, runs `FUN_002E7C48(...)`, then stores the
  selected entry pointer in `DAT_0049D3E4`.
- `FUN_002E86B0(...)` (`SetCurrentZone`) uses the same lookup path and also
  stores the resulting entry pointer in `DAT_0049D3E4`, but without starting
  the load/update side.
- `FUN_002E8730(...)` (`GetCurrentZoneName`) reads the current entry from
  `DAT_0049D3E4`, strips the trailing `_net`-style suffix from `entry + 0x74`,
  and returns the stripped name through callback/config hash `0x3324405C`.

So the runtime's "current zone" identity is neither the full physical
`worldzones/.../*.pak.ps2` path nor the anonymous inner `.tex` name. It is the
zone-map entry name stored at `entry + 0x74`, with `_net` trimmed when exposed
as the public current-zone name.

3. Save-zone / pending-load state:

- `FUN_002E8800(...)` (`SetSaveZoneNameToCurrent`) copies either:
  - the current entry name from `DAT_0049D3E4 + 0x74`, or
  - `"No zone name set"`
  into the global buffer at `0x5723A8`.
- `FUN_002E8878(...)` (`GetSaveZoneName`) returns that saved string through
  callback/config hash `0x20F4889D`.
- `FUN_002E88A8(...)` (`FinishPendingZoneLoads`) waits until `DAT_0049D470 == 0`
  by pumping `FUN_001222F0()` and `FUN_002EBAB8()`, and logs the explicit ped
  warning string:
  - `"DOH - FinishPendingZoneLoads called before ped parts loaded!\n"`
- `FUN_002E8930(...)` (`LoadPedPak`) iterates loaded zone entries
  (`entry + 0xE8 != 0`), runs `FUN_002E75C8()`, then forces `FUN_0025F350()`.

The loaded/parsing predicates are also less vague now:

- `FUN_002E8A60(...)` (`ZoneLoaded`) looks up a zone entry by hash and returns
  whether `entry + 0xE8` is non-zero.
- `FUN_002EB8A0(...)` (`ZoneLoadedAndParsed`) starts from the same lookup and
  loaded flag, but then also rejects the zone under additional global/current
  tracking conditions (`DAT_00572E48`, `DAT_00572E58...`, `DAT_004A00E0`,
  `DAT_004A0094`). So this is a stricter "loaded and not currently blocked by
  parse/runtime bookkeeping" predicate, not just a second spelling of
  `ZoneLoaded`.

This narrows the remaining contradiction further:

- `SetPakZoneFolder` and `FUN_002E6E08(...)` now explain the physical
  `worldzones/.../*.pak.ps2` side.
- `SetCurrentZone` / `GetCurrentZoneName` now explain the short zone-name side.
- what is still missing is the higher-level producer that feeds these commands
  their config payloads and decides when the scene-facing `levels\\...\\.tex`
  request becomes zone-command traffic against this physical-folder/current-zone
  runtime.

So the next best target is no longer the command layer itself. It is the caller
or QB/config payload that invokes:

- `SetPakZoneFolder`
- `CreateZoneMap`
- `StartPakLoad`
- `SetCurrentZone`

with the real zone arguments.

### Phase 235-236: There Is Still No Native `levels -> worldzones` Alias Between The Scene Owner Path And The Zone Command Path

The native boundary is sharper now, and the contradiction is cleaner rather
than looser.

On the scene/owner side:

- `FUN_00290038(...)` is still the direct user of
  `levels\\%s\\%s%s.tex`
- `FUN_0016AD60(...)` does one confirmed rewrite before the backend:
  - `FUN_0016AA10(...)` formats `"%s.%s"`
  - `FUN_00157F60()` resolves the platform suffix as `"PS2"`
  - so the actual loader input becomes `levels\\...\\.tex.PS2`
- `FUN_0016AD60(...)` hashes the bare logical path for its owner cache key, but
  passes the rewritten `...tex.PS2` path onward into:
  - `FUN_001A0890(...)`
  - `FUN_001A0280(...)`
  - `FUN_001A0480(...)`
  - `FUN_001E9FA8(...)`
  - `FUN_001216F0(...)`
- `FUN_001216F0(...)` does another normalization step for cache/index lookup by
  truncating a copy before the platform token (`ps2`/`xbx`/`ngc`/`xen`/`???`),
  but on cache miss it still sends the original input string to the file/backend
  open path
- `FUN_00125160(...)` / `FUN_00125298(...)` then only normalize slashes and
  hash the path they were given; they do not add a `levels -> worldzones`
  rewrite

On the zone runtime side:

- `SetPakZoneFolder` writes the physical folder root into `0x49D3E8`
- `FUN_002E6E08(...)` then combines that root with the zone entry name and
  platform suffix to build physical paths like:
  - `worlds/worldZones/z_ho/z_ho.pak.ps2`
  - `worlds/worldZones/z_ho/z_ho_net.pak.ps2`

What is **not** visible in native code so far:

- no alias/remap from `levels\\...\\.tex(.PS2)` to
  `worlds\\worldzones\\...\\*.pak.ps2` before `FUN_001216F0(...)`
- no master table or registry that groups the scene command table at
  `0x0049D580` together with the zone command table at `0x0049FDB8`
  - both roots still have `Ref count: 0`
  - adjacent roots like `0x0049D560`, `0x0049D700`, and `0x0049FDC0` also stay
    at `0`

So the current best synthesis is:

- the scene owner path and the zone command path are both real
- each one has its own internally coherent path-building logic
- but the bridge between them is still not in the native loader/backend path
- the most likely remaining location is the higher-level QB/config producer that
  invokes the scene and zone command families, not a hidden package/backend
  alias inside `FUN_001216F0(...)` or `FUN_00125298(...)`

### Phase 237: The Repo QB Decompiler Is Not Yet Useful For The Extracted Hollywood Zone Payloads

Using the current repo CLI on the extracted Hollywood inner pack:

- `qb TestOutput/z_ho_inner_extract/z_ho.pak/worlds/worldzones/z_ho`

does parse all 8 QB-like files without error, but the generated `.q` outputs are
nearly empty and do not expose useful command or path text. So for this branch,
the current repo QB tooling does not yet replace native decompilation as a way
to discover the producer that feeds `SetPakZoneFolder` / `CreateZoneMap` /
`StartPakLoad`.

### Phase 242-245: The Zone Command Slice Continues Into A Small `ZoneProfiles` Segment, Then Into Generic Appearance/NavMesh Commands

The command band at `0x0049FDB8` does not actually stop at `LoadPedPak`.

The wider tail dump at
`phase243_zone_command_tail_ptrs.txt` and
the string dump at
`phase242_zone_command_tail_strings_mem.txt`
show the contiguous layout from `0x0049FE60` onward:

- `PedPakLoaded`
- `ForceTransitionAreaUpdate`
- `AddZoneProfiles`
- `RemoveZoneProfiles`
- `SetZoneProfiles`
- `PrintLoadedProfiles`
- then the already-classified generic appearance/editable-list family:
  - `CreateRandomAppearance`
  - `AddEditableList`
  - `RemoveEditableList`
  - `ForEachInEditableList`
  - `SelectFrom`
- and then the later navmesh/debug commands from the same flat band

So the earlier `SetPakZoneFolder` / `CreateZoneMap` / `StartPakLoad` slice is
not a self-contained standalone registry. It is an inner window inside a larger
flat `string_ptr, function_ptr` command band.

The new helper decomp in
`phase237_zone_profile_handoff.c` and
`phase244_zone_profile_helpers.c` makes the
tail classification sharper:

- `ForceTransitionAreaUpdate` (`FUN_00345928`) just calls
  `FUN_00345488(DAT_004A1088)`
- `RemoveZoneProfiles` (`FUN_002947C0`) parses arg `0` and calls
  `FUN_00218B38(...)`
- `SetZoneProfiles` (`FUN_002947F8`) parses arg `0`, clears state with
  `FUN_00218B08()`, then applies `FUN_002189D8(...)`
- `PrintLoadedProfiles` (`FUN_00294828`) is effectively a stub through
  `FUN_00219110()`

`FUN_00218B38(...)` itself is not a path loader. It is a global loaded-profile
array mutation routine over `DAT_0049B068...DAT_0049B1E8`, removing entries from
that list and compacting the array afterward.

So this entire `ZoneProfiles` tail now looks like adjacent runtime profile /
appearance state, not the missing bridge from scene-facing
`levels\\...\\.tex.PS2` requests into physical `worldzones\\...\\*.pak.ps2`
pack loading.

The `PedPakLoaded` entry is also now classified. The instruction dump in
`phase245_pedpakloaded_instructions.txt`
shows the unlabeled slot at `0x002E8A38` is just a tiny boolean predicate over
two globals, not another hidden loader.

That leaves the command-band conclusion as:

- the zone map / pack-loading commands are real
- they sit inside a broader contiguous command band
- but the newly exposed tail entries are still not the missing scene-to-zone
  bridge

### Phase 242: Command-Root Registry Searches Are Still Negative

The command-root search also stayed negative in a useful way.

`phase242_command_root_scalar_search.txt`
found no raw scalar hits for the scene and zone command roots or their nearby
band starts:

- `0x0049D560`
- `0x0049D580`
- `0x0049FDB0`
- `0x0049FDB8`
- `0x0049FDC0`

That matches the earlier xref-negative result in
`phase236_command_table_roots_xrefs.txt`:
the roots are real flat command bands, but there is still no evidence of a
higher-level native registry that publishes those table addresses directly.

What *is* still concretely reachable are direct callers of individual command
functions, not the roots themselves. So if the remaining bridge is native, the
best live target is still the direct-call side of `LoadScene`, `SetPakZoneFolder`,
`CreateZoneMap`, `StartPakLoad`, or their immediate helpers, not another search
for a master table of command-band roots.

### Phase 246: The Immediate Direct Callers Still Split Scene And Zone Runtime Apart

The first direct-caller pass did not expose a single native routine that drives
both the scene owner path and the zone command path together.

The new decomp in
`phase246_direct_command_callers.c` shows:

- `FUN_002EC5D8(...)` is a broad scene-reset / teardown / reload routine that
  eventually calls `FUN_00290018(0,0)` (`UnloadAllLevelGeometry`), but does
  **not** also
  call the zone map helpers in the same function.
- `FUN_003163F0(...)` is a separate large runtime/config routine that directly
  calls `FUN_002E8608(...)` (`DestroyZoneMap`) after building config objects and
  dispatching multiple hashed callbacks, but it does **not** route through
  the real `LoadScene` entry.
- `FUN_00304ED0(...)` is another separate large runtime/gameplay routine that
  calls `FUN_002E8C30(...)` directly, again without collapsing into the
  scene-owner path.

So even on the direct-call side, the current evidence still looks like:

- one native family centered on scene cleanup / owner teardown
- a different native family centered on zone runtime helpers
- no newly exposed shared direct caller that obviously turns
  `levels\\...\\.tex.PS2` scene requests into physical `worldzones` pack
  traffic

This does not solve the bridge, but it does close another false hope: the
command roots are not published through a master table, and the first immediate
direct callers are not a clean convergence point either.

### Phase 248-252: The `Skate::ChangeLevel` / `Skate::ResetLevel` Family Is A Real Shared Orchestration Layer, But Not Yet The Bridge

The next caller layer finally exposed a coherent subsystem that touches both the
scene path and the current-zone path.

The key decomp is in:

- `phase248_unique_parent_paths.c`
- `phase249_30dx_family.c`
- `phase251_30dx_callers.c`
- `phase252_30dx_strings.txt`

The adjacent string block at `0x004F0180` is decisive:

- `%s_Zone_Origin`
- `Skate::ChangeLevel(%d)\n`
- `Skate::ResetLevel() - Regenerating level now\n`
- `%s%s`
- `_NodeArray`
- `Worlds/worldZones/`
- `/`
- `.rnb`

That labels the whole nearby family.

#### 1. `Skate::ChangeLevel` reaches cleanup / scene teardown, not the real `LoadScene`

`FUN_0030DD60(...)` is the actual `ChangeLevel` worker:

- logs with the `Skate::ChangeLevel(%d)` string
- stores the requested level/checksum-like value into object fields
- builds a small config object
- calls `FUN_002EC5D8(...)`
- and `FUN_002EC5D8(...)` then calls
  `FUN_00290018(0,0)` (`UnloadAllLevelGeometry`)

So this is a real higher-level native parent above scene cleanup / reset work,
not just another leaf helper, but it is **not** yet the confirmed native path
into `FUN_00290038(...)` (`LoadScene`).

#### 2. `Skate::ResetLevel` reaches the current-zone helpers

`FUN_0030DEC8(...)` is the matching reset/regeneration worker:

- logs with the `Skate::ResetLevel()` string
- calls `FUN_002E8B78(...)` to fetch the current zone name
- builds strings from that zone name using:
  - `%s%s`
  - `_NodeArray`
- and, under one path, explicitly builds physical paths using:
  - `Worlds/worldZones/`
  - `/`
  - `.rnb`

So this is the first decomp-backed native routine that clearly sits above the
current-zone helpers while also working with physical worldzone-path material.

#### 3. The nearby siblings still fit the same level/zone orchestration family

- `FUN_0030DA20(...)`:
  - calls `FUN_002E8B78(...)`
  - builds `%s_Zone_Origin`
  - stores/updates zone-origin data
- `FUN_0030E470(...)`:
  - is a higher-level driver that conditionally calls `FUN_0030DEC8(...)`
- `FUN_003100F8(...)`:
  - also calls `FUN_0030DEC8(...)`
- `FUN_003126B8(...)`:
  - is a thin wrapper into `FUN_0030DD60(...)`

So the `0x0030DA20` / `0x0030DEC8` / `0x0030DD60` / `0x0030E470` /
`0x003100F8` / `0x003126B8` band is not random neighborhood noise. It is a
real level/zone transition family.

#### What this means for the open contradiction

This is still the first strong native evidence that the missing bridge is
**not** inside the low-level archive/backend code and **not** in the flat
command-band roots themselves. But the concrete result here is narrower than it
first looked:

- `ChangeLevel` flows into cleanup / unload work through
  `FUN_002EC5D8(...) -> FUN_00290018(...)`
- `ResetLevel` and adjacent helpers flow into current-zone naming and physical
  `Worlds/worldZones/...` path construction

What is still missing is the exact shared decision point where one requested
level identity drives both:

- the logical scene-owner path (`levels\\...\\.tex.PS2`)
- and the physical worldzone-path material (`Worlds/worldZones/...`)

But this is still a much narrower target than before. The best next step is to
continue upward and sideways from the `Skate::ChangeLevel` /
`Skate::ResetLevel` family and its enclosing dispatcher hosts, rather than
returning to command tables or backend path code.

### Phase 247-252: The First Real Scene/Zone Convergence Point Exists, But It Sits Under Network/Session Dispatch

There *is* a native convergence point above the split direct callers, but it is
not yet the single-player zone bridge we need.

The key new artifact is
`phase247_scene_branch_callers.c`:

- `FUN_003126B8(...)` is a tiny wrapper over `FUN_0030DD60(...)`
- `FUN_0030DD60(...)` builds a small config object and calls
  `FUN_002EC5D8(...)`
- `FUN_00317888(...)` is a large opcode/handler-table installer using repeated
  `FUN_002C6D90(...)` calls

Inside that same installed opcode family:

- opcode `0x4A -> FUN_003126B8 -> FUN_0030DD60 -> FUN_002EC5D8`
- opcode `0x42 -> FUN_003163F0 -> ... -> FUN_002E8608` (`DestroyZoneMap`)

So the scene-side family and at least one zone-runtime family do coexist inside
the same higher-level installed handler set.

However, the next correction is just as important:

`FUN_002EC5D8(...)` is not a top-level `LoadScene` command handler. The table at
`phase227_zone_map_table_ptrs.txt` shows the
entry at `0x0049D72C` is:

- `Cleanup -> FUN_002EC5D8`

That means the scene-side branch we were chasing is a cleanup/reset command that
stops at `FUN_00290018(...)` (`UnloadAllLevelGeometry`), not the canonical
scene-load entry point `FUN_00290038(...)` (`LoadScene`).

The higher-level dispatcher context is also now much clearer:

- the table at
  `phase251_dispatcher_table_ptrs.txt`
  resolves `0x0049E49C -> FUN_002EE430` as:
  - `JoinServer`
- `FUN_002EE430(...)` in
  `phase249_dispatcher_callers.c` installs the
  `FUN_00317888(...)` handler family for one or more slots
- the same table also contains nearby network/session commands like:
  - `LeaveServer`
  - `SetNetworkMode`
  - `SetServerMode`
  - `StartNetworkGame`

So the first native convergence point we found is real, but it sits inside a
network/session command family, not an obviously single-player level/zone load
family.

That makes the current state:

- `FUN_00317888(...)` is a real mixed handler installer that can reach both
  scene cleanup/load behavior and zone-runtime helpers
- but the confirmed top-level host for that installer is `JoinServer`, i.e.
  network/session setup
- so this branch is probably an important engine-side cousin of the real bridge,
  not the main `levels\\...\\.tex.PS2 -> worlds\\worldzones\\...\\*.pak.ps2`
  path we need for the decoder

The best next target is therefore narrower again:

- find a non-network top-level dispatcher/command family analogous to
  `JoinServer` that installs or invokes the scene/zone handlers in the
  single-player world-loading path
- or go back to the direct producers of `CreateZoneMap` / `StartPakLoad` now
  that `FUN_002E8C30(...)` and the network/session convergence branch are both
  classified as side paths

### Phase 247: `FUN_002E8C30(...)` Is A Current-Zone Switch Helper Over Already-Loaded Zone Entries

The helper path behind the immediate zone-side callers is now substantially
clearer.

The existing helper decomp in
`phase238_current_zone_helpers.c` shows that
`FUN_002E8C30(int zone_id)` is not a loader and not a scene-to-pack bridge. It:

- requires the zone map to be live (`DAT_00572394`, `DAT_00572378`)
- requires no pending async/load state (`DAT_0049D470 == 0`, `DAT_0049D46C == 0`)
- walks the existing `0x572370` zone map entries
- finds an already-loaded entry (`entry + 0xE8 != 0`) whose id field
  `entry + 0x70` matches `zone_id`
- skips the entry if it is already current (`DAT_0049D3E4 == entry`)
- emits a callback/config object through `FUN_002BB038(...)`
- then schedules three state transitions through:
  - `FUN_002E6C60(0x2E7DC0, entry, 0, 0)`
  - `FUN_002E6C60(0x2E7E68, entry, 2, 0)`
  - `FUN_002E6C60(0x2E7C48, entry, 3, 0)`
- and finally updates `DAT_0049D3E4 = entry`

So `FUN_002E8C30(...)` is a "make this already-loaded zone current / transition
to it" helper, not the missing function that resolves `levels\\...\\.tex.PS2`
into a physical `worldzones` pack.

The direct callers reinforce that classification:

- `FUN_00304ED0(...)` in
  `phase240_zone_scene_bridge_candidates.c`
  calls `FUN_002E8C30(uStack_298, 0)` after spatial/runtime tests and then
  continues through movement/collision/camera-style logic. This looks like a
  gameplay/runtime zone-transition path, not a pack-load request.
- `FUN_003C00D8(...)` in the same artifact calls
  `FUN_002E8C30(*(param_2 + 0x88), 1)` and then copies transform/position data.
  Again this is runtime state handoff, not asset loading.
- `FUN_00399AA8(...)` likewise reaches `FUN_002E8C30(...)` from another spatial
  runtime branch, not from scene-owner setup.

Combined with Phase 246, the best current reading is:

- `LoadScene` still lives in a scene-owner family
- `FUN_002E8C30(...)` lives in a zone-current/runtime-transition family
- the current-zone helper only operates *after* the relevant zone entry already
  exists in the `0x572370` map and is marked loaded

So this branch is important for zone activation, but it is still downstream of
the missing bridge we actually want.

### Phase 247: `FUN_002E8C30(...)` Is Current-Zone Transition Bookkeeping, Not A Loader Bridge

The existing helper decomp in
`phase238_current_zone_helpers.c` is enough
to classify the `FUN_002E8C30(...)` branch much more narrowly.

`FUN_002E8C30(int zone_id)` only runs when:

- the zone map is initialized
- no pending zone load is active (`DAT_0049D470 == 0`)
- no related blocker flag is set (`DAT_0049D46C == 0`)
- some runtime mode/state check through `FUN_0033B128(DAT_004A0D1C, 0x1DED1EA4)`
  succeeds

Then it scans the already-built zone map at `0x572370`, finds a **loaded**
entry whose `entry + 0x70` matches the requested `zone_id`, and if that entry is
not already `DAT_0049D3E4` it:

- emits a small callback/config object through `FUN_002BB038(...)`
- schedules three transition helpers:
  - `FUN_002E7DC0`
  - `FUN_002E7E68`
  - `FUN_002E7C48`
- updates `DAT_0049D3E4` to the new entry

So this helper is not performing path resolution, HDP/WAD lookup, inner-PAK
lookup, or scene-owner creation. It is a current-zone transition/switch helper
over **already loaded** zone-map entries.

That matches the shape of its direct callers:

- `FUN_00304ED0(...)` uses `FUN_00277A40(...)` / hit-style data and then passes
  `uStack_298` into `FUN_002E8C30(...)`; this looks like gameplay/runtime state
  reacting to a detected zone/area id, not an asset loader.
- `FUN_00399AA8(...)` calls `FUN_00395738(...)`, gets an id via
  `FUN_003958B8(...)`, and then calls `FUN_002E8C30(...)` before updating many
  transform/state fields; again this looks like a runtime actor/transition
  controller, not a pack resolver.
- `FUN_003C00D8(...)` is a tiny helper that forwards `*(param_2 + 0x88)` into
  `FUN_002E8C30(...)` and then copies vector-ish data through `FUN_0023C3A0`
  / `FUN_0023C3B0`; this also reads like a runtime state update helper.

So the `FUN_002E8C30(...)` family is now best understood as a **separate runtime
zone-switch subsystem**, not the missing bridge from `levels\\...\\.tex.PS2`
scene requests to physical `worldzones\\...\\*.pak.ps2` loads.

### Phase 255-262: The Dispatcher Host Families Close As Network/Session Infrastructure

The dispatcher branch is now materially tighter, and it is mostly a negative
result for the zone decoder.

First, the cleanup-side ambiguity is closed. The focused note in
`phase255_cleanup_vs_loadscene_findings.md`
matches the decomp:

- `opcode 0x4A -> FUN_003126B8 -> FUN_0030DD60 -> FUN_002EC5D8 -> FUN_00290018`
- `FUN_002EC5D8(...)` is the `Cleanup` command
- `FUN_00290018(...)` is `UnloadAllLevelGeometry`
- there is still no nearby edge from this branch into the real
  `FUN_00290038(...)` (`LoadScene`)

So the `Skate::ChangeLevel` neighborhood remains cleanup/reset only.

Second, the dispatcher installers themselves are now better rooted. The new
artifacts are:

- `phase257_dispatcher_host_xrefs.txt`
- `phase258_dispatcher_host_call_edges.txt`
- `phase260_fun_290b18.c`
- `phase261_fun_290b18_xrefs.txt`
- `phase262_table_49e440_ptrs.txt`

What they show:

- `FUN_00317888(...)` has only two confirmed direct parents:
  - `FUN_002EE430(...)`
  - `FUN_003DBCC0(...)`
- `FUN_002EE430(...)` is still the function half of the `JoinServer` entry in
  `phase251_dispatcher_table_ptrs.txt`.
- `FUN_003D2A38(...)` installs opcode `0x55 -> FUN_003DBCC0(...)`, and
  `FUN_003DBCC0(...)` immediately calls `FUN_00317888(...)` for slots `0` and
  `1`.

The last missing host label is now also resolved:

- `FUN_00317388(...)` has one incoming edge:
  - `FUN_00290B18(...)`
- `FUN_00290B18(...)` is a tiny wrapper over `FUN_00317388(DAT_004A0D04)` plus
  session-object bookkeeping on `DAT_004A11B8`
- `phase262_table_49e440_ptrs.txt` shows
  `FUN_00290B18(...)` is the function half of:
  - `StartServer -> FUN_00290B18(...)`

So the concrete dispatcher host picture is now:

- `StartServer -> FUN_00290B18 -> FUN_00317388`
- `JoinServer -> FUN_002EE430 -> FUN_00317888`
- `FUN_003D2A38 -> FUN_003DBCC0 -> FUN_00317888`

And the surrounding published command bands remain explicitly multiplayer /
session facing:

- `StartServer`
- `JoinServer`
- `LeaveServer`
- `SetNetworkMode`
- `SetServerMode`
- `StartNetworkGame`
- plus adjacent CTF/flag commands in the `0x0049E440` table

So this whole branch now looks like network/session dispatcher infrastructure,
not the missing single-player `levels\\...\\.tex.PS2 ->
worlds\\worldzones\\...\\*.pak.ps2` bridge.

That closes another false lead. The next best target is no longer the shared
dispatcher installers. It is either:

- a non-network scene/world load family above `FUN_00290038(...)`
- or a config/QB-driven producer that invokes the zone-world commands outside
  the multiplayer/session command bands

### Phase 263-264: The First Concrete Shared Data-Side Producer Lead Is `qb.pab.ps2`, Not The Local Zone QB Files

The latest pass tightened the split on the native side and then produced the
first real shared **data-side** lead.

On the native side, the scene/load family is still isolated:

- `phase263_scene_load_call_edges.txt`
  shows `FUN_00290038(...)` (`LoadScene`) still has no direct native caller
  beyond its published command entry
- the same artifact shows `FUN_00157318(...)` is only reached from
  `FUN_00290208(...)` (`AddScene`)
- the subagent pass over the same material confirmed that
  `FUN_00290038(...)` and `FUN_00157318(...)` only converge into shared
  scene-owner loading machinery (`FUN_0016AD60(...)`, `FUN_00157118(...)`,
  `FUN_00198060(...)`), not into the separate worldzone pack activation path

So the native split still stands.

The more important new result came from a shipped-data hash scan, written up in
`phase264_qbpab_command_hash_hits.md`.

Using the THAW-era lowercase-normalized QBKey path, the extracted build shows:

- `LoadScene` only hits `DATAP\\pak\\qb.pab.ps2`
- `SetPakZoneFolder` only hits `DATAP\\pak\\qb.pab.ps2`
- `StartPakLoad` only hits `DATAP\\pak\\qb.pab.ps2`
- `CreateZoneMap` has no straightforward raw hit in the extracted build
- `SetCurrentZone` does not hit `qb.pab.ps2`; it only shows up in several
  `createapark\\cap_assets\\*.pak.ps2` payloads

Just as importantly, the extracted `*.qb.ps2` corpus is cleanly negative for
those same raw hashes, including the local Hollywood files. So the first
concrete shared command-hash carrier is **not** `z_ho.qb.ps2` or
`z_ho_scripts.qb.ps2`. It is the global `qb.pab.ps2` container.

The strongest `qb.pab.ps2` offsets are:

- `LoadScene`:
  - `0x00043334`
  - `0x0018E549`
- `SetPakZoneFolder`:
  - `0x0018E4D5`
- `StartPakLoad`:
  - `0x0002B048`
  - `0x0018E952`
  - `0x0023D6E1`

And there is a meaningful broader band around `0x0018D000-0x00192000`:

- short zone names like:
  - `z_mainmenu`
  - `z_ho`
  - `z_bh`
  - `z_dt`
- then the command-hash cluster around `0x0018E4D5-0x0018E952`
- then Hollywood-specific assets like:
  - `Hollywood`
  - `music\\vag\\backgrounds\\z_ho_bg`
  - `loadscrn_hollywood`
  - `loadscrn_hollywood_2`
  - `Z_HO`

There is also a separate global string cluster near `0x0002E234` with:

- `d:\\data\\Worlds\\WorldZones\\`
- `z_world.pak`
- nearby zone/global symbols like `Z_STORYSELECT`, `Z_Mainmenu`, and
  `CAP_assets`

This is the first direct evidence that a global shipped script/config container
knows about both:

- scene-owner command material (`LoadScene`)
- zone-pack command material (`SetPakZoneFolder`, `StartPakLoad`,
  `ZoneLoaded`, `ZoneLoadedAndParsed`, `FinishPendingZoneLoads`)

However, at least one of those hit clusters is clearly Create-A-Park-specific:

- the `SetPakZoneFolder` hit at `0x0018E4D5` sits next to
  `Worlds/CreateAPark/`

So the result is not yet “we found the Hollywood bridge.” The more precise
reading is:

- the missing producer is now much more likely to live in the global
  `qb.pab.ps2` / packaged-script layer than in the local `z_ho*.qb.ps2` files
- but we still need to isolate the worldzone/story-zone script cluster from the
  Create-A-Park one inside that global container

That makes the next best target narrower again:

- map the `qb.pab.ps2` command-hash hit regions to specific global script
  clusters, especially the `0x0018D000-0x00192000` band and the
  `WorldZones` string cluster near `0x0002E234`
- then trace whichever of those clusters references story zones like `Z_HO`,
  `Hollywood`, or `loadscrn_hollywood` together with the scene/zone command
  hashes

### Phase 265: The `qb.pab.ps2` Story-Zone / Loadscreen Table Is Real, While The `WorldZones` String Cluster Looks More Like Package Catalog Data

The next pass split the two most interesting `qb.pab.ps2` regions more cleanly.

First, the `0x0002E234` `WorldZones` cluster now looks less like top-level
story routing and more like package/source descriptor data.

The strongest offsets in
`qb.pab.ps2`
are:

- `0x2D45C`: `Z_Mainmenu`
- `0x2DF0C`: `Z_Mainmenu_Net`
- `0x2E10C`: `Z_Mainmenu`
- `0x2E234`: `d:\\data\\Worlds\\WorldZones\\`
- `0x2E270`: `z_world.pak`

That pattern reads like zone metadata plus source/package naming, not the
actual scene/transition command script. In other words, this cluster now looks
more like a global worldzone package-definition/catalog subsystem than the
missing bridge itself.

Second, the broad `0x0018D000-0x00192000` band is now substantially stronger as
the real story-zone/load config lead. The focused note is in
`phase265_qbpab_zone_load_table.md`.

The corrected reading is:

- the full span is **mixed**
- but its latter half contains a very coherent repeated story-zone record
  pattern

The strongest repeated records are:

- `Chicago`
  - `music\\vag\\backgrounds\\z_ch_bg`
  - `loadscrn_chicago_classic`
  - `loadscrn_chicago`
  - `Z_CH`
- `Minneapolis`
  - `music\\vag\\backgrounds\\z_dn_bg`
  - `loadscrn_minneapolis_classic`
  - `loadscrn_minneapolis`
  - `Z_DN`
- `The Mall`
  - `music\\vag\\backgrounds\\z_ma_bg`
  - `loadscrn_mall_classic`
  - `loadscrn_mall`
  - `Z_MA`
- `Marseilles`
  - `music\\vag\\backgrounds\\z_ms_bg`
  - `loadscrn_marseilles_classic`
  - `loadscrn_marseilles`
  - `Z_MS`
- `Kyoto`
  - `music\\vag\\backgrounds\\z_ja_bg`
  - `loadscrn_kyoto_classic`
  - `loadscrn_kyoto`
  - `Z_JA`
- `Atlanta`
  - `music\\vag\\backgrounds\\z_at_bg`
  - `loadscrn_atlanta_classic`
  - `loadscrn_atlanta`
  - `Z_AT`
- `Santa Cruz`
  - `music\\vag\\backgrounds\\z_sz_bg`
  - `loadscrn_santacruz_classic`
  - `loadscrn_santacruz`
  - `Z_SZ`
- `Beverly Hills`
  - `music\\vag\\backgrounds\\z_bh_bg`
  - `loadscrn_beverly_hills`
  - `Z_BH`
- `Hollywood`
  - `music\\vag\\backgrounds\\z_ho_bg`
  - `loadscrn_hollywood`
  - `loadscrn_hollywood_2`
  - `Z_HO`
- `Downtown`
  - `music\\vag\\backgrounds\\z_dt_bg`
  - `loadscrn_downtown`
  - `Z_DT`

Just before those, the same broad span also contains:

- a dense short-zone list:
  - `z_mainmenu`
  - `z_ho`
  - `z_bh`
  - `z_dt`
  - `z_el`
  - `z_sm`
  - `z_oi`
  - `z_lv`
  - `z_sr`
  - `z_at`
  - `z_ch`
  - `z_dn`
  - `z_ja`
  - `z_ma`
  - `z_ms`
  - `z_sz`
  - `z_sv`
  - `z_sv2`
- and template-like records such as:
  - `Z_Viewer`
  - `Z_StorySelect`
  - `loadscrn_generic`
  - `TestLevel_Sky`

That is the strongest current shipped-data evidence for a global level-load /
story-zone configuration block.

The important caution is that the broad band is still mixed:

- `Worlds/CreateAPark/` appears earlier in the same span
- `editable_list` appears there too
- earlier bytes also contain unrelated UI / goal text

So the correct model is now:

- `qb.pab.ps2` contains at least two different relevant global structures:
  - a worldzone package/source catalog near `0x2E234`
  - a coherent story-zone/loadscreen/music table in the latter half of
    `0x18D000-0x192000`
- neither one alone proves the missing bridge yet
- but together they make `qb.pab.ps2` the strongest shipped-data lead by far

At the same time, the repo/tooling gap is clearer:

- `PakArchive` only knows `.pab` as a companion payload file for `.pak`, not as
  a standalone parsed container
- `RecursiveUnpacker` / `ArchiveCommand` only publish `.pak`, not `.pab`
- `QbCommand` only recognizes `.qb` or filenames containing `.qb.`, so
  `qb.pab.ps2` is not treated as a QB file by the current CLI
- a direct byte-level probe of `qb.pab.ps2` reports **zero** standard PAK magic
  hits, so this file is not a normal PAK table

So the next best target narrows again:

- isolate the actual boundaries of the coherent story-zone/load subtable inside
  `qb.pab.ps2`
- determine whether the nearby command-hash hits from Phase 264 belong to that
  same subtable or to adjacent Create-A-Park/global utility data
- and only after that decide whether the missing bridge is script-interpreter
  driven or encoded through another compiled global container format

## Phase 266: `qb.pab.ps2` Regions Resolved To Named `qb.pak.ps2` Scripts

The broad `qb.pab.ps2` archaeology is now anchored to concrete named script
entries by replaying the current repo `PakArchive` logic over `qb.pak.ps2` and
its companion `qb.pab.ps2`.

The important offsets resolve as:

- `0x18D000`
  - `scripts/game/level_strings.qb.ps2`
- `0x18E4D5`
  - `scripts/game/level_strings.qb.ps2`
- `0x18E549`
  - `scripts/game/level_strings.qb.ps2`
- `0x18E952`
  - `scripts/game/level_strings.qb.ps2`
- `0x19012C`
  - `scripts/game/level_strings.qb.ps2`
- `0x191CFC`
  - `scripts/game/levels.qb.ps2`
- `0x191F9C`
  - `scripts/game/levels.qb.ps2`
- `0x02E234`
  - `scripts/mainmenu/labelmenu.qb.ps2`

Relevant owning entries:

- `scripts/game/level_strings.qb.ps2`
  - offset `0x183EC0`
  - size `0xDB54`
  - end `0x191A14`
- `scripts/game/levels.qb.ps2`
  - offset `0x191960`
  - size `0x7090`
  - end `0x1989F0`
- `scripts/mainmenu/labelmenu.qb.ps2`
  - offset `0x02DC70`
  - size `0x1568`
  - end `0x02F1D8`

This reframes the earlier global-cluster model:

- the short-zone list and the `0x18E4xx-0x18E9xx` command-hash cluster are in
  `scripts/game/level_strings.qb.ps2`
- the human-readable per-level records like
  `Hollywood -> music\\vag\\backgrounds\\z_ho_bg -> loadscrn_hollywood -> Z_HO`
  are in `scripts/game/levels.qb.ps2`
- the `d:\\data\\Worlds\\WorldZones\\...` cluster near `0x2E234` is in
  `scripts/mainmenu/labelmenu.qb.ps2`

Direct command-hash scans over the extracted script bodies sharpen that
further:

- `scripts/game/level_strings.qb.ps2`
  - `LoadScene` at relative `0xA689`
  - `SetPakZoneFolder` at relative `0xA615`
  - `StartPakLoad` at relative `0xAA92`
- `scripts/game/levels.qb.ps2`
  - none of:
    - `LoadScene`
    - `SetPakZoneFolder`
    - `StartPakLoad`
    - `ZoneLoadedAndParsed`
    - `FinishPendingZoneLoads`
- `scripts/mainmenu/labelmenu.qb.ps2`
  - none of the same command hashes

That is the strongest shipped-data narrowing so far. The likely bridge-like
command cluster is not in the human-readable per-level display/loadscreen table
itself. It is in `scripts/game/level_strings.qb.ps2`, with
`scripts/game/levels.qb.ps2` adjacent but separate.

This also corrects the broad Phase 264 reading: raw `qb.pab.ps2` command-hash
hits are not all part of one shared zone-load system, because other hits map to
unrelated scripts like:

- `scripts/game/terrainsounds.qb.ps2`
- `scripts/mission_peds.qb.ps2`
- `scripts/engine/buttonscripts.qb.ps2`
- `scripts/game/sounds/global_sfx_dat_ps2.qb.ps2`
- `scripts/game/menu/gamemenu_debug.qb.ps2`

So the best next target is no longer “keep scanning raw `qb.pab.ps2`”. It is:

- `scripts/game/level_strings.qb.ps2`
- `scripts/game/levels.qb.ps2`

with `scripts/mainmenu/labelmenu.qb.ps2` as a separate worldzone-catalog side
band rather than the main story-level bridge.

## Phase 267: `level_strings.qb.ps2` Is The Best Named Script Lead, But It Is Still Mixed

After extracting the named `qb.pak.ps2` entries directly, the nearby script
family now looks like this:

- `scripts/game/level_strings.qb.ps2`
  - `LoadScene` at relative `0xA689`
  - `SetPakZoneFolder` at relative `0xA615`
  - `StartPakLoad` at relative `0xAA92`
- `scripts/game/levels.qb.ps2`
  - none of those hashes
- `scripts/mainmenu/labelmenu.qb.ps2`
  - none of those hashes
- `scripts/game/zone_management.qb.ps2`
  - none of those hashes
- `scripts/game/zone_links.qb.ps2`
  - none of those hashes
- `scripts/game/startup.qb.ps2`
  - none of those hashes

Also absent from all of those named scripts in direct raw-hash form:

- `CreateZoneMap`
- `SetCurrentZone`
- `DestroyZoneMap`

So the strongest direct script-side carrier is still only
`scripts/game/level_strings.qb.ps2`.

But this is also an important correction: `level_strings.qb.ps2` is visibly a
mixed/global script or string container, not a clean dedicated level-routing
table. It also contains general strings like:

- `Hollywood`
- `Invitation`
- `Lil' Jon`
- `GAME SETTINGS`
- `HIGH SCORES`
- `Load Game`

and a separate short-zone list:

- `z_mainmenu`
- `mainmenu`
- `z_ho`
- `z_bh`
- `z_dt`
- `z_el`
- `z_sm`
- `z_oi`
- `z_lv`
- `z_sr`

The immediate `SetPakZoneFolder` neighborhood is still mixed and
Create-A-Park-adjacent. Nearby printable strings include:

- `scn_`
- `net.pre`
- `Worlds/`
- `CreateAP`
- `ark/`

So the named-script resolution is real, but it still does **not** prove that
the `LoadScene` / `SetPakZoneFolder` / `StartPakLoad` cluster in
`level_strings.qb.ps2` is the final story-zone bridge.

At the same time, the strong per-level story/loadscreen records remain cleanly
separated in `scripts/game/levels.qb.ps2`, for example the Hollywood record:

- `Hollywood`
- `music\\vag\\backgrounds\\z_ho_bg`
- `loadscrn_hollywood`
- `loadscrn_hollywood_2`
- `Z_HO`

but that script has none of the visible scene/zone command hashes.

So the best next target narrows again:

- token-level parsing or decompilation of `scripts/game/level_strings.qb.ps2`
- understanding how it relates to `scripts/game/levels.qb.ps2`

not more raw whole-blob `qb.pab.ps2` offset scans.

## Phase 268: These THAW `*.qb.ps2` Bodies Are Not Raw QB Token Streams

Checking the extracted named scripts against the repo's current QB parser model
in `QbFile.TokenizeAll(...)` exposed a more important structural correction.

`QbFile` assumes a flat THUG-style token stream:

- one-byte token IDs in range `0..68`
- immediate payloads by token type
- `EndOfFile = 0` terminating the stream

But the extracted THAW script bodies isolated here do not match that shape.

### `scripts/game/level_strings.qb.ps2`

This file begins with plain ASCII string data, not QB tokens:

- `Slide`
- `New goal list unlocked. Secret goals!...`
- `New goal list unlocked. Pro goals!`
- `Part of Team Challenge complete!`
- `Press \\m5 to pick up Records`

It also clearly contains a mixed/general string table:

- `Hollywood`
- `Invitation`
- `Lil' Jon`
- `GAME SETTINGS`
- `HIGH SCORES`
- `Load Game`
- plus the short-zone list:
  - `z_mainmenu`
  - `mainmenu`
  - `z_ho`
  - `z_bh`
  - `z_dt`
  - `z_el`
  - `z_sm`
  - `z_oi`
  - `z_lv`
  - `z_sr`

### `scripts/game/levels.qb.ps2`

This file begins with a pointer-heavy binary table before embedded strings.
The clean repeated per-level records are visible later:

- `Beverly Hills`
- `music\\vag\\backgrounds\\z_bh_bg`
- `loadscrn_beverly_hills`
- `Z_BH`
- `Hollywood`
- `music\\vag\\backgrounds\\z_ho_bg`
- `loadscrn_hollywood`
- `loadscrn_hollywood_2`
- `Z_HO`

### `scripts/game/zone_management.qb.ps2`

This nearby named script also begins with binary record/pointer-looking data,
not a token stream.

### Meaning

This weakens the earlier command-hash interpretation in an important way:

- `LoadScene`, `SetPakZoneFolder`, and `StartPakLoad` are still present as raw
  32-bit values inside `scripts/game/level_strings.qb.ps2`
- but they are not yet proven executable QB command tokens
- at this point they are only proven to be values inside a mixed structured
  data container that the current generic `QbFile` parser cannot read

So the next best target changes again. It is no longer:

- “token-walk these extracted THAW scripts with the existing generic QB parser”

It is now:

- identify the THAW PS2 `*.qb.ps2` container/record format
- or find the runtime loader/interpreter that materializes these records before
  command dispatch

That is a stronger lead than continuing more raw `qb.pab` offset scans.

## Phase 272: The Native Zone Pack -> Zone QB Bridge Is Real

The worldzone branch is now concrete enough that the remaining ambiguity is no
longer in the backend or the command executor.

### `FUN_002e7710(...)` is the async physical pack loader

From `phase236_zone_load_state_helpers.c`, `FUN_002e7710(...)`:

- builds the physical path through `FUN_002e6e08(...)`
- uses the already-understood `worlds/worldZones/<zone>/<zone>.pak.<platform>`
  family
- increments pending-load state in `DAT_0049d470`
- schedules an async load through `FUN_0025e7c8(...)` with callback
  `0x2e7130`

So `FUN_002e7130(...)` is the completion callback for the physical worldzone
pack load, not a loose helper.

### `FUN_002e7130(...)` derives zone hashes and executes `%s.qb.%s`

From `phase270_qb_path_builders.c`, on successful completion
(`param_1 == 0`) it:

- stores the resolved pack handle/key at `entry + 0x70`
- normalizes/copies the zone name into `entry + 0x74`
- hashes the base zone name plus:
  - `%s_sfx`
  - `%s_gfx`
  - `%s_NodeArray`
  - `%s_SFX_NodeArray`
  - `%s_GFX_NodeArray`
- conditionally calls `FUN_002995b8(entry)` for the current zone
- formats `%s.qb.%s`
- hashes that path
- stores the checksum into the executor object at `+0xd4`
- runs `FUN_002ba2c8(...)`

That gives the first confirmed native bridge from:

- physical worldzone pack selection

to:

- packaged `%s.qb.%s` script/config execution

### `FUN_002e7c48(...)` / `FUN_002e8048(...)` are the higher-level wrappers

From `phase224_zone_map_family.c`:

- `FUN_002e7c48(...)` starts loads for the selected entry plus dependency lists
  at `+0xa6` and `+0xc6`
- in blocking mode it spins `FUN_002e8e60()` until `DAT_0049d470 == 0`
- then it finalizes with `FUN_002995b8(entry)`
- `FUN_002e8048(...)` resolves hash `0x5F143FF8` through the `0x572370` zone
  map, calls `FUN_002e7c48(...)`, and stores the selected entry in
  `DAT_0049d3e4`

`phase238_current_zone_helpers.c` also sharpens `FUN_002e8c30(...)`: it is a
transition helper that queues `FUN_002e7dc0`, `FUN_002e7e68`, and
`FUN_002e7c48` when switching between loaded zones that share the same pack
handle.

### Meaning

The live path is now:

1. zone hash -> `0x572370` zone-map entry
2. `FUN_002e7710(...)` async physical pack load
3. `FUN_002e7130(...)` completion callback
4. `%s.qb.%s` executor dispatch

So the best next target changes again. It is no longer:

- more HDP/WAD/backend archaeology
- more generic callback-executor internals

It is now:

- the actual `%s.qb.%s` payload/container that `FUN_002e7130(...)` executes
- or the relationship between this worldzone-QB branch and the separate
  `LoadScene -> levels\\...\\.tex.PS2` owner path

## Phase 273: `FUN_002bb038(...)` Is The Job/Context Constructor

The next bridge after `FUN_002e7130(...)` is now decompiled directly in
`phase273_qb_executor_ctor.c`.

### What `FUN_002bb038(...)` does

It is not the executor loop and not the file-hash step.

Normal path:

- allocates a `0xFC` context object
- runs `FUN_002b81e8(...)` to zero/reset it
- runs `FUN_002b8a58(...) -> FUN_002b87b0(...)`
- stores extra fields at:
  - `+0xBC`
  - `+0xD0`
  - `+0xD8`
  - optionally `+0xDC/+0xE0`

Most importantly, `FUN_002b87b0(...)` shows that the first argument to
`FUN_002bb038(...)` is the selector/job hash:

- it ends up at `ctx + 0xCC`
- and if no explicit stream pointer is supplied it is used in
  `FUN_002bea18(DAT_0049d304, selector)` to resolve the initial script stream at
  `ctx + 0x14`

So in the zone path:

```c
ctx = FUN_002bb038(0xAB73DA2E, payload, 0, 0, 0, 0, 0, 0);
ctx->d4 = hash("%s.qb.%s");
FUN_002ba2c8(ctx);
```

the hash `0xAB73DA2E` is **not** the `%s.qb.%s` file hash. It is the
selector/job type used against `DAT_0049d304`.

### What this means

This separates two previously conflated steps:

1. construct a generic callback/job context from:
   - selector hash
   - zone payload object
2. separately attach the concrete `%s.qb.%s` checksum at `ctx + 0xD4`
3. then execute the context with `FUN_002ba2c8(...)`

So the remaining gap is now narrower than before. It is no longer
“what constructor comes after `%s.qb.%s`?” The constructor is known.

The best next targets are now:

- the registry/object family behind `DAT_0049d304` and `FUN_002bea18(...)`
- the concrete meaning of selector `0xAB73DA2E`
- the consumers of `ctx + 0xD4` after the `%s.qb.%s` checksum is attached

## Phase 275: `DAT_0049D304` Is Only A Selector-Stream Cache

The selector side is now materially clearer from:

- `phase274_selector_registry.c`
- `phase275_executor_stream_registry.md`
- `phase275_selector_registry_classification.md`
- `phase275_selector_registry_helpers.c`
- `phase275_selector_registry_hosts.c`

### `DAT_0049D304` is not the master selector registry

The cleanest classification now is:

- `DAT_0049D304` is a bootstrap-created singleton cache of executable selector
  bytecode streams
- it is **not** the source-of-truth checksum registry for selector hashes
- the source-of-truth lookup happens first through the generic checksum/object
  table behind `FUN_002A7280(...)` / `FUN_002A72C8(...)`

Concrete structure from `FUN_002BE4C8(...)` / `FUN_002BE608(...)`:

- singleton size `0x28`
- `+0x18 = FUN_002BF030(..., 8, 0)` -> 256-bucket hash table
- `+0x1C/+0x20` -> inactive/LRU list endpoints
- `+0x24` -> cache mode / eviction control

So selector hashes like `0xAB73DA2E` do not resolve “inside `DAT_0049D304`”
directly. The real path is:

1. selector hash -> generic checksum/object table via `FUN_002A72C8(...)`
2. resolved object must be type `7`
3. `FUN_002BEA18(...)` clones/caches that type-`7` payload into
   `DAT_0049D304`
4. executor consumes the returned raw bytecode buffer

### `FUN_002BEA18(...)` returns a raw cached bytecode stream

`FUN_002BEA18(DAT_0049D304, selector)`:

- accepts either a raw selector hash or a live object resolving through
  `FUN_002A72C8(...)`
- requires type byte `+2 == 7` for the live object path
- on cache hit:
  - increments refcount at `entry + 0x0C`
  - moves the entry in the inactive/LRU list
  - returns `entry + 0x08`
- on cache miss:
  - reads the type-7 payload block at `object + 0x0C`
  - clones bytes from `payload + 0x0C`
  - normalizes the copied bytes with `FUN_002B47B8(...)`
  - creates a `0x18` cache entry:
    - `+0x04 = selector hash`
    - `+0x08 = cached bytecode pointer`
    - `+0x0C = refcount`
    - `+0x10/+0x14 = inactive/LRU links`
  - returns the cached bytecode pointer

This is now supported both by the selector-cache helpers and by executor-side
users like:

- `FUN_002B87B0(...)`
- `FUN_002BA2C8(...)`
- `FUN_002B95D8(...)`
- recursive graph walkers such as `FUN_00251BA8(...)`

All of those treat the return as a raw opcode byte stream, not an object
pointer.

### Type `7` objects are the real selector-script records

`FUN_002B43F8(...)` now makes the producer-side object shape concrete:

- allocates a live object through `FUN_002A73F0(...)`
- forces `object->type = 7`
- copies selector-script bytes into a payload block of `size + 0x0C`
- stores three header dwords at the front of that block
- actual selector bytes begin at `payload + 0x0C`
- stores that payload block at `object + 0x0C`
- invalidates the cached selector entry in `DAT_0049D304` if needed

The paired cache refresh sites in `FUN_002B6940(...)` and `FUN_002A7440(...)`
support the same reading: when a type-7 record is replaced or retargeted, the
selector-stream cache entry is rebuilt.

So the real selector family relevant to the zone-QB executor is:

- generic checksum-keyed runtime objects
- with type `7` as the named selector-script record kind

### Meaning of `0xAB73DA2E`

The zone callback path is now constrained more tightly:

1. `FUN_002E7130(...)` calls `FUN_002BB038(0xAB73DA2E, payload, ...)`
2. `FUN_002BB038(...) -> FUN_002B8A58(...) -> FUN_002B87B0(...)`
3. with no explicit stream pointer, `FUN_002B87B0(...)` resolves
   `0xAB73DA2E` through `FUN_002BEA18(DAT_0049D304, selector)`
4. that only works if a live type-7 object with selector hash `0xAB73DA2E`
   already exists
5. later the caller separately attaches the concrete `%s.qb.%s` hash at
   `ctx + 0xD4`

So `0xAB73DA2E` is no longer ambiguous:

- it is **not** the `%s.qb.%s` filename hash
- it is the selector hash of a preloaded type-7 selector-script record

The best next upstream target is therefore no longer the selector-stream cache
itself. It is the packaged-QB/container step that materializes those type-7
records in the generic checksum table before zone callbacks need them.

## Phase 276: `ctx + 0xD4` Is A Plain `%s.qb.%s` Hash

Targeted analysis of the QB hash attachment and zone loader also narrowed the
attachment-side ambiguity.

### `FUN_002BB6A0(...)` is only a hash wrapper

`FUN_002BB6A0(...)` decompiles as a thin wrapper over `FUN_001185C8(...)`, and
`FUN_001185C8(...)` is the normalized lowercase/slash-folding CRC helper.

That means the zone callback path:

```c
FUN_00474D88(buf, "%s.qb.%s", zone_name, platform);
ctx->d4 = FUN_002BB6A0(buf);
```

is simply:

```c
ctx->d4 = normalized_hash("%s.qb.%s");
```

So `ctx + 0xD4` is:

- not a file object
- not a pack handle
- not a richer selector/container wrapper

It is just the concrete normalized `%s.qb.%s` path hash carried alongside the
generic selector stream chosen by `ctx + 0xCC`.

### What remains unresolved

This leaves one very specific merge point:

- the generic selector bytecode stream resolved from the type-7 selector hash
  at `ctx + 0xCC`
- plus the attached concrete `%s.qb.%s` path hash at `ctx + 0xD4`

That merge point is still not proven.

One cleanup path (`FUN_002D7300(...)`) does call `FUN_00117598(...)` on:

- `ctx + 0xD0`
- `ctx + 0xD4`
- `ctx + 0xD8`

but the later direct decomp changed how that evidence should be read.

`FUN_00117598(...)` is not a scalar/hash helper. It expects a structured
wrapper object with:

- `a0 + 0x0C` = referenced object pointer
- `a0 + 0x08` = intrusive list node

and unhooks that wrapper while touching `object + 4`, `node + 0x0c`, and
`node + 0x10`.

That looks contradictory until the object sizes are compared:

- `FUN_002BB038(...)` allocates the zone-QB executor as a `0xFC` object
- `FUN_002D7300(...)` accesses `+0x100` and `+0x114`

So `FUN_002D7300(...)` is not operating on the same executor family. The
cleanup-side evidence is a false-family collision on the same offsets, not
evidence that the zone callback's `ctx + 0xD4` field is a heap object pointer.

So the best next target remains:

- the consumer path that actually uses `ctx + 0xD4` after the zone callback
  stores it

If that still stays hidden, the next fallback target is the upstream packaged
QB/container path around `FUN_002B6218(...)` and neighbors that constructs the
live type-7 selector records.

## Phase 276b: Packaged QB Bytes -> Live Type-7 Selector Objects

The upstream producer side is now much tighter from:

- `phase276_type7_producer_path.md`
- `phase276_type7_producer_band.c`
- `phase276_type7_producer_callers.c`
- `phase276_type7_bridge_wrappers.c`

### Narrowest proven producer path

The strongest currently proven materialization chain is:

1. `FUN_003042A0(path, ..., heap_id)`
2. `FUN_002B6BE8(path, ..., trace_flag, heap_id)`
3. `blob = FUN_00120B20(path, 0, 0, 0, 0)`
4. `FUN_002B6218(path, blob, ..., 0, trace_flag)`
5. inside `FUN_002B6218(...)`, opcode `0x23` records call
   `FUN_002B43F8(selector_hash, content_hash, script_bytes, script_len, path)`
6. `FUN_002B43F8(...)` allocates the live object, forces `type = 7`, and stores
   the selector-script payload block at `object + 0x0C`

That is now the last proven upstream step before the resulting object becomes
visible through `FUN_002A7280(...)` / `FUN_002A72C8(...)` and then consumable
by `FUN_002BEA18(...)`.

### What `FUN_002B6218(...)` is really doing

The new decomp clarifies the record grammar materially:

- `0x23` records are the direct producer records for live type-7 selector
  objects
- for that branch `FUN_002B6218(...)` derives:
  - `selector_hash = *(u32 *)(record + 2)`
  - `script_start = record + 6`
  - `script_end = FUN_002B51D0(script_start)` -> walk until terminator `'$'`
  - `content_hash = FUN_002B5248(script_start)` -> hash script bytes up to `'$'`

If the live selector entry is absent or changed, it then calls:

```c
FUN_002B43F8(selector_hash, content_hash, script_start,
             script_end - script_start, path);
```

So the current least-ambiguous wording is:

- packaged QB/config bytes are parsed by `FUN_002B6218(...)`
- `0x23` records are the direct producer records for live type-7 selector
  objects
- `FUN_002B43F8(...)` is the final materialization step

### Secondary record family

The same pass also shows `0x16` records go through `FUN_002B4648(...)`, which
materializes other runtime object types rather than the selector type-7 path.
That is useful negative evidence: the selector-script producer path is not a
vague “any record in this container” branch. It is specifically the `0x23`
record family.

### What remains unresolved above this

There is still a second wrapper path:

- `FUN_00304300(...) -> FUN_002B6D28(...) -> FUN_002B6218(path, buffer, ...)`

That proves `FUN_002B6218(...)` can consume an already-materialized in-memory
buffer, not only a blob freshly loaded through `FUN_00120B20(...)`.

So the remaining upstream gap is now very narrow:

- what higher-level packaged QB/container owner produces the `buffer` argument
  passed into `FUN_002B6D28(...)`

That is now a better next upstream target than re-reading `FUN_002B6218(...)`
itself.

## Phase 277: `FUN_0025C108(...)` Owns The Already-Buffered Selector Path

The buffered branch is now tighter from:

- `phase277_buffer_owner_path.md`
- `phase277_buffer_owner_callers.c`
- `phase277_buffer_owner_release.c`

The narrowest proven chain is:

1. `FUN_0025C108(...)`
2. `FUN_00304300(*(param_1 + 0x0C), buffer, 1, 1)`
3. `FUN_002B6D28(...)`
4. `FUN_002B6218(...)`
5. `0x23` records
6. `FUN_002B43F8(...)`

So `FUN_0025C108(...)` is now the first concrete owner above the
already-materialized buffer path.

What it still does **not** close is the family identity of `param_1` or the
semantic type of `param_1 + 0x0C`. That remains the next narrow upstream gap.

## Phase 279: The `FUN_002D7300(...)` Cleanup Contradiction Is A False-Family Collision

The `ctx + 0xD4` side is now tighter from:

- `phase279_ctx_d4_lifecycle.md`
- `phase278_refcount_cleanup.md`
- `phase278_refcount_cleanup.c`

The stable model is:

- `FUN_002BB038(...)` allocates the zone-QB executor as `0xFC` bytes
- `FUN_002E7130(...)` explicitly writes `ctx + 0xD4 = FUN_002BB6A0("%s.qb.%s")`
- executor-side helpers `FUN_002B87B0(...)`, `FUN_002B8E88(...)`,
  `FUN_002B9228(...)`, `FUN_002BA2C8(...)`, and `FUN_002B8B70(...)` do not
  explicitly consume `+0xD4` in the current coverage

The only apparent contradiction was `FUN_002D7300(...)` calling
`FUN_00117598(...)` on `+0xD0/+0xD4/+0xD8`, but that no longer argues against
the hash reading:

- `FUN_00117598(...)` expects a structured wrapper object, not a scalar hash
- `FUN_002D7300(...)` accesses `+0x100` and `+0x114`, so it is a different
  object family from the `0xFC` executor built by `FUN_002BB038(...)`

So the real remaining gap is not field typing. It is the hidden consumer that
actually consults the attached `%s.qb.%s` hash after `FUN_002E7130(...)` stores
it.

## Phase 280: The Visible Executor Payload Bridge Is Still `+0xD4`-Negative

The executor-side bridge is now tighter from:

- `phase280_executor_payload_bridge.md`
- `phase280_executor_payload_bridge.c`
- `phase280_executor_payload_bridge_xrefs.txt`
- `phase280_executor_d4_consumer.md`

The stable result is that the outer executor/payload bridge still does **not**
expose the hidden `%s.qb.%s` hash consumer.

### What the bridge helpers actually proved

The new helper decomp locks down the surrounding executor fields:

- `FUN_002B8578(ctx)` returns `ctx + 0xC0`
- `FUN_002B8580(ctx, obj)` is the ref-managed setter for `ctx + 0xC0`
- `FUN_002B8610(ctx, mask)` tests flag bits on `*(ctx + 0xC0) + 0x0C`
- `FUN_002B8AD8(ctx, payload)` wraps/attaches a small payload-side helper object
- `FUN_002B92D8(ctx)` refreshes the current stream/state through
  `FUN_002B5DD8(ctx + 0x18, ctx + 0x14, ctx + 0x20, &ctx + 0x1C)`

None of those helpers visibly reads `ctx + 0xD4`.

So `ctx + 0xC0` is now cleanly separated from `ctx + 0xD4`:

- `+0xC0` = managed attached payload/root callback object
- `+0xD4` = concrete normalized `%s.qb.%s` hash attachment

### Concrete callback family behind `FUN_002B9438(...)`

The visible callback target is now also concrete:

- `FUN_002B9438(...)` falls back to `ctx + 0xC0`
- it dispatches through callback-object vtable slot `+0x1C`
- the proven concrete family is the callback/config object vtable rooted at
  `DAT_004AD150`
- in that family, slot `+0x1C = FUN_0013E5E8(...)`

So the next plausible `ctx + 0xD4` consumer boundary is not the outer executor
loop anymore. It is the callback implementation side reached from
`FUN_002B9438(...)`, or a sibling direct callback path reached from
`FUN_002B94B0(...)`.

### Stronger negative result

The combined executor-side evidence now says:

- `FUN_002BA2C8(...)` does not visibly consume `ctx + 0xD4`
- its direct dispatch helpers do not visibly consume `ctx + 0xD4`
- the immediate payload-bridge helpers around `ctx + 0xC0/+0x18/+0x20` do not
  visibly consume `ctx + 0xD4`

That makes the visible outer executor family low-yield for more `+0xD4`
searches.

## Phase 281: `FUN_0025C108(...)` Is Broader Source-Manager Plumbing

The source-side branch is now tighter from:

- `phase281_source_object_classification.md`
- `phase280_source_object_helpers.c`
- `phase281_source_manager_callers.c`

The important correction is that `FUN_0025C108(...)` is not a narrow zone-QB
bridge.

### What `FUN_0025C108(...)` actually proved

`FUN_0025C108(...)` takes `param_1 + 0x0C` through a helper family that:

- strips the extensionless basename from a path-like source
- hashes that basename
- updates `DAT_0049B21C` through `FUN_00253530(...)`

The surrounding helpers now read as:

- `FUN_00253B30(path, out_name)` = basename-without-extension extractor
- `FUN_00253C98(path)` = normalized hash of that basename
- `FUN_00253530(manager, basename_hash, ...)` = real hashed-source manager
  insertion/update path

### Why this matters

That same helper family is reused by:

- `FUN_002EC140(...)`
- `FUN_003E3A08(...)`
- `FUN_003E3B48(...)`
- `FUN_0023C628(...)`

So this is broader packaged-source/content-manager plumbing, not a zone-QB-only
subsystem. The already-buffered zone-QB path is just one client of it.

That removes `FUN_0025C108(...)` as the best immediate next target for the
zone-QB `%s.qb.%s` hash merge point.

## Best next target after Phase 280-281

The highest-value remaining branch is now:

- the callback implementation family behind `FUN_002B9438(...)`
- especially deeper `FUN_0013E5E8(...)` symbol branches or sibling callback
  targets that may actually use the executor context argument

If that still stays negative, the next fallback is the direct-code callback
bridge around `FUN_002B94B0(...)`, not more work on `FUN_0025C108(...)` or the
outer executor helpers.

## Phase 282: Instruction-Level Check On `FUN_0013E5E8(...)`

The callback-family branch tightened again from:

- `phase282_callback_ctx_disasm.md`
- `phase282_callback_ctx_disasm.txt`

The useful result is that the concrete `DAT_004AD150` slot-`+0x1C`
implementation `FUN_0013E5E8(...)` does **not** preserve the incoming fourth
argument at function entry.

### Why that matters

`FUN_002B9438(...)` clearly dispatches a callback as:

- callback object
- symbol hash
- payload state
- executor context

But the raw instructions at `FUN_0013E5E8(...)` entry only preserve:

- `a0 -> s1`
- `a2 -> s0`

with no early save of `a3` before the main symbol-dispatch body starts.

That is strong evidence that `FUN_0013E5E8(...)` itself is not the first real
consumer of the executor context or the attached `%s.qb.%s` hash at
`ctx + 0xD4`.

### New best target

This shifts the callback-side priority again:

- `FUN_0013E5E8(...)` is still the proven concrete slot-`+0x1C` target
- but the next likely real consumer is now either:
  - a deeper sibling callback method that `FUN_0013E5E8(...)` invokes, or
  - the separate direct-code callback bridge around `FUN_002B94B0(...)`

So the direct-code callback path is now a stronger next target than another
broad pass over the visible outer executor helpers.

## Phase 286: Callback Survey Closed, Reconcile Branch Still Live

The callback-side narrowing is now strong enough that the best live branch is no
longer ambiguous.

From:

- `phase285_zone_callback_followup_targets.md`
- `phase286_reconcile_branch_narrowing.md`
- `phase136_owner_adjacent_reconcile_callers.c`
- `phase138_reconcile_immediate_callers.c`

the only already-proven zone-relevant native branch remains:

- `0xFFFFFFFFAA42A104`
- `FUN_0013E5E8(...)`
- vtable slot `+0x8C`
- `FUN_0013FF18(...)`
- `FUN_0016F3C0(...)`
- `FUN_0016A2D0(...)`
- `FUN_00164CE0(...)`

### Why the target moved

`FUN_0013FF18(...)` is now best read as a bundle unpacker:

- it reads up to seven symbol bundles from `DAT_00498808`
- calls `FUN_00140AB8(...)` on the source-name-like field
- then forwards the normalized target/source pair into `FUN_0016F3C0(...)`

That means the real source-selection and destination-pairing logic is no longer
inside the callback family itself.

### What is now concrete downstream

`FUN_0016F3C0(...)`:

- normalizes two names
- strips a shared suffix
- iterates registered candidate owners
- then calls `FUN_0016A2D0(candidate_owner, target_name, source_name)`

`FUN_0016A2D0(...)`:

- resolves an existing destination through `FUN_0016A100(...)`
- builds a temporary source object through `FUN_00169B98(...)`
- reconciles through `FUN_00164CE0(...)`
- then releases the temporary source with `FUN_00169FA0(...)`

So the real remaining gap is now cleaner:

- destination lookup semantics in `FUN_0016A100(...)`
- source-object construction semantics in `FUN_00169B98(...)`

### QbKey side note

A local search over the repo's `QbKeyNames*.txt` files did not resolve the
seven `FUN_0013FF18(...)` bundle hashes, so the callback bundle naming is still
not the highest-value branch.

## Best next target after Phase 286

The highest-value next pass is now:

- `FUN_0016A100(...)` for exact destination lookup behavior
- `FUN_00169B98(...)` for the temporary source object shape

Only after those are explicit does another pass on `FUN_00164CE0(...)` or the
remaining callback siblings make sense.

## Phase 287: Destination Lookup Is Now Explicit

The destination side tightened again from:

- `phase286_lookup_string_consts.txt`
- `phase287_lookup_and_source_helpers.c`

### `FUN_0016F3C0(...)`

`FUN_0016F3C0(param_1, param_2, target_name, source_name)` now reads as:

- normalize/lowercase `target_name`
- ensure it ends with `.png`
- normalize/lowercase `source_name`
- strip `source_name` at the first `.png`
- walk the intrusive list headed by `param_1 + 0x10`
- for each entry:
  - accept when `entry + 0x08 == param_2`
  - or `param_2 == -0x3B1871DE`
  - or `entry + 0x08 == 0`
  - and `entry + 0x04 != 0`
- then call:
  `FUN_0016A2D0(*(entry + 0x04), normalized_target_with_png, normalized_source_without_png)`

So the iterated destination objects are the `entry + 0x04` pointers in that
registered list, while `entry + 0x08` is only the id/filter gate.

### `FUN_0016A100(...)`

The destination resolver is also explicit now:

- hash the name with `FUN_001185C8(...)`
- probe `FUN_0016A4A8(owner, hash * multiplier)` using:
  - `1, 2, 3, 4, 6, 0x0C, 8, 0x18`
- if all miss:
  - strip the last 4 chars when `len > 4`
  - append `_auto32m0.png`
  - rehash
  - retry the same multiplier order

The exact suffix constants are now confirmed:

- `0x004B0478 = ".png"`
- `0x004AF8A8 = "_auto32m0.png"`

So the destination lookup side is no longer ambiguous.

## Phase 288: Source Construction Is A Wrapper, Not The Real Source Selector

The source side also tightened from:

- `phase288_source_constructors.md`
- `phase288_source_constructors.c`

`FUN_00169B98(owner, source_name, 0, 0, flag)` is now best understood as a
generic owner-child construction wrapper:

- call owner vtable slot `+0x14`
- set/clear only bit `0x10000` through `FUN_00168028(...)`
- key the new object by `FUN_00167F58(obj) -> *(u32 *)obj`
- insert it into the owner hash table

Then `FUN_00169FA0(...)` later:

- dispatches through owner vtable slot `+0x34`
- removes the same keyed object from the hash table

So the live reconcile path:

- builds a temporary child object from `normalized_source_without_png`
- reconciles with it
- and tears it back out

That means `FUN_00169B98(...)` itself is **not** the missing source-selection
logic. The next real source-side target is the concrete owner slot-`+0x14`
creator behind it.

## Best next target after Phase 288

The highest-value next branch is now the actual owner child-image creator path
behind `FUN_00169B98(...)`, which should map back into:

- `FUN_001A0540(...)`
- `FUN_00164B90(...)`

The destination side is explicit enough now that more work on `FUN_0016F3C0`
or `FUN_0016A100` is lower priority than that creator path.

## Phase 289: The Slot-`+0x14` Creator Maps Into The Generic Image Loader Family

The source-constructor branch tightened one more step from:

- `phase289_slot14_loader_boundary.md`
- `phase3_texture_data_layer.c`
- `vtable_004b41c8_ext.txt`

### What is now explicit

`FUN_001A0540(...)` is the owner child-image factory wrapper:

- allocate wrapper through `FUN_0019BB68(...)`
- call `FUN_00164B90(wrapper, path, mode, flags)`

`FUN_00164B90(...)` then:

- formats the path string
- hashes it into `*obj`
- dispatches through the image-wrapper load slot at `DAT_004B41C8 + 0x2C`

That load slot is now explicit:

- `DAT_004B41C8 + 0x2C = FUN_0019C4C8(...)`

And `FUN_0019C4C8(...)` calls `FUN_001E5CC8(...)`, the normal raw-image load
constructor path.

### Why this is still not the full zone answer

The strongest currently proven direct higher-level user of `FUN_00164B90(...)`
is still the generic standalone image path, which aligns with the earlier
`"images/%s.img.ps2"` family.

So the source side is now split like this:

- `FUN_00164B90(...)` itself still reads as generic image-loader plumbing
- the live zone-reconcile branch reaches the same owner slot indirectly through
  `FUN_00169B98(...)`

That means this branch is still relevant, but it does **not** by itself prove a
zone-specific source-selection path inside `FUN_00164B90(...)`.

## Best next target after Phase 289

The next high-value target is the exact handoff between:

- `FUN_00169B98(...)` as the manager-backed temporary source build
- and the actual path/value passed into the owner slot-`+0x14` loader from the
  live `FUN_0013FF18 -> FUN_0016F3C0` branch

So the best remaining question is no longer “what does `FUN_00164B90(...)`
load?” It is “what exact normalized source names from the live zone callback
branch are being fed into that generic loader path?”

## Phase 290: The Seven Source-Name Hashes Are Now Explicit

The source-name handoff is now tighter from:

- `phase290_source_name_handoff.md`
- `phase141_fun_13ff18_callers.c`
- `phase219_scene_path_string_dumps.txt`
- `phase222_preload_format_mem.txt`

### Exact source-name side of `FUN_0013FF18(...)`

All seven `FUN_0013FF18(...)` branches store the source-name field in the same
slot `uStack_5C`, and that exact slot is fed into both:

- `FUN_00140AB8(param_1, uStack_5C)`
- `FUN_0016F3C0(..., uStack_5C)`

The source-name hash map is now explicit:

- `0xF362DBBA -> source-name 0x676F1DF1`
- `0x7A993873 -> source-name 0x9B03ADAD`
- `0xE39069C9 -> source-name 0x020AFC17`
- `0x9497595F -> source-name 0x750DCC81`
- `0x0AF3CCFC -> source-name 0xEB695922`
- `0x7DF4FC6A -> source-name 0x9C6E69B4`
- `0xE4FDADD0 -> source-name 0x0567380E`

This was later tightened by local checksum-map evidence: these hashes are best
read first as QB/config field keys, not direct asset names:

- `0x676F1DF1 = with`
- `0x9B03ADAD = with1`
- `0x020AFC17 = with2`
- `0x750DCC81 = with3`
- `0xEB695922 = with4`
- `0x9C6E69B4 = with5`
- `0x0567380E = with6`

So the remaining gap is now the values stored under those `with*` fields.

### What `FUN_00140AB8(...)` contributes

`FUN_00140AB8(...)` also looks cleaner now:

- it classifies incoming source names by suffix family
- known special cases include `.skin`, `.mdl`, `.stex`, and extensionless
  fallback handling
- it loads/caches direct forms and, for `.skin` / `.mdl` families, derives
  `%s.tex.%s` side-loads into the local store

So `FUN_00140AB8(...)` is a side-load/cache helper around the incoming source
name. It does **not** by itself explain what those seven source-name hashes mean.

## Best next target after Phase 290

The remaining high-value branch is now data/name recovery, not more control-flow
decomp:

- recover the real names behind the seven source-name hashes from Hollywood
  script/config data or a stronger THAW PS2 hash corpus

The live branch control flow is now explicit enough that more decomp on the same
native functions is likely lower-yield than recovering those source-name names.

## Phase 291: Current Repo Tooling Does Not Yet Bypass The Source-Name Gap

The local repo tooling was checked directly against the current blocker and does
not yet provide a shortcut.

- the existing `QbKeyNames*.txt` corpus still does not resolve any of the seven
  source-name hashes from `phase290_source_name_handoff.md`
- the current generic QB decompiler path is still not usable for the relevant
  THAW PS2 script bodies:
  - `qb level_strings.qb.ps2` produced `0 scripts, 0 globals, 0/0 names resolved`
  - `qb levels.qb.ps2` produced `0 scripts, 0 globals, 0/0 names resolved`
  - the generated outputs were effectively empty / meaningless
- the current repo-side texture/model metadata helpers only operate on
  already-materialized state:
  - `ThawZoneTexHeaderSourceResolver.cs` resolves by `Tex0` and `(Tbp, Cbp)`
  - `ThawZoneTexMdlSupport.cs` extracts GS texture state from parsed `.mdl`
  - `Ps2TextureLoader.cs` builds caches and `(Tbp, Cbp) -> checksum` maps

That means those helpers are downstream of the unresolved source-selection step
in:

```text
FUN_0013FF18
-> FUN_0016F3C0
-> FUN_0016A2D0
-> FUN_00164CE0
-> FUN_0019C620
```

So there is still no strong local bypass for the seven source-name hashes.
The best remaining targets stay the same:

1. recover the real names behind those hashes from THAW PS2 container data
2. or identify the THAW PS2 `*.qb.ps2` container/record format well enough to
   expose the selector/config records that drive the live branch

## Phase 292: The Seven Hashes Are `with*` Field Keys, Not Direct Asset Names

The strongest local correction after Phase 291 is that the seven unresolved
“source-name hashes” now resolve cleanly as a field-key family in the shipped
debug checksum maps:

- `0x676F1DF1 = with`
- `0x9B03ADAD = with1`
- `0x020AFC17 = with2`
- `0x750DCC81 = with3`
- `0xEB695922 = with4`
- `0x9C6E69B4 = with5`
- `0x0567380E = with6`

This evidence came from focused `rg -a` scans over the shipped:

- `.../Extracted/DATAP/pak/dbg.pak.ps2`

which also showed nearby sibling names like:

- `only_with`
- `onlywith`

That makes the new interpretation much stronger than a one-name coincidence.

This does **not** yet recover the actual values stored under those fields.
Plain-text scans over:

- `qb.pab.ps2`
- `scripts/game/level_strings.qb.ps2`
- `scripts/game/levels.qb.ps2`
- `scripts/cutscene_zone_map.qb.ps2`

did not expose a clean `with* -> value` pairing.

So the next best target is now narrower and more concrete:

1. recover the values stored under `with`, `with1`, `with2`, `with3`, `with4`,
   `with5`, and `with6`
2. or identify the record/access format behind `FUN_002AE858(...)` and
   `FUN_002AE260(...)` well enough to expose those values directly

## Phase 293: The `with*` Keys Sit Inside A Broader Appearance/Composition Family

Focused local checksum-map scans over the shipped `dbg.pak.ps2` corrected the
shape of the problem again.

The strongest sibling names near the `with*` keys are not zone-only texture
identifiers. They are appearance/customization-style fields such as:

- `only_with`
- `onlywith`
- `base_texture`
- `accessory2`
- `accessory3`
- `common_frontlogo_params`
- `custom_frontlogo_params`
- `y_scale`
- `use_default_hsv`
- `rotate`
- `all`
- `desc_id`

Taken together with the earlier callback-band classification, the best current
reading is:

- `FUN_0013FF18(...)` is likely consuming a generic appearance/body-part
  composition record family
- the repeated `with*` keys are companion/variant slots inside that family
- this is no longer well-modeled as a flat zone-only source-name list

This is still a structural inference from checksum-map adjacency plus the
existing decomp. It does **not** yet recover the actual values stored under
those slots.

## Phase 294: The Live Branch Is A `replace* / with* / in*` Family, And The Access API Is Typed

Another local checksum scan over the shipped `dbg.pak.ps2` resolved the other
two thirds of the repeated branch family:

- `0xF362DBBA = replace`
- `0x7A993873 = replace1`
- `0xE39069C9 = replace2`
- `0x9497595F = replace3`
- `0x0AF3CCFC = replace4`
- `0x7DF4FC6A = replace5`
- `0xE4FDADD0 = replace6`

- `0xA01371B1 = in`
- `0xED189051 = in1`
- `0x7411C1EB = in2`
- `0x0316F17D = in3`
- `0x9D7264DE = in4`
- `0xEA755448 = in5`
- `0x737C05F2 = in6`

So the live `FUN_0013FF18(...)` branch is now best modeled as a repeated:

- `replace*`
- `with*`
- `in*`

record family, not “seven unresolved source-name hashes.”

The accessor layer is also clearer now from existing decomp:

- `FUN_002ADAE8(...)` = type `3/4` scalar getter
- `FUN_002ADC50(...)` = type `1` scalar getter
- `FUN_002ADF68(...)` = type `10` getter
- `FUN_002AE030(...)` = type `12` list/array wrapper getter
- `FUN_002AE260(...)` = recursive/general keyed accessor across direct cells,
  nested `0x1A00` records, and `0x2E00` / `0x2C00` indirection

Strong call-site inference now says:

- type `3/4` values are string-like
  - `FUN_002ADAE8(..., key=name, ...)` feeds directly into path formatting in
    the worldzones path builder
- type `12` values are array/list wrappers
  - `FUN_002AE030(...)` returns a wrapper walked through `FUN_002AF668(...)`

That makes the best current reading of the live branch:

- `replace*` = destination/target-name side
- `with*` = source-name/value side
- `in*` = optional scope/group/owner override carried into `FUN_0016F3C0(...)`

This matches the already-proven flow:

```text
FUN_0013FF18
-> FUN_00140AB8(param_1, with*)
-> FUN_0016F3C0(DAT_00498800, in*, replace*, with*)
-> FUN_0016A2D0(...)
-> FUN_00164CE0(...)
-> FUN_0019C620(...)
```

So the remaining gap is no longer field naming. It is concrete record/value
recovery for the `replace* / with* / in*` family that reaches the live
Hollywood path.

## Phase 295: The Shipped `replace / with` Records Are Appearance Replacement Data

The next raw-byte pass over shipped `qb.pab.ps2` materially changed the value of
this branch.

The `replace / with` side is no longer just a checksum-name inference from the
debug checksum maps. It is now visible directly in shipped compiled QB data as
two tight paired clusters:

- `replace @ 0x2445EC`
- `with    @ 0x244610`
- `replace @ 0x24475C`
- `with    @ 0x244780`

and each `replace/with` pair is separated by `0x24` bytes.

Those raw records concretely contain:

- `models/skater_male/Body_M_Head.skin`
- `replace = Beard_M_None.png`
- `with = textures/skater_male/Beard_M_Beard01.png`
- `models/ped_male/GenericSkater/Ped_GenericSkater_Head02.skin`

and then a second repeated block around:

- `models/peds/igc/IGC_CAS_M.skin`
- `replace = Beard_M_None.png`
- `with = textures/skater_male/Beard_M_Beard01.png`
- `models/peds/Ped_RandomSkater/Ped_RandomSkater_HEAD.skin`

Resolved surrounding hashes in those same records include:

- `0x1E90C5A9 = mesh`
- `0x4BB2084E = desc_id`
- `0x8758324F = Ped_GenericSkater_Head02`
- `0x7EBB7D1C = Ped_RandomSkater_HEAD`

So this is now concrete shipped evidence that the exposed `replace/with` data is
generic appearance/customization replacement content, not just an abstract
field-family guess.

Just as importantly, the shipped `in` hits do **not** cluster with those
records:

- `in @ 0x19996C`
- `in @ 0x1999A8`
- `in @ 0x1999E4`
- `in @ 0x19A512`

No `replace/in` or `with/in` neighborhoods were found near the `0x2446xx`
clusters, so the earlier decomp-side “repeated `replace* / with* / in*`
family” should no longer be treated as a single locally proven shipped record
block.

The practical decoder consequence is strong:

- `FUN_0013FF18(...) -> FUN_0016F3C0(...) -> FUN_0016A2D0(...) ->
  FUN_00164CE0(...) -> FUN_0019C620(...)`
  still reaches the generic image reconcile path
- but the only concrete shipped records exposed through that branch so far are
  head-skin / beard / CAS / random-skater appearance replacement data
- so this branch is now much more likely to be generic appearance
  infrastructure that happens to reuse the image reconcile logic, not the
  missing THAW worldzone texture bridge

The adjacent callback-side semantics reinforce that conclusion. In the same
family, `FUN_001402B8(...)` reads fields such as:

- `desc_id`
- `in`
- `with`
- `temp_texture`
- `x_scale`
- `y_scale`
- `is_deck`
- optional `base_texture`
- optional `logo_texture`

and then routes through `FUN_0016F558(...)`, another manager-driven image
reconcile path using the same general object family as `FUN_0016F3C0(...)`.
That is a graphic/appearance composition shape, not a believable zone-world
owner/source path.

That makes this branch low-value for further zone-TEX work unless a later pass
ties it back to `worldzones` or zone-owner data. The next best target should
move back to the actual zone owner/blob path around `FUN_001E9AC0(...)`, or the
zone pack callback / zone-QB bridge around `FUN_002E7710(...)` and
`FUN_002E7130(...)`.

## Phase 295: The Live Branch Now Looks Like Appearance Graphic Replacement, Not A Flat Asset-Name List

The next caller-level pass materially tightened the meaning of the
`replace* / with* / in*` family.

The strongest anchor is `FUN_001402B8(...)`, which reads:

- `desc_id`
- `in`
- `with`
- `temp_texture`
- `x_scale`
- `y_scale`
- `is_deck`
- optional `base_texture`
- `logo_texture`

and then builds temporary image objects, conditionally combines base/logo
textures, and finishes with:

```text
FUN_0016F558(DAT_00498800, in, logo_texture, with)
```

This is direct caller-level evidence that the live family is doing
appearance/logo/material-style texture composition or replacement, not just
moving around generic source names.

The relevant local checksum resolutions from the shipped debug pack are:

- `0x4BB2084E = desc_id`
- `0xC553C612 = temp_texture`
- `0x6AA8D676 = x_scale`
- `0xCCDFDDC2 = y_scale`
- `0xC876ADF3 = rotate`
- `0x25A0E7E9 = is_deck`
- `0x499F4BE9 = base_texture`
- `0x49B318FD = logo_texture`

with nearby CAS/body-part context keys including:

- `skater_m_head`
- `skater_f_head`
- `female_cas_part`
- `male_cas_part`
- `pass`
- `src`

`FUN_0016F558(...)` then sharpens the role split:

- it normalizes `logo_texture` as the target texture name
- it uses `in` as the same group/filter slot used by `FUN_0016F3C0(...)`
- it resolves `with` through `FUN_0016A4A8(...)`, i.e. as a live source
  image/object key in the owner hash table
- then it reconciles through `FUN_0016A378(...) -> FUN_00164CE0(...)`

So the best current reading is now:

- `replace*` = target texture/material name
- `with*` = source image/object key
- `in*` = scope/group/filter

and the best next target is no longer the accessor layer alone. It is the
appearance/image-manager side around `FUN_0016F558(...)`, `FUN_00170B30(...)`,
and `FUN_0016E758(...)`, or concrete value recovery from compiled CAS/editable
payloads carrying this family.

## Phase 296: Recheck Of The Producer Boundary Above `FUN_001216F0(...)`

The next parallel pass closed the two most tempting fallback theories again and
left the same narrow target standing.

First, cache aliasing is still effectively dead. The visible cache chain is
still only:

- `FUN_00140AB8(...)` loads bytes with `FUN_001216F0(...)`
- `FUN_0025E110(...)` copies those bytes verbatim into cache storage
- `FUN_0025E288(...)` finds the cache record
- `FUN_0025D150(...)` only rebases to the payload start inside that record

So this pass still did **not** expose a second producer that would feed
transformed or non-public owner bytes into cache.

Second, the worldzone pack callback branch still does not visibly fold back
into the scene-owner loader. `FUN_002E7710(...)` starts the async pack load,
`FUN_002E7130(...)` handles success, conditionally runs `FUN_002995B8(...)`,
attaches the normalized `%s.qb.%s` hash at `ctx + 0xD4`, and then hands control
to the generic QB/config executor. But the current native coverage still does
**not** show a concrete bridge from that path into:

- `FUN_00290038(...)`
- `FUN_00290208(...)`
- `FUN_00157318(...)`
- `FUN_0016AD60(...)`

So the only plausible convergence there is still the unresolved QB/container
merge point after the callback, not anything visible in the native call graph.

That leaves the direct contradiction unchanged. The scene-owner side is still:

```text
LoadScene / AddScene
-> levels\...\*.tex(.PS2)
-> FUN_0016AD60(...)
-> FUN_001E9FA8(...)
-> FUN_001216F0(...)
-> FUN_001E9AC0(...)
```

and the current coverage still shows no native `levels -> worldzones` remap in:

- `FUN_0016AD60(...)`
- `FUN_001216F0(...)`
- `FUN_00125160(...)`
- `FUN_00125298(...)`

So the strongest next target is still the higher-level producer/selection path
**above** `FUN_001216F0(...)`, not cache aliasing, not more
`FUN_001E9AC0(...)` header reinterpretation, and not the already-split visible
zone pack callback path.

## Phase 297: `FUN_002B6218(...)` Looks Like A Direct Two-Pass Container Parser

The next local pass made `FUN_002B6218(...)` much more concrete.

It now reads as a direct parser over the incoming packaged-QB/container bytes,
not as a hidden transformation stage.

### Pass 1: name/publication

The first loop only does three meaningful things:

- stops on `0x00`
- steps over generic records with `FUN_002B30A8(...)`
- handles `0x2B` records by publishing a hash/string pair through
  `FUN_002A8958(...)`

After that first loop, it also publishes the outer path itself through
`FUN_002A8958(hash(path), path)`.

So the first pass is effectively a hash-to-string registration pass for the
container and its local symbols.

### Pass 2: record materialization

The second pass interprets concrete record types:

- `0x23`
  - selector-script record
  - `selector_hash = *(u32 *)(record + 2)`
  - `script_start = record + 6`
  - `script_end = FUN_002B51D0(script_start)` up to `'$'`
  - `content_hash = FUN_002B5248(script_start)`
  - materializes or replaces through `FUN_002B43F8(...)`

- `0x16`
  - packaged object/material record
  - resolves nested payload spans with `FUN_002B4B68(...)`
  - rebuilds through `FUN_002B4648(...)`

- `0x45`
  - parser/context control marker
  - switches or skips active heap/context state for specific hashes

The helper band is still small and local:

- `FUN_002B4B68(...)` only skips inline control wrappers
- `FUN_002B51D0(...)` only walks to `'$'`
- `FUN_002B5248(...)` only hashes bytes up to `'$'`
- `FUN_002B30A8(...)` is the generic record stepper

So this branch no longer looks like a plausible place where public extracted
bytes are secretly rewritten into a different runtime byte view.

### Consequence

If this packaged-QB/container branch still matters to the overall mismatch, the
remaining room for divergence is upstream:

- which buffer reaches `FUN_002B6218(...)`
- whether it came from `FUN_00120B20(...)`
- whether it came from the already-buffered path through
  `FUN_00304300(...) -> FUN_002B6D28(...)`
- or whether the alternate branch `FUN_002B6678(...) -> FUN_002BFD20(...)` is
  the real branch for the relevant asset family

So `FUN_002B6218(...)` itself is now a weaker candidate for the hidden
transformation than the upstream buffer-provenance paths above it.

## Phase 299: The Buffered Branch Now Collapses To `param_2` Provenance

The next focused read tightened the already-buffered branch enough that the
interesting part is no longer the parser.

The stable chain is still:

```text
FUN_0025C108(obj, buffer, ...)
-> FUN_00304300(*(u32 *)(obj + 0x0C), buffer, 1, 1)
-> FUN_002B6D28(path_like_source, buffer, 1, 1)
-> FUN_002B6218(path_like_source, buffer, 1, 1)
-> 0x23 records
-> FUN_002B43F8(...)
```

But the local helper band plus the wrapper notes now give a clean split:

- `obj + 0x0C`
  - behaves as a path-like packaged-source handle or direct path pointer
  - basename/exensionless hash comes from `FUN_00253B30(...)` /
    `FUN_00253C98(...)`
- `buffer`
  - is the already-materialized payload forwarded unchanged into
    `FUN_00304300(...)`
  - then into `FUN_002B6D28(...)`
  - then into `FUN_002B6218(...)`

So the remaining room for divergence in this branch is no longer parser
behavior. It is only:

- who filled `buffer`
- and whether that producer created a different in-memory view than the public
  extracted bytes

That makes the wrapper family around `FUN_0025C108(...)` the best next target,
especially:

- the virtual `+100` slot on `*(obj + 0x18)`
- and the concrete callers that first pass `buffer` into `FUN_0025C108(...)`

## Phase 300: `FUN_0025C108(...)` And `FUN_002594B8(...)` Look Like Sibling Methods

The next local comparison finally produced the first concrete same-family clue
around `FUN_0025C108(...)`.

`FUN_0025C108(...)` and `FUN_002594B8(...)` now look like sibling methods on a
broader packaged-source job family. Both:

- operate on an object with at least:
  - `obj + 0x0C` = path-like source handle
  - `obj + 0x18` = child/family callback object
  - `obj + 0x1C` = normalized/hash field
- run a parser/update step using a caller-supplied `buffer`
- invoke the same virtual slot on `obj + 0x18`
- then call the same shared helper `FUN_00258130(...)`

The shared shape is:

```text
(**(code **)(*(int *)(obj + 0x18) + 100))
    (obj + *(short *)(*(int *)(obj + 0x18) + 0x60), buffer);
FUN_00258130(obj, buffer, ...);
```

`FUN_002594B8(...)` is the cleanest sibling clue. It:

- patches a platform suffix into `*(obj + 0x0C)`
- runs `FUN_002B6678(*(obj + 0x0C), buffer, 1)`
- computes/stores a normalized hash in `obj + 0x1C`
- invokes the same `obj + 0x18` virtual `+100` slot
- then calls `FUN_00258130(...)`

`FUN_00258130(...)` also sharpens the contract. It is not a trivial wrapper:

- if `obj + 0x0C` is present, it releases/resets the child/aux handle there
- otherwise it falls back to `obj + 0x18`
- then it invokes virtual slot `+0xC4` on the chosen handle

So the broader reading is now:

- per-method parse/update happens first
- `obj + 0x18` receives an immediate `+100` callback with the parsed `buffer`
- `FUN_00258130(...)` then performs shared release/finalization through
  virtual `+0xC4`

That makes the next best target narrower again: not parser internals, but the
caller band around `FUN_00258130(...)`, because that is now the shortest route
to recovering the wrapper-family identity and the first concrete supplier of
`buffer`.

## Phase 300: `FUN_002594B8(...)` Shares The Same Wrapper/Callback Shape

The next local comparison produced a useful structural match.

`FUN_002594B8(...)` has the same high-level wrapper/callback shape as
`FUN_0025C108(...)`:

- both use `obj + 0x0C` as a path-like source handle
- both parse the incoming payload buffer
- both refresh path-derived IDs afterward
- both call the virtual `+100` slot on `*(obj + 0x18)`
- both finish through `FUN_00258130(...)`

In concrete terms:

- `FUN_0025C108(...)`
  - buffered branch
  - `FUN_00304300(*(u32 *)(obj + 0x0C), buffer, 1, 1)`

- `FUN_002594B8(...)`
  - alternate object-walker branch
  - `FUN_002B6678(*(u32 *)(obj + 0x0C), buffer, 1)`

Then both do:

- update `obj + 0x1C`
- dispatch virtual `+100` on `*(obj + 0x18)`
- call `FUN_00258130(...)`

That is the strongest local hint so far that `obj + 0x18` belongs to a shared
wrapper family, not a one-off helper case. So the best next target is now even
more clearly the wrapper/vtable family around:

- `FUN_0025C108(...)`
- `FUN_002594B8(...)`
- `FUN_00258130(...)`
- and the vtable implementing the shared `+100` slot

## Phase 298: `FUN_002B6678(...) -> FUN_002BFD20(...)` Is A Structured-Object Walk, Not Another Raw Parser

The next local pass materially weakened the alternate branch as a candidate for
the original runtime/public byte mismatch.

`FUN_002B6678(...)` now looks like:

```text
FUN_002B6678(path, object_block, trace_flag)
-> FUN_002A8958(hash(path), path)
-> FUN_002BFD20(object_block, object_block, FUN_002A7440, hash(path), trace)
-> *(u32 *)object_block = 1
```

So this branch is not reading raw packaged-QB/container bytes directly the way
`FUN_002B6218(...)` does. It is walking an already-structured object block and
rebinding live registry objects through the callback `FUN_002A7440(...)`.

The strongest proof is `FUN_002BFC20(...)`, which advances through entries by a
typed in-memory layout at `entry + 2`:

- `3/4`
- `5`
- `6`
- `7`
- `10`
- `12`

with the step size derived from embedded offsets and helper walkers such as
`FUN_002BFB70(...)` and `FUN_002BF458(...)`.

That is a structured object/container family, not a raw file/record grammar.

`FUN_002A7440(...)` reinforces the same reading. It stores the incoming path
hash at `entry + 8`, resolves the current live object for the entry hash, and
invalidates selector-stream cache state if a type-`7` payload changed. So the
visible job here is live-object rebinding and cache invalidation, not hidden
byte rewriting.

That means `FUN_002B6678(...) -> FUN_002BFD20(...)` is now a weaker candidate
than `FUN_002B6218(...)` for the original runtime/public divergence. If this
branch still matters, the better target is the provenance of the structured
block it receives, not the walker itself.

So the narrowest visible branch point on this side is now `FUN_002B6D28(...)`,
because it is the place that chooses between:

- direct raw packaged-container parsing through `FUN_002B6218(...)`
- structured object-block walking through `FUN_002B6678(...)`

## Phase 301: The `FUN_00258130(...)` Sibling Band Exposes Two Distinct Collaborators

The new sibling-band decomp changes the target again in a useful way: the
shared wrapper family is no longer just a vague post-parse band.

The methods now split into two collaborator patterns.

### Wrapper-local child at `obj + 0x18`

These methods all use the wrapper's own child object:

- `FUN_002594B8(...)`
- `FUN_00259960(...)`
- `FUN_0025A478(...)`
- `FUN_0025B550(...)`
- `FUN_0025B978(...)`
- `FUN_0025C108(...)`
- `FUN_0025C7C0(...)`
- `FUN_0025CC38(...)`

Across that family, `obj + 0x18` exposes a stable vtable surface:

- slot `+0x1C` = getter used by `FUN_0025B978(...)`
- slot `+0x24` = getter used by `FUN_0025B550(...)`, `FUN_00259960(...)`,
  `FUN_0025A478(...)`, `FUN_0025B978(...)`, `FUN_0025C7C0(...)`,
  `FUN_0025CC38(...)`
- slot `+100` = post-result accept hook used by every visible sibling
- slot `+0xC4` = shared finalize/reset hook invoked by `FUN_00258130(...)`
- `+0x18/+0x20/+0x60/+0xC0` = self offsets used to call those slots

This is now the strongest local evidence that `obj + 0x18` is a real
vtable-bearing child object in the wrapper family, not just a borrowed callback
holder.

### Separate global/provider object from `FUN_00258490(...)`

Other siblings first resolve a different object through `FUN_00258490(...)`:

- `FUN_00258CA0(...)`
- `FUN_002590F8(...)`
- `FUN_00259C38(...)`
- `FUN_0025A110(...)`

That returned object then uses its own child/vtable path at
`(*(provider + 0x18)) + 0x14`.

So the wrapper band is not one monolithic callback family. It contains two
distinct collaborators:

- a wrapper-local child at `obj + 0x18`
- a separate provider/global side reached through `FUN_00258490(...)`

### Consequence

The next best target is now narrower than "wrapper family" in general:

- the real init bodies behind:
  - `FUN_002597A8(...)`
  - `FUN_00259F60(...)`
  - `FUN_0025A7D0(...)`
  - `FUN_0025B068(...)`
  - `FUN_0025CB00(...)`
  - `FUN_0025CF60(...)`
- and `FUN_00258490(...)`

That should expose which vtables are actually assigned to the wrapper-local
child objects, and whether the visible sibling band is one class hierarchy or a
small cluster of related wrapper/child pairs.

## Phase 310: The `FUN_00258130(...)` Sibling Band Is A Real Wrapper-Class Hierarchy

The newer ctor/vtable pass resolves the wrapper side much more concretely.

`FUN_00255578(manager, type)` is the live wrapper constructor switch. It
allocates one of a small set of object sizes and dispatches into per-type ctors
that all share the same base init:

- `FUN_00257D90(...)`
  - clears `self + 0x00/+0x04/+0x08/+0x0C/+0x10`
  - masks flags in `self + 0x14`
  - installs base vtable `DAT_004CA830` at `self + 0x18`

Derived ctors then overwrite `self + 0x18` with concrete wrapper-class
vtables.

Confirmed map so far:

- type `0x0E` -> `FUN_002593B8(...)` -> `DAT_004CAE00`
- type `0x0C/0x0D` -> `FUN_0025BE88(...)` -> `DAT_004CBCA8`
- type `0x10` -> `FUN_0025C6C0(...)` -> `DAT_004CBE18`
- type `0x12` -> `FUN_0025CB38(...)` -> `DAT_004CBF90`

The strongest concrete owner path is now:

1. `FUN_0025D560(...)`
2. `FUN_0025D488(record, hash)`
3. `FUN_00255BD8(manager, key, type, ..., payload_ptr, ..., ctx, ...)`
4. `FUN_00255578(manager, type)`

`FUN_00255BD8(...)` then:

- creates the live wrapper object via `FUN_00255578(...)`
- stores the key at `self + 0x00` via `FUN_00258498(...)`
- optionally stores a provider/global object at `self + 0x10` via
  `FUN_00258488(...)`
- calls wrapper vtable slot `+0x6C`
- calls wrapper vtable slot `+0x3C` to populate from the payload
- inserts the live object into the manager hash if successful
- returns wrapper vtable slot `+0x14`

So the `FUN_00258130(...)` sibling methods are no longer best described as a
loose callback band. They are live wrapper-class vtable slots on objects built
by `FUN_00255578(...)`.

## Phase 311: `DAT_004AD150` Was A Structural False Match

The earlier `phase302_obj18_wrapper_family.md` hypothesis should now be treated
as superseded.

`DAT_004AD150` belongs to the separate scoped callback/config family built by:

- `FUN_0013DBC8(...)`
- `FUN_0013DD98(...)`
- `FUN_0013DC80(...)`
- `FUN_0013E478(...)`
- `FUN_0013E4D0(...)`

That family uses `obj + 0x08 = &DAT_004AD150` on a separate `0x7C` object.

By contrast, the live wrapper family uses:

- `FUN_00255578(...)`
- `FUN_00257D90(...)`
- derived ctors such as `FUN_002593B8(...)`, `FUN_0025BE88(...)`,
  `FUN_0025C6C0(...)`
- and stores its vtable at `self + 0x18`

The overlap between `DAT_004AD150` and the wrapper vtables is only structural:
both families have non-null entries at several similar offsets. The concrete
targets differ across the live slots:

- wrapper `+0x14/+0x1C/+0x24/+0x54/+0x64/+0x6C/+0x74/+0x7C/+0x84/+0x8C/+0x94/+0xC4/+0xD4`
  do not match the `DAT_004AD150` functions at those same offsets

So the strongest current reading is:

- `phase302_obj18_wrapper_family.md` was a good structural lead, but not the
  right identity
- the live branch is the wrapper-class hierarchy from `FUN_00255578(...)`
- the callback/config family is adjacent infrastructure, not the same object

## Phase 310: Wrapper ctor switch and live class map

The sibling-band question is materially tighter now: these methods are not just
nearby helper code, they are live wrapper-class vtable slots.

`FUN_00255578(manager, type)` is the concrete constructor switch. It allocates
one of several small object sizes, runs base init through `FUN_00257D90(...)`,
then installs a type-specific vtable at `self + 0x18`. Base init clears the
live object fields at `+0x00/+0x04/+0x08/+0x0C/+0x10`, masks flags in
`self + 0x14`, and installs base vtable `DAT_004CA830`.

The closest relevant derived ctor/vtable/method matches are now confirmed:

- type `0x0E` -> `FUN_002593B8(...)` -> `DAT_004CAE00` ->
  slot `+0x7C = FUN_002594B8(...)`
- type `0x0C/0x0D` -> `FUN_0025BE88(...)` -> `DAT_004CBCA8` ->
  slot `+0x7C = FUN_0025C108(...)`
- type `0x10` -> `FUN_0025C6C0(...)` -> `DAT_004CBE18` ->
  slot `+0x7C = FUN_0025C7C0(...)`
- type `0x12` -> `FUN_0025CB38(...)` -> `DAT_004CBF90`

The best concrete caller chain is now:

1. `FUN_0025D560(...)`
2. `FUN_0025D488(record, hash)`
3. `FUN_00255BD8(manager, key, type, ..., payload_ptr, ..., ctx, ...)`
4. `FUN_00255578(manager, type)`

`FUN_00255BD8(...)` then:

- stores the wrapper key at `self + 0x00` via `FUN_00258498(...)`
- optionally stores a provider/global object at `self + 0x10` via
  `FUN_00258488(...)`
- calls wrapper vtable slot `+0x6C`
- calls wrapper vtable slot `+0x3C` to populate from the payload
- inserts the live object into the manager hash on success
- returns wrapper vtable slot `+0x14`

So the best concrete constructor is `FUN_00255578(...)`, and the best concrete
caller path is `FUN_0025D560(...) -> FUN_0025D488(...) -> FUN_00255BD8(...)`.
## Phase 314: `FUN_0025D560(...)` Uses An Explicit Record-Hash -> Wrapper-Subtype Map

The record-type side is now concrete.

`FUN_0025D260(record_hash)` is the subtype switch used by
`FUN_0025D488(...) -> FUN_00255BD8(...) -> FUN_00255578(manager, type)`.

Recovered mapping:

- type `0x02`
  - `0x689028A5`
  - `0xDAD5E950`
- type `0x03`
  - `0x7EA7357B`
- type `0x04`
  - `0x2B0A3095`
  - `0x8BFA5E8E`
- type `0x05`
  - `0x72A6D78C`
- type `0x06`
  - `0x745DCD45`
  - `0x9DE9087F`
- type `0x07`
  - `0x7330095C`
- type `0x08`
  - `0x64112E85`
  - `0x9BCC234D`
- type `0x0A`
  - `0x2F1A6A09`
- type `0x0C`
  - `0x4BC1E85E`
- type `0x0D`
  - `0x49875607`
- type `0x0E`
  - `0x5D796624`
  - `0xA7F505C4`
- type `0x0F`
  - `0x91E1028D`
- type `0x10`
  - `0xFF2D0E91`
- type `0x11`
  - `0x199F902B`
- type `0x12`
  - `0x7E1ABC70`

So the per-record hash stream in `FUN_0025D560(...)` is no longer opaque: it
directly selects the live wrapper subclass through `FUN_0025D260(...)`.

`FUN_0025D488(record, record_hash)` is now best read as a thin dispatcher:

1. `type = FUN_0025D260(record_hash)`
2. reject duplicates through `FUN_00256AC8(...)`
3. compute payload pointer through `FUN_0025D150(record)`
4. forward the recognized record to
   `FUN_00255BD8(manager, key, type, ..., payload_ptr, ...)`

`FUN_00255BD8(...)` then allocates the live wrapper via `FUN_00255578(...)`,
populates it, inserts it into the manager hash, and returns wrapper slot
`+0x14`.

## Phase 314: First Concrete Higher-Level Driver Is The Zone/Worldzone Async Pack Callback Path

The higher-level driver is no longer speculative either.

The first concrete non-generic path into this wrapper-record system is:

1. `FUN_002E75C8(...)`
2. `FUN_0025E7C8(...)`
3. `FUN_0025E7F8(...)`
4. `FUN_0025E550(...)`
5. `FUN_0025DE18(...)`
6. `FUN_0025D560(...)`

Why this matters:

- `FUN_002E75C8(...)` lives in the already-established zone load callback band
- `FUN_0025E7F8(...)` allocates an async file handle and queue entry through
  `FUN_0025D000(...)`
- completion routes through `FUN_0025E550(...)`
- `FUN_0025DE18(...)` is the final wrapper that invokes `FUN_0025D560(...)`

So the record stream that seeds these live wrapper objects is now best
classified as the worldzone/zone packaged-content callback job family, not a
disconnected generic content parser.

The visible queue/table layers above `FUN_0025D560(...)` are:

- `FUN_0025D000(...)` records in the global queue rooted at `DAT_0055A468`
- the loaded-entry table rooted at `DAT_0055AEE8`, consumed by
  `FUN_0025E460(...)`

That is currently the strongest concrete bridge from worldzone load callbacks
into the wrapper-class hierarchy.

## Phase 316: Concise Record-Type And Driver Summary

The record-kind side is now durable enough to summarize compactly.

`FUN_0025D260(record_hash)` is the record-kind to wrapper-subtype switch used
by `FUN_0025D488(...)` before `FUN_00255BD8(...) -> FUN_00255578(type)`. The
recognized hashes map to subtype IDs `0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
0x08, 0x0A, 0x0C, 0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12`, with `0x559566CC` and
all unmapped/default cases returning `0`.

The first concrete non-generic higher-level driver of those records is still
the zone/worldzone async callback path:

1. `FUN_002E75C8(...)`
2. `FUN_0025E7C8(...)`
3. `FUN_0025E7F8(...)`
4. `FUN_0025E550(...)`
5. `FUN_0025DE18(...)`
6. `FUN_0025D560(...)`

So the wrapper records being classified here are best understood as part of
the worldzone/zone packaged-content callback job family.

## Phase 317: Record-Hash Families By Nearby Wrapper Usage

The mapped record hashes now split into a few durable asset-family clusters.

- texture/image-adjacent:
  - type `0x04` -> `0x2B0A3095`, `0x8BFA5E8E`
    - `FUN_0025B550(...) -> FUN_0016AC20(...)`
  - type `0x02` -> `0x689028A5`, `0xDAD5E950`
    - `FUN_0025B978(...) -> FUN_00169CD0(DAT_005401E4, ...)`
- script/container:
  - type `0x0C/0x0D` -> `0x4BC1E85E`, `0x49875607`
    - `FUN_0025C108(...) -> FUN_00304300(...) -> FUN_002B6218(...)`
  - type `0x0E` -> `0x5D796624`, `0xA7F505C4`
    - `FUN_002594B8(...) -> FUN_002B6678(...)`
- model/scene:
  - type `0x12` -> `0x7E1ABC70`
    - `FUN_0025CC38(...) -> FUN_00155670(...)`
- other structured metadata:
  - type `0x10` -> `0xFF2D0E91`
    - `FUN_0025C7C0(...)` walks a tagged list and calls `FUN_0035A1E0(...)`

The remaining mapped kinds are still unresolved in this pass:
`0x7EA7357B`, `0x72A6D78C`, `0x745DCD45`, `0x9DE9087F`, `0x7330095C`,
`0x64112E85`, `0x9BCC234D`, `0x2F1A6A09`, `0x91E1028D`, `0x199F902B`.

So the `FUN_0025D560(...)` record stream is already a mixed packaged-content
family, not a single texture-only or script-only asset class.

## Phase 320: Subtype content labels for the path-backed wrapper families

The subtype set that looked most promising from the wrapper vtables is now
cleaner:

- `0x0C / 0x0D` = packaged-QB / packaged object-bundle wrapper
- `0x0E` = structured object/script rebinding wrapper
- `0x10` = binary table / registry-list wrapper
- `0x12` = strongest model-bearing / scene-object-bearing wrapper

The useful correction is negative: `0x0C / 0x0D / 0x0E / 0x10` do **not**
currently look like direct texture-bearing content. `0x12` is the only strong
model-bearing candidate in that group.

`0x0C / 0x0D`:

- `+0x7C = FUN_0025C108(...)`
- parses a path-backed payload through `FUN_00304300(...)`
- publishes basename-derived objects through `FUN_00253530(...)`
- tracks a live object/list handle at `self + 0x24`
- cleans them up in `FUN_0025C430(...)`

So this family is packaged script/object-bundle content, not raw texture/model
payload.

`0x0E`:

- `+0x7C = FUN_002594B8(...)`
- path/platform normalization through `FUN_00157F60()`
- structured-object walker/rebinder `FUN_002B6678(...)`
- cleanup via `FUN_002596F8(...)`

So this family is structured object/script rebinding, not direct texture/model
payload.

`0x10`:

- `+0x7C = FUN_0025C7C0(...)`
- walks compact `(name, hash)` records from the incoming buffer
- publishes them through `FUN_0035A1E0(...)`

So this is binary registry/list content.

`0x12`:

- `+0x7C = FUN_0025CC38(...)`
- downstream `FUN_00155670(...)`
- creates/configures live objects through `FUN_001A1950(...)` and
  `FUN_00155748(...)`
- cleanup via `FUN_0025CEB8(...) -> FUN_00155B78(...)`

So `0x12` is the only strong model-bearing / scene-object-bearing subtype in
this band.

## Phase 321: The worldzone sidecar loader is generic until `FUN_0025D560(...)`

The auxiliary sidecar load chain:

1. `FUN_002E75C8(...)`
2. `FUN_0025E7C8(...)`
3. `FUN_0025E7F8(...)`
4. `FUN_0025E550(...)`
5. `FUN_0025DE18(...)`
6. `FUN_0025D560(...)`

is now best read as a generic worldzone packaged-content sidecar loader, not a
content-specific bridge.

- `FUN_002E75C8(...)`
  - resolves the physical sidecar path
  - allocates the destination buffer from `zone_entry + 0x18`
- `FUN_0025E7F8(...)`
  - opens the path and builds the async job
- `FUN_0025E550(...)`
  - is only the completion worker
- `FUN_0025DE18(...)`
  - hands the loaded sidecar payload into `FUN_0025D560(...)`

So the first meaningful content split is record dispatch inside
`FUN_0025D560(...)`, not anything earlier in the async path.

The strongest current sidecar candidates are:

- direct model-bearing:
  - type `0x12` / hash `0x7E1ABC70`
- strong script/object sidecar families:
  - type `0x0C / 0x0D`
  - type `0x0E`
  - type `0x10`

There is still no obvious direct texture-bearing wrapper family in that
`0x0C / 0x0D / 0x0E / 0x10 / 0x12` subset.

## Phase 322: Common wrapper paths around `FUN_00257E30(...)` and `FUN_00257FA8(...)`

The subtype-specific `+0x3C` populate methods are now clearly just a thin
prefilter into one common helper:

- optional payload rewrite through wrapper slot `+0x8C`
- then `FUN_00257FA8(...)`

`FUN_00257FA8(...)` is the common in-memory populate frame:

- call wrapper slot `+0xAC`
- call wrapper slot `+0x9C(param_5, param_6)`
- write `param_3 & 0x00FFFFFF` into the low 24 bits of `self + 0x14`
- call wrapper slot `+0x7C(param_2, param_3 & 0x00FFFFFF)`

Best reading:

- `+0xAC` = common operation reset/prepare
- `+0x9C` = subtype-specific aux-state setter
- low 24 bits of `self + 0x14` = remembered payload length
- `+0x7C` = actual subtype payload consumer

`FUN_00257E30(...)` is the common path-backed load helper:

- call wrapper slot `+0xAC`
- open the staged source path with `FUN_00122068(...)`
- store the file handle at `self + 0x0C -> +0x8C`
- allocate/read into `self + 0x08`
- in sync mode write the byte count into the low 24 bits of `self + 0x14`
- then call wrapper slot `+0x74`

So the subtype split does **not** start at `+0x3C`. It starts at the shared
hooks behind:

- `+0x74`
- `+0x7C`
- `+0x9C`
- `+0xAC`

The shared common slots that are now durable enough to label are:

- `+0x84 = FUN_002581A8(...)`
  - close staged source handle at `self + 0x0C -> +0x8C`
  - then call child slot `+0xD4`
- `+0x8C = FUN_00258218(...)`
  - clone/copy the incoming payload into wrapper-owned buffer `self + 0x08`
- `+0x94 = FUN_002582F8(...)`
  - free `self + 0x08`

Practically, that sharpens the direct texture-bearing candidates in the
worldzone sidecar record stream:

- type `0x02` -> `FUN_0025B978(...) -> FUN_00169CD0(...)`
- type `0x04` -> `FUN_0025B550(...) -> FUN_0016AC20(...)`

If a direct sidecar bridge into texture decode exists in this wrapper system,
it is now much more likely to sit in those image-adjacent immediate-builder
families than in the packaged-QB or structured-object families.

## Phase 323: The `0x02 / 0x04` image split is child/source vs owner/blob

The image-adjacent branch is now materially tighter:

- `0x02` = child/source side
  - `FUN_0025B978(...) -> FUN_00169CD0(DAT_005401E4, ...)`
  - `DAT_005401E4` is initialized from the literal `sprite`
- `0x04` = owner/blob side
  - `FUN_0025B550(...) -> FUN_0016AC20(...)`
  - cached owner/blob objects rooted at `DAT_005401CC`

Neither branch is doing path lookup at the wrapper layer. Their `+0x9C`
methods only allocate/fill small state blocks in `self + 0x0C`.

So the real split is:

- `0x02` = manager-owned child/source image-object branch
- `0x04` = cached owner/blob image-object branch

That makes both branches texture-relevant, but it makes `0x02` look more like a
source/subordinate image layer used in later composition/reconcile flows.

## Phase 324: Wrapper child slots `+0x24` and `+0x1C` are tiny field accessors

The shared wrapper-local child getters are now much less ambiguous.

Recovered instruction-level behavior:

- `+0x24` / `0x002584A0`
  - `return *(u32 *)(child + 0x00)`
- `+0x14` / `0x00258478`
  - `return *(u32 *)(child + 0x04)`
- `+0x1C` / `0x00258670`
  - `return (*(u32 *)(child + 0x14) >> 31)`
- `+0x6C` / `0x00258650`
  - set/clear that same top bit in `child + 0x14`

So the previous idea that `+0x24` and `+0x1C` were returning two child objects
was too strong. The `0x02` branch consumes:

- one primary child-state value from `+0x24`
- one boolean/flag from the top bit of child field `+0x14`

while `0x04` only consumes the primary child-state value from `+0x24`.

## Phase 325: The `0x02` top-bit flag is mirrored into object bit `0x10000`

The extra `0x02` flag path is now explicit:

- wrapper slot `+0x1C` / `FUN_00258670(...)`
  - returns the top bit of child field `+0x14`
- `FUN_00169CD0(...)`
  - passes that boolean only into `FUN_00168028(new_obj, flag)`
- `FUN_00168028(...)`
  - sets/clears only bit `0x10000` in `new_obj + 0x04`

So the child top bit is **not** part of lookup or cache-key derivation. It is
just a child/source property bit mirrored onto the constructed `0x02`
child/source image object.

The strongest current reading is:

- child field `+0x14` = child/source status word
- top bit = mode/property flag
- object field `+0x04` bit `0x10000` = the propagated version of that flag

Because `DAT_005401E4` is tied to `sprite`, that bit is plausibly a
sprite/source-style marker, but the exact symbolic meaning of object bit
`0x10000` is still not proven from current coverage.

Practically, this narrows the remaining unknown again:

- the top-bit path is not a hidden payload locator
- the remaining real target is the child-state initializer behind fields
  `+0x00`, `+0x04`, and `+0x14`

## Phase 323: The image-adjacent split is owner/blob versus child/source

The `0x02 / 0x04` branch is now tighter than just "both are image-adjacent."

`0x04`:

- `FUN_0025B550(...) -> FUN_0016AC20(...)`
- builds cached owner/blob image objects in `DAT_005401CC`
- through `FUN_001A0918(...) -> FUN_001A01D8(...) -> FUN_001A0480(...)`

`0x02`:

- `FUN_0025B978(...) -> FUN_00169CD0(DAT_005401E4, ...)`
- `DAT_005401E4` is initialized from the literal `sprite`
- produces manager-owned child/source image objects

So the strongest current reading is:

- `0x04` = owner/cached image branch
- `0x02` = child/source image branch

Neither branch is doing path lookup at the wrapper layer. Their wrapper `+0x9C`
methods only allocate/fill small state blocks in `self + 0x0C`.

## Phase 324: `+0x24` and `+0x1C` are field accessors, not deeper child-object fetches

Direct instruction dumps of the unlabeled common wrapper slot targets sharpened
the image-adjacent branch further.

Recovered shared slot behavior:

- `+0x24` (`0x002584A0`) = `return *(u32 *)(a0 + 0x00)`
- `+0x14` (`0x00258478`) = `return *(u32 *)(a0 + 0x04)`
- `+0x1C` (`0x00258670`) = `return (*(u32 *)(a0 + 0x14) >> 31)`
- `+0x6C` (`0x00258650`) = write that sign-bit flag back into `*(u32 *)(a0 + 0x14)`

That means the wrapper-local child slots used by the `0x02 / 0x04` image
branches are not returning opaque child objects. They are exposing fields of an
embedded wrapper-local child state record.

Practical effect:

- `0x04` consumes child-state field `+0x00` as the scalar key/value it passes
  into `FUN_0016AC20(...)`
- `0x02` consumes that same child-state field `+0x00`, plus a boolean from the
  sign bit of child-state field `+0x14`, before calling `FUN_00169CD0(...)`

So the next best target is no longer another getter layer. It is the child-state
initializer path that populates those embedded fields in the wrapper family.

## Phase 323: `0x02` vs `0x04` is now child/source vs owner/blob

The image-adjacent branch is no longer just "two image-ish wrapper families."

`0x02`:

- `FUN_0025B978(...) -> FUN_00169CD0(DAT_005401E4, ...)`
- `DAT_005401E4` is initialized from the literal `sprite`
- uses owner slot `+0x1C`, not the owner-style `+0x24` constructor path
- indexes created objects by the object's own checksum via `FUN_00167F58(obj)`

Best reading:

- `0x02` = child/source image-object pool
- likely a sprite/source manager rather than the final cached owner/blob family

`0x04`:

- `FUN_0025B550(...) -> FUN_0016AC20(...)`
- cached owner/blob image construction in `DAT_005401CC`
- builds through `FUN_001A0918(...) -> FUN_001A01D8(...) -> FUN_001A0480(...)`

Best reading:

- `0x04` = owner/cached image-object branch

Useful negative result:

- neither `0x02` nor `0x04` performs path lookup at the wrapper layer
- their wrapper `+0x9C` methods only allocate/fill compact backing state blocks
  in `self + 0x0C`
- the separate provider path through `FUN_00258490(...)` is not used by these
  two branches

So the remaining gap is now the identity of the wrapper-local child objects
feeding those branches, not another path resolver above them.

## Phase 324: wrapper `+0x24` is the real child object, wrapper `+0x1C` is a flag bit

Instruction-level recovery from `phase324_child_getter_disasm.txt` tightened
the wrapper-local child surface:

- `0x002584A0` (wrapper slot `+0x24`)
  - `return *(a0 + 0x00)`
- `0x00258670` (wrapper slot `+0x1C`)
  - `return (*(a0 + 0x14) >> 31)`
- `0x00258650` (wrapper slot `+0x6C`)
  - set/clear that same top-bit flag in `*(a0 + 0x14)`

That changes the `0x02` reading in an important way:

- `FUN_0025B978(...)` does **not** consume two child objects
- it consumes:
  - one real child object from `+0x24`
  - one boolean/property bit from `+0x1C`

And that extra flag only reaches `FUN_00169CD0(...)` as the value later passed
to `FUN_00168028(new_obj, flag)`.

So the current best split is:

- `0x04` = owner/blob image wrapper around one child object
- `0x02` = child/source image wrapper around one child object plus one flag bit

That makes the next target even narrower:

- the object family stored in the wrapper-local child field behind `+0x24`
- and the meaning of the top-bit flag in child field `+0x14`

## Phase 323: The image-adjacent split is `sprite` child/source objects vs cached owner/blob objects

The narrowed image-adjacent branch now reads much more concretely.

Type `0x02`:

- wrapper `+0x7C = FUN_0025B978(...)`
- downstream `FUN_00169CD0(DAT_005401E4, payload, size, child24, state90, state94, child1c, 0)`
- uses both wrapper-local child slots:
  - `+0x24`
  - `+0x1C`
- `FUN_00169CD0(...)`
  - dispatches through manager slot `+0x1C`
  - tags the created object through `FUN_00168028(...)`
  - indexes it by the created object's own checksum via `FUN_00167F58(obj) = *obj`

The strongest new structural clue is that `DAT_005401E4` is initialized in
`FUN_0016B310(...)` through:

- `DAT_005401E4 = FUN_0016AB20(0x4AFB50)`

and the dumped string at `0x004AFB50` is:

- `"sprite"`

So the `0x02` path is not a generic owner cache. It is a dedicated `sprite`
manager branch producing child/source-like image objects.

Type `0x04`:

- wrapper `+0x7C = FUN_0025B550(...)`
- downstream `FUN_0016AC20(child24, payload, size, state90, state94, state98, state9c)`
- uses only wrapper-local child slot `+0x24`
- `FUN_0016AC20(...)`
  - derives external key `child24 + state94`
  - looks that key up in owner cache `DAT_005401CC`
  - if missing, allocates through `FUN_001A0918(...)`
  - which enters the normal owner/blob path:
    - `FUN_001A01D8(...)`
    - `FUN_001A0480(...)`

So `0x04` is the cached owner/blob image branch, not the child/source branch.

There is one more important negative result from the same pass:

- neither `0x02` nor `0x04` is visibly doing path-backed lookup at the wrapper layer
- both use already-materialized wrapper-local child objects
- the separate provider object from `FUN_00258490(...)` is **not** used by these
  two families

The strongest current reading is therefore:

- type `0x02` = `sprite` manager child/source-image objects
- type `0x04` = cached owner/blob image objects

That is a better split than the older generic “both are image-adjacent” label.

The next best target is now the identity of the wrapper-local child objects
returned by:

- slot `+0x24`
- slot `+0x1C`

for the `0x02` / `0x04` subtypes. That is the narrowest remaining gap between
the wrapper system and a concrete texture-bearing source object.

## Phase 323: Image-adjacent wrapper split (`0x02` vs `0x04`)

The two image-adjacent immediate-builder families are now materially tighter:

- type `0x02` -> `FUN_0025B978(...) -> FUN_00169CD0(...)`
- type `0x04` -> `FUN_0025B550(...) -> FUN_0016AC20(...)`

The important correction is that neither branch currently looks like a
path-backed source resolver at the wrapper layer.

Both subtypes first allocate a small backing state block at `self + 0x0C`,
clone optional inline state into it, and optionally apply a few keyed config
overrides. There is no visible path open, filename hash lookup, or package
selection in:

- `FUN_0025B5E8(...)` for type `0x04`
- `FUN_0025BA48(...)` for type `0x02`

So the direct split is not "raw path lookup" versus "decoded payload." It is
"owner-like image build" versus "child/source-like image build."

### Type `0x04`

`FUN_0025B550(...)`:

1. gets a wrapper-local child object through slot `+0x24`
2. reads config from `self + 0x0C`
3. calls:
   `FUN_0016AC20(child24, payload, size, state90, state94, state98, state9c)`

`FUN_0016AC20(...)` then:

- computes key `child24 + state94`
- checks cache through `FUN_0016AF98(...)`
- if missing, allocates through `FUN_001A0918(...)`
- `FUN_001A0918(...) -> FUN_001A01D8(...)` creates a `DAT_004B4170`
  owner-family object and enters the normal owner init/load path
- caches the result in the global owner table rooted at `DAT_005401CC`

So `0x04` is now best read as a cached owner-like image wrapper family built
around an already-materialized child/image-context handle.

### Type `0x02`

`FUN_0025B978(...)`:

1. gets a wrapper-local child object through slot `+0x24`
2. gets a second child value through slot `+0x1C`
3. reads config from `self + 0x0C`
4. calls:
   `FUN_00169CD0(DAT_005401E4, payload, size, child24, state90, state94, child1c, 0)`

`FUN_00169CD0(...)` then:

- dispatches through manager `DAT_005401E4 + 0x10` slot `+0x1C`
- constructs a new object from the payload, `child24`, and the state block
- tags the result through `FUN_00168028(obj, child1c)`
- hashes/indexes it in the manager table rooted at `DAT_005401E4 + 0x08`

So `0x02` is now best read as a manager-owned child/source-like image wrapper
family built around:

- a main child/image-context handle from slot `+0x24`
- plus an extra tag/association from slot `+0x1C`

### Provider-path result

The separate provider object accessed through `FUN_00258490(self)` is **not**
used by either `0x02` or `0x04`.

- `FUN_00258490(self)` is only `return self + 0x10`
- that provider path belongs to other sibling families like
  `FUN_00258CA0(...)`, `FUN_002590F8(...)`, `FUN_00259C38(...)`, and
  `FUN_0025A110(...)`

So for the image-adjacent families, the real identity-bearing inputs are the
wrapper-local child slots:

- `+0x24`
- `+0x1C`

not the separate provider object.

### Best current reading

This is now the strongest decoder-relevant split inside the sidecar wrapper
system:

- `0x04` = cached owner-like image wrapper family
- `0x02` = manager-owned child/source-like image wrapper family

That makes these two families more plausible direct bridges toward
texture-bearing worldzone content than any of the packaged-QB or
structured-object families.

The next narrow target is therefore the identity of the wrapper-local child
objects returned by:

- slot `+0x24`
- slot `+0x1C`

for the `0x02` and `0x04` subtypes.

## Phase 324: THAW PS2 sample generator boundary and sample-tree layout

The retained sample generator is under `tools/corpus/SampleGenerator`. Its
media, research-cache, and sample-output roots are supplied through CLI options
or environment variables; no machine-specific source root is embedded in the
tool.

For THAW PS2 the configured mode is `BuildMode.Iso`. The current pipeline:

1. discovers disc images below the configured media root
2. extracts and caches the raw disc tree below the configured research root
3. mirrors that tree into the configured sample root
4. recursively unpacks archives in-place

The current repo path:

- `Sample/Builds/Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)`

therefore has the expected raw-style layout:

- `Extracted`
- `IOP`
- `MOVIES`
- `MUSIC`
- `STREAMS`
- `TEASERS`
- `DATAP.HED`
- `DATAP.WAD`
- `DATAPD.HDP`
- `DATAPF.HDP`
- `SLUS_212.95`
- `SYSTEM.CNF`

and does not require a normalized top-level `PAK/` folder.

For the Hollywood zone pack, the repo sample extracted file:

- `Sample/Builds/.../Extracted/DATAP/worlds/worldzones/z_ho/z_ho.pak.ps2`

matches the external unpacked research build file at the same relative path
byte-for-byte, including SHA-256:

- `1D2C7CA87F18451BC32D26346D7D6C0F91F96048712A60C763C3FA90B739699E`

So the current THAW PS2 sample tree is a reproducible mirror of the unpacked
research build. The bytes remain trustworthy, and consumers should use the live
`Extracted\\DATAP\\worlds\\worldzones` layout rather than assuming a classified
top-level `PAK/` directory.

## Phase 325: Wrapper slot `+0x24` is wrapper base state, not a child object

Phase 324 still left one ambiguity: `FUN_002584A0(...)` only proves
`return *(u32 *)(a0 + 0x00)`, but it did not yet prove whether `a0` was a
nested child-state pointer or the wrapper itself.

That ambiguity is now closed. Across the live wrapper families relevant here,
the self-offset paired with slot `+0x24` is zero:

- `DAT_004CB838` (type `0x04`): `+0x020 = 0`, `+0x024 = FUN_002584A0`
- `DAT_004CB9A0` (type `0x02`): `+0x020 = 0`, `+0x024 = FUN_002584A0`
- `DAT_004CBCA8`, `DAT_004CAE00`, `DAT_004CBE18`, and `DAT_004CBF90` follow the
  same pattern

The neighboring shared accessor slots do too:

- `+0x014 = FUN_00258478` with self-offset `+0x010 = 0`
- `+0x01C = FUN_00258670` with self-offset `+0x018 = 0`
- `+0x06C = FUN_00258650` with self-offset `+0x068 = 0`

So these are generic base-wrapper field accessors, not child-object fetches.

That matches wrapper construction:

- `FUN_00255BD8(...)` allocates the subtype through `FUN_00255578(...)`
- `FUN_00258498(wrapper, key)` stores the wrapper key/checksum at `self + 0x00`
- `FUN_00258488(wrapper, provider)` stores the separate collaborator at
  `self + 0x10`

Therefore the value returned by slot `+0x24` is the wrapper key at `self + 0x00`,
not a concrete image/source object or another wrapper.

That sharpens the image-adjacent split again:

- `FUN_0025B550(...)` / type `0x04` passes wrapper key `self + 0x00` into
  `FUN_0016AC20(...)`
- `FUN_0025B978(...)` / type `0x02` passes wrapper key `self + 0x00` plus the
  top-bit flag from `self + 0x14` into `FUN_00169CD0(...)`

So the remaining unknown is now the semantics of wrapper base fields:

- `self + 0x00`
- `self + 0x04`
- `self + 0x14` top-bit flag

not the identity of another child-object family behind slot `+0x24`.

## Phase 327: `record[3]` is the shared per-record selector key

The dispatcher path now makes `record[3]` stronger than just "some checksum-like
field."

From `FUN_0025D560(...)` in `phase276_type7_producer_callers.c`, records are
only processed if:

- the caller-supplied filter is `0`
- or the caller-supplied filter equals `record[3]`

So `record[3]` is a first-class selector visible at the packaged-record
dispatcher entry point.

That matches the shared wrapper construction path:

- `FUN_0025D488(...)` rejects duplicates through `FUN_00256AC8(manager, record[3])`
- then calls `FUN_00255BD8(manager, record[3], type, record[6], payload_ptr, record[2], manager_ctx, 0)`
- `FUN_00255BD8(...)` writes that exact value into wrapper `self + 0x00`

Current best live field split:

- `record[0]` = record-kind hash for `FUN_0025D260(...)`
- `record[1]` = payload offset for `FUN_0025D150(...)`
- `record[2]` = payload-side subtype parameter
- `record[3]` = per-record selector/content key
- `record[4]` = temporary manager-context swap
- `record[6]` = optional provider/collaborator input
- `record[7]` = extra flags used by some kinds such as `0x7EA7357B`

So the strongest current label is:

- `record[3]` / `self + 0x00` = shared per-record selector key, checksum-like
  content id

That is still not enough to prove its exact naming domain, but it does rule out
weaker interpretations like "group id," "payload pointer," or "late child
object slot."

## Phase 332: Confirmed two overlapping data sections, decoder still incomplete

### What we verified

Extraction from PAK is byte-perfect: the 3,266,032 bytes at `0x9BF70` inside
`z_ho.pak.ps2` are byte-identical to the standalone `0009BF70.tex` file
(SHA256 confirmed). **The decoder is what's failing**, not the extraction.

PC ground truth was obtained by decoding the equivalent THAW PC `.tex.wpc`
(magic `0xABADD00D`) via the existing `xbxtex` command. This gave us 848 PNGs
of what the textures *should* look like:

- `0xA20AA4CB` → star icon with skull (64x64)
- `0x0D1361E6` → "Jeep" branded snowscape ad (128x128)
- `0x26055DCC` → wall texture (128x128)
- `0x8663DA77` → floor tile (128x128)

The current PS2 decoder produces: only `A20AA4CB` is recognizable, others are
either pure noise or partial structure.

### Two overlapping data sections in zone .tex

The file actually contains two distinct CLUT/pixel sections that overlap:

1. **PACKED_BASE = 0x0A onwards: DMA upload source bytes**
   - Bytes at `PACKED_BASE + record.data_offset` ARE referenced by DMA REF tags
     in the upload chain
   - Format: GS-swizzled (when uploaded as PSMCT32 64x32 to TBP, GS reads as PSMT4)
   - Verified by cross-referencing record bytes against DMA upload data offsets;
     that incorporated probe result is preserved here and in decoder tests.

2. **fileLength - maxEnd onwards: Prepared CPU-side source data**
   - Different bytes at `dataBaseOffset + record.data_offset` (legacy heuristic)
   - Format: appears to be partially-prepared, in some kind of pre-decoded layout
   - Source for record A20AA4CB which decodes to a recognizable star icon
   - For most records, the bytes look like 8-entry PSMCT32 grayscale palettes
     followed by linear PSMT4 indices, but the existing layout transforms only
     match a small number of layout modes

### What still doesn't work

The **CLUT format** at the OLD location appears to be neither plain PSMCT16 nor
plain PSMCT32. For record 389 (snowscape):
- `pal_bytes = 32` per the record
- The first 32 bytes look like 8 PSMCT32 grayscale entries (R=G=B + variable A)
- Decoding as 16 PSMCT16 entries produces garbage colors
- Decoding as 8 PSMCT32 entries produces a recognizable snow texture, but the
  pixel data uses all 16 nibble indices so 8 entries is insufficient

It's plausible that:
- The runtime stores CLUTs in an expanded form different from any standard PSM
- OR the CLUT bytes at this location are not the actual palette but some derived
  data (e.g., linear A8 alpha modulation table)
- OR the texture is decoded with **multi-stage palette lookups** that we haven't
  identified yet

The pixel **swizzle** also varies per record. The existing decoder applies
`TransformPsmt4SlotBlocksForLayout` for layout modes `0x02000001` and `0x02000005`
with hand-tuned bit permutations. These work for a small subset of records (like
A20AA4CB) but produce garbage for most others.

### Diagnostic tools added in `tools/`

- `zone_tex_diagnostic.py` — record layout + DMA upload cross-check (proves
  byte-level extraction is correct)
- `zone_tex_synthetic_test.py` — round-trip swizzle validation on known-good data
- `zone_tex_compare_swizzle.py` — verify two unswizzle implementations match
- `zone_tex_visualize_indices.py` — visualize PSMT4 indices as grayscale
- `zone_tex_layout_samples.py` — sample one record per layout mode
- `zone_tex_decode_record0.py` — manual end-to-end Python reference decoder
- `zone_tex_brute_force.py` — try multiple (location, format) combinations and
  score against PC ground truth via histogram similarity
- `zone_tex_try_layouts.py` — try various swizzle hypotheses for a single record

### Open questions

1. **What is the actual CLUT format at the OLD location?** PSMCT32 (8 entries)
   gives recognizable grayscale snow but only 8 colors. PSMCT16 (16 entries)
   gives garbage rainbow.
2. **What is the pixel layout at the OLD location?** Linear PSMT4 partially
   works for some records, fully linear for others requires Conv4to32 + a
   layout-specific block permutation.
3. **Is there a second runtime decode pass** (not yet identified) that converts
   from the OLD location's "prepared" format to the final RGBA pixels?
4. **PC version uses 24-bit RGB DXT-compressed textures** while PS2 uses 4-bit
   paletted with mostly-grayscale palettes. The PC version may be **fully
   independent assets**, not derived from the same source as the PS2 version,
   in which case structural similarity is the only available cross-check.

### Reverted change

A previous attempt to set `dataBaseOffset = PACKED_BASE = 0x0A` and
`dataOffsetBias = 0` was tested. It changed which bytes are read but produced
*different* garbled output, NOT correct output. The change was reverted because
the legacy heuristic correctly decodes A20AA4CB while the PACKED_BASE attempt
broke A20AA4CB without fixing other records.

The correct base for decoder progress is unclear. The strongest evidence we have
is:
- A20AA4CB works at the OLD location with the existing transforms
- The OLD location contains data that LOOKS like 8 PSMCT32 grayscale entries
  + linear pixels for at least one PSMT4 record
- The NEW location (PACKED_BASE) is what the DMA chain references and what
  PCSX2 would see in VRAM, but standard Conv4to32 alone doesn't recover
  recognizable images

### Practical answer to "is extraction the problem?"

**No.** Extraction is byte-perfect. The PS2 zone .tex format genuinely needs more
runtime decompilation work to identify the exact CLUT/pixel decoding pipeline
the runtime uses for these prepared-source buffers.

## Phase 333: FUN_0019cd48 decompiled — runtime decode formula identified

The retained phase 333 buffer-producer and zone-loader analysis reveals the
exact runtime decode formula.

### FUN_0019cd48 (the consumer)

```c
// At entry, get source blob from wrapper+0x10 (with flag-based indirection)
int blob = *(wrapper + 0x10);  // (or **(wrapper+0x10) if flag bit 2 set)

// Allocate a NEW 0x10-byte control struct, store at wrapper+0x10
ctrl = alloc(0x10);
*(wrapper + 0x10) = ctrl;
ctrl[0] = blob;  // ctrl[0] keeps a pointer to the OLD blob

// Allocate the output RGBA32 buffer
ctrl[2] = alloc(width * height * 4);  // ctrl+8

// Read source pointers from the OLD blob (NOT from the new ctrl)
clut_ptr = *(blob + 0x18);    // PSMCT32 CLUT (4 bytes per entry)
pixel_ptr = *(blob + 0x14);   // Linear pixel indices (PSMT4 nibbles or PSMT8 bytes)
out_ptr = ctrl[2];

// Get bits-per-pixel from a vtable call
bpp = vtable[+0xe4](wrapper);  // Returns 4 or 8

if (bpp == 8) {
    // PSMT8 path with CSM1 unswizzle
    src = pixel_ptr + (height - 1) * width;  // Bottom-up start
    for (y = 0; y < height; y++) {
        for (x = 0; x < width; x++) {
            byte index = src[x];
            byte unswizzled = DAT_005ad180[index];  // CSM1 lookup table
            out_ptr[y*width+x] = clut_ptr[unswizzled * 4];
        }
        src -= width;  // Move UP one row
    }
} else {
    // PSMT4 path - NO swizzle
    src = pixel_ptr + (height - 1) * (width / 2);  // Bottom-up start
    for (y = 0; y < height; y++) {
        for (x = 0; x < width / 2; x++) {
            byte b = src[x];
            out_ptr[y*width + 2*x]   = clut_ptr[(b & 0xf) * 4];
            out_ptr[y*width + 2*x+1] = clut_ptr[(b >> 4) * 4];
        }
        src -= width / 2;  // Move UP one row
    }
}

// Then upload the decoded RGBA32 to GS VRAM via FUN_001e5ed0 -> FUN_001e6818
```

### Key facts from the decompilation

1. **The decode is purely CPU-side**, not GS swizzle. The PSM in the record's
   TEX0 says PSMT4 but the runtime decodes to RGBA32 then uploads as PSMCT32.
2. **CLUT is stored as PSMCT32** in the prepared blob (4 bytes per entry,
   regardless of the upload PSMCT16 size). 16 entries for PSMT4, 256 for PSMT8.
3. **Pixels are stored linearly** in raster scan order, **bottom-up** (PS2
   convention).
4. **No swizzle for PSMT4**. PSMT8 only uses CSM1 lookup table `DAT_005ad180`.
5. **Output is RGBA32 stored top-down** (because the bottom-up source is read
   into top-down output).

### Remaining mysteries

For some records like 389 (snowscape, PSMT4 128x128, layout=0x02000001):
- `pal_bytes = 32` per the record
- The OLD location bytes show 8 grayscale PSMCT32 entries (32 bytes)
- The texture uses all 16 nibble indices, not just 0-7
- We have NOT yet found where the runtime gets entries 8-15 from
- The pixel data at `OLD + data_offset + 32` may also not be exactly right —
  decoding it as bottom-up linear gives recognizable mountain horizons but in
  grayscale only

For records like A20AA4CB (star icon, PSMT4 64x64, layout=0x02000005):
- `pal_bytes = 64` (full 16-entry PSMCT32)
- The existing decoder's Layout02000005BlockPermutation accidentally produces
  the right output via Conv4to32 + 32x16 block permutation
- A pure linear bottom-up decode does NOT give a recognizable star

So at least two distinct cases still need work:
- **Case A** (A20AA4CB-like): pal_bytes=64, needs some kind of swizzle on the
  pixel data (current Conv4to32 + permutation works by accident)
- **Case B** (snowscape-like): pal_bytes=32, palette has only 8 entries
  (or entries 8-15 are stored elsewhere)

### Next decompilation targets

The actual **buffer producer** that fills `blob+0x14` and `blob+0x18` with
linear/swizzled pixels and PSMCT32 CLUT entries has NOT been found. FUN_001e6818
only builds the DMA upload chain — it doesn't write pixel/CLUT bytes.

Suspect candidates that haven't been fully traced:
- `FUN_0019c620` (vtable +0x44 of DAT_004b41c8) — referenced as palette filler
- The wrapper allocator chain (`FUN_001a0540`, `FUN_001a05c0`, `FUN_001a06c8`)
- A function that takes the record's `data_offset`/`cumul_off` and copies bytes
  into the prepared buffer

### Diagnostic tools for this phase

- Use
  [`DecompileFunctionsByAddress.java`](../../tools/reverse-engineering/ghidra/DecompileFunctionsByAddress.java)
  and [`DumpInstructionsByAddress.java`](../../tools/reverse-engineering/ghidra/DumpInstructionsByAddress.java)
  to reproduce targeted analysis of the function addresses named above.
- Use the retained [`content-search`](../../tools/validation/thaw-zone-texture/Commands/ContentSearchCommand.cs)
  and [`decode-provenance`](../../tools/validation/thaw-zone-texture/Commands/DecodeProvenanceCommand.cs)
  commands to compare archive content with ground-truth PNGs.

## Phase 336: SOLVED

The complete decode pipeline was identified by walking the FUN_001e9ac0 owner
blob structure end-to-end and validating against PC ground truth.

### The owner blob header

At a fixed offset in the file (0x1080 in z_ho.tex; locatable by scanning), there
is a 16-byte header:

```
+0x00 u16  global_u16
+0x02 u16  primary_count
+0x04 i32  secondary_count   = number of texture records
+0x08 i32  base_a_offset     (used for record +0x38 relocation)
+0x0c i32  base_b_offset     (used for record +0x28 / +0x30 relocation)
```

It is followed by `primary_count * 0x50` bytes of primary records, then
`secondary_count * 0x40` bytes of secondary records (the ones documented in the
"Record Table" section above), then the DMA upload chain.

The header is found by scanning for any (header_offset, primary_count,
secondary_count) triple where
`header_offset + 0x10 + primary_count*0x50 + secondary_count*0x40` equals
the DMA chain start (the first `0x10000006` CNT tag in the file).

### Per-record relocation (mirroring FUN_001e9ac0)

For each secondary record:

```
pixel_abs = header_offset + record.cumul_off    + base_b
clut_abs  = header_offset + record.data_offset  + base_b
```

These point at the actual prepared CLUT and pixel bytes in the file. The runtime
also relocates `record.upload_off` by adding `header_offset + base_a`, but that
value is the DMA upload chain position (not needed for CPU decode).

### Decoder formula

For each PSMT4 record (~97% of records):

1. **Read CLUT** of `pal_bytes` length at `clut_abs`:
   - `pal_bytes == 32` → 16 PSMCT16 entries (5/5/5 RGB, expand to 8-bit via `<< 3`)
   - `pal_bytes == 64` → 16 PSMCT32 entries (4-byte RGBA)

2. **Read pixel bytes** of `tw*th/2` length at `pixel_abs`. The bytes are stored
   in PSMCT32-uploaded layout for the texture's PSMT4 region. Apply
   **`Ps2TexSwizzle.UnswizzlePsmt4(bytes, tw, th)`** which handles the
   Conv4to32 mapping for all `(tw, th)` combinations.

3. **Render** by walking the unswizzled nibbles **bottom-up** (PS2 stores
   textures bottom-up; the runtime walks them in reverse to produce a top-down
   RGBA32 output buffer):
   ```
   for each output row y in [0, th):
       src_y = th - 1 - y
       for each x in [0, tw):
           idx = unswizzled[src_y * tw + x]
           out_rgba[y*tw + x] = palette[idx]
   ```

For each PSMT8 record (~3%): same flow but use `UnswizzlePsmt8`, read 1 byte per
pixel as the palette index, and apply CSM1 unswizzle to the 256-entry palette
(swap entries [8..15] with [16..23] within each group of 32).

### Validation

Tested against PC `.tex.wpc` ground truth (decoded via the existing `xbxtex`
command on `z_ho.pak.wpc`'s 003F2AC0.tex):

| Record | Type | Result |
|---|---|---|
| 0D1361E6 (snowscape, PSMT4 128x128) | PSMT4 + PSMCT16 | pixel-perfect match |
| A20AA4CB (star icon, PSMT4 64x64) | PSMT4 + PSMCT32 | pixel-perfect match |
| 26055DCC (concrete wall, PSMT4 128x128) | PSMT4 + PSMCT16 | pixel-perfect match |
| 8663DA77 (stone tile, PSMT4 128x128) | PSMT4 + PSMCT16 | pixel-perfect match |
| D6615C6E ("Coming Soon!" sign, PSMT4 128x64) | non-square PSMT4 | pixel-perfect match |
| DA974F22 (creature face, PSMT8 128x128) | PSMT8 + PSMCT16 + CSM1 | pixel-perfect match |
| 023292D1 (stone wall, PSMT8 128x128 from z_bh) | PSMT8 | recognizable correct |

Decoded counts:
- z_ho.tex (Hollywood): 853 unique textures from 990 records
- z_at.tex: 652 textures
- z_bh.tex: 851 textures

All 13 zone tex regression tests pass after switching the public decoder to the
owner blob path.

### Why the heuristic decoders failed

The legacy `TryGetHeaderDataLayout` heuristic computed
`dataBaseOffset = fileLength - maxEnd`. By coincidence this equals
`base_b_offset` in z_ho.tex (both 0x51590), so reads at
`dataBaseOffset + record.data_offset` landed in roughly the right place. But the
legacy code missed the `+ header_offset` component (the 0x1080 header start),
so all reads were 0x1080 bytes off, landing in unrelated bytes. The "lucky"
A20AA4CB decode worked because its `data_offset + 0x51590` happened to align
with leftover prepared data from another record at exactly the right alignment
for the layout transform — hence why no other records worked.

The correct base offset is `header_offset + base_b_offset`, NOT just
`base_b_offset`.

## Phase 337: Sub-page widths needed linear pixel reads, not Conv8to32

Phase 336's blanket recommendation to apply `Ps2TexSwizzle.UnswizzlePsmt4` /
`UnswizzlePsmt8` to every record produced visible checkerboard / banding
artifacts on sub-page-width records (PSMT8 64x128 / 64x64 / 32x128 across
z_bh, and PSMT4 32x64 / 128x64 in the same zone). The validation set at the
time only included 128x128 paletted records, where the swizzle math happens
to work correctly because the data does fill a complete Conv4to32 / Conv8to32
page.

The decoder in
`src/NeversoftMultitool/Core/Formats/Texture/Ps2Scene/ZoneTex/ThawZoneTexOwnerBlobDecoder.cs`
now uses a dimension-gated strategy that matches both the FUN_0019cd48 runtime
decompilation (Phase 333) and PC ground truth at
`TestOutput/z_bh_pc/textures/`:

- **PSMT4: full UnswizzlePsmt4 path (Conv4to32 -> Conv4to16 -> linear).**
  The standard build-tool layout for every PSMT4 dimension we see in zone
  .tex files is what `Ps2TexSwizzle.UnswizzlePsmt4` already handles.
- **PSMT8: Conv8to32, except sub-page-width AND multi-page-tall records
  (width < 128 AND height > 64) which are read linearly.** PSMT8 page
  geometry is 128x64. In that quadrant (32x128, 64x128, 32x256...), the
  Conv8to32 algorithm zero-pads the right half of each page and produces
  visible checkerboard / banding. FUN_0019cd48 (Phase 333) reads those
  records linearly. 64x64, 256x64, 128x128, 256x128, etc. continue through
  Conv8to32 because they either fit a single page or fill the page width.

The CSM1 index remap (DAT_005ad180) is still applied for PSMT8 during
palette lookup regardless of which path produced the index buffer.

### Validation

Regression test
`tests/NeversoftMultitool.Tests/Core/Formats/Texture/Ps2Scene/ZoneTex/ThawZoneTexOwnerBlobLinearDecodeTests.cs`
exercises 11 representative checksums (1 anchor + 10 previously-failing
records) and asserts mean-absolute-error vs PC ground truth <= 32 per channel.
RGB on fully-transparent pixels is excluded -- `xbxtex` zeroes those channels
while the PS2 decoder retains the palette[0] RGB; both are visually
equivalent.

Sample MAE (after fix, lower is better):

| Record | Pre-fix MAE | Post-fix MAE |
|---|---|---|
| 0xD2411F1A (PSMT8 64x128, "wooden door") | 122 (checkerboard) | < 32 |
| 0xCD2F89B8 (PSMT8 64x128) | 41 | < 32 |
| 0x6CC9E390 (PSMT8 64x128, "white pillar") | 35 | < 32 |
| 0x93BC556C (PSMT8 64x128) | 37 | < 32 |
| 0x2AC5ACDE (PSMT4 128x128, "grass tuft" anchor) | < 32 | < 32 |
| 0x023292D1 (PSMT8 128x128, "stone wall" anchor) | < 32 | < 32 |

A first iteration of this fix used a stricter PSMT4 gate (Conv4to32 only
when admitted, otherwise linear) and a width-only PSMT8 gate (linear when
< 128 wide). That regressed PSMT4 64x128 / 128x64 records that were
correctly stored in Conv4to16 layout (e.g. 0xF84D0B01 brick wall) and
PSMT8 64x64 records that fit a single page and were correctly stored in
Conv8to32 layout (e.g. 0x243A495E foliage). The current rule preserves
every working pre-fix decode while still fixing the multi-page-tall
sub-page-wide PSMT8 cases.
