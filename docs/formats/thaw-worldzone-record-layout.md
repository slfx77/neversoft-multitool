# Phase 400: `.91E1028D` Full File Layout — Multi-Block Chain + Outlier Sub-Formats

## Summary

This pass closes most of phase 399's open structural questions. The `.91E1028D` PAK entries (worldzone placement records) are a **chain of FUN_0025A818-shaped sub-blocks** followed by a 0x6290993B-style geometry trailer. 4 of 7 extracted samples in BH/HO fit the chain template cleanly. The 3 outliers split: one is the same record format with a degenerate (count-less) header, and two are a different sub-format that looks compressed/packed.

## Decoded File Layout (4/7 fitting samples)

```
struct File_91E1028D {                  // for 4/7 BH+HO samples
    u8     pre_header[0xC];             // file id / not consumed by FUN_0025A818
    Block  blocks[N];                   // chain, advance by 8 + count*0x74
    u8     geometry_trailer[];          // 0x6290993B-style child records
};

struct Block {                          // matches FUN_0025A818 input shape
    u32    count;                       // number of records in this block
    u32    flag;                        // looks like a checksum/group tag
    Item   items[count];                // 0x58 bytes per item, source layout
    u8     item_tails[count][0x1C];     // additional per-item data
};

struct Item {                           // 88 source bytes -> 96 RAM bytes after FUN_0025A818 expand
    u32    zero_00;                     // always 0
    u32    sig_FFFFFFFF;                // +0x04
    u32    sig_80808080;                // +0x08
    u32    sig_FF00FF00;                // +0x0C
    u32    small_int_10;                // small u32 (0..N), per-item
    u32    sig_4B189680;                // +0x14 = 1e7f
    u32    sig_FFFFFFFF;                // +0x18
    float  field_1C;                    // varies: 0.0, -0.03125, -9.65, etc.
    float  bbox[6];                     // +0x20..+0x37 (PS2 interleaved AABB, see below)
    u32    zero_38, zero_3C;            // padding
    u32    flag_word;                   // +0x40 (bits 0x10, 0x200, 0x400 control post-process)
    u32    sig_FFFFFFFF;                // +0x44
    u32    ref_or_count;                // +0x48 (post-processed via FUN_00279E18 if flag&0x10==0)
    u32    hash_4C;                     // +0x4C (key/checksum)
    u32    link_50;                     // +0x50 (rewritten as self-relative pointer if flag&0x200)
    u32    sig_FFFFFFFF;                // +0x54
};
```

### Bbox layout

The 6 floats at `item+0x20..+0x37` decode as `(min_x, min_y, max_z, max_x, max_y, min_z)` — PS2's typical (vec3 + 1 word) split where bbox-min and bbox-max are stored as two interleaved quads. Verified against two unrelated files (different objects, different worldzones); both produce valid `min < max` on all three axes.

### Item-tails (per-item, 0x1C bytes)

Located immediately after the items array within each block. Holds `0x4B189680 / 0xFFFFFFFF / 4 floats / 0x00200103 / u32 / u32 / 0xFFFFFFFF` repeated per item. Looks like a small auxiliary record with another 4-component vector and a couple of indices — needs further decode.

## Chain Walk Results

Walking each fitting file as `start@0xC, advance by 8 + count*0x74`:

```
00015410.91E1028D (0x4E8): block 0 cnt=2 flag=0x88C21962 -> block 1 cnt=2 flag=0xF72612F4 -> trailer @ 0x1EC (0x2FC bytes)
00026D20.91E1028D (0x308): block 0 cnt=2 flag=0xF72612F4 -> block 1 cnt=2 flag=0x92E19120 -> block 2 cnt=1 flag=0x6E2F434E -> trailer @ 0x268 (0xA0 bytes)
00016F40.91E1028D (0x308): block 0 cnt=2 flag=0x1E45B7C1 -> block 1 cnt=2 flag=0x88C21962 -> trailer @ 0x1EC (0x11C bytes)
00028820.91E1028D (0x308): block 0 cnt=2 flag=0xF72612F4 -> block 1 cnt=2 flag=0x92E19120 -> block 2 cnt=1 flag=0x6E2F434E -> trailer @ 0x268 (0xA0 bytes)
```

Notably, files 00026D20 and 00028820 (different worldzones) have **identical block headers** — they likely reference the same shared object types.

## Geometry Trailer

For 00015410, the post-chain region at +0x1EC..+0x4E7 contains 0x6290993B-style records:

