# THAW worldzone MDL (PS2)

Level geometry format used by *Tony Hawk's American Wasteland* on PlayStation 2.
One file per worldzone — streets, rooftops, interiors — packed inside
`z_<zone>.pak.ps2`. Representative file used throughout this doc:
`z_bh.pak.ps2 :: 003B1940.mdl` (Beverly Hills zone).

- **Container**: Neversoft PAK archive; one MDL entry per zone.
- **Entry type hash**: `0x7EA7357B` (QbKey(".mdl") hashed for the level-MDL class).
- **Cross-platform twin**: THAW Xbox/PC ships `.mdl.xbx` / `.mdl.wpc`, a
  different format handled by `XbxSceneFile`/`ThawSceneFile`. This doc covers
  the PS2 sibling only.
- **Source in this repo**:
  - Parser: [`Ps2GeomFile.ParsePakMdl`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomFile.cs),
    [`Ps2MdlPreamble`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2MdlPreamble.cs),
    [`Ps2GeomVifVertexDecoder`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomVifVertexDecoder.cs).
  - Engine decomp: [`tools/ghidra/thaw-ps2/output/phase410..424_*.c`](../../tools/ghidra/thaw-ps2/output/).
  - Raw captures: `TestOutput/mdl_ground_truth/` (disk bytes + EE-RAM snapshot at base 0x0155FA00).

> **Status header.** The runtime behaviour of this format has been decompiled
> end-to-end: PAK dispatch → `FUN_0025D488` → `FUN_00255BD8` → WorldZone factory
> → resource-manager handle → `FUN_001D4248` → `FUN_001CFB58`. The relocation
> arithmetic is empirically verified against a RAM snapshot. Several on-disk
> preamble fields (centre, radius, bbox, flags) were named by pattern matching
> validated against rendered output, not by decomp; those are flagged below.

---

## 1. File identity

The level MDL **has no magic number**. The first u32 is a scrambled-looking
value that differs file-to-file (`0xBFDD1111` for z_bh). File identity is
established by the enclosing PAK entry's type-hash field, which equals
`0x7EA7357B` for level MDLs.

In this parser we distinguish level MDLs from object MDLs after parsing, using
a structural rule (see [`Ps2GeomFile.IsLevelMdl`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomFile.cs)):

- **Object MDL** (`0x9BCC234D`, cars and similar): a bone table follows a
  `CDCDCDCD` sentinel, and there are ~5–15 preamble records.
- **Level MDL** (`0x7EA7357B`): no sentinel / no bone table, and thousands of
  preamble records (5,649 for z_bh).

### 1.1 Resource-class mapping

The engine dispatches on the PAK entry's type hash via
[`FUN_0025D260`](../../tools/ghidra/thaw-ps2/output/phase424_upstream.c)
(decompiled in phase 424), which is a `switch(type_hash)` that returns a
resource-class index. Level MDL gets **case 3**, distinct from object MDL
(case 6) and animations (`.ska` = case 6 too):

| Case | Type hash(es) | Role |
|---:|---|---|
| 2 | `0x689028A5`, `0xDAD5E950` | (unmapped, likely misc asset) |
| 3 | `0x7EA7357B` | **Level MDL (worldzone)** |
| 4 | `0x2B0A3095`, `0x8BFA5E8E` | (unmapped) |
| 5 | `0x72A6D78C` | (unmapped) |
| 6 | `0x9BCC234D`, `0x9DE9087F`, `0x745DCD45` (`.ska`) | **Object MDL + animation** |
| 7 | `0x7330095C` | (unmapped) |
| 10 | `0x2F1A6A09` | (unmapped) |
| 12 | `0x4BC1E85E` | (unmapped) |
| 13 | `0x49875607` | (unmapped) |
| 14 | `0x5D796624`, `0xA7F505C4` | (unmapped) |
| 15 | `0x91E1028D` | (unmapped) |
| 16 | `0xFF2D0E91` | (unmapped) |
| 17 | `0x199F902B` | (unmapped) |
| 18 | `0x7E1ABC70` | (unmapped) |

