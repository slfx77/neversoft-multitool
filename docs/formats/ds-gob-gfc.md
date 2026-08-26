# Vicarious Visions GOB / GFC container (Nintendo DS)

The three Tony Hawk DS carts keep essentially all of their content in one pair of
files reached through the Nitro filesystem — a `.gob` data blob and a `.gfc` index:

| Build | index | data |
| --- | --- | --- |
| Tony Hawk's American Sk8land (2005) | `vvobj/generated/gob/main.gfc` | `vvobj/generated/gob/main.gob` |
| Tony Hawk's Downhill Jam (2006) | `vvobj/generated/gob/main.gfc` | `vvobj/generated/gob/main.gob` |
| Tony Hawk's Proving Ground (2007) | `gob/mainUS.gfc` | `gob/mainUS.gob` |

Implementation: `Core/Formats/Gob/` (`GobIndex`, `GobCodec`, `GobArchive`,
`GobNames`) plus `Core/Formats/ArchiveFs/GobArchiveFileSystem.cs`. Pinned by
`GobArchiveTests`, `GobArchiveFileSystemTests`, and `GobNamesTests`.

## Index layout

Every field is **big-endian**, and the layout consumes the file exactly:

```
u32 magic = 0x00008008
u32 gobLength                        // equals the .gob's length, exactly
u32 chunkCount
u32 fileCount
chunkCount x { u32 storedSize, u32 offset, u16 =0, u16 nextChunk,
               u8 codec, u8 =0, u16 =0 }
chunkCount x   u32 checksum          // adler32 of the STORED bytes, seeded 0
fileCount  x { u32 nameCrc, u32 uncompressedSize, u32 firstChunk }
```

so `gfcLength == 16 + 20*chunkCount + 12*fileCount`.

- `nextChunk == 0x7FFF` terminates a chain; `firstChunk == 0xFFFFFFFF` is a file
  with no chunks at all (7 in Downhill Jam, 8 in Proving Ground).
- The three reserved fields are zero in all 41,643 corpus records, and the parser
  requires that — a look-alike file should error with a reason rather than
  garbage-parse.
- Chains **partition** the chunk array: no cycles, no chunk owned by two files,
  every chunk reached.

A logical file is rebuilt by walking `firstChunk → nextChunk`, decoding each
chunk, and concatenating. The result is exactly `uncompressedSize` for **every**
non-empty file in all three carts — 14,606, 4,650 and 5,657 of them.

## Codecs

The byte at record+12 is an ASCII character:

| Byte | Meaning | Sk8land | DHJ | PG |
| --- | --- | --- | --- | --- |
| `'0'` (0x30) | stored verbatim | 14,179 | 8,495 | 7,374 |
| `'z'` (0x7A) | `78 9C` + raw deflate + 4-byte BE trailer | 4,361 | 3,592 | 3,642 |

**The compressed framing is not quite zlib: its trailer is an Adler-32 seeded
with 0 instead of 1.** A stock `ZLibStream` (or Python `zlib.decompress`)
inflates the body correctly and then reports `incorrect data check`. An earlier
pass at this format read that as evidence that the stored length must be padded
or must run to the next record's offset; it does not. The length is exact, and
only the seed differs. `GobCodec` therefore inflates `stored[2..^4]` with
`DeflateStream` and verifies the trailer itself.

The same seed-0 Adler is the index's per-chunk checksum, there computed over the
**stored** (still-compressed) bytes — 41,643/41,643 exact — so every read is
integrity-checked for free.

`'0'` is *stored*, not a second compression scheme. Some stored chunks begin
`10 00 21 43`, which is a GBA-BIOS LZ77 header — that is the *content* (a DS
asset compressed in its own right), not a container codec.

## Deduplication

Chunk bytes may be shared: Sk8land's 18,540 chunks resolve to 6,907 distinct
`(offset, size)` blobs, so 11,633 chunks reuse one. Downhill Jam and Proving
Ground dedup nothing.

(An earlier note recorded `entryCount > uniqueCount` as evidence of this
dedup. Both halves were wrong: `entryCount` is the chunk count and `uniqueCount`
is the file count. Dedup is a separate property, and only Sk8land has it.)

## Corpus

| Cart | chunks | files | `.gob` bytes | rebuilt bytes | distinct blobs | named |
| --- | --- | --- | --- | --- | --- | --- |
| Sk8land | 18,540 | 14,606 | 21,411,156 | 81,141,680 | 6,907 | 564 |
| Downhill Jam | 12,087 | 4,657 | 60,741,360 | 71,658,474 | 12,087 | 1,010 |
| Proving Ground | 11,016 | 5,665 | 48,392,876 | 55,721,067 | 11,016 | 777 |

## Names