```
+0x1EC: float 43.22                    // start of more bbox/geometry data
+0x208: 0x00000604                     // sub-tag (matches 0x6290993B sub-record tags)
+0x210: 0x64967688                     // family tag (matches 0x6290993B file content)
+0x218: count=5
+0x21C: count=8
+0x220: 3 floats (-38.49, 44.42, -52.74)  // real-looking object bbox
... (multiple 0x58-stride records follow)
```

The trailer's `0x64967688` tag and `0x00000604` sub-tag are the same constants observed in standalone `.6290993B` PAK entries. So `.91E1028D` files **inline** the same per-piece geometry data that `.6290993B` files carry — effectively an embedded copy. Either path could feed the runtime; both produce equivalent geometry input.

## Outlier Analysis

### `0003D3A0.91E1028D` (z_ho, 0x308 bytes) — variant header

```
+0x00: 0x0000C740   // 51008, possibly a size/id
+0x04: 0xFFFFFFFF
+0x08: 0xFFFFFFFF
+0x0C: 0x00000000   // count would be 0 -> chain fails
+0x10: 0x00000000   // flag
+0x14: 0x00000000   // item+0x00 = 0
+0x18: 0xFFFFFFFF   // item+0x04 sig
+0x1C: 0x80808080   // item+0x08 sig
+0x20: 0xFF00FF00   // item+0x0C sig
+0x28: 0x4B189680   // item+0x14 sig
+0x30: 4.097, -0.0005, 0.0075, 24.90  // floats matching bbox layout
```