The class index drives the factory dispatch in
[`FUN_00255578`](../../tools/ghidra/thaw-ps2/output/phase423_resource_creation.c#L161).
For case 3, it allocates a `0x1C`-byte WorldZone outer object via
`FUN_0011C4B0(DAT_004985F0, 0x1C, 1, 0)`, runs the base constructor
[`FUN_00257D90`](../../tools/ghidra/thaw-ps2/output/phase422c_setup_path.c#L10),
and stamps vtable `0x004CAB10` at `outer+0x18` (see
[`FUN_00258B28`](../../tools/ghidra/thaw-ps2/output/phase423_resource_creation.c#L252)).
`FUN_00258B28` has exactly 1 caller (`FUN_00255578` at
[phase424_xrefs.txt](../../tools/ghidra/thaw-ps2/output/phase424_xrefs.txt)),
so this factory is dedicated to the worldzone class.

## 2. File layout (top-down)

A level MDL has three conceptual sections at known byte offsets:

```
┌────────────────────────────────────────────────────────────────────┐
│ [0 .. K)                                                           │
│   "Pre-VIF region". Not consumed by the geometry pipeline. Looks   │
│   packed (~49% zeros; 115/256 unique byte values). Probably GS     │
│   register defaults, VU microcode, or palette preload — used by    │
│   an OTHER subsystem. See Open Questions #1.                       │
├────────────────────────────────────────────────────────────────────┤
│ [K .. first_preamble)                                              │
│   Scene-graph skeleton. A tree of 0x50-byte CGeomNode records      │
│   linked by file-relative offsets. file[K] is the u32 offset from  │
│   K to the tree root. Walked by FUN_001CFB58 at load.              │
│                                                                    │
│   Also holds the VIF leaf chunks: each leaf is a 16-byte DMA+VIF   │
│   preamble followed by QWC×16 bytes of UNPACK stream. Chunks are   │
│   addressed by the preamble table's Field40.                       │
├────────────────────────────────────────────────────────────────────┤
│ [first_preamble .. end)                                            │
│   Preamble table. A flat array of 0x50-byte per-node records that  │
│   the scene graph references via a 1-based index. Each entry       │
│   carries a class hash, centre, radius, bbox, flags, and Field40.  │
│   For leaf entries, Field40 = VIF chunk offset (relative to K).    │
└────────────────────────────────────────────────────────────────────┘
```

`K` is per-file. Observed values across five test MDLs: `0x110`, `0xFF0`,
`0x1040`, `0x10A0`, `0x10B0`. Our parser derives `K` heuristically by scanning
the low file region for the first `OFFSET(0) + STCYCL(1,1)` VIF pair and
subtracting the smallest leaf's `Field40`
([`Ps2GeomFile.TryDeriveDataSectionOffset`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomFile.cs)).
The engine path for obtaining `K` is still partly unresolved; see
§6.3 "The relocation base" below.

## 3. Preamble records

Each preamble record is exactly `0x50` bytes. Records are contiguous in
`[first_preamble..end)`. A record is identified by the constant signature
`0x4B189680` at record+0x18.

Fields with **decomp evidence** are cited against `phase417_cgeomnode_walker.c`
(the runtime tree walker) or the resource-creation path. Fields marked
**empirical** were reverse-engineered by pattern matching across z_bh's 5,649
records validated against the rendered output.

| Offset | Type | Name | Evidence | Notes |
|---:|---|---|---|---|
| +0x00 | u32 | `ClassHash` | empirical | leaf #0 = `0xEC2518B8`; 1,671 distinct values across 3,977 z_bh leaves. |
| +0x04 | u32 | `Field04` | empirical | Mostly zero; sometimes `0x03000300` packed bitfield. Not yet decoded. |
| +0x08 | u32 | `Field08` | empirical | `0xFFFFFFFF` on 3,972/3,977 leaves on disk. **Cleared to 0 at load** (see §5). |
| +0x0C | u32 | `Field0C` | empirical | `0x80808080` constant on disk. **Overwritten with a runtime pool pointer at load**. Looks like a vertex-colour scale default. |
| +0x10 | u32 | `Field10` | empirical | `0xFF00FF00` constant on all 3,977 z_bh leaves. Probably a GS register default (`FBA_1` / `TEST_1`?). |
| +0x14 | u32 | `Field14` | empirical | Small int, 72 distinct values in z_bh. **Meaning unknown**, see Open Questions #2. |
| +0x18 | u32 | `Signature` | [phase417:260](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L260) | Fixed constant `0x4B189680`. Used to anchor parsing. |
| +0x1C | u32 | `Flags` (runtime) | [phase417:73,84,90,…](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L73) | Runtime flag bitfield. On disk this is `0xFFFFFFFF` and is **cleared to a real flag value at load**. |
| +0x20 | f32 | `CentreX` | empirical | Leaf bounding-sphere centre (world space). For object MDLs this position overloads as a quaternion `qx`. |
| +0x24 | f32 | `CentreY` | empirical | |
| +0x28 | f32 | `CentreZ` | empirical | |
| +0x2C | f32 | `Radius` | empirical | Bounding-sphere radius. Overloads as quaternion `qw` for object MDLs. |
| +0x30 | f32 | `SizeX` | empirical | Half-extent of AABB along X. |
| +0x34 | f32 | `SizeY` | empirical | |
| +0x38 | f32 | `SizeZ` | empirical | |
| +0x3C | u32 | `NodeFlags` (on-disk) | [`Ps2MdlPreamble.cs:215`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2MdlPreamble.cs#L215) | `LEAF` (bit 1), `OBJECT` (bit 2), `ZPUSH0..3`, `NOSHADOW` (bit 8), `BILLBOARD`. Names lifted from THUG `Sample/thug/Code/Gfx/NGPS/NX/scene.cpp` NODEFLAG_* enum; not independently verified against THAW flag consumers. |
| +0x40 | u32 | `Field40` | [`Ps2MdlPreamble.cs:216`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2MdlPreamble.cs#L216) | For leaves: byte offset (relative to `K`) of the VIF chunk for this leaf. For internal tree nodes: byte offset of the child record. **Rebased to absolute pointer at load**. |
| +0x44 | u32 | `Field44` | empirical | Packed `0x50XX0000` pattern with 256 distinct low-byte values in z_bh. Unknown. |
| +0x48 | u32 | `Field48` | [`Ps2MdlPreamble.cs:217`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2MdlPreamble.cs#L217) | `0xFFFFFFFF` sentinel (null) for 41% of z_bh records; otherwise a byte offset pointing back into the preamble table — interpreted as a sibling link. **Rebased to absolute pointer at load**. |
| +0x4C | u32 | `MaterialGroup` | empirical | Small int 1–19. Groups 1–8 correlate 100% with GS ALPHA=0x0A (opaque); groups 9–19 correlate with 0x44 (blend), 0x48 (add), 0x42 (sub). **Overwritten with a runtime pointer at load**, so the disk value is only observable before relocation. |

### 3.1 `LEAF` bit and Field40

For records with bit 1 of `NodeFlags` set (`LEAF`), `Field40` is the absolute
offset (relative to `K`) of this leaf's VIF chunk. The chunk layout is:

```
K + Field40:
  +0x00   DMA source-chain tag (8 bytes)
            bytes 0..1: QWC (quadword count)
            byte 6:     ID = 6 (ref tag)
  +0x08   VIF OFFSET opcode        = 0x02000000
  +0x0C   VIF STCYCL opcode (CL=1, WL=1) = 0x01000101
  +0x10   VIF UNPACK stream — positions, UVs, normals, colours, GS
          register writes via DIRECT — total size = QWC * 16 bytes.
```

The VIF stream is fed to [`Ps2GeomVifVertexDecoder`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomVifVertexDecoder.cs),
which emits interleaved vertex data the same way as for .geom.ps2 files.

At runtime the engine does **not** re-submit the on-disk DMA tag; it reads
QWC and the inline OFFSET/STCYCL codes to rebuild a fresh DMA chain. For
static conversion we simply slice `[K + Field40 + 8, K + Field40 + 16 + QWC*16)`
and feed it to the VIF decoder.

### 3.2 Multi-batch leaves

~4% of z_bh leaves pack 2 or more independent VIF batches into a single
chunk, each batch ending with its own `MSCAL` microprogram call. Our parser
splits these on `MSCAL` boundaries and emits one glTF mesh per batch so that
per-batch GS contexts (TEX0, ALPHA) are preserved. See
[`Ps2GeomVifVertexDecoder.ExtractBatchesFromVif`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomVifVertexDecoder.cs).

### 3.3 Billboard leaves (Format B)

A further ~5% of leaves encode axis-aligned billboard parameters via
`V4_32 float` without STMOD. For these we synthesise an XY quad at the
anchor at the recorded `Size`. See
[`Ps2GeomVifVertexDecoder.ExtractBillboardFromVif`](../../src/NeversoftMultitool/Core/Formats/Mesh/Ps2Scene/Geom/Ps2GeomVifVertexDecoder.cs).

## 4. Scene-graph skeleton `[K .. first_preamble)`

This region starts at `K` with a u32 offset-to-root:

```
file[K .. K+4]     u32  root_node_offset   (e.g. 0x002A5F20 for z_bh)
file[K .. K+4+root_node_offset]  ...VIF leaf chunks + nested tree nodes...
```

The root CGeomNode record sits at `K + root_node_offset`. Its fields
(decompiled against the runtime walker, see §5):

- `+0x1C` (**runtime** flags) — for the root, observed `0x01` (`ACTIVE`).
- `+0x20` — offset to first child record (relative to `K`).
- `+0x28` — offset to sibling (or −1).
- `+0x2C` — 1-based index into the preamble table, identifying which
  per-node instance-data record this skeleton node corresponds to.
- `+0x3C`, `+0x44` — additional runtime pointers (GIF-tag register list,
  matrix/handle), populated at load from the preamble record.

**Important**: the on-disk skeleton and the runtime CGeomNode share the
`0x50` stride but are **not the same record**. The skeleton uses offset
fields; the runtime tree uses pointer fields at mostly-overlapping
offsets. What happens at load is in-place offset→pointer rewriting plus
a fold of data from the corresponding preamble record. See §5.

## 5. Runtime layout after load

`FUN_001D4248` does a 2-hop on the skeleton root pointer and hands the
result to `FUN_001CFB58`, which walks the tree recursively, rewriting
offset fields to absolute RAM pointers in place. Every read of
`*(node + N)` in the walker is listed below, with evidence lines from
[`phase417_cgeomnode_walker.c`](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c):

| Offset | Type | Role | Evidence |
|---:|---|---|---|
| +0x18 | u64 | Lookup marker, tested against `0x400200000000` | [phase417:260](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L260) |
| +0x1C | u32 | Flags bitfield — see §5.1 | [phase417:73,84,90,256,260,262,267,376](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L73) |
| +0x20 | ptr | Child (relocated `iVar22 + val`). Points at a GIF-tag register list (V4 stride at +0x26, triplet array at +0x28) | [phase417:91,98,125,132](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L91) |
| +0x24 | ptr | Auxiliary pointer (−1 = null) | [phase417:78-83](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L78) |
| +0x28 | ptr | Next sibling | [phase417:215,242,248,384](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L215) |
| +0x2C | u32 | 1-based index into preamble table at `ctx+0x10` | [phase417:107-108](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L107) |
| +0x34 | ptr | Conditional (zeroed when +0x1C bit 9 and +0x18 bit 41/42 set) | [phase417:261,265](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L261) |
| +0x38 | ptr | Complex structure, nested relocation loop | [phase417:270,316-367](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L316) |
| +0x44 | ptr | Matrix/handle struct (fields at +0x10, +0x18, +0x3C, +0x40) | [phase417:87-88,115,273](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L273) |
| +0x4C | ptr | Alternate traversal pointer | [phase417:250,254](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L250) |

### 5.1 Runtime flags (`+0x1C`)

Bits tested in the walker. Semantics for most are not pinned down; names in
parens are tentative, carried over from THUG's NODEFLAG_*.

`0x2` (`LEAF`), `0x4` (`OBJECT`), `0x8`, `0x10`, `0x200`, `0x400`, `0x800`,
`0x8000`, `0x100000`, `0x400000`, `0x800000`, `0x4000000`, `0x8000000`.

### 5.2 GIF-tag walk

The walker at
[phase417:132-210](../../tools/ghidra/thaw-ps2/output/phase417_cgeomnode_walker.c#L132)
scans the triplet register array found at `(*(node+0x20)) + 0x28` and
records pointers to **GS register codes 0x06 (TEX0_1), 0x08 (CLAMP_1),
and 0x42 (ALPHA_1)**. Crucially, **it stores the pointers without
transforming the register VALUES**. That is, nothing in this pipeline
implements a "shadow" or "decal" flag: the ALPHA byte that the artist
authored flows straight through to the GS. This rules out the worldzone
parser as the source of the opaque-shadow rendering bug; that issue lives
downstream in per-texture alpha extraction.

### 5.3 Fields cleared or overwritten at load (RAM-vs-disk diff)

Comparing the disk bytes of `003B1940.mdl` against the PCSX2 EE-RAM snapshot
`ee_mdl_at_0155FA00.bin`:

| Preamble offset | Disk value (on-disk) | RAM value (post-load) | Interpretation |
|---:|---|---|---|
| +0x08 | `0xFFFFFFFF` | `0x00000000` | Cleared |
| +0x0C | `0x80808080` | runtime pool ptr | Overwritten |
| +0x1C | `0xFFFFFFFF` | real flag bits | Decoded |
| +0x4C | small int 1-19 | runtime pool ptr | Overwritten |

All other bytes in the preamble region match disk byte-for-byte. No
decompression, decoding, or bulk rewrite happens at load time; only the
specific relocations listed here.

## 6. Relocation

### 6.1 The formula

The single rule that drives load-time relocation is:

```
runtime_ptr = base_ee + K + disk_offset
```

where `base_ee` is the RAM address the file was loaded at, `K` is the
per-file `data_section_offset` (§2), and `disk_offset` is the on-disk
byte offset found in an offset-typed field (`Field40`, `Field48`,
skeleton `+0x20/0x28`, etc.).

Empirically verified against the z_bh RAM snapshot (`base_ee = 0x0155FA00`,
`K = 0xFF0`):

- **Leaf #0's `Field40`**: disk `0x5050` → RAM `0x01565A40` = `0x0155FA00 + 0xFF0 + 0x5050` ✓
- **First preamble's `Field40`**: disk `0x2C69A0` → RAM `0x01827390` = `0x0155FA00 + 0xFF0 + 0x2C69A0` ✓

### 6.2 How the engine obtains `base_ee + K`

The relocation base is passed to `FUN_001CFB58` as its `param_2`, sourced
from `FUN_001A0360(X)` where `X = *(inner_obj + 4)` and `inner_obj` is the
resource-manager handle reachable via `outer_obj + 0x10`. The resource
handle is looked up in a per-context hash table keyed on the PAK-entry
metadata ([`FUN_002564A8`](../../tools/ghidra/thaw-ps2/output/phase422d_inner_obj.c),
[`FUN_002562F0`](../../tools/ghidra/thaw-ps2/output/phase423_resource_creation.c)).

The chain is:

```
outer (WorldZone)        +0x10 ──► inner_obj (ResourceHandle)
                                    +0x04 ──► X (stream-handle struct)
                                               +0x14 ──► base_ee + K
                                                         │
                                                         └─► passed as param_2 of FUN_001D4248
```

### 6.3 The PAK dispatcher (`FUN_0025D560`)

`FUN_0025D560` (decompiled in phase 424b, see
[phase424b_pak_dispatcher.c](../../tools/ghidra/thaw-ps2/output/phase424b_pak_dispatcher.c))
is the per-PAK iterator. It walks PAK entries via
`FUN_0025D0E8(pak)` (get-first) and `FUN_0025D0F0(entry)` (get-next), and
for each one switches on the type hash. The level-MDL branch (`0x7EA7357B`)
at lines 235-258 is:

```c
if (uVar1 == 0x7ea7357b) {
    if (DAT_0049b2e4 == 0) {
        uStack_520 = puVar9[6];                              // pak_entry word 6
    }
    else {
        if ((puVar9[7] & 1) == 0) goto LAB_0025dd64;         // skip if bit 0 unset
        uStack_520 = puVar9[6];
    }
    uStack_518 = puVar9[7] & 1;                              // bit 0 of word 7
    uStack_51c = ((int)puVar9[7] >> 4 ^ 1U) & 1;             // bit 4 of word 7, inverted
    uStack_514 = (int)puVar9[7] >> 3 & 1;                    // bit 3 of word 7
    if (uStack_514 != 0) { uStack_51c = 0; }
    uVar4 = FUN_0025d488(uVar3, 0x7ea7357b, auStack_5b0);    // load + parse
    *puStack_6c = (int)uVar4;                                // out parameter
    if ((puVar9[7] & 0x20) == 0) {
        FUN_0015ab10(uVar4, 0x4cc130);                       // register with default name
    }
    else {
        uVar4 = FUN_0025d118(uVar3);
        FUN_0015ab10(*puStack_6c, uVar4);                    // register with PAK-entry name
    }
}
```

**PAK-entry struct fields used by the level-MDL branch** (`puVar9` is one
PAK-entry record, accessed by word index):

| Word | Used for | Notes |
|---:|---|---|
| `puVar9[3]` | resource lookup key (passed to FUN_0025D488 → FUN_00255BD8) | Hash of the entry's filename |
| `puVar9[6]` | resource-handle key for the inner_obj that gets stored at `outer+0x10` | Stored as `uStack_520` and passed as FUN_00255BD8's `param_4` |
| `puVar9[7]` | 32-bit flag bitfield — bits 0/3/4/5 drive load behaviour | bit 0 = "must load", bit 3 = "X", bit 4 = "Y", bit 5 = "use named registration" |

`FUN_0025D488` is then called with `(pak_entry, type_hash, &params_struct)`.
Inside it, [`FUN_0025D150`](../../tools/ghidra/thaw-ps2/output/phase422c_setup_path.c#L199)
computes `file_data_addr = pak_entry + pak_entry[1]` (entry word 1 = file
offset). That file_data_addr is passed onward through `FUN_00255BD8` and
ultimately reaches `FUN_001D4248`'s `param_1`.

### 6.4 What's at outer+0x10? Where does X come from?

The remaining piece — the precise origin of `X+0x14 = base_ee + K` — is
*partly* answered by the dispatcher trace but not fully closed:

- `FUN_00255BD8` calls `FUN_002564A8(ctx, pak_entry[6], 1)` and stores the
  result at `outer+0x10` (via [`FUN_00258488`](../../tools/ghidra/thaw-ps2/output/phase422d_inner_obj.c#L291)).
- `FUN_002564A8` is a hash-table lookup in `ctx+0x28` keyed on
  `pak_entry[6]`. It returns either an existing handle or, on miss, calls
  `FUN_002562F0` (also a hash-table lookup, in a secondary table).
- The handle at `outer+0x10` therefore points at **a previously-loaded
  resource** identified by `pak_entry[6]`. That resource's `+0x04` field
  is `X` — itself a stream-handle struct whose `+0x14` is the relocation
  base passed to `FUN_001D4248`.

So `X+0x14` is populated **when the *upstream* resource was loaded**, not
during the level-MDL parse. The level MDL borrows the relocation context
from a sibling resource that the same PAK previously registered. Locating
the exact write to `X+0x14` would require:

1. Identifying which PAK entry in z_bh has hash equal to z_bh's MDL
   `puVar9[6]`, and
2. Decompiling the load path for that resource type.

This level of detail is **not needed for geometry conversion** — our
heuristic `K` derivation in §2 produces the correct value across all
tested MDL files. It is documented here for completeness so the next
investigator can pick it up if needed.

### 6.5 WorldZone vtable

WorldZone vtable lives at `0x004CAB10`. Each slot is an 8-byte
`(adjust_short, pad, fn_ptr)` triple. Most slots inherit from a base
class; the ones I've identified, with addresses verified by re-counting
the byte dump in [phase422b_vtable_bytes.txt](../../tools/ghidra/thaw-ps2/output/phase422b_vtable_bytes.txt):

| `*(vtable+N)` | Address | Behaviour |
|---:|---|---|
| +0x0C | `0x00258F48` | `return 3` — resource-class ID accessor |
| **+0x14** | **`0x00258478`** | **`return *(this+4)` — file-data getter** ★ |
| +0x1C | `0x00258670` | (not yet decompiled) |
| +0x24 | `0x002584A0` | `return *(this+0)` — checksum accessor |
| **+0x2C** | **`0x00258F58`** | thunks to `FUN_00257DD0` — **virtual destructor** (resets vtable to base `&DAT_004CA830`, conditionally calls `FUN_0011C998` to free) |
| +0x34 | `0x00258B60` | "load by file name" — formats a path via `FUN_00157040`, opens it, calls `FUN_00257E30` (used by `FUN_00255E58`, the load-from-disk path) |
| **+0x3C** | **`0x00258C18`** | **load/tick entry** — calls vtable+0x8C (optional copy) then `FUN_00257FA8` |
| +0x44 | `0x00258EC8` | (not yet decompiled) |
| +0x4C | `0x00258F30` | `return 0` |
| +0x54 | `0x002580D8` | (not yet decompiled) |
| +0x5C | `0x00258F38` | (not yet decompiled) |
| **+0x64** | **`0x00258480`** | **`*(this+4) = arg2` — file-data setter** ★ |
| +0x6C | `0x00258650` | (not yet decompiled) |
| +0x74 | `0x002580F0` | (not yet decompiled) |
| **+0x7C** | **`0x00258CA0`** | **the parse dispatcher** — calls `FUN_001571D0(workspace, file_data, X+4, ...)` |
| +0x84 | `0x002581A8` | (not yet decompiled) |
| **+0x8C** | **`0x00258218`** | **memcpy `file_data` into a freshly allocated private buffer**, store at `this+8`, return buffer ptr |
| +0x94 | `0x002582F8` | (not yet decompiled) |
| +0x9C | `0x00258D38` | "ConfigStore" — copies 4 config hashes (`0x9E6EA452`/`0xB5BD28D8`/`0x3D559E7D`/`0x417149C8`) into a sub-buffer |
| +0xA4 | `0x00258F78` | `return 0xA4` (likely `sizeof(WorldZone)`) |
| +0xAC | `0x00258380` | set flag bit 24 in `this+0x14` |

**Earlier docs in this file mis-labelled `vtable+0x3C` as the destructor
and pointed it at `FUN_00257DD0`. That was wrong; the destructor lives
at `vtable+0x2C`. Phase 425 corrected this by re-counting the dump.**

The actual file-data entry chain is:

```
FUN_00255BD8 line 147 dispatches vtable+0x3C with file_data
  → FUN_00258C18(this, file_data, ...)
      ├─ if (param_4 != 0):  vtable+0x8C → FUN_00258218 (memcpy file_data into private buffer)
      └─ FUN_00257FA8(this, file_data_or_buffer, ...)
            ├─ vtable+0xAC → FUN_00258380 (set flag bit 24)
            ├─ vtable+0x9C → FUN_00258D38 (copy 4 config hashes)
            └─ vtable+0x7C → FUN_00258CA0(this, file_data, ...)
                  ├─ FUN_00258490 → returns *(this+0x10) = inner_obj
                  ├─ vtable+0x14 on inner_obj → FUN_00258478 → returns *(inner_obj+4) = X-buffer
                  └─ FUN_001571D0(workspace, file_data, X-buffer, ...)
                        → FUN_00197EB0(file_data, X-buffer, ...)
                            ├─ FUN_001A0360(X-buffer) → *(X-buffer + 0x14) = base_ee + K
                            └─ FUN_001D4248(file_data, base_ee + K, ...) → CGeomNode walker
```

So the relocation base comes from `*(*(outer_obj+0x10) + 4) + 0x14`,
i.e., a 3-hop dereference starting from the WorldZone outer obj. The
buffer at `inner_obj+4` is owned by an upstream resource (not the
worldzone itself), and its `+0x14` was populated when *that* resource
was loaded.

For static conversion this chain doesn't matter — the heuristic K
derivation in §2 is sufficient. But it's now end-to-end traced.

## 7. Object MDLs vs. level MDLs

THAW PS2 uses the same 0x50-byte preamble record for the car MDLs
(`0x9BCC234D`), with a different interpretation of a few fields:

| Offset | Object MDL | Level MDL |
|---|---|---|
| +0x20..0x2C | Quaternion (qx, qy, qz, qw) — orientation | Centre(f32×3) + Radius(f32) |
| +0x30..0x38 | Size(f32×3) | Size(f32×3) |
| +0x40 | Child-record offset for internal nodes | VIF chunk offset for leaves |

Object MDLs additionally carry a `CDCDCDCD` sentinel and a post-sentinel
bone table (`ThawPs2Skin`). Level MDLs never carry either.

## 8. Known good test data

Small in-repo (`Sample/Builds/`):

- *None* — THAW PAK files exceed the sample-data size cap. Run against
  full game data. Representative file: `z_bh.pak.ps2 :: 003B1940.mdl`.

Ground-truth captures in `TestOutput/mdl_ground_truth/`:

- `003B1940.mdl` — raw disk bytes, 8.4 MB.
- `ee_mdl_at_0155FA00.bin` — PCSX2 EE RAM snapshot of the same MDL at
  base `0x0155FA00`, taken after the WorldZone parse completes.

All 5,649 preamble records in z_bh round-trip through
`Ps2MdlPreamble.TryParse`; 3,977 of them have the `LEAF` bit and all of
those decode to one or more VIF batches.

## 9. Open questions

1. **Pre-VIF region `[0..K)`.** Looks packed; not consumed by the
   geometry pipeline. Candidates: VU microcode, GS register defaults,
   shared palette/CLUT pre-load. Finding its consumer would require
   tracing who else reads `file_data + 0` beyond the MDL parser.

2. **Preamble `+0x04`, `+0x14`, `+0x44`.** Have structure
   (limited cardinality, distinctive packing) but no known consumer.

3. **Four config hashes at outer+0x94..0xA0.** `0x9E6EA452 /
   0xB5BD28D8 / 0x3D559E7D / 0x417149C8` ([phase412:96-106](../../tools/ghidra/thaw-ps2/output/phase412_level_mdl_parse.c#L96)).
   Passed as `FUN_00197EB0`'s flag bits; no branch in the decomped
   pipeline tests them.

4. **Runtime flag bits beyond LEAF/OBJECT.** 13 bit positions tested in
   `FUN_001CFB58`, named only tentatively via THUG's NODEFLAG_*.

5. **Where `X+0x14 = base_ee + K` is written.** §6.3.

## 10. Evidence trail

- Decomp phases 410–424: [tools/ghidra/thaw-ps2/output/phase41*.c](../../tools/ghidra/thaw-ps2/output/) and
  [phase42*.c](../../tools/ghidra/thaw-ps2/output/).
- Running summary with line-number citations: [phase421_verified_fields.md](../../tools/ghidra/thaw-ps2/output/phase421_verified_fields.md).
- Earlier exploratory notes (superseded): [phase41x_summary.md](../../tools/ghidra/thaw-ps2/output/phase41x_summary.md).
- Diagnostic scripts: [tools/diagnostics/thaw_mdl_bone_probe.py](../../tools/diagnostics/thaw_mdl_bone_probe.py)
  (bone-relative geometry probe), [tools/diagnostics/thaw_pcsx2_runbook.md](../../tools/diagnostics/thaw_pcsx2_runbook.md)
  (how to collect an EE-RAM snapshot).

## 11. Changelog

- **2026-04-23**: first draft. Covers runtime path end-to-end; on-disk
  preamble fields are a mix of decomp-verified and empirical. Phase 424
  added the resource-class ID table (§1.1) and the PAK-dispatcher trace
  (§6.3-6.5). Outstanding gap is `FUN_00257DD0` — the actual file-data
  parse entry called from WorldZone vtable+0x3C, which would resolve
  precisely how `X+0x14` is populated.