`nameCrc` is a **standard CRC-32** (the zlib polynomial, init/final `0xFFFFFFFF`)
of the name **lowercased**, spelled with a leading `.\`:

```
.\sfx\npcs\mullen_talk.swav        .\Level_Downtown_Collision.prp
.\BkgrndHollywood.meta_bin         .\sound_ui.xml
```

This is *not* the Neversoft CRC behind `QbKey`, so the proven pairs live in their
own resource, `Core/Formats/Gob/GobNames.txt` (1,724 entries). They were
harvested from the carts' own ARM9, ARM9 overlays, and decompressed GOB content,
and a candidate was accepted only when it re-hashed to a file's own key —
`GobNamesTests` re-checks every stored pair. Candidates were additionally
restricted to filename-shaped strings, because a 32-bit key over a large
candidate pool collides by chance: the unrestricted pool of ~1.5M strings carried
~26 expected coincidences, the filtered pool carries 0.05.

A second pass runs bare STEMS through the loader's own templates
(`.\<stem>.texture.bin`, `.\<stem>SMK.bin`, …) — the first pass could not, because
it only accepts candidates that already end in an extension. Restricted to
deliberate string literals and XML text (~9,100 stems), that adds 16 names with a
control template scoring 0 against 0.10 expected.

Extraction names an unresolved file after its key (`3f2a10c8.bin`), which is
stable across runs and across carts.

### Rejected: harvesting names from other builds' filenames

Hashing every filename staged under `Sample/Builds` (the `harvest_disc_names`
trick) grows the candidate pool to ~400k, worth ~6.6 expected chance collisions
on its own, and what it "resolved" was console-only paths a DS cart cannot
contain (`.\Veh_Scooter.usg.xbx`, `DATAP\pak\...\000F7B40.ska`,
`fonts\s2goal.dds`). Noise, no signal — these carts spell their own names in
their own ROMs.

### REFUTED: recovering the `.\%08x.<kind>.bin` names by CRC meet-in-the-middle

Most files are named by the loader's own `sprintf` templates, which are spelled
in ARM9:

```
.\%08x.%s.bin      .\%08x.%08x.%s.bin   .\%08x.textureinfo.bin
.\%s.texture.bin   .\%sSMK.bin          .\%s%sSMK.bin
```

Those look recoverable: CRC-32 is affine, so the 8 hex digits split 4+4 and meet
in the middle. Tabulate `D(x1) = crc32(zeros_L, crc32(".\\" + x1)) ^ crc32(zeros_L, 0)`
and `G(x2) = crc32(x2 + suffix, 0)` (65,536 each), and a target `t` has a
preimage iff `t ^ G(x2) ∈ {D(x1)}`. It runs in ~40 s per suffix and appears to
work spectacularly — six suffixes "name" **99.8%** of every cart.

**It is noise.** The 8-hex search space is 16⁸ = 2³², exactly the size of the
CRC-32 codomain, so each target has ≈1 preimage by chance and
P(≥1 hit) = 1 − e⁻¹ = **63.2%** per suffix. Two controls settle it:

- a suffix that *cannot exist*, `.qqzzxnotreal.bin`, scores **9,087** on Sk8land —
  statistically identical to `.texture.bin`'s **9,327** (63.9% of 14,606);
- cross-template corroboration — requiring an id to own both `.texture.bin` and
  `.textureinfo.bin`, which would cut the expected false-positive count to ~0.05
  — yields **0**, exactly matching its own control.

A preimage here therefore carries no information, and shipping it would have put
a plausible, entirely wrong name on ~14,000 files per cart. Do not re-attempt it
without an independent oracle. `.\%08x.%08x.%s.bin` (two unknown u32s) was never
in reach at all.

## Model sets — where the ids actually are

That refutation killed the *unconstrained* search. The independent oracle it
asked for turned out to be sitting in the cart: **the code that asks for a file
has to hold its id, and it holds it as a plain little-endian u32.**

Hashing `.\<id>.<kind>.bin` over a candidate pool and testing membership in the
container's own key set separates the two pools completely:

| id pool | distinct | expected by chance | `textureinfo` | `collisionspheres` | controls |
| --- | --- | --- | --- | --- | --- |
| GOB content, LE u32 | 2,347,360 | 8.0 | 6 | 8 | 10, 14, 6 |
| GOB content, BE u32 | 2,347,360 | 8.0 | 11 | 10 | 8, 11, 3 |
| **ARM9 + overlay9, LE u32** | **114,276** | **0.4** | **198** | **182** | **1, 0, 0** |

The container rows sit *at* chance. The code row is ~500x chance. And the method
carries its own positive control: `texture` scores **1,096** against the GOB
pool, because a texture bank record does store its `pixelId` as a plain u32 —
the one id that is in the container is the one the method finds there.

### The templates, and what they name

Read from each cart's own ARM9, never assumed from a sibling — the three games
do not agree:

```
.\%08x.%s.bin           textureinfo, collisionspheres, pvs, animation
.\%08x.%08x.%s.bin      geometry, animation
.\%s.texture.bin        a bank naming its own texel blob
.\%sSMK.bin  .\%s%sSMK.bin
```

A model set is keyed by one id (`idA`); its geometry and animation take a second
(`idB`). Sk8land spells animation clips **indexed** as
`.\<idA>.<idB>.<n>.animation.bin`; Downhill Jam and Proving Ground instead ship
one `.\<idA>.animation.bin` per set, and have no indexed form at all.

| kind | template | Sk8land | DHJ | PG |
| --- | --- | --- | --- | --- |
| `textureinfo` | 1-id | 198 | 118 | 152 |
| `collisionspheres` | 1-id | 182 | 103 | — |
| `pvs` | 1-id | 15 | — | — |
| `animation` | 1-id | — | 324 | 470 |
| `geometry` | 2-id | **1,167** | **1,325** | **1,858** |
| `animation` | 2-id | 183 | — | — |
| `animation` | 2-id indexed | **11,156** | — | — |
| *worst control* | | *4* | *1* | *2* |

which names **12,900 / 14,606**, **1,870 / 4,657** and **2,485 / 5,665** files.
Geometry coverage is 1167/1167, 1325/1404 and 1858/2170.

### Two disciplines that make the names proven rather than plausible

**Search the pairs the code spells, not the cross product.** The loader is
handed `idA` and `idB` together and stores them together: for 1,164 of Sk8land's
1,167 geometry files the two ids are **adjacent code words**, and the three that
are not are exactly the chance hits the controls predict. Sweeping adjacent
pairs (~400k) instead of `idA x pool` (~23M) drops the expected chance count
from ~100 to ~1 *and raises recall*, because a model whose `idA` owns no
texture bank is unreachable from an idA-seeded search — Downhill Jam went
1,159 -> 1,325 and Proving Ground 1,567 -> 1,858.

**Gate a wide search on content.** Before that narrowing, the `idA x pool`
sweep produced 1,250 geometry hits of which only 1,167 were geometry files —
and the controls landed at 65-83 raw, 3-4 after the content gate. A raw hit
from a wide search is not a name.

### Known limit

79 Downhill Jam and 312 Proving Ground geometry files are still unnamed: either
their ids are computed rather than stored, or they live in an overlay this pass
does not reach. They keep their `<crc32>.bin` names, which stay stable.

## What is inside — content types

Unnamed files extract as `<crc32>.bin`, so `GobContentTypes` gives them a real
extension from their content where that can be proven. Every rule is scored
against the files whose real name IS known — **0 of 2,351 named files are
mislabelled**, and 1,874 of them are positively identified.

| Extension | Recognized by | Notes |
| --- | --- | --- |
| `.swav` | `SWAV` | Nitro wave, 1,405 across the carts — **the only standard Nintendo format the GOB carries** |
| `.strm` `.swar` `.sbnk` `.sseq` `.sdat` | their own magics | the rest of the Nitro audio family |
| `.xml` | `<` + printable | menu/config trees; the source of most proven stem names |
| `.sac` | `20 00 4B 00` | |
| `.hwas` | `sawh` | VV streamed audio: `{'hwas', blockSize, sampleRate, channels, …}` |
| `.prp` | `PFPF` | props |
| `.lwc` | `LWC` prefix | the version byte is *not* checked |
| `.comp` | `pmoc` (LE `'comp'`) | container of sub-records |

`.pal` is deliberately **not** a rule — see the withdrawal recorded in
`GobContentTypes`: "exactly 512 bytes of u16s with bit 15 clear" matches every
real palette *and* a 32×32 4bpp texel blob, and once the texture banks named
their own texel files the Rosetta caught it mislabelling 13 of them.

Content sniffing reaches only ~4% of the unnamed bulk, but that ceased to be the
binding limit once the loader's own names became recoverable (see **Model sets**
below). Both families that once dominated the unidentified mass are now
identified, and by name rather than by shape:

- **`{4, a, 0, 0, 0, b, 0, 0, 0, c, …}` with a constant `0x54` at index 15** is
  the **geometry** format documented under *Meshes* — `[0]` is version 4,
  `[15]` is 84 = `0x54`, and `a`/`b`/`c` are the bounding-box extents sitting on
  the diagonal of words 1/5/9.
- **`{u32 ?, u32 nRot, u32 nTrans, u32 version}` + two offset tables of `n+1`**
  is the **animation** format — Sk8land alone carries 10,733 of them. See
  *Animation* below for the joint-count oracle that proves it.

Files matching no rule and carrying no recovered name keep `.bin`. Guessing at
those would be worse than leaving them opaque.

## Textures

A texture is split across two GOB files: a **bank** carrying the GX parameters and
palettes, and a separate texel blob the loader names `.\%08x.texture.bin` from the
bank record's id. `Core/Formats/Texture/Nds/` implements both; `nds-texture`
decodes a whole cart to PNG.

The bank has no magic. Its layout came from the ARM9 fix-up routine (Sk8land
`FUN_020BF6B0` @ `0x020BF6B0`), which walks the records patching runtime pointers
and so states the geometry of the whole file:

```
u16 textureCount             // the fix-up loop bound
u16 paletteCount             // only used to place the palette DATA
u32 reserved
textureCount x 28 bytes:
    +0  u32 pixelId          // -> ".\%08x.texture.bin"
    +4  u32 pixelBytes
    +8  u32 texImageParam    // fmt 26-28, sizeS 20-22, sizeT 23-25, colour0 29
    +24 u32 palettePtr       // patched to &paletteRecord[i]