Same item format as the fitting files, but with a degenerate header (no count). The runtime probably either (a) reads from a fixed offset (file+0x14) for a single implicit record, or (b) uses `record[+8]` (the PAK entry's size field) to derive count. With size 0x308, count would be `(0x308 - 0x14) / 0x74` ≈ 6.6, not integer.

### `000516F0.91E1028D` (z_bh, 0x27BA8 bytes) and `00060930.91E1028D` (z_ho, 0x253E8 bytes) — packed/compressed sub-format

```
000516F0 first dwords: 0xF4E31568 0x15681215 0x1E15EC77 0xE8071568 ...
00060930 first dwords: 0x80F3F3F3 0x8056C393 0xD0CBC393 0x00589959 ...
```

- 0 to 25 `.91E1028D` signature dwords across 150KB+ (vs ~30+ per record in fitting files).
- Repeating `0x68 0x15` byte pairs in 000516F0 strongly suggest LZ-style back-references or RLE.
- 00060930 has high-entropy bytes (0x9X, 0xCX, 0xFX) consistent with packed/compressed data.

Both files are an order of magnitude larger than the fitting samples. Most likely either (a) a different runtime path consumes them, or (b) they need decompression before FUN_0025A818-style parsing.

## Practical Consequence

For BH/HO worldzone object placement, the **4 fitting `.91E1028D` files** plus the **5 T08-preceded `.6290993B` files** in BH carry the placement payload. The 3 outlier `.91E1028D` files are a separate puzzle.

The bbox+flags+links layout means we can now confidently parse 4-of-7 placement-record files. What we still don't have:

1. **Full transform vs. bbox-only**: The 6 bbox floats can't represent rotation. Either rotation is in the item-tails (0x1C extra bytes per item), or rotation isn't stored — objects may be axis-aligned by design, or rotated via a small set of pre-canned orientations referenced by `flag_word` / `hash_4C`.
2. **Outlier interpretation**: Whether the 3 large/packed `.91E1028D` files are decompressible or use a different runtime path entirely.
3. **`flag_word` semantics**: Bit `0x10` triggers `FUN_00279E18(ref_or_count)` (looks like a checksum-to-pointer lookup); bit `0x200` rewrites `link_50` as a self-relative pointer; bit `0x400` does the same for an unknown adjacent field. These are known to control behaviour but we don't yet know what each bit *means*.

## Item Registration Path (FUN_00359068)

Re-reading `FUN_00359068` (the helper called at the end of `FUN_0025A818` when `wrapper+0x1C != 0`) reveals what the item flag byte actually selects:

```c
// For each item, checking low bit of flag_word (byte at item+0x41):
if ((item[+0x41] & 1) != 0) {
    FUN_00353FA0(DAT_004A10CC, item[+0x4C], 0);
    FUN_00354210(DAT_004A10CC, item[+0x44], item[+0x4C]);
}
```

So two item fields are passed into a global hash-table at `DAT_004A10CC`:

- `item[+0x44]` is the **key**
- `item[+0x4C]` is the **value**

`FUN_00354210(table, key, value)` almost certainly inserts `key -> value` into the table, and `FUN_00353FA0(table, value, 0)` looks like a secondary indexed insert. That means `item[+0x44]` is the **resource identifier** that some other code looks up to resolve `item[+0x4C]`.

In a placement context the most plausible reading is:
- `item[+0x44]` = QbKey of the object/class name (e.g. QbKey of `"Light_Standard"`)
- `item[+0x4C]` = either a QbKey of the specific instance or an index into another data table

Per-item field semantics now look like:

```
item +0x00     zero (padding)
item +0x04..+0x1B   signatures / constant markers
item +0x1C     small float (Z-min? axis scalar?)
item +0x20..+0x37   bbox (6 floats, PS2 interleaved)
item +0x38..+0x3F   zero padding
item +0x40     flag_word:
                 bit 0 (0x01):  register item via FUN_00359068 (hash-table insert)
                 bit 4 (0x10):  if *clear*, run FUN_00279E18(src[+0x48]) -> dest[+0x48]
                 bit 9 (0x200): rewrite item[+0x50] as self-relative pointer
                 bit 10 (0x400): rewrite item[+0x54] as self-relative pointer
                 (high bytes = packed GS register or similar, still TBD)
item +0x44     hash-table KEY  (object/class QbKey)
item +0x48     raw checksum -> transformed via FUN_00279E18 if flag bit 4 clear
item +0x4C     hash-table VALUE (instance QbKey or table index)
item +0x50     self-relative pointer (valid when flag bit 9 set) -> another item in same array
item +0x54     self-relative pointer (valid when flag bit 10 set) -> another item in same array
```

Items form a **linked structure** via +0x50 / +0x54 when the flag bits are set — parent/child or next/prev in a node graph. Combined with the bbox field, this strongly suggests the records encode a scene-node tree where each node has a bbox, a class/instance hash pair, and pointers to related nodes. That's exactly the shape worldzone object placement should take.

## Phase 401 Decomp Results — Item Field Semantics

`FUN_00279E18` (phase401): **hash-table LOOKUP** at `DAT_0055D384` (table base) with size `2^DAT_0055D380`. Walks a bucket chain; each entry is `[key:u32, value:ptr, next:ptr]` (12 bytes); returns the first byte (`undefined1`) at the value pointer. So `item[+0x48]` is a small integer **resource index** that the runtime resolves to a byte value. Only fires when `flag_word & 0x10 == 0`.

`FUN_00353FA0` and `FUN_00354210` (phase401): **bidirectional hash-table inserts** using a manager at `DAT_004A10CC`:

```
manager + 0x0C  -- primary table base (bucket-chained)
manager + 0x08  -- bit-width (2^N buckets)
manager + 0x28  -- secondary table base
manager + 0x24  -- secondary bit-width
```

`FUN_00359068` calls:
```c
FUN_00353FA0(mgr, item[+0x4C], 0);            // primary:   item[+0x4C] -> identity (reverse lookup)
FUN_00354210(mgr, item[+0x44], item[+0x4C]);  // secondary: item[+0x44] -> item[+0x4C]
```

So the engine builds two lookups: find-by-instance (primary, item[+0x4C]) and find-by-class (secondary, item[+0x44] -> item[+0x4C]).

## Item Heterogeneity — `item 0` ≠ `item 1`

Cross-checking `item[+0x44]` and `item[+0x4C]` values across the 4 fitting files (8 item-0s and 8 item-1s observed) reveals **items in a block are not uniform records** — they alternate between two distinct types:

```
ITEM 0 pattern (always at position 0 of each block):
  +0x40 flag_word:  high byte 0x50/0x51/0x53, low bytes 00   (looks like packed PS2 GS register value)
  +0x44:            small value (0x00009030, 0x00009120, 0x0000CF20) OR 0xFFFFFFFF
  +0x48:            always 0x00000002
  +0x4C:            hash-sized (0x88C21962, 0x80212262)

ITEM 1 pattern (always at position 1 of each block):
  +0x40 flag_word:  0x00000002 or 0x00000001 (looks like a count or type)
  +0x44:            hash-sized (0x6E2F434E, 0x1E45B7C1, 0x88C21962)
  +0x48:            always 0x00000000
  +0x4C:            0xFFFFFFFF
```

Item 0's `+0x44` values are **small integers** — plausibly byte offsets into an adjacent `.mdl`. (The diagnostic `parse_91e_records` was correctly identifying `batch_ref` as "0x400 <= value < mdl_len" — it's an MDL byte offset.) Item 1's `+0x44` values are hash-sized but **none resolve** against our 20547-entry QbKey dictionary, so they are not classic Neversoft QbKey name hashes. Either they are:

- Build-tool-assigned instance IDs (not from string hashing), or
- QbKeys of strings that aren't in our dictionary (e.g. level-specific object names that never appear in script files)

The simplest consistent reading is that each block encodes **one placed object**:

- Item 0 = "rendering metadata" (batch offset into MDL, GS register, material instance)
- Item 1 = "placement/identity metadata" (object class hash, parent link via +0x50/+0x54)

That is why count is always 2 in the fitting files — blocks come in `(render-side, scene-side)` pairs. The count=1 block in `00026D20` / `00028820` (block 2) is a leftover that has only the scene-side record.

Notable cross-file value sharing:
- `0x88C21962` appears 8x across blocks — likely a shared object class (a common level prop referenced from many placements)
- `0xF72612F4` appears as **block flag** in 3 files AND as an item `+0x4C` value — so the block flag is also a class identifier, and items can cross-reference other blocks

## Block Flag = Object Group QbKey

The block-level `flag` field at `+0x04` inside each block is itself one of the same hash space as item fields. Values like `0xF72612F4`, `0x88C21962`, `0x92E19120`, `0x1E45B7C1` appear both as block flags and as item `+0x44` / `+0x4C` fields. This makes block flag a **group/class identifier**, and the records inside the block instantiate that class.

## Phase 402 — Final Triaged Results

Three follow-up probes were run to close the remaining gaps.

### Probe 1: `item[+0x44]` -> MDL content (option 1)

Result: the small `+0x44` values do NOT land in VIF UNPACK regions. On `00026D20.91E1028D`, `+0x44 = 0x9120` against the preceding `.mdl` (do=0x1D990) maps to MDL content:

```
MDL+0x9120: 0x00008C70 0x00000000 0xF72612F4 0x00000000 0xFFFFFFFF
                                  ^^^^^^^^^^
                                  the block-flag / class-ID we've already seen
```

So `item[+0x44]` links into the MDL's **preamble/metadata region**, not its VIF stream. At the referenced offset sits a record containing the same class hash (`0xF72612F4`) that appears as a block flag. That confirms `item[+0x44]` is an index into the MDL's internal object table — it associates a worldzone placement record with a specific MDL object/piece definition.

### Probe 2: `+0x50` / `+0x54` link graph (option 2)

Result: across all 18 items in the 4 fitting files, **zero items have flag bits 0x200 or 0x400 set**. The link-rewriting code path in `FUN_0025A818` never fires in BH/HO data. The `+0x50` / `+0x54` bytes are inert — they contain raw values (often `0x80808080 / 0xFF00FF00` that look like header signatures but are just residual data at the end of a 0x58-stride item).

So the item-graph feature exists but is dormant in these worldzones. Placement data is self-contained per record, not graph-linked.

### Probe 3: `flag_word` high bytes (option 3)

Item 0 flag_words collected across all blocks:

```
0x50270000  (2x)     0x50C70000  (2x)     0x50D30000  (1x)
0x50E70000  (1x)     0x51080000  (1x)     0x536D0000  (3x)
```

Pattern: high byte in {0x50, 0x51, 0x53} (shared `0101_00xx` top nibble), middle byte varies (0x08, 0x27, 0x6D, 0xC7, 0xD3, 0xE7), low 2 bytes always 0. These don't match PS2 GS register address conventions (registers 0x00..0x3F) and the `xx` low bits don't map cleanly to PSM/TW/TH packings either. Most likely a **build-tool-assigned discriminator** with a type in the top nibble — not a runtime-decodable GS value.

Item 1 flag_words are simply 1 or 2 across all samples. Consistent with a minor type / child-count indicator.

## Final Picture For `.91E1028D` Worldzone Records

