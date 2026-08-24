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

## What is inside — content types

Unnamed files extract as `<crc32>.bin`, so `GobContentTypes` gives them a real
extension from their content where that can be proven. Every rule is scored
against the files whose real name IS known — **0 of 2,351 named files are
mislabelled**, and 1,874 of them are positively identified.

| Extension | Recognized by | Notes |
| --- | --- | --- |
| `.swav` | `SWAV` | Nitro wave, 1,405 across the carts — **the only standard Nintendo format the GOB carries** |
| `.xml` | `<` + printable | menu/config trees; the source of most proven stem names |
| `.pal` | exactly 512 B, all u16 bit 15 clear | 256-entry BGR555 palette |
| `.sac` | `20 00 4B 00` | |
| `.hwas` | `sawh` | VV streamed audio: `{'hwas', blockSize, sampleRate, channels, …}` |
| `.prp` | `PFPF` | props |
| `.lwc` | `LWC` + version byte | |
| `.comp` | `pmoc` (LE `'comp'`) | container of sub-records |

Coverage of the unnamed bulk is only ~4%, and that is a real limit rather than a
gap in effort. Two Vicarious Visions families dominate and neither is identified:

- **~46% (10,302 files)** — `{u32 id, u32 n1, u32 n2, u32 d}` followed by TWO
  offset tables of `n+1` entries each (`u32[4] == 16 + (n1 + n2 + 2) * 4` holds,
  and the tables are monotone and in range on every file sampled). Its sub-records
  begin `{u16 id, u16 kind, …}` — the same shape the `comp` container's members
  use, so the two are related.
- **~21% (4,741 files)** — `{4, a, 0, 0, 0, b, 0, 0, 0, c, …}` then signed 32-bit
  triples that look like fixed-point coordinates, with a constant `0x54` at
  index 15.

Files matching no rule keep `.bin`. Guessing at these would be worse than
leaving them opaque.

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

Texcoords arrive as 12.4 fixed-point **texels** and become UVs by dividing by the
size the site's own TEXIMAGE_PARAM declares. Values outside 0..1 are ordinary
tiling — 77% of texcoords land inside the unit square and the tails reach ±64,
which is a road surface repeating — so the wrap mode has to be carried across
too: GX bits 16/17 enable repeat and 18/19 mirror, and a flip bit only mirrors
while repeat is on.

### Using it

```bash
nmt nds-mesh "Tony Hawk's American Sk8land (USA).nds" -o out/models
```

3,492 models convert across the three carts (1,062 + 1,014 + 1,416; the rest of
the version-4 files are authored-empty), 300,933 triangles, 1,065 of them with
resolved texture images, 0 glTF validator errors and 0 warnings.

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