paletteCount x 16 bytes:
    +0  u32 format
    +4  u32 dataOffset       // in u16 ENTRIES, not bytes
    +12 u32 dataPtr          // patched to paletteData + dataOffset
palette data: per palette, { u32 entryCount, u16 entries[entryCount] } padded to 4
```

Two details in the palette are easy to get wrong and both were, first time round.
The record's offset is counted in **u16 entries** — the fix-up adds it to a
`ushort*`, so it scales by two — and a palette is not "everything up to the next
record's offset" but a self-describing `{u32 entryCount, u16 entries[]}` blob. The
wrong reading still produced monotone, in-range, plausible palettes; it just
coloured every texture incorrectly.

The two counts are equal in every shipped bank, which is why the header first read
as a `{n, n}` magic; the fix-up shows they are distinct fields and that texture `i`
binds palette `i`.

**Detection is an identity, not a magic**: every record's
`width x height x bpp / 8`, computed from its own TEXIMAGE_PARAM bits, must equal
the declared texel-byte count. That alone still admits three Sk8land false
positives, so `TryParseValidated` additionally requires every record's texel blob
to exist in the container at exactly the declared length. Under both checks the
corpus yields:

| Cart | banks | textures |
| --- | --- | --- |
| Sk8land | 91 | 1,120 |
| Downhill Jam | 46 | 1,619 |
| Proving Ground | 77 | 1,849 |

Formats are the standard GX set and mostly 16-colour: 4,432 `Palette16`, 138
`Palette256`, 11 `Palette4`, 7 `Direct16`, 5 `A5I3`. No shipped texture uses
`Compressed4X4`, whose palette-index block the bank does not carry, so the decoder
rejects it rather than approximating.

Two decode details are measured rather than assumed:

- 4bpp is **low-nibble-first**. Scored over 205 real 16-colour textures by
  horizontal continuity, low-first wins 204 to 1 (median smoothness ratio 0.77) —
  the same test that caught a swapped nibble order in the `.fnt` work.
- Rows are stored **bottom-up**, as this studio's GameCube art is (see
  `NgcTexFile`). Decoding in storage order renders the Jeep logo upside down and
  mirrors the "SKATE SHOP" sign.

A caution worth recording: a spatial-coherence score (median neighbour-delta /
stddev **0.427** against **1.048** for the same image pixel-shuffled) says the
decode is not noise, and it was reported as evidence the textures were correct. It
is not — a vertically flipped image and a wrong-but-valid palette are both exactly
as coherent as the right answer. Both defects shipped past that check and were
caught only by looking at a texture with a recognisable subject. Render a contact
sheet and read the lettering.

Because a bank names its own texel files, those names are proven the same way as
any other harvested name, which is what takes `GobNames.txt` from 1,724 to
**6,235** entries (26% of the container).

## Meshes

**Solved.** A model is a `.\%08x.%08x.geometry.bin` file (Sk8land `FUN_02046440`
composing the name through `FUN_020464ac`), and its geometry section is a packed
Nintendo **GX display list** — the standard console format, emitted straight into
the buffer the game DMAs to the GX FIFO.

The earlier "there are no GX display lists in the container" note was wrong, and
wrong for one reason worth recording: that probe parsed every file **from offset
0**, so a list beginning after an 84-byte header could never consume. Reading
Sk8land `0067ee06` at `+0x54` gives `2a 29 10 11` — TEXIMAGE_PARAM, POLYGON_ATTR,
MTX_MODE, MTX_PUSH — a textbook command word.

### File layout

An 84-byte header of 21 little-endian u32s, then a prologue, then the list:

| Word | Offset | Meaning |
| --- | --- | --- |
| 0 | `0x00` | version, always 4 |
| 1, 5, 9 | `0x04`, `0x14`, `0x24` | bounding-box X/Y/Z **extents**, 20.12 |
| 10-12 | `0x28`-`0x30` | bounding-box minimum corner, 20.12 |
| 14 | `0x38` | joint count |
| 15 | `0x3C` | 84 — start of the prologue, constant in every shipped file |
| 16 | `0x40` | sub-object count |
| 17 | `0x44` | offset of the sub-object offset table |
| 18 | `0x48` | end of the sub-object records |
| 19 | `0x4C` | prologue size + 8 |
| 20 | `0x50` | a further offset inside the record region |

**The display list runs from `76 + w19` to the first sub-object offset** (or to
`w18` when there are none). That start formula reads oddly — a size counted from
76 rather than an offset — and it is the only one that is exact: deriving it from
the counts as `84 + joints*12 + subObjects*4` is 93% right, because joint records
are not a fixed 12 bytes in every file. Across all three carts the declared span
parses as GX commands and consumes **exactly**, for **4,741 of 4,741** files
(1,167 Sk8land, 1,404 Downhill Jam, 2,170 Proving Ground).

That exactness is the detection gate. The format has no magic and its files have
no recoverable names, but commands are packed four to a word with their
parameters following, so a single wrong parameter width desynchronises the stream
within a few words.

The header's word 14 is corroborated independently: the ARM9 model constructor
(`FUN_02046980`) reads `*(int *)(geometry + 0x38)` and sizes three joint arrays
from it (`count*0x28`, and `count*0x10` twice).

### Decoding

Running the list means running the GX vertex pipeline — a matrix stack, a current
vertex/colour/texcoord state that the partial vertex commands mutate, and
primitive assembly per BEGIN_VTXS mode. Three details matter:

- **`VTX_DIFF`'s three 10-bit deltas are added to the 4.12 coordinate with no
  further scaling.** The widely copied "sign extend, then divide by 8" reading
  inflates every axis. This was measured, not chosen: see the oracle below.
- **Each matrix mode needs its own stack.** One matrix serving projection,
  position and texture lets a texture transform drag the geometry.
- **`MTX_STORE` / `MTX_RESTORE` slots must be implemented.** A skinned model
  keeps its joint matrices in the 32 GX slots and restores one per vertex run;
  ignoring them collapses the model onto whatever matrix was built last.

Models are **Z-up**, so the glTF writer rotates `(x, y, z)` to `(x, z, -y)` — a
rotation, not a mirror, so triangle winding is preserved. Texcoords arrive in
texels and become UVs by dividing by the size in the material's own
TEXIMAGE_PARAM.

### The bounding box is an oracle

The header declares the model's own extents, and a decoder with a wrong vertex
format, fixed-point scale or matrix convention will not reproduce them. That is
the check the DS textures never had, and it is what settled `VTX_DIFF`: Sk8land
`0067ee06` declares 21.78 / 79.01 / 0.24 and the unscaled reading returns
21.78 / 78.99 / 0.24, while the divide-by-8 reading returns 25.67 / 96.30 / 1.92.

Among rigid, self-contained models the reconstructed box matches the declared one
to within 2% for **731/808** Sk8land, **793/808** Downhill Jam and **944/973**
Proving Ground files.

Two classes are excluded, because the file genuinely does not determine their
vertices:

- **Skinned models** (joint count > 0) take their bind pose from joint matrices
  the runtime loads before the list runs.
- **Models whose list restores a matrix slot it never stored** are drawn relative
  to a runtime matrix. These come out *uniformly* scaled — right shape, wrong
  size, all three axes off by the same factor — which is exactly what a missing
  outer transform looks like, and not what a decoding error looks like.

### Texture binding

TEXIMAGE_PARAM's low 16 bits are a VRAM address the runtime patches in, so they
are **zero in every shipped file** (one distinct value across 25,803 sites). The
texture is named elsewhere: by the **sub-object records** the header's table
points at.

A record is:

```
+0   u32 scratch       // zero on disk; the loader caches the index here
+4   u32 textureIndex  // ordinal in the model's texture bank
+8   u32 patchCount
+12  i32 rel[count]    // RECORD-relative offsets of the words to patch
```

This is the ARM9 loader's own contract. `FUN_02045edc` walks the table — count at
`+0x40`, table offset at `+0x44` — and rewrites each record's second word:

```c
record[1] = record[0] * 0x1c + textureBank + 8;   // -> &bank.records[index]
```

`0x1c` is the bank's 28-byte record stride and `+8` its header, so the word being
replaced was an index into the bank. The renderer then writes that texture's VRAM
address into every word `rel[]` lists.

Verified across all three carts: **every one of the 25,351 listed offsets lands
exactly on a TEXIMAGE_PARAM parameter** (4,578 / 9,619 / 11,154), and the record
size is `12 + count*4` without exception.

### Which bank — a join, because the ids are unrecoverable

A model's bank is `.\<idA>.textureinfo.bin` and its geometry
`.\<idA>.<idB>.geometry.bin`, the same idA. Neither id is stored: probing every
distinct u32 in the container against the textureinfo template scores at chance,
and recovering them by CRC preimage is the search already refuted above, because
the 8-hex space *is* the CRC-32 codomain.

A different join needs no names. Both sides independently declare the same GX
state — a bank record stores a full `texImageParam`, and the model's site carries
the same size and format bits with only the address blanked. A bank is compatible
with a model when every site's index is in range and the size/format bits agree.

The true bank always satisfies that, so it is always among the candidates.
**Where exactly one bank survives, it therefore is the model's bank — a proof,
not a guess.** Where several survive they never agree on the actual texel blob
(measured), so nothing is bound rather than something plausible.

Coverage: **461/866 Sk8land, 280/946 Downhill Jam, 324/1330 Proving Ground**
textured models resolve to real images. The rest export with correct UVs and a
material naming the texture slot, but no image.

Two details the comparison gets right by measurement:

- **Bit 29 (colour-0 transparency) is excluded.** Banks set it on 99-197 records
  per cart and no model site ever does, so including it rejects the true bank for
  about a sixth of all models.
- **PLTT_BASE is not a second constraint.** It holds small ordinals that look like
  indices, but against 463 known-correct pairs no simple function of the bank's
  palette record reproduces it (best fit `dataOffset >> 4`, 7%). It is a baked
  export-time VRAM address, and it is not the texture selector either — its values
  match the sub-object indices for only ~9% of models, i.e. chance.

Two routes that do **not** work, so they are not re-attempted: the container's
file order is CRC-sorted, so a model and its bank are not adjacent (median
distance ~2,400 entries); and while the *physical* GOB layout does retain some
build-order locality, the nearest bank is the right one only 51.8% of the time —
a real signal, but nowhere near a binding.

### UV mapping

Texcoords arrive as 12.4 fixed-point **texels** and become UVs by dividing by
the size the site's own TEXIMAGE_PARAM declares — and they STAY in that
coordinate space. The art is stored bottom-up, and the correction for it lives
in the **embedded image**, which the mesh writer re-flips back to file
orientation, not in the UVs.

That took two user screenshots to get right. Raw V against the decoder's
upright PNGs rendered every face upside-down. The obvious fix, `v = 1 − t/h`,
fixed the orientation and drew thin seams down every texture-atlas border: a
coordinate flip is exact only at texel CENTRES, but the DS samples
`floor(texel)` with no filtering, and island borders are authored at exact
integer texel coordinates — so every border row sampled one off. Flipping the
embedded image instead makes sampling bit-exact with the console by
construction, tiling and mirroring included.

DS materials also emit **nearest samplers**: the hardware has no texture
filtering at all, and a viewer's linear default both softens the art and bleeds
neighbouring atlas islands across UV borders. Values outside 0..1 are ordinary
tiling — 77% of texcoords land inside the unit square and the tails reach ±64,
a road surface repeating — so the wrap mode has to be carried across too: GX
bits 16/17 enable repeat and 18/19 mirror, and a flip bit only mirrors while
repeat is on.

### Using it

```bash
nmt nds-mesh "Tony Hawk's American Sk8land (USA).nds" -o out/models
```

3,492 models convert across the three carts (1,062 + 1,014 + 1,416; the rest of
the version-4 files are authored-empty), 300,933 triangles, 1,065 of them with
resolved texture images, 0 glTF validator errors and 0 warnings.

## Animation — decoded, and exported

These files made up the container's largest unidentified mass — Sk8land ships
10,733 of them, 11,156 of its recovered animation names land on that exact
family, and the joint-count oracle proved the identity before the decode: the
geometry file states its own joint count at word 14, and the clip's channel
counts match the geometry's joint-flag census for every one of the 11,156
reachable (model, clip) pairs.

Implementation: `Core/Formats/Animation/NdsAnimationFile.cs` (parser/evaluator),
`Core/Formats/Mesh/Nds/NdsPoseScatter.cs` (application),
`Core/Formats/Mesh/Conversion/NdsAnimatedModelWriter.cs` (glTF baking); CLI
`nds-mesh --animations`.

```
+0   u32 frames
+4   u32 rotationChannels
+8   u32 translationChannels
+12  u32 scaleChannels          // first read as "version = 1"; it is a count
+16  u32 tableEnd               // == 20 + (nRot+nTrans+nScale)*4, exact corpus-wide
+20  u32 channelOffset[nRot], [nTrans], [nScale]
```

Each channel:

```
+0   u16 frames                 // == the clip's frames, in all 245,936+ channels
+2   u16 keyCount
+4   u16 id                     // per-kind ordinal (redundant)
+6   u8  keySize                // rotation 12, translation/scale 16
+8   u32 seekTableRel           // == 16; u32 key indices, one per 32 frames
+12  u32 keysRel                // keys run from here to EXACTLY the channel end
```

A key is `{u16 time, u16 flag, payload}` — rotation payload a **unit quaternion
in s16 4.12** (measured: |q|² ≈ 4096² across the corpus), translation and scale
fx32 triples. Times are frames; the final key of every channel lands on the
clip's last frame. `channelSize == keysRel + keyCount*keySize` held for every
channel, which is the identity that pinned the layout.

**Flag bit 0 is HOLD** (decompiled key walk, ITCM `0x01FFD3B4` and siblings):
the runtime refuses to take the next key for interpolation while the previous
key carries it, so the value steps — except exactly at the next key's time,
which always emits that key's own value. Interpolating through a held key
produces in-between poses the game never displays; a skater's arm swinging
through the body was the visible symptom before this was read out of the code.
Interpolation is otherwise hemisphere-corrected **component lerp** (nlerp) for
quaternions and plain lerp for vectors, with the factor computed on the
hardware divider from `(t − prevTime·4)·0x1000 / ((nextTime − prevTime)·4)` —
key times compare as `time·4` against a quarter-frame clock. The runtime does
NOT normalise the lerped quaternion — the unit-q matrix formula runs on the
slightly short vector, so mid-segment hardware matrices are microscopically
non-orthonormal; the exporter normalises (glTF requires it) and records the
deviation. The dispatch on the channel's keySize byte also reveals variants
Sk8land never ships: keySize 1 is a constant identity/zero channel, an 8-byte
rotation key holds four s8 Q1.7 quaternion components, and a compressed 8-byte
translation key is `{s8 x, y, z, s8 shift}` — likely the DHJ/PG comp dialect.
Notably the runtime's own s8-rotation LERP is buggy (the base term is dropped,
`(f·(b−a) + a) >> 7`), so only held (flag-1) segments of that variant can ever
have worked in a shipped game.

An earlier note here read the offset table as two fence-post arrays starting at
+16, which manufactured a phantom empty first rotation channel ("the root never
rotates") and mislabelled each kind's last channel. The table starts at +20 with
+16 as the end marker; roots do rotate.

### How animation is applied — there is no skeleton

The runtime has no skeleton: no parent table, no per-joint matrix slots, no
CPU-side matrix composition. The hierarchy is **compiled into the display list**
as `MTX_PUSH / MTX_MULT_4x3 / … / MTX_POP` nesting, the shipped operand values
of those matrix commands ARE the bind pose, and animating a model means
**overwriting the operands in RAM** and DMA-ing the unchanged list to the GX
FIFO (draw routine ITCM `0x01FFBBF0`; scatter `0x01FFDA6C`; evaluator
`0x01FFD120` — all decompiled).

The geometry prologue's joint records are the scatter table:

```
84: u32 recordOffset[jointCount]           // file-relative; 0 = no record
record: { u16 targetCount, u16 flags, i32 targetRel[targetCount] }
```

`flags` bit 0 = rotation, bit 1 = translation, bit 2 = scale — the on-disk
flags census {2: 1212, 3: 944, 7: 243, 1: 84, 6: 5} is exactly the observed
kind histogram. Targets are record-relative offsets of display-list matrix
operands: a rotating joint's target is the 9-word 3x3 block of a `MTX_MULT_4x3`
(or `MTX_MULT_3x3`), its translation goes to the same command's row 3
(target+0x24), a translation-only joint targets a `MTX_TRANS` operand or a
MULT_4x3 row 3 directly, and scale goes to the following `MTX_SCALE`. Every one
of Sk8land's joint targets resolves under those rules (1,655 rotation blocks,
184 TRANS operands, 1,033 row-3s — zero misses). Channel-to-joint mapping is
positional per kind: the k-th rotation channel drives the k-th joint whose
record has bit 0, and the count equality is the application gate.

**The frame-0 oracle pinned the quaternion convention**: scattering frame 0 of
the skater's first clip reproduces the shipped bind operands at vertex RMS
0.001; the transposed convention lands at 0.42. Bind pose is literally an
authored frame.

### Export

`NdsAnimatedModelWriter` animates exactly the way the hardware does — patch a
copy of the list, re-run the interpreter — so each frame's vertex positions are
correct by construction. Bones are the matrices the list transforms vertices
with, identified by **provenance** (the offset of the last matrix command that
produced the value); they are flat, carrying their measured per-frame GLOBAL
transforms, and every vertex binds to exactly one (the DS has no weight
blending). Fail-closed: an inapplicable clip, a failed decompose, or a changed
matrix set skips the clip and leaves the static document untouched. The 30 fps
cadence is an explicit export policy, not a measured runtime property.

Clip counts: 46 model sets carry **225** clips each (skaters) and 31 carry
**26**, contiguous from index 0. Skinned models' rest pose sits inside the
declared header box only loosely — for animated props that box is the swept
volume (a wheel declares its rotation circle, √2 wider than the wheel), so the
bounding-box oracle deliberately excludes skinned files.

### Downhill Jam / Proving Ground — same channels, unbound

DHJ and PG spell animation as one `.\<idA>.animation.bin` per id. The `comp`
wrapper turns out to BE the clip: `{u32 'pmoc', u32 frames, u32 nRot,
u32 nTrans, u32 nScale, u32 channelOffset[nRot+nTrans+nScale]}` — the Sk8land
header with a magic in place of the table-end word — and the channels inside
are byte-for-byte the same grammar. All **322 + 467** files parse exactly
(rotation channels keySize 12, vector channels 16, every channel consuming to
its declared end). One dialect difference: their last key lands on `frames-1`
where Sk8land's lands on `frames`.

What is missing is the BINDING. Their animation ids are a disjoint id
population — no animation idA owns geometry, a texture bank, or anything else,
so the clip-to-model link lives somewhere not yet read (a scene file or the
code). The census join that bound texture banks was measured and does not
transfer: joining on channel counts vs the geometry's joint-flag census leaves
**306 of 322** DHJ and **458 of 467** PG animations ambiguous — many rigs share
a census — so a unique-survivor rule has almost no coverage, and anything less
would be a guess. Their clips parse and stay unexported.

## Audio — the standard part

Outside the GOB, American Sk8land's Nitro filesystem carries
`vvobj/generated/sound/sound_stream.sdat`, a genuine 40 MB Nitro **SDAT** sound
archive holding the game's whole soundtrack: 30 `STRM` streams, 62 minutes,
named in the archive's own SYMB block (`STRM_CALIFORNIA`, `STRM_DRUMS_OF_FIRE`,
`STRM_BG_HOLLYWOOD`, …). Both it and the GOB's SWAV effects decode through
`Core/Formats/Nds/` — see `NitroAdpcm`, `SwavFile`, `StrmFile`, `SdatArchive` —
and convert to WAV via the `audio` command and the Audio tab.

Every wave in the corpus is type 2, Nintendo IMA-ADPCM. Two details matter:

- The step is divided by 8, 4, 2 and 1 **separately**, each truncating. The common
  `((n & 7) * 2 + 1) * step / 8` one-liner is not equivalent (nibble 7 at step 7
  gives 11, not 13) and drifts.
- Saturation is to ±0x7FFF, not the full s16 range.

A STRM's block table asserts its own correctness —
`(blockCount - 1) * samplesPerBlock + lastBlockSamples == sampleCount` holds for
all 30 tracks — and each ADPCM block re-seeds its own predictor, which a decode
confirms: block boundaries are *smoother* than mid-block audio (mean |Δ| 521 vs
985 on `STRM_CALIFORNIA`), so the framing carries no clicks.

## Where the format came from

ARM9 is uncompressed in all three carts and holds the loader. In Sk8land
(load address `0x02000000`):

| Address | What |
| --- | --- |
| `0x0210B9C4`, `0x0210B9CC` | `%s.gob`, `%s.gfc` |
| `0x0210BCBC` | base name `vvobj/generated/gob/main` |
| `0x0210B9D4`+ | zlib 1.2.1 error strings (the linked inflate) |
| `0x020B9914` | 32-bit byte-swap helper |
| `0x020B9934` | `strlwr` — the lowercase-before-hash rule |
| `0x020B87EC`-`0x020B88E0` | four `read(4)` + bswap into `+0x10C/+0x110/+0x114` |
| `0x020B8968` | `seek(chunkCount << 4, SEEK_CUR)` — skip the record array |
| `0x020B899C` / `0x020B89D0` | the `chunkCount*4` checksum array, read or skipped |
| `0x020B8A68` | `read(fileCount * 0xC)`, then bswap of fields +0/+4/+8 |
| `0x020B8B38` | an optional `fileCount * 0x108` debug array (256-byte name + 2 u32s) |

That last array is read only when its pointer is non-null, which it never is in
retail — which is exactly why the tail is `4*chunkCount + 12*fileCount` and
nothing more. Had it shipped, every file would have carried its name inline.

## Using it

```bash
# A cart extracts to its Nitro tree, including the container pair.
nmt archive "Tony Hawk's American Sk8land (USA).nds" -o out/cart

# Then the container itself (the .gfc must sit beside it).
nmt archive out/cart/vvobj/generated/gob/main.gob -o out/gob

# Or in one step — the recursive unpacker walks the cart into the GOB.
nmt unpack out/
```

The pair also opens in place: `.gob` is a nested-open extension, with the sibling
`.gfc` resolved as its companion, so the Texture and Audio tabs browse a cart
without an unpack step.