```
struct File_91E1028D (fitting-variant) {
    u8      pre_header[0xC];
    Block   blocks[];             // chain: 8 + count * 0x74 bytes each
    u8      geometry_trailer[];   // 0x6290993B-style records (same bytes that would be in a standalone .6290993B)
};

struct Block {                    // count=2 for normal pairs, count=1 for leftover
    u32     count;
    u32     flag;                 // class/group identifier (e.g. 0xF72612F4) - cross-referenced from item fields
    Item    items[count];         // 0x58 source bytes each
    u8      block_footer[count * 0x1C];
};

// items are HETEROGENEOUS by position; the two item positions have different roles:

struct Item_RenderSide {          // always at position 0 in the block
    u32     sigs_and_flags[8];    // +0x00..+0x1F (constant markers + small int)
    float   bbox[6];              // +0x20..+0x37 (world-space AABB, PS2 interleaved)
    u32     pad_38, pad_3C;
    u32     flag_word;            // high-byte 0x50/0x51/0x53, build-tool discriminator
    u32     mdl_offset;           // +0x44: byte offset into adjacent .mdl preamble (often 0xFFFFFFFF)
    u32     always_two;           // +0x48: 0x00000002 in every sample
    u32     class_hash;           // +0x4C: hash matching block-flag of some other block (cross-ref)
    u32     pad_50;               // +0x50: raw (link-rewrite dormant)
    u32     sig_FF_54;            // +0x54: 0xFFFFFFFF
};

struct Item_SceneSide {           // always at position 1 in the block
    u32     sigs[5];              // +0x00..+0x13 (different sig pattern than item 0)
    float   vec_or_extents[4];    // +0x14..+0x23 (meaning unclear; values overlap with item 0's bbox)
    float   pad_vec[4];           // +0x24..+0x33
    u32     const_00200103;       // +0x34 tag
    u32     vary_38;              // +0x38 small u32
    u32     packed_3C;            // +0x3C (0x50XX0000 pattern like item 0 flag_word)
    u32     ref_40;               // +0x40: 1 or 2
    u32     object_hash;          // +0x44: class hash (not in QbKey dictionary)
    u32     zero_48;              // +0x48: 0
    u32     sig_FF_4C;            // +0x4C: 0xFFFFFFFF
    u32     pad_50_header_of_next; // +0x50..+0x57: residual (inert)
};
```

## What We Have And Don't Have

**Have:**
- End-to-end file layout for 4/7 `.91E1028D` samples.
- World-space AABB (6 floats) per placed object in Item 0.
- Class identifier linking placements to MDL preamble records.
- Confirmed absence of runtime link-graph usage in BH/HO.

**Don't have:**
- Object rotation. No field in the decoded layout is a quaternion or rotation matrix. Possibilities:
  - Objects are axis-aligned by design in these worldzones.
  - Rotation is encoded in one of the constants we still don't understand (unlikely — rotation needs 4 floats or 3 angles, and we don't see that much free per-object data).
  - Rotation lives in the `block_footer` (`count * 0x1C` bytes) or in the MDL preamble at the offset referenced by `+0x44`.
- Resolution of the object hashes (`0x88C21962`, `0x1E45B7C1`, `0xF72612F4`, etc.) against known names. 0/20547 match.
- Decode of the 3 outlier `.91E1028D` files (one degenerate-header variant, two likely compressed).

## Practical Outlook

With world-space AABBs per object in hand, a first rendering pass is viable: position each MDL piece at the AABB center and treat extent as scale. That will look right for axis-aligned props and wrong (or partially wrong) for anything rotated. Visual comparison against reference footage will quickly reveal whether rotation is in the MDL preamble data we haven't decoded.

## Phase 403 — MDL Preamble Record Decode (rotation source recovered)

Following `item[+0x44]` into the adjacent `.mdl` for the file `00026D20.91E1028D` / MDL at `do=0x1D990` uncovered a **second-level record table** inside the MDL preamble. 11 records at 0x50 stride starting at `MDL+0x9030`, detected by the shared `0x4B189680` signature at `rec+0x18` (same constant as in `.91E1028D` items).

### MDL preamble record layout (0x50 bytes)

```
rec +0x00  u32    class_hash          // matches .91E1028D block.flag
rec +0x04  u32    zero pad
rec +0x08  u32    0xFFFFFFFF          (sig)
rec +0x0C  u32    0x80808080          (sig)
rec +0x10  u32    0xFF00_SS00         SS = sequence byte: 0x01..0x05 for primary, 0x00/0xFF for header/secondary
rec +0x14  u32    zero
rec +0x18  u32    0x4B189680          (sig)
rec +0x1C  u32    0xFFFFFFFF          (sig)
rec +0x20  f32[4] rotation            unit quaternion after magnitude normalization
rec +0x30  f32[3] size                per-axis extent / half-extents
rec +0x3C  u32    flags               5 for primary records, 0x00200103 / 0x00004143 for secondary
rec +0x40  u32    vif_offset_a        byte offset into MDL VIF stream (render-side A)
rec +0x44  u32    0xFFFFFFFF          (sig)
rec +0x48  u32    vif_offset_b        byte offset into MDL VIF stream (render-side B)
rec +0x4C  u32    zero
```

### Quaternion evidence

For the 11 records, normalizing the `+0x20..+0x2C` vec4 to unit magnitude consistently produces valid unit quaternions with `qw` in the `[0.86, 1.0]` range — exactly the shape of real rotations encoded as `(qx, qy, qz, qw)`:

```
idx hash       seq  raw(qx,qy,qz,qw)                     normalized(unit)          angle
0   0x00000000 0xFF (-9.643, 12.315, -4.714, 141.443)    (-0.068, 0.086, -0.033, 0.993)  13.2°
1   0x88C21962 0x01 ( 4.097,  0.003, -0.010,  22.183)    ( 0.182, 0.000, -0.000, 0.983)  20.9°
3   0xF72612F4 0x03 ( 4.097,  0.003, -0.010,  22.183)    ( 0.182, 0.000, -0.000, 0.983)  20.9°  (identical to rec 1)
5   0x1E45B7C1 0x05 ( 0.567, -0.000, 62.407, 169.804)    ( 0.003,-0.000,  0.345, 0.939)  40.4°
6   0x92E19120 0x00 (34.312, 24.500,  0.000,  99.331)    ( 0.318, 0.227,  0.000, 0.921)  46.0°
7   0x88C21962 0xFF (10.594, 40.531, -0.156,  73.054)    ( 0.126, 0.481, -0.002, 0.867)  59.7°
9   0x88C21962 0xFF (-0.031,  0.000,  0.000,  20.613)    (-0.002, 0.000,  0.000, 1.000)  0.2° (identity)
```

Records 1-4 have near-identical transforms but different class hashes — same physical object with multiple class identifiers (likely LOD or render-variant tags). Records 9-10 are near-identity rotation.

The raw vec4 magnitudes vary (20..180) and don't obviously encode additional data (probably just un-normalized form from the build tool, normalized at load time). Alternatively the magnitude could carry an ancillary scalar (priority / bounding-sphere radius / etc.) but nothing in the 11-record sample confirms that.

### Size field at +0x30..+0x38

```
Rec 1-4 : (6.032, 13.958, 13.958)   -- same object shape (3 copies of "class hash x" + "class hash y" etc.)
Rec 9-10: (6.031, 13.938, 13.938)   -- same shape, different rotation  (near-identity)
Rec 5   : (38.486, 0.000, 94.779)   -- large object with degenerate Y (thin slab?)
Rec 6   : (94.625, 34.812, 41.500)
Rec 7-8 : (65.844, 12.156, 33.281)
Rec 0   : (41.489, 34.807, 94.607)  -- zone header (bbox extents)
```

Consistent with **half-extents or per-axis size** of a bounding volume attached to each record.

### VIF link fields at +0x40 and +0x48

Dumping MDL content at the referenced offsets (`0x8BD0`, `0x8C20`, `0x8C70`, ..., `0x8E50`, `0x8EA0`, `0x8EF0`) shows packed sint16/u16 data patterns (`0xFFFFFFFB 0xFFFFFE15`, `0xFF91FFA0 0x000000C0`, etc.) — classic VIF vertex / material stream content. So `+0x40` and `+0x48` are **render-side pointers** that connect each preamble record to its geometry / material batch in the MDL.

### Full placement chain (recovered)

```
PAK entry table
   |
   +-- .91E1028D entry  (placement record stream for worldzone)
   |       items[].+0x44  ------> MDL preamble record offset
   |                             (e.g. item[+0x44] = 0x9120)
   |
   +-- preceding .mdl entry
           preamble records @ stride 0x50     -- THE NEW PIECE FROM PHASE 403
              +0x00 class_hash  == .91E1028D block.flag
              +0x20 rotation quaternion (normalize to unit length)
              +0x30 size / half-extents
              +0x40/+0x48 VIF stream offsets for geometry data
```

### What This Unblocks

- **Rotation is now solved for BH/HO object MDLs.** The quaternion lives in the MDL preamble (one level deeper than the `.91E1028D`), indexed by `item[+0x44]`.
- The `.91E1028D` item provides world-space AABB; the MDL preamble provides rotation and size. Together they define the full placement: world-position from AABB center, rotation from quaternion, scale from size vector.
- The remaining placement gap is the pivot-point / origin offset (if any). Objects may rotate around their own local origin rather than AABB center — visual inspection will tell.

## Implementation Path Forward

Minimal-risk first C# pass:

1. Parse `.91E1028D` as `[pre_header(0xC) | blocks(chain) | trailer]`.
2. For each block, extract `item[+0x44]` -> MDL preamble record offset.
3. Parse MDL preamble record -> extract quaternion + size.
4. Build a placement: `translate(aabb_center) * rotate(normalized_quat) * scale(size)`.
5. Apply placement to the MDL's glTF output via a scene-graph node per placement record.

## Phase 404 — Final Unknowns Resolved

### Z_HO cross-validation (confirmed)

Running the same MDL-preamble-record extraction on Z_HO shows the same structure: 11 records at 0x50 stride in one MDL (@0x1F490), 10 records in another (@0x9D10). All normalize to unit quaternions with `qw ∈ [0.87, 1.00]`. Rotation angles range 0° to 60° across both zones.

Notably, the Z_HO MDL @0x1F490 has **byte-identical preamble records to Z_BH's MDL @0x1D990** — same class hashes, same quaternions, same sizes, same offsets. That means both zones reference the same shared object assets (common props like lampposts, benches) from a common master table.

### Block footer decoded (count × 0x1C bytes)

The `count * 0x1C` region after the items array breaks into two per-item slots:

```
slot 0 (per block, 0x1C bytes):
  +0x00 u32   counter (always 1)
  +0x04 u32   0x4B189680 sig
  +0x08 u32   0xFFFFFFFF sig
  +0x0C f32   Z-coordinate / offset
  +0x10 f32   second Z or axis value
  +0x14 u32   axis/mode (0/2/8 observed)
  +0x18 f32   height or magnitude

slot 1 (per block, 0x1C bytes):
  +0x00 f32[3] size vector (reordered from MDL preamble +0x30..+0x38)
  +0x0C u32    0x00200103 MDL-link tag
  +0x10 u32    MDL byte offset (second VIF pointer)
  +0x14 u32    packed flag (0x50XX0000 pattern)
  +0x18 u32    MDL preamble record offset OR 0xFFFFFFFF
```

Item 1's `+0x20..+0x2F` floats overlap with slot 0's content (same block, different view). So item 1 is not a fully-independent record but a READ-THROUGH view into the footer region from a different offset.

### Outliers fully categorized

```
000516F0.91E1028D (z_bh, 0x27BA8): dense float data, NO signatures.
                                    NOT placement records — likely vertex/collision/nav mesh.
0003D3A0.91E1028D (z_ho, 0x308)  : 9 MDL-preamble records at stride 0x50
                                    after a 0x10-byte header. Same format as MDL preambles.
                                    Standalone placement dump (no MDL wrapper).
00060930.91E1028D (z_ho, 0x253E8): 0x780 bytes of packed data, then 4 MDL-preamble records,
                                    then ~150KB more bulk data. Mixed content — at least partly
                                    carries preamble-style records.
```

### `.6290993B` files categorized by preceding PAK entry

```
SIE(91E1028D)-preceded .6290993B (4 files):
  0x58-stride records with 0x64967688 family tag.
  Placement-aux geometry (bbox corner data for objects referenced by the SIE).

MDL(9BCC234D)-preceded .6290993B (10 files):
  Opaque packed data, per-MDL auxiliary.
  Sizes 84 B to 1608 B.
  Likely collision hulls / lightmap data / attachment sockets. Not placement data.
```

For worldzone object placement, we only need the 4 SIE-preceded files. The 10 MDL-preceded files are rendering-enhancement data and can be ignored in the first implementation pass.

### `item[+0x48]` / FUN_00279E18 resolved

`item[+0x48]` is always a small constant: `2` or `1` for item 0, `0` for item 1. It's not per-object data. FUN_00279E18 looks it up in a global 12-byte-entry hash table and returns a byte value — almost certainly a render-state flag or category index. Irrelevant for placement; can be ignored.

### Pivot point: SIE bbox is the placement, MDL size is local extent

Cross-referencing `00026D20.91E1028D` block 0 item 0 (referencing MDL preamble record 3) shows:

```
SIE item0 bbox (world space): (0, 0, 13.938) -> (6.031, 13.938, 20.613)
                               center: (3.016, 6.969, 17.275)
                               extent: (6.031, 13.938, 6.675)

MDL rec quat vec4:  (4.097, 0.003, -0.010, 22.183)  -> unit quat after normalize
MDL rec size:       (6.032, 13.958, 13.958)
```

X and Y sizes match the SIE extent (~6 and ~14). Z differs (MDL size says 13.958, SIE extent says 6.675) — the MDL carries the **model's local extents**, and the SIE bbox is a sub-region of that (maybe a collision-volume or tile-fit area). The quat vec4 first-3 components are NOT the position (values near origin).

So the placement chain is:
- World position = SIE bbox center
- Rotation = MDL preamble record's normalized quaternion
- Scale = MDL preamble size / source-mesh extent (if needed)
- Pivot: rotation applied around the bbox center (most plausible — local origin would require an additional offset field we haven't found)

## Phase 405 — Quaternion Investigation In Depth

### Finding: 4-vec is `unit_quaternion × magnitude`

Decomposition test on rec 1 of the z_bh MDL @0x1D990:

```
raw 4-vec:            (4.097, 0.003, -0.010, 22.183)
normalized unit quat: (0.182, 0.000, -0.000, 0.983)   (qx, qy, qz, qw order)
4-vec magnitude:      22.56
unit_quat × 22.56:    (4.106, 0.000, 0.000, 22.175)   ≈ raw 4-vec ✓
```

All 4 components match within rounding. The storage format is a unit quaternion multiplied by a scalar.

### Magnitude meaning: identity-matches-size-diagonal, otherwise unclear

```
rec  angle   qmag    smag    rotated-AABB-diag   qmag/smag   qmag/aabb-diag
0    13.2°   142.38  109.01  129.81              1.306       1.097
1    20.9°   22.56   20.64   26.20               1.093       0.861
5    40.4°   180.91  102.29  102.57              1.769       1.764
6    46.0°   107.91  109.03  153.95              0.990       0.701
7    59.7°   84.21   74.77   102.18              1.126       0.824
9    0.2°    20.61   20.61   20.67               1.000       0.997
10   0.2°    20.63   20.63   20.72               1.000       0.996
```

At identity rotation (recs 9, 10), `qmag == smag` **exactly**. For non-identity rotations, `qmag` varies unpredictably vs both `smag` and the rotated AABB diagonal. No clean relationship like `size × cos(θ/2)`, `size × sin(θ/2)`, or any standard formula matches across all records.

### Best explanation

The magnitude is **build-tool-assigned scalar data stored with the quaternion**. It is *probably* a bounding-sphere radius or LOD metric that equals the size diagonal when rotation is identity and drifts from that under rotation. The engine almost certainly normalizes the 4-vec at runtime to recover the unit quaternion. The magnitude may be independently read by culling / LOD code but is not needed for placement math.

### For implementation

```csharp
// Read 4 floats at rec+0x20..+0x2C
float qx = ReadF32(rec + 0x20);
float qy = ReadF32(rec + 0x24);
float qz = ReadF32(rec + 0x28);
float qw = ReadF32(rec + 0x2C);
// Normalize to unit quaternion
float mag = MathF.Sqrt(qx*qx + qy*qy + qz*qz + qw*qw);
Quaternion rotation = new Quaternion(qx/mag, qy/mag, qz/mag, qw/mag);
// Apply rotation; the original magnitude is not needed for placement
```

### What still needs visual verification

1. **Sign conventions of the quaternion.** PS2 engines may use LH or RH coordinate systems, and the rotation axis may need a sign flip when converting to glTF's RH Y-up. The unit-quaternion interpretation is correct; only the conversion to the output coordinate system is untested.

2. **Pivot point.** Whether rotation is applied around the SIE bbox center (simple translate+rotate+scale) or around an implicit local origin offset. The SIE bbox center is the most plausible pivot given the available data; empirical adjustment may be needed.

3. **Hash resolution.** Object class hashes don't resolve against the 20,547-name QbKey dictionary. These are likely build-tool-assigned IDs, not string CRC32 hashes. Not needed for placement math — only used for grouping/linking.

## All Probes Complete

The `.91E1028D` placement format is now end-to-end decoded. The quaternion investigation confirms the 4-vec is a unit quaternion times a scalar magnitude; normalization yields the rotation. Remaining open items are either non-blocking (auxiliary files, outlier variants that don't carry placement data) or empirical (sign conventions, pivot offsets) that can only be settled through visual rendering. Ready for C# implementation.
