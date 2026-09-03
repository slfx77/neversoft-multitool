# Backlog — Unimplemented / Deferred Formats

Created 2026-07-03. Distilled from `CLAUDE.md` (*Deferred Items* / *Not Yet Implemented*) + `memory/`.
**Re-verified 2026-07-07** with a full-corpus extension census, magic-byte probes, and
conversion sweeps — several entries here turned out stale (see *Done* below). NxTools (`Sample/nxtools`)
was surveyed as a reference source: it covers THUG2/THAW scene+tex families across xbx/wpc/xen/ps3 and
Downhill Jam (`thdj` = ngc/wii, later engine gen), plus a full PS1 `.psx` importer — but has NO coverage
for `.stex` payloads or THAW GameCube.

**Re-verified 2026-08-10 against the current tree, tests, and corpus evidence.** Standalone payload-bearing
PS2 `.stex`, bare `.col`/`.skin` routing, PSX colour-pulse playback, and the N64 ERZ/archive/texture/mesh
foundation have shipped since the earlier audit. Their investigations are retained under *Done*; do not
schedule work from their old descriptions.

**End-goal checkpoint 2026-09-02:** the non-GBA baseline is 50 staged build directories / 623,478
extracted or loose files. Later media and structural routes have moved substantially since the historical
notes below: P8/PG SKA key streams parse 44,649/44,649; every THPS4 PC delimiter-free TEX/IMG/COL/
SKIN/MDL/SCN/SKA family and DEE/SMO media route ships; PSP PMF audio ships; late
`.wav.ps3`/`.wav.xen` audio ships; big-endian X360 collision renders; and an exact-stem
PS2/Xbox/THPS4-PC collision overlay is available opt-in. The 670 proof-bound PSX/Dreamcast/
Spider-Man-Windows environment surfaces and complete THPS3 runtime BSP collision flags now use that
same opt-in route, with raw collision vertices and the runtime's unconditional face-class rejection.
All 24 authored THPS2X DDM main-level
families also compose their TRG-authored sky/background layers and exact-stem v6 PSX collision
surface. THPS3 PS2 now composes all 13 single-player main BSPs from the exact shipping
`SKATE3/Scripts/levels.qb`, including 11 authored skies and 11 unambiguous backdrop colours. These do
not imply native late-rig binding. PSP static worlds and embedded textures now consume all 668
`.psp_level` files exactly; strict same-build manifests compose 42 Remix and 40-per-build P8 main
variants while leaving ambiguous editor/mission/global layers standalone. These results do not imply
full Wii and remaining PSP level composition, or systematic gameplay-object coverage. Collectables and level objects
remain the intentional final stage; see `docs/corpus-robustness.md` for the conservative matrix.

**Status legend:** 🔴 Open · 🔶 Partial · 🟢 Verified this session · ✅ Done · ⚪ By design

---

## Remaining — needs work

### 🔶 Next-gen platform frontier (PSP / PS3 / Wii / X360) — triaged 2026-08-26

A volume-weighted census of the ten next-gen builds found **~35 GB unsupported**
(X360 15.9 · PS3 12.2 · PSP 3.4 · Wii 3.3). Eight format families were triaged with
first-hand evidence and every claimed cheap win adversarially verified. **Shipped from
that pass:** `.bik.xen`, `.pmf`, `.at3`, `.ogg` routing (see CLAUDE.md). What remains,
with the evidence already gathered:

| Family | Volume | Status from triage |
| --- | --- | --- |
| ~~**`FA CE CA A7` texture container**~~ | ~~12,335 identified files~~ | **SHIPPED 2026-09-02** — 12,335/12,335 files / 90,477 records parse; DXT1, DXT5, ARGB8888, and Xenon DXN/BC5 decode. Xenon uses complete 16-byte BC5 blocks as tiling units and every embedded allocation passes exact alignment, logical-size, and non-aliasing gates. All **3,570 non-empty PS3 dictionaries / 23,090 records** now decode: 2,388 / 15,573 use an exact named or type-hash twin; 1,083 / 6,577 reuse a byte-identical dictionary's payload only inside the same `PS3_GAME/USRDIR/DATA` build and only when every eligible owner supplies one byte-identical exact-length payload; 19 / 91 use exact raw-PAK names; and 80 / 849 use a preserved typed-table ordinal only after the complete descriptor/payload population agrees on count, name CRC, collision-neutral logical stem, and required size. The ordinal invariant agrees with **877/877 independently exact-name controls, zero counterexamples**. Raw, wrapper-decoded, and PAB-backed owners use the shared archive reader. Conflicting exact-name spellings, short owners, cross-build matches, duplicate descriptor keys, incomplete populations, and metadata mismatches fail closed; an existing `_VRAM.PAK` remains authoritative. |
| ~~**next-gen `.ska`**~~ | ~~44,649 P8/PG files~~ | **STRUCTURAL KEY-STREAM PARSING SHIPPED 2026-09-02** — strict big-endian wrapper/section/size/track gates accept P8 X360 9,467/9,467, P8 PS3 9,467/9,467, PG X360 8,641/8,641, and PG PS3 17,074/17,074 (**44,649/44,649 total**). This establishes bounded quaternion/translation keys, including PG's header variant and fixed-prefix tracks. It is deliberately not a claim of native skeleton/mesh binding or visual-motion validation; those joins remain open. |
| ~~**PSP mesh `0xC0EDBABE`**~~ (`.skin.psp`/`.skin`/`.geom.psp`) | ~~9,509 files / 215 MB~~ | **RIGID EXPORT SHIPPED 2026-09-02** — 9,509 wrappers produce 6,894,277 vertices and 4,600,331 theoretical triangles through detector, probe, GUI, CLI, and GLB. Another 543 authored-empty/name-collision wrappers fail closed. The PSP GE display lists, vertex layouts, indices, normals, UVs, colours, and rigid bind positions are parsed; weight bytes are validated but are not applied until the `.ske` bone-index join is derived. |
| ~~**`.img` on new platforms**~~ | ~~36,479 files / remaining volume~~ | **SHIPPED 2026-09-02 for every derived family.** PSP: 4,515 THUG2 Remix plus 3,141 in each P8 Final/Rev1 build (**10,797 build-tree `.img.psp` files total; 6,282 P8**). Xbox 360: 13,712/13,712 `.img.xen`, including 1,034 raw-DEFLATE wrappers, 601 multi-mip descriptors, DXT1/3/5, and all 14 DXN/BC5 maps. Wii: 12,127/12,127 `.img.ngc` (11,917 direct plus 210 evidence-gated padded crops). PS3: 2,851/2,853 `.img.ps3`; the two rejects are physically truncated payloads. All routes share preview and PNG export; PSP `.psp_level` embedded art now uses its own shipped world-container decoder. |
| **`FA AA BA CA` scene container** (`.skin`/`.mdl`/`.scn` on `.xen`/`.ps3`) | 11,182 files / 547 MB | **THAW SHIPPED 2026-08-28** (`NextGenSceneFile`, routed through `MeshTypeDetector`/`MeshContentProbe`/`MeshModelParser`, so the `mesh` CLI and the GUI open these directly): 809/809 files in the THAW X360 cutscene tree convert to **305,605 triangles with 0 glTF validator errors and 0 warnings**, and `baseball_bat` matches its GameCube twin end-to-end at 107 verts / 152 tris / 107-of-107 identical positions. Materials are emitted as untextured placeholders — the next-gen material record is not derived, so no texture binding is claimed. **Two shared defects surfaced with it, both of the same shape — a rule that looked universal but encoded a hidden assumption.** (1) `ModelDocumentGeometryAdapter.IsDegenerate` culled triangles on an ABSOLUTE area epsilon, i.e. an assumption about the units a format is authored in — P8 stores a whole cutscene character inside ~0.4 units, so two thirds of its real triangles were dropped (1,892 -> 671 on one file). The threshold is now scaled by the longest edge and **clamped at 1 so it can only relax, never tighten**; the clamp was added after a purely relative form quietly dropped slivers across N64, GBA, NDS and THAW PC, moving pinned censuses. THAW's own sweep moves 305,605 -> **310,700**; `baseball_bat` stays at exactly 152 and still matches its GameCube twin. (2) `IsNextGenScene` detected on the `FAAABACA` sentinel alone, but that word is exporter filler, not a platform marker: **all 723 THAW PC scene files carry it**, and this check sits ahead of the little-endian THAW reader, so it claimed every one and broke a working corpus. It now also requires the header to resolve BIG-endian — 0/723 THAW PC and 0/4,242 THAW GC claimed, next-gen unaffected. Both were invisible to a default test run because the tests that catch them are `[CorpusFact]`. **PROJECT 8 AND PROVING GROUND SHIPPED** (`NextGenLaterRevision`; P8 the same day, PG on 2026-08-28 once the two were found to be one format): whole-build sweeps convert **PG 713/1,646 files → 1,587,368 triangles** and **P8 786/1,296 → 1,364,089**, both **0 glTF validator errors and 0 warnings**. P8's own cutscene tree improved with the unification, 80/193 → **108/193** and 92,110 → **422,203** triangles. Derivation below. The container is the shipped THAW `ThawSceneFile` layout read big-endian, with the 28 reserved header bytes filled by the `FAAABACA` sentinel; BABEFACE + pad still locate the CScene. Next-gen structs are their OWN sizes: CScene object 160 B (`sectorCount`/`offCSector`/`offCGeom`/`offSMesh` at +0x78/+0x7C/+0x80/+0x8C), CSector 64 B (checksum +0x04, flags +0x1C), CGeom 112 B (bounds +0x20/+0x30, meshCount +0x5C), **sMesh 128 B** — sphere centre +0x00, radius +0x10, material checksum +0x14, a four-byte VERTEX DECLARATION at +0x18 (normal/tangent/uv/colour offsets), `u16 indexCount`+`u16 vertexCount` at +0x20, vertex-buffer pointer +0x50, vertex byte size +0x5C, index-buffer pointer +0x70. **The pointers were the missing piece: each points at a 20-byte GPU descriptor and the data starts at descriptor+20** (the blob before the VB is the byte-REVERSE of the one before the IB, so it is runtime state — take the stride from `vbSize/vertexCount`, which is exact on every mesh). Vertex: `float32 BE ×3` position, two 10/10/10/2 packed unit vectors, BGRA colour (128 = 1.0), `float32 ×2` UV; indices are BE u16 tri-strips. Sweep: **3,960/3,960 THAW X360 files, 95,571 meshes, 3.76 M vertices, zero parse failures**; on the non-skinned `.mdl`/`.scn` families (26,185 meshes) zero indices out of range and zero positions outside the file's own declared bbox. Cross-platform proof vs the GameCube twin (`baseball_bat`, independent GX code path): 107 verts, 152 tris, identical bbox, **107/107 positions byte-identical**. **Remaining across next-gen scene families:** skin weights/bone indices and authored material/texture binding are not derived; P8 PS3 remains a validated subset and Proving Ground PS3 topology remains intentionally disabled. **P8 DERIVED AND SHIPPED 2026-08-28** — same FAAABACA container and same **128-byte sMesh**, but a later revision throughout: CScene begins directly with `bounds_min` (no version/objSize prefix), CGeom bounds at +0x10/+0x20 with meshCount +0x4C, and sMesh moves to sphere centre +0x00, **radius +0x0C**, material checksum +0x14 (unchanged from THAW and PC), `u16 indexCount` +0x24 / `u16 vertexCount` +0x26, index-domain buffer +0x40, **vertex buffer +0x60**. Buffers use a **48-byte descriptor with a `CAFEBAB4` x4 magic**, first-batch count +0x20, that batch's bone palette +0x24 (NOT a class word — see below), two filler slots, data at +0x30; stride 32, position float32 BE x3 at +0 as in THAW. Because the 16-byte magic is a strong anchor, the sMesh table is best LOCATED rather than trusted from CScene offsets — requiring every record's +0x60 to resolve to a descriptor whose count matches +0x26 recovers it exactly, and CScene field offsets are NOT stable across P8 file kinds (fitting them to one file gave 821/3380). Verified: **every single-batch mesh decodes exactly — 141/141 (P8 138, PG 3) reproduce the declared bounding-sphere radius to ratio 1.000**, an oracle a wrong base/stride/format cannot satisfy. **RESOLVED:** batched meshes are a CHAIN — a 48-byte descriptor holds the first batch's count at +0x20 with data at +0x30, and every later batch is introduced by a 16-byte header whose leading big-endian word is its count; the chain sums exactly to `vertexCount` (78+78+550+550 = 1256; 18+101+18+16+16 = 169). **PROVING GROUND IS THE SAME FORMAT, and two readings had to be corrected to see it (2026-08-28).** (1) P8's indices were located by searching for a `FACEF001 FACEF000` pair, and PG contains no such pair anywhere — but those words are unresolved-pointer FILLER of the `BAADF00D` family, and the two builds merely park it in different slots: P8 writes it into the index block's header where PG writes zeros, and PG writes it into the vertex descriptor's tail and the inter-batch headers where P8 writes zeros. **The record STATES its index block at +0x5C in both builds**, with the payload +0x20 in, so no search is needed — and reading the stated pointer recovered files in P8 as well (cutscene tree 80 → 108 files, 92,110 → 422,203 triangles). The attribute block is the analogous stated pair: pointer +0x40, **byte size +0x4C** (`vertexCount*16`), `FFFFFFFF`/0 when absent. (2) The descriptor word at +0x24 was read as a class flag; it actually takes ~70 values, does not correlate with batching at all, and its multi-byte forms are ascending zero-terminated byte triples (`15 2B 2D 00`, `23 17 18 00`, `02 03 04 00`) — a per-batch **bone palette**, read but not consumed. Demanding it be a class rejected the sMesh table outright on most of PG; the real anchor test is `0 < firstBatchCount <= vertexCount` (3201/3201 PG, 3386/3393 P8). Cross-build Rosetta: the shared `bam_mugging` cutscene has byte-identical mesh records apart from four unresolved pointer slots and identical vertex bytes apart from one word, and the reader agrees on all 6 meshes / 1,892 triangles / every position and index in both builds. Every mesh in both sweeps reproduces its declared bounding-sphere radius at ratio 1.000, with the highest index it uses being its own last vertex. **THE NO-DESCRIPTOR POPULATION IS A SECOND VERTEX LAYOUT, AND IT IS THE LEVELS (2026-08-29).** A record whose `+0x60` is `0xFFFFFFFF` has no descriptor: the whole vertex, position first, sits in the `+0x40` block at a per-mesh stride the record states as `+0x4C / vertexCount` (16-56 bytes observed). The two layouts are perfectly disjoint, no file mixes them, and the split is by FILE KIND — every `.skin` uses descriptors, every `.mdl` (1,687) and `.scn` (361) is descriptor-less, zero counter-examples either way. `z_bw_bridge`'s 3.1 MB zone states **698 meshes at strides 24/28/32/36/40/44/48/52**, so no fixed stride could ever have read it. The **mesh table is stated as well** (offset `scene+0x80`, count `scene+0x4C`), which removes 78 records the search anchor invented — 60 of them passing every oracle *vacuously* with `indexCount == 0` and `radius == 0` — and recovers 93 real ones; the offset word is validated with a fallback to the search rather than trusted, since it is constant within a build (352 P8 / 368 PG over 3,824 files) and the corpus cannot tell a field from a constant there. The declared **bounding sphere stays a HARD gate**: it is the only check specific to vertex-base correctness (a batch walk "consumes exactly" from any base whose first count is big enough), and the populations separate hugely — authored staleness tops out at ratio 2.39 on 8 of 6,609 meshes (proven authored: the separately mastered PS3 build ships a *corrected* radius over byte-identical positions) while a misread base lands at 1e36 or infinity. New sweeps: **PG 1,536/1,646 → 2,204,550 triangles, P8 1,233/1,296 → 1,655,508, 0 validator errors and 0 warnings across 2,769 GLBs**. The 110 PG / 63 P8 non-conversions are authored-empty and provably so — exactly the files whose scene bounding box is degenerate (inverted on P8, all-zero on PG), set-identity with zero exceptions. **PS3 SHIPPED FOR PROJECT 8, REFUSED FOR PROVING GROUND (2026-08-30).** PS3 moves the attribute stream and index buffer into a sibling **VRAM companion** — the kind is SWAPPED, not suffixed (`.skin.ps3`→`.skiv.ps3`, `.mdl`→`.mdv`, `.scn`→`.scv`) — addressed by the same `+0x40`/`+0x5C` pointers as **RAW offsets from byte 0, no scene base and no `0x20` header skip**, resolved through the existing `AssetSource.TryReadCompanion`. **P8 PS3: 615/1,807 files → 653,648 triangles, 0 validator errors/warnings**, with `anl_pigeon` decoding position-for-position identically to its Xbox 360 copy; the wrong-companion control scores 0/103. **PG's PS3 descriptor is longer** — 0x40 bytes, `FACEF000 FACEF001` at +0x38/+0x3C, count +0x30, data +0x40 (231/231 PG-PS3, 0 of 708 elsewhere) — and reproduces 99/99 spheres where the X360 shape gets 0/99. **But PG-PS3's topology is wrong and the build is DECLINED rather than shipped broken**: the bounding sphere is order-INSENSITIVE and the glTF validator only checks index range, so both passed while the model rendered as a shattered fan. A locality oracle (median triangle edge ÷ bounding radius) puts P8 PS3 at 0.21-0.39 — matching its X360 sibling — and PG PS3 at 0.50-0.62 for single- and multi-batch meshes alike, at every index base 0x00-0x60 and for the swapped-pointer and positions-in-VRAM alternatives, so it is neither a shift, nor the batch chain, nor a swapped pointer. **Still open:** (ii) the attribute stride is stated at **+0x5A** (a fixed 16 holds for only 60% of meshes) with the layout declared at +0x18..+0x23 — byte +0x1A is UV0's offset, +0x1B the colour's, and the u16 at +0x1C a 2-bit-per-slot mask over 8 texcoord slots (`stride == (+0x1A-32) + 4*popcount2(mask)`, 6,510/6,510; 1-7 UV sets, 28% genuinely differing); the reader now uses it for UV0 but does not emit the extra sets; (iii) **authored normals now SHIP** on the descriptor path — the vertex carries three 11/11/10 packed signed unit vectors at +0x10/+0x14/+0x18, unit on **100.000% of 97,296 vertices vs a 6.3% control**, and **+0x10 is the normal** (mean signed dot with our emitted facet normal +0.909 P8 / +0.923 PG, the other two ±0.007); the positive sign independently confirms the strip winding. Derived normals now run only for the descriptor-less layout. Still unused: +0x1C is `BAADF00D` on 100%, +0x0C is two BE u16 fractions with an implied third (blend-weight shaped, but no bone-index field found so skinning is not claimed), and the colour word is `0xAARRGGBB` with **alpha first**; (iv) materials remain untextured placeholders on both revisions. **The sentinel-less files need no work**: all 1,350 are whole-file-compressed duplicates (raw DEFLATE on X360, Okumura LZSS on PS3) whose payloads are SHA-identical to scenes already present uncompressed — zero new assets. |
| ~~**`.fsb` FMOD banks / THAW X360 XMA banks**~~ | ~~14 banks / 2.3 GB~~ | **SHIPPED 2026-09-02** — strict FSB3.1 parsing consumes all 12 banks / 1,782,745,082 bytes exactly and exposes all 22,454 authored names: 5,418 PS3 MP3 streams and 17,036 X360 XMA1 streams. Named raw extraction emits exact MP3 or canonical RIFF/XMA; CLI and GUI convert either codec to per-stream PCM WAV through ffmpeg, with staged atomic targets and a real-corpus XMA decode test. The shared probe content-gates compound `.fsb.ps3` / `.fsb.xen` names, and conversion paths independently fail closed on the same parser. **THAW X360's paired `xma.dat`/`xma.wad` family is shipped too:** the two BE indices consume exactly 3,703 streams / 516,990,976 WAD bytes, with every 2 KiB-aligned range forming a gapless permutation through EOF. Existing QBKey resources name 2,425 streams; the remaining 1,278 use deterministic `0xHASH` names without bundling a corpus-sized lookup. Both measured dialects (3,592 22.05 kHz mono effects; 111 48 kHz stereo music) extract as canonical RIFF/XMA and decode through the same ffmpeg bridge; corpus tests pin the full population and a real decode. |
| ~~**late `.wav.ps3` / `.wav.xen` audio**~~ | ~~6,534 files / 65,231,394 B~~ | **SHIPPED 2026-09-02** — 3,759 PS3 files (3,530 raw MP3 + 229 one-stream FSB3) and 2,775 Xenon RIFF/XMA1 files classify exactly. Probe, GUI, CLI, duration, collision-safe naming, and staged WAV conversion share the content gate; one real file from each of the 27 authored codec/rate/channel/loop layouts decodes through ffmpeg. FSB-contained MP3 is trimmed at a strictly validated zero alignment tail before transcoding; raw `.wav.ps3` MP3 remains exact-to-EOF. |
| ~~**Wii DSP-ADPCM** (extensionless streams)~~ | ~~6,578 files / 563 MB~~ | **SHIPPED 2026-09-02** — 6,578/6,578 satisfy `size == 0x60 + ceil(nibbles/2)`, the derived nibble↔sample relation, and `header ps == first data byte`. Content-gated extensionless discovery, probe, GUI preview, CLI, and PCM WAV conversion use the same decoder. |
| **Wii scenes / levels** | 1,591 candidates | **PARTIAL** — the THAW GameCube layout safely covers 11/392 DHJ and 56/1,199 Proving Ground Wii candidates. The remaining candidates fail closed; full scene, sky/background, and object composition is still open. |
| ~~**`.psp_level`**~~ | ~~668 files / 203 MB~~ | **STATIC WORLD + EMBEDDED TEXTURES SHIPPED 2026-09-02.** All 668/668 files consume exactly: Remix 80 / 80,327,774 bytes; P8 Final 294 / 61,488,910; P8 Rev1 294 / 61,488,910. The shared scene path emits 1,785,387 GE strips, 8,646,324 vertices, and 5,075,550 theoretical triangles with decoded T4/T8 embedded textures and explicit fixed/float vertex layouts. Same-build QB evidence also ships a strict authored composition subset: Remix 42 main variants (40 sky, two explicit no-sky) and each P8 build 40 world-zone variants (36 sky, four no-sky), in runtime sky→main order with independent namespaces and camera-locked sky metadata. Five Remix editor themes are recorded but not auto-joined because one shared main has no unique selected theme; P8 missions, SFX layers, global and `z_world` remain standalone. Missing/ambiguous/malformed optional composition falls back to standalone. Remaining: the trailing 64-byte dynamic-object records are bounded/skipped, not rendered. |
| ~~**`.tif` on disc**~~ | ~~8,531 corpus-wide~~ | **SHIPPED 2026-08-26** with mip export — `TiffMipChain` retargets the header IFD pointer per level (7,308/7,308 frames decode; 1,487/1,487 chains exactly floor-halved), writing `_mipN.png` companions. See CLAUDE.md. |
| **hash-named pak entries** | 3,657 files | 8 extension keys resolved by forward-hash against a 15/15 control and now registered in `PakArchive.KnownTypes`: `.vtex`/`.vstex`/`.vimg`/`.vskin`/`.vmdl`/`.vgeom`/`.vfnt`/`.mhkc`. `.vtex`/`.vstex` are consumed as PS3 texture VRAM twins under exact-name, same-build byte-identical-content, or complete typed-population ownership proof; collision-renamed texture entries are paired only under the last gate. `.vimg` is consumed by the PS3 IMG path. The mesh/font/collision-side payload meanings remain inspection work. |

Closed as non-content: `.pup` (PS3 firmware), `DATA/SPACERS/spacer_N.dat` (5,850 filler files), ghost saves.

### ✅ PSP PMF ATRAC3+ audio — SHIPPED 2026-09-02

The earlier decoder-stall conclusion was a framing error rather than an ffmpeg limitation. Strict PSMF
private-PES demux now removes each eight-byte PSP frame header, wraps the 568/752-byte ATRAC3+ bodies
in OMA, and rejects inter-packet garbage outside declared stuffing/padding. All 334 PMFs classify:
333 carry audio and decode to PCM16 WAV or AAC in the converted MP4; `ICON1.PMF` is the sole authored
video-only stream. Corpus gates cover path and archive-byte conversion, both layouts, and the complete
1,325-frame former failure case.

### ✅ Collision v8/v9/v10 rendering and conservative overlay — SHIPPED 2026-09-02

Standalone COL rendering now covers 11,265 little-endian v8/v9/v10 files, with only the known
legacy-v1 `canada.col.ps2` rejected; THAW X360 adds 764/764 big-endian v10 files (32,034 objects,
1,268,567 vertices, 996,233 faces). THPS4 PC's separately gated 601 `*col.dat` files are described
above. The Levels tab and `mesh --collision-overlay` can opt into a translucent collision layer only
for a same-owner, exact-stem companion whose endian and complete payload validate. The loose-file
census finds 3,365/3,620 PS2 `.geom.ps2` → `.col.ps2` pairs, 90/192 Xbox `.scn.xbx` → `.col.xbx`
pairs, and 29/29 THPS4 PC delimiter-free `*scn.dat` → `*col.dat` pairs. All 13 authored THPS4 main
levels compose the render scene and v8 overlay. THPS2X has 104 exact-stem DDM/PSX structural pairs,
all carrying non-super PSX revision 6, but only the 24 pairs with both exact `_o.ddm` and `_t.trg`
authored-level markers are promoted. Those 24 collision payloads contain 19,527 objects/meshes and
328,442 structurally valid faces (306,154 visible plus 22,288 hidden collision-only), producing
485,549 non-degenerate overlay triangles; 89 other declared face records fail the parser's structural
gates. Many broad PS2/Xbox pairs are objects rather than levels. No exact loose X360 scene/COL pairs exist, and NGC, PS3,
hashed/offset, malformed, ambiguous, or remote-directory candidates remain excluded. NGC/Wii COL
positions are still external, so their inspection route cannot be promoted to rendered overlay.

### ✅ THPS2X authored sky/background composition — SHIPPED 2026-09-02

DDM level conversion now reads each exact `<level>_t.trg` and joins only the objects registered by
`BackgroundCreate`. All 500/500 authored registrations resolve without ambiguity to 25 unique sky
objects across 24 level families; 20 levels have sky geometry and four deliberately do not. The
shared viewer/export path retains backdrop colour, placement anchor, camera-lock semantics, and the
authored paint order for multi-layer skies.

### ✅ THPS3 PS2 authored BSP sky/background composition — SHIPPED 2026-09-02

The default RenderWare BSP viewer/export route now reads the exact shipping
`SKATE3/Scripts/levels.qb` master list and its `loadlevelgeometry` calls. All 13 single-player mains
resolve uniquely beneath the same build's runtime `SKATE3/pre` tree; 11 compose an authored sky BSP
and 11 retain an unambiguous `SetBackgroundColor`. Foundry and Warehouse explicitly author no sky,
while Tutorials proves why basename guessing is wrong by pairing `Tut.bsp` with
`Sk3Ed_Bch_Sky.bsp`. Main and sky keep independent texture providers and material windows, sky
geometry is camera-locked, and optional missing/malformed composition fails open to the standalone
main BSP. Corpus gates pin the exact 13-entry manifest/resolution list, all 13 composed documents,
Burnside's valid untextured sky geometry, and a real Tutorials GLB export.

THPS3 collision now has a dedicated default-off view over the main BSP only. The Neversoft atomic
extension supplies a version-6 side table with one little-endian `u16` flag per triangle: 39/43 BSPs
carry a complete non-empty runtime payload and emit 771,579 of 772,002 triangles across 394 flag
values. Three DCC/source exports correctly lack the plugin and `Ware_Test10.bsp` is authored-empty.
Truncated sector salvage, incomplete flag ownership, or invalid indices fail closed; 423 geometric
degenerates are omitted. The camera-locked sky is never mislabeled as level collision, and GLB/Blend
metadata retains exact classification groups after the shared translucent material is merged.

### ✅ THUG2 PS2 v2 IMG GS-swizzled class — SHIPPED 2026-08-26 (2,629 files were failing)

`Ps2ImgV2File` rejected any nonzero word at +24 ("IMG MXL must be zero"), but THUG2 PS2
ships 2,629 v2 `.img.ps2` whose word24 is a **GS-swizzle flag**, not a mip count:
`0x00200000` on 633 PSMT8 + 1,268 PSMT4 files, `0x00100000` on 728 PSMT4 files. They failed
wholesale and invisibly (the v2 sweep only counted successes). Surfaced while proving the
Remix PSP `.img.psp` re-encode, whose +24 carries the analogous PSP GE swizzle flag.

Fix: accept bits 20-21, undo the rearrange with the existing `Ps2TexSwizzle` mappings at
the stored buffer's dimensions, then de-stride as before. A real mip count in the low bits
is still refused. Pinned by `Ps2ImgV2GsSwizzleTests` (per-class SHA fixtures, a
cross-decoder PSP-twin test, and a corpus census of the MXL word).

**Oracle note worth keeping**: Xbox `.img.xbx` twins were the obvious ground truth and are
NOT usable — a control on the *unflagged* (already-correct) files matched only 641/2,240,
because the ports re-authored art and PS2 loadscreens are 16-bit. The Remix PSP twins
scored 917/937 on the same control, so they refereed the fix: 1,185/1,185 comparable
flagged files decode pixel-identical, vs 62 for leaving the data linear and 370 for the
PSMCT16 upload variant. Corroborated visually (timer-font digits, ESRB fine print).

Still open, found alongside: some v2 files fall through to `ThawSceneTexFile` on failure and
report "Version 2 (expected 6)", which masks the real reason — the fallback chain should
surface the first parser's error when the extension/version already identified the family.

### 🔶 Handheld + Wii + THPS4-PC corpus expansion — 2026-08-20

Thirteen new builds added (7 GBA carts, 3 DS carts, 2 Wii discs, THPS4 PC) — all staged and green.
Container support shipped for NDS and Wii; the GBA 3D-mesh and DS GOB/GFC decoders remain open
research (both blocked on disassembling the game's own loader). Status:

- ✅ **Corpus staging** — 7 GBA (`.gba`), 3 DS (`.nds`), and THPS4 PC (2-CD ISO+BIN/CUE) added to
  `SampleGeneratorConfig.cs`. THPS4 PC needed `FindDiscImagePaths` extended to merge an ISO CD1 with a
  raw-BIN CD2 (both formats on one shelf). Dates are US release dates (THPS4 PC pinned from its CD1 PVD,
  2003-07-18). QB corpus sweeps re-pinned for the +356 loose THPS4 `.qb`.
- ✅ **NDS Nitro filesystem** (`Core/Formats/Nds/NdsRomArchive.cs`) — `.nds` opens/extracts through the
  detector, `ArchiveFileSystem`, CLI `archive`, the Archive-Extraction tab, and the recursive unpacker;
  entries are plain byte ranges served by `FileArchiveFileSystem`. Exposes the Nitro tree plus
  `_system/` (header, arm9/arm7, banner, overlays). Proving Ground DS's 16 `bink/*.bik` are now
  playable in the Video tab. Pinned by `NdsRomArchiveTests` (synthetic + 3-cart `[CorpusTheory]`).
- ✅ **VV GOB/GFC containers (DS) — SHIPPED 2026-08-23/25.** `Core/Formats/Gob/` +
  `GobArchiveFileSystem`; every cart rebuilds bit-exact (14,606 / 4,650 / 5,657 files, 41,643 chunk
  checksums). DS **textures** and **static meshes** (packed GX display lists, UVs, texture binding)
  convert via `nds-texture` / `nds-mesh`. **Model-set naming landed 2026-08-25**: the loader's ids
  live in ARM9 as plain u32s, so its own filename templates resolve — `GobNames.txt` 6,235 →
  **22,819** proven pairs, Sk8land now 14,550/14,606 named, and the container's two dominant
  unidentified families turned out to be the **geometry** and **animation** formats. Full
  derivation, including the controls that make the naming sound and the refutations that do not
  work, in `docs/formats/ds-gob-gfc.md`. Remaining DS work is tracked as its own phase list below.
- 🔶 **DS formats above the static tier** — animation clips, model-set skeleton association,
  collision worlds, and SWAV/STRM/HWAS PCM/ADPCM conversion now ship through shared routes.
  The animation outcome remains partial: Sk8land has 77 models / 11,156 applicable clips; DHJ has
  322 applicable bindings but 121 currently bake, and Proving Ground has 467 applicable bindings
  but 131 bake because singular joint transforms fail closed. Remaining DS composition work includes
  skinning fidelity, collision-sphere/PVS semantics, and systematic level/sky/object assembly.
- 🔴 **GBA 3D level meshes** — `tools/research/gba-3d/FINDINGS.md`. Confirmed geometry is STORED (not
  procedural): a raw ROM model region (~0x750000+ in THPS2) with a bounds+count+pointer descriptor
  table and small-index face lists, reached via an in-RAM object directory. The vertex-position codec
  is unresolved (not plain s16 triples) — needs the model loader disassembled (`gba_disasm.py` +
  Ghidra). Then the implementation wave: `GbaRomArchive` carve route → mesh parser/writer → viewer,
  per the N64 template. GAX audio (Shin'en) is separate and out of scope.
- ✅ **Wii (Downhill Jam, Proving Ground) — SHIPPED 2026-08-20, validated against the real discs.**
  RVZ→ISO converted with DolphinTool; read natively via `Core/Formats/DiscImage/WiiDisc` (magic
  `0x5D1C9EA3`@0x18; partition table @0x40000; DATA partition type 0) + ticket→title-key AES-128-CBC +
  `WiiPartitionStream : Stream` (0x8000 clusters → AES-CBC(cluster[0x400..0x8000], titleKey, IV =
  cluster bytes 0x3D0..0x3E0); one-cluster cache). `GcmFileSystem.ReadFileList` gained an `offsetShift`
  (2 for Wii's word-shifted FST/file offsets); `DiscKind.Wii` wired into `DiscImageArchive.SniffIso`
  (probed before GCM) + `ExtractFile`. Common key NEVER in the repo — resolved from
  `NEVERSOFT_WII_COMMON_KEY` → `%APPDATA%\NeversoftMultitool\wii_common_key.bin` → actionable hint
  (`WiiCommonKey`). Validation: reader listing == DolphinTool's 3,601-file DATA listing exactly, and
  `fonts/small.fnt.ngc` SHA-256 byte-identical to DolphinTool's own extraction. Both builds staged
  (DHJ 10,179 files; PG 13,611 with 835 nested archives auto-expanded); the `Vid1AudioExtractorTests`
  DHJ-Wii reference now resolves to the real `movies/JX_Interview01.vid`. Pinned by `WiiDiscTests`
  (2 synthetic-key `[Fact]` building a test-key-encrypted cluster + a key-free end-to-end
  `[CorpusFact]`). Proving Ground Wii is the Page 44 Neversoft-engine port; its DATA partition is
  `.ngc`/`.ps2`/`.skin`/`.qb` lineage that converts through the existing parsers.
- ✅ **THPS4 PC delimiter-free DAT + media — SHIPPED 2026-09-02.** `.tgr` ships for all 27 content-gated BIKi movies.
  All 3,612 DEE carriers satisfy their strict BIKi/Bink-DCT profile and decode to PCM16 WAV; all 47
  SMO soundtrack carriers satisfy a separate stereo profile and now route directly to WAV as well as
  through the video path. Delimiter-free `*tex.dat` is **601/601** (8,332 RGBA textures / 38,093
  exact-size mips, one authored-empty dictionary) and `*col.dat` is **601/601** v8 (11,701 objects,
  646,877 vertices, 669,796 faces). The collision gate validates the complete post-face BSP graph,
  proves every face reachable through 1,557,770 object-local references, and consumes exact EOF.
  All **880/880 `*img.dat`** files decode through their P8/BGRA32 layouts. The independent early-PC
  scene parser consumes **420/420 `*skin.dat`, 152/152 `*mdl.dat`, and 29/29 `*scn.dat`** exactly,
  preserving planar vertex pools, materials, hierarchy placement, and companion textures; all scenes
  render. `Levels.qb` supplies 13 exact sky/main compositions and two editor shells (including
  Motox → Hof_Sky and excluding unused residue). Finally, **1,966/1,966 `*ska.dat`** files parse with
  the shipped 2,048-entry Q/T tables and reach the shared animation export IR. Each family has its own
  strict name and payload gate rather than a generic `.dat` alias.

### 🔴 PS1-era residual extension survey — 2026-08-17 (answers "any PSX-side gaps left?")

Full extension census re-run over the four PS1-era final builds plus the THPS PS1 lineage, with
magic-byte probes on everything not already routed. The character-paired `.bin` mystery is settled,
and three small asset-bearing gaps remain. Everything else is code, saves, or replays.

**Settled — `.bin` paired with characters is CODE, not assets.** Spider-Man pairs a `.bin` with each
boss/NPC stem (`blackcat/carnage/chopper/cop/docock/hostage/jonah/lizman/mysterio/rhino/scorpion/
superock/thug/venom.bin`, 1–58 KB, plus `l*lsc.bin` level scripts and `shell.bin`): disassembly-shaped
MIPS throughout (`lui/addiu` pairs, `jr $ra` epilogues, stack prologues) — **per-character AI code
overlays**, each with a paired `.rel` relocation table, exactly the "modules" krystalgamer's
spidey-decomp covers. THPS2's 29 `.bin` are the same class (front-end screens `mainmenu/options/
tricksel/…` + `GAME.BIN`/`FRONT.BIN`/`EDITOR.BIN`). Not convertible as assets; the interesting
residue is the DATA tables embedded per overlay (AI params, per-character anim indices — same class
as the pickup tables RE'd out of the main EXEs), which is per-overlay RE work, not a converter.

**N64 cross-reference lead for anim naming — measured 2026-08-17.** The Spider-Man N64 cart carries
an **uncarved ~1.5 MB character-AI segment at ROM 0x1D59BB2–0x1DEBAEE+**, per-character in order
(blackcat 0x1D72, carnage 0x1D79, chopper 0x1D80, cop 0x1D8B, docock 0x1D91, lizman 0x1DA1,
mysterio 0x1DA9, rhino 0x1DAF, scorpion 0x1DB0/0x1DB8, simby 0x1DBA/0x1DC8, spclone 0x1DCB,
superock 0x1DD1, thug 0x1DDE, turret 0x1DE2, venom 0x1DEB). **Bundle-naming angle
(2026-08-17)**: current N64 naming is triggers-first (TRG scripts spell filenames; contiguous
slot-run alignment) with the PS1 content-identity resource as fallback — 418/594 slots named, and
content identity structurally cannot separate shared-rig characters. Each AI block must bind to
its model somehow (the PS1 side calls Spool_GetModel by name hash); if the N64 blocks carry a
bundle-slot immediate or hash constant, that is per-character naming evidence for exactly the
class the fallback cannot reach (82 unnamed Spider-Man slots). **Probe run 2026-08-17 — the
constant-anchored route WORKS, and the mechanism is now mapped.** Byte-identical signature
matching cannot transfer (recompiled, measured), but code-embedded constants survive: scanning the
CARVED boot.bin (the raw ROM shows nothing — boot code is ERZ-compressed in-cart, and the earlier
"no QbKey hash table exists" scan searched DATA only) for split-immediate QbKey hashes (BE MIPS
`lui`/`ori` pairs) finds a spool/unload routine at boot.bin file 0x73700 whose body:
  - loads SIXTEEN hash constants in a row, each fed to `jal 0x8008A674` — **all sixteen resolved**
    by hashing boot.bin's own strings: thug, police, hostage, cop, scorpion, rhino, jonah,
    Mysterio, simby, and the level-script overlays l2a1lsc/l5a5lsc/l5a6lsc/l5a7lsc/l6a1lsc/
    l6a2lsc/l6a3lsc — the character+overlay roster, i.e. `0x8008A674` is the N64's
    **spool-by-name-hash function** (the Spool equivalent asked about);
  - then hashes strings AT RUNTIME: `lui/addiu` string pointers (VA 0x80020cdc…) through
    `jal 0x800AA70C` (a string→hash routine) into the same spool call — so boot.bin carries
    NAMES IN PLAINTEXT. Two tables located: the character/viewer model list at file 0x2F30
    (spidey, parker, blackcat, ock_suit, brock, henchman, thug, jjviewer, scorpion, daredevl,
    police, swat, rhino, venom, lizman2, lizard, mjviewer, symbi_02, mystview, punisher, docock,
    carnage, superock, captain) and the overlay-FILE list at 0xA218 (cop, hostage, jonah,
    l2a1lsc…l6a3lsc, simby — the PS1 `.bin` stems verbatim).
  **Next blocker, named**: boot.bin is a MULTI-SEGMENT carve (concatenated decompressed boot
  packages), so jal-target VAs (0x8008A674, 0x800AA70C) do not map to file offsets under any
  single base (ROM entry 0x80000400 tried, off by segments). Recover the per-package load
  addresses from the carver/boot loader, then disassemble the spool function to find the
  hash→bundle-slot resolution — the direct naming lever. The AI segment itself showed no hash
  constants — its model binding goes through this boot-side machinery. The segment is referenced by NO master-directory
group — the whole-carve block scan finds none of it in any carved asset, so it must be DMA'd by
hardcoded ROM address — meaning `N64AssetCarver` currently misses it entirely. The CODE is
recompiled (distinct-block coverage of any PS1 overlay vs boot.bin is only 3–8%, all generic MIPS
idioms with no stable base — an earlier same-day "entire overlay present in boot.bin" reading was
wrong: 11.5k raw hits collapsed to a handful of epilogue-shaped blocks matching thousands of
positions; measure DISTINCT probe coverage and base-offset agreement, not hit counts). The DATA
survives: 34 contiguous byte-mirrored runs ≥64 bytes (u16 tables under u16-swap, u32 tables under
u32-swap, up to 808 bytes — carnage) pair 16+ PS1 overlay tails with their N64 blocks. docock's
shared tail is a table of (u32 id, u32 index) pairs — exactly the shape a per-character
anim/behaviour assignment would take, and docock's 43 anim clips are already cross-matched
PS1↔N64 sample-for-sample. Uses: (1) carve the segment (name blocks by the run anchors); (2) mine
anim-slot assignments from the shared tables on either platform; (3) where assignments are code
immediates, two independent compilations of the same source (LE PS1 + BE N64) cross-check which
constants are source-level. Probe method retained here; runs list in the 2026-08-17 session notes.

**Actionable small gaps (in rough value order):**
1. ✅ **`.fnt` bitmap fonts — SHIPPED 2026-08-18.** `Core/Formats/Font/` + the `fnt` CLI command
   + Texture-tab routing. Output is **PNG glyph atlas + schema-v1 JSON metrics, and nothing
   else** (user call 2026-08-17: every installable bitmap-font format on the FreeType stack —
   `.fon`/FNT, BDF, PCF — is 1-bit monochrome and would threshold the 16-level anti-aliased art
   AND drop its palette colour; colour-bitmap OpenType (CBDT/CBLC) is the only true-fidelity
   typing route, noted and not planned).
   - **Layout** transcribed from the matched decomp's only reader, `Font::Font(unsigned char *)`
     (`src/FONTTOOLS.cpp:194-330`): `u32 glyphCount`, then 16-byte records
     `{u32 widthUnits, i32 height, i32 baseline, i32 advanceWidth}`, then a 16-entry `u16` CLUT,
     then 4bpp low-nibble-first pixels at `widthUnits*4` wide, row stride `widthUnits*2` bytes,
     per-glyph size `2*(widthUnits*height) + 2*((widthUnits*height)&1)` — the trailing pad is
     the loader's own `oddPixelPadding`, not a row pad. Baseline is **signed** (18 corpus glyphs
     are negative).
   - **Corpus: 443 files → 383 parse on an exact-EOF gate, 19,777 glyphs, 0 errors, 0 ambiguous.**
     382 are the canonical layout across 15 builds (THPS1/2/3/4 PSX, THPS2 DC, Spider-Man ×4 PSX
     + DC + PC, SM2:EE ×3). The remaining 60 (48 THAW, 12 THPS3-PS2) are genuinely unrelated
     formats sharing the extension and are reported as **skipped, not errors**.
   - **One variant**, THPS2 DC `LEVSEL.FNT`: 12-byte records `{widthUnits, height, baseline}` —
     no advance width — and **no embedded CLUT**. Established by exact EOF plus a per-pixel
     render check. **Its 4bpp pixels are HIGH nibble first**, opposite to the PS1 layout;
     measured via a horizontal-smoothness metric that picks low-first for all 382 paletted files
     and high-first for this one alone. Exported as coverage alpha on white, because with no
     CLUT and no decompiled Dreamcast loader the values' meaning is not established.
     **Post-ship correction (2026-08-18)**: this file first shipped decoded low-first, i.e. with
     every horizontal pixel pair swapped. It slipped because its test pinned counts, size and
     offsets but no pixels, while the paletted fixture had a full atlas SHA — and because a
     pairwise swap leaves glyphs readable as their letters, so the visual check I ran could not
     discriminate. Fixed with a per-layout nibble order, an atlas SHA for this file, and a
     corpus test that re-derives both orders from the data.
   - **Transparency is by CLUT value**: `0x0000` is the PS1 GPU's not-drawn texel and every
     paletted file carries one. Bit 15 (STP) is set on 91% of entries and is ignored —
     `Font::draw` issues the glyph's main pass with `Transparent = 0`, so the hardware never
     consults it. Two Spider-Man `sp_fnt01` copies hold a magenta entry (the key the PSX
     *texture* path uses) but **no glyph in the corpus references one**, so it is dead data.
   - **The character map is NOT file-derivable and is never inferred.** `Font::CharMap` is
     runtime state game code assigns. Measured proof: the March-2000 THPS2 prototype's
     `player/s2bio.fnt` has exactly 74 glyphs and maps 48→`a` precisely as decompiled, while
     retail THPS2's `s2bio.fnt` has 94, puts `_` at 48, shifts lowercase to 49-74 and appends 19
     accented glyphs. Same game, same filename, different ordering. The manifest therefore emits
     `characterMapStatus: "notApplied"` with all three decompiled modes published as candidates;
     `fnt --charmap 0|1|2` opts in and flips it to `"appliedFromCallerArgument"`.
   - **Known limit, pinned by a test**: exact EOF makes the two layouts unambiguous across the
     whole shipped corpus but is not a proof for arbitrary bytes — a truncated paletted font can
     coincidentally satisfy the 12-byte reading. The paletted layout is always tried first, so
     only an already-damaged file can fall through.
2. ✅ **`.seq` PSY-Q MIDI sequences — SHIPPED 2026-08-17.** `SeqFile` (pQES header + MIDI event
   stream with running status), `VabProgramSet` (programs→tones→PCM with SPU loop points), and
   `SeqSynthesizer` (SsPitchFromNote pitch — the same formula the SFX cue resolver pins — SPU ADSR
   envelope stepped from the register words, sample-loop sustain, tempo map, equal-power pan)
   render SEQ+VAB→WAV. Routed: CLI `audio` (`.seq`), GUI Audio tab (needs the same-stem `.vab`
   sibling, resolved via the companion API so archive entries work). All 11 Apocalypse songs render
   audibly (corpus-pinned); `city` really is a 17.5-minute piece (notes to tick 1,026,413 — checked,
   not a parser bug). **Format lesson pinned by test**: VAB `programCount` counts USED programs and
   the tone region packs used slots in ASCENDING SLOT ORDER — Apocalypse's music banks use slots
   60–75, so the slot-indexed tone walk (decomp-correct for SFX banks whose used slots are 0..N−1)
   silences everything. Documented approximations: single pass (no loop-marker repeat), ±2-semitone
   bend range, linear resampling, envelope without the SPU's stepped quantisation.
3. ✅ **`title_h.zlb` — already handled** (correcting this survey's own 2026-08-17 claim, same day:
   the bitmap facade routes `.zlb` as gzip-wrapped RLE/BMR — `RleImage`/`BitmapFile` — and
   `title_h.zlb` converts to PNG today). Not a gap; retained here only so the extension census
   stays reconciled.

**Classified, deliberately not converted:** `.rec`/`.dem` demo replays (input streams; `.rec`
already documented byte-identical on N64), `.prk` park saves, `.rel` relocation tables,
`amap<N>to<M>.dat` (80 THPS2 files, 2 KB each — anim-index permutation tables between skater rigs;
header + 0..N byte permutations visible in the raw), `trickdb.dat`/`sizes.dat`/`prefs.dat` (small
data tables/manifests), `.psh` (already parsed as part-name headers). THPS2's 1,283 `.bmp` route
through the existing BMP facade.

### 🔶 THAW GameCube platform — textures ✅ 2026-07-07, meshes ✅ 2026-07-08, collision inspection ✅ 2026-08-10, proof-bound rendering ✅ 2026-09-02
- Source: 2026-07-07 corpus census + format RE sessions (textures 07-07, meshes 07-08).
- ✅ **Textures done**: `.tex.ngc` (722) + `.img.ngc` (2,647) parse via `NgcTexFile` (extended from the
  earlier committed skeleton). Format (established via PC↔GC Rosetta pairs, pixel-exact on `anl_pigeon`,
  MAE 0.46 vs the PC DXT decode): dictionary header (u8 ver=1, u8, u16be count, u32be tableOffset=8) +
  count×32B record table + data region. Record: ver=4, depth=32, u16, u32be checksum, u16, log2W, log2H,
  mips, gxFormat, u8, u8, u32be colorSize, u32be dataOffset (ABSOLUTE), u32be alphaOffset (ABSOLUTE,
  FFFFFFFF=none), u32be 0. `.img.ngc` = bare record, no dict header. Only two GX formats in the corpus:
  CMPR (0x0E, 4,266 records — 8×8 tiles of four DXT1 blocks, BE colors, MSB indices; DXT5-equivalents get
  a SECOND CMPR chain at alphaOffset whose GREEN channel is the alpha, the same trick as THUG GC
  `texture.cpp`) and format byte 0x06 (224 records) which covers BOTH RGBA8 (180 — 4×4 tiles, AR/GB 32B
  planes; real height = colorSize/(4·width)) and C8+RGB5A3 palette (44 — CAS icons/banners; distinguished
  arithmetically: colorSize == width×rows + 512). Images are stored bottom-up (y-flip on decode).
  **Sweep: 3,369/3,369 files, 4,630+ textures, 0 failures** (one 32-byte count=0 stub parses as empty).
- ✅ **Meshes done** (`.skin.ngc` 588, `.mdl.ngc` 134): GX display-list parser shipped 2026-07-08 as
  `Core/Formats/Mesh/XbxScene/NgcSceneFile.cs` (produces XbxScene, shares the Xbox glTF writer; routed
  via `mesh` CLI + GUI Mesh Converter). Container = THUG GC `s_plat_load_scene_guts` layout with a
  64-byte extended header (0xAAFFEEFF sentinel at +0x2C). Full spec in CLAUDE.md. Key discoveries:
  skin positions are s16**/32** (THAW halved THUG's 1.9.6 shift), UVs s16 (u=a/1024, v=1−b/1024),
  material passes reference textures by INDEX into the companion `.tex.ngc` (record order).
  Rosetta-validated: pigeon exact vs PC (46 verts/45 tris both), ped_baller UVs+normals vs PS2 decode.
  **Sweep: 722/722 files, 427,343 triangles, 0 failures, 0 glTF validator errors**; textured renders
  verified (ped_baller Lakers jersey, pigeon alpha-cut wings, board_default griptape+trucks).
  The shipped parser and its corpus coverage are pinned by `NgcSceneFileTests`.
- ✅ **Collision structural inspection shipped 2026-08-10** (`.col.ngc`, 722 canonical loose files;
  680 apk-expanded copies are accepted but deliberately excluded from the oracle): `NgcColFile` + `ngccol` CLI emit a
  schema-v1 JSON manifest per file. The 2026-07-07 layout notes were partly wrong; the engine-exact
  layout was transcribed from the THUG source's `__PLAT_NGC__` paths (`NxScene.cpp read_collision`,
  `CollTriData.h/.cpp`) and corpus-verified byte-exact on **722/722 canonical files**: 24B BE header
  (version=10, numObjects, totalVerts, totalFaces, ssRows, ssCols) + 32B scene bounds + 64B object
  records (checksum, u16 flags, u16 numVerts, u16 numFaces, u8 small-face selector, u8 fixed-vertex selector,
  u32 faceByteOffset, bboxMin/Max 4×f32,
  u32 0 = the runtime vertex-pool pointer slot, u32 bspNodeByteOffset, u32 cornerIntensityByteOffset
  = 3×cumulative faces, u32 pad) + **totalFaces×3 per-corner INTENSITY bytes** (the region the old
  note called a "0xFF-wiped vertex region" — 0xFF is just uniform full intensity, valid data; 78 of
  722 files carry varied authored values) + align4 + 10-byte BE face records + 2-byte pad when the
  face count is odd + u32 node-array size + 8-byte BSP nodes (leaf when byte 3 == 3: u16 numFaces,
  pad, axis, u32 pool offset; interior: i32 split point with axis in the low 2 bits, u32 child byte
  offset with a left-is-greater low bit) + u16 face-index pool to exact EOF. Canonical corpus: 819 objects,
  237,175 declared external vertices, 411,057 faces, 35,944 leaves + 35,125 interior nodes, max tree depth 7;
  face indices stay within cumulative declared object ranges in 693 files and cross them in 29; the ssRows/ssCols grid
  has NO cell table — the engine builds supersectors at runtime. **Vertex positions are absent BY
  DESIGN, not wiped**: `InitCollObjTriData` binds `mp_raw_vert_pos` to the render scene's
  `mp_pos_pool`, which answers the old "needs a study of how the engine sources the vertices" —
  the collision file itself still provides neither positions nor an ownership oracle. The `ngccol`
  inspector therefore remains topology/metadata-only and synthesizes no geometry. Pinned by
  `NgcColFileTests` (fixture + strictness + corpus totals).
- ✅ **Proof-bound collision rendering and overlay shipped 2026-09-02.** `NgcSceneFile` now retains
  the source-order scene-wide float pool and per-object s16/32 skin lists. The shared `mesh`, GUI,
  GLB, and default-off `mesh --collision-overlay` paths admit a `.col.ngc` only when ownership and
  coordinates are exact: one same-directory loose `<stem>.mdl/.skin/.scn.ngc`, or one COL plus one
  typed render entry in the selected PAK directory; matching object count/checksum/order; exactly
  one position-pool kind whose total count equals `totalVerts`; finite positions; every face index
  in range; every referenced point inside both object and scene bounds (the audited collision/render
  compiler precision requires a 1/32-unit tolerance for both static and skin winners); and at least
  one non-degenerate triangle. No basename proximity,
  cross-directory/hash search, size-only pairing, or synthetic coordinate fallback exists. Scene
  overlay is fail-open; standalone collision conversion and the GUI collision row are fail-closed.
  The canonical loose audit accepts **210/722** families (**23 static MDL + 187 skin**), declines
  **495** incompatible non-empty families, and identifies **17** authored-empty families. The 680
  PAK-expanded typed-entry copies independently yield **225 accepted, 289 declined, and 166 empty**;
  direct hash-named archive rendering is additionally pinned on a real APK/MPK owner. Pigeon proves
  both a 45-triangle standalone GLB and a 45-triangle translucent scene overlay. Tests cover wrong
  owners, multiple typed candidates, malformed peers, pool ambiguity, checksums, counts, ranges,
  bounds, empty/degenerate geometry, loose corpus totals, and archive ownership.
- ✅ **`.apk.ngc` / `.pak.ngc` archives — extraction shipped 2026-07-09, offset model CORRECTED
  2026-07-10.** They are big-endian Neversoft PAKs (sentinel-detected; `PakArchive` handles both
  endians). `.mpk.ngc` = the companion DATA file (like PS2 .pab), not padding — 3,603 of 4,424 are
  32-byte stubs (self-contained apk), 821 carry real data for cutscene apks. GC quirks vs PS2:
  name QbKey at +0x0C, flag 0x80000000 = data-in-pak (absent = companion-resident at RAW stored
  mpk offsets). **All PAK data offsets (LE and GC in-pak) are relative to the entry's own header
  position** (Queen-Bee `HeaderStart + FileOffset`); the 2026-07-09 "hoisted tiling" model was a
  near-equivalent approximation, and the original absolute-offset reads silently garbled every
  multi-entry LE pak. Signature-validated 2026-07-10: PS2 12,120 + PC 12,756 + GC 14,325 payload
  hits, 0 mismatches; `PakArchiveTests` pins the offset rules. 48 `*_sfx.pak.ngc` = raw audio
  blobs (skipped). Routed: `archive` CLI, `unpack`, GUI Archive Extractor.
  ✅ **Sample/Builds pak-extracted subtrees were regenerated after the offset fix on 2026-07-16.**
  A 2026-08-11 source-slice audit matched **748/748 payloads byte-for-byte** (8,183,728 bytes,
  zero missing or mismatched): PS2 `qb` 266/266, `rocket` 130/130, `storyselect` 8/8; GC `BH11`
  67/67, `qb_i` 269/269, and `storyselect` 8/8.
- ✅ **THAW QB decoding — shipped 2026-07-10 for ALL THREE platforms** (`.qb.ps2`/`.qb.wpc`/
  `.qb.ngc` + `.sqb.*`): THAW uses the sectioned QB format (Guitar Hero family, Queen-Bee
  reference at `Sample/queen-bee`), NOT the raw THPS3-THUG2 token stream and NOT "BE tokens with
  a size prefix" as previously guessed. `QbSectionParser` (auto endian + old/new info-encoding
  detection, LZSS scripts, THAW tokens 0x47-0x4A, inline-script struct items) synthesizes classic
  token streams for the existing decompiler. Sweep: 11,909/11,909 files, 49,755 scripts, 0
  failures. **Name resolution 97.1% PS2 / 99.2% PC / 89.1% GC** via 137,054 re-hash-validated
  pairs recovered from the shipped `dbg.pak` debug archives and embedded as
  `QbKeyNames.ThawDbg.txt`.
- ✅ **THAW animation family — shipped 2026-07-10 for ALL THREE platforms** (`.ske` + `.ske.ngc`,
  `.ska` + `.ska.ngc`): the GC files are field-for-field endian mirrors of the PS2/PC ones, and
  NO platform parsed them before (the old "BE payload" framing was wrong twice over). THAW SKE
  (`ThawSkeletonFile`, 973/973): u16 version=1 + u16 hdrSize=0x30 header, vec4[N] local
  translations, mat4[N] PRECOMPUTED inverse bind matrices, name/parent/flip QbKey arrays.
  THAW SKA v0x28 (`SkaFile.ThawParser`, ~21,350/21,350 incl. P8/THPG grammar-verified): THUG
  compressed grammar + THAW deltas verified against the THAW PS2 ELF key readers (bit16 scalar
  table, bit15 compact bytes, bit8 u16 timestamps, bit19 partial mask, bit28 hi-res float
  camera/object masters, bits 14+17 additive translations). Key blobs + standardkey tables ship
  raw LE even on GC. **The rumored cutscene `.ska` "descriptor block with embedded cam pak path"
  does NOT exist** — that data is `<name>_cam_pak_info.qb.ngc`, a sectioned QB string array the
  QB parser already handles. Camera masters export as node-TRS GLB rigs via `ska`; camera masters
  do not carry the bit-24 QbKey names used by object masters, so their sole track can retain a
  checksum-style fallback name.
  Durable coverage: `ThawSkeletonFileTests`, `ThawSkaFileTests`, and the cross-game animation
  corpus tests. **Bit28 custom events shipped 2026-08-10:** the endian-aware reader consumes the
  bounded `{u32 timestamp, u32 type, u32 totalSize, payload}` records after Q/T, decodes the two
  live THAW payloads (type 1 horizontal-FOV-radians float, despite its historical
  `CHANGE_FOCAL_LENGTH` enum name, and type 4 RunScript QbKey), and preserves
  unknown payloads losslessly. The CLI writes a stable `<stem>.ska.json` inspection sidecar only
  when events exist. A 20,425-file THAW sweep pins 100 physical event-bearing files (36 PS2,
  35 GC, 29 PC), counts 2–121, only types 1/4 and 16-byte live records, exact tail consumption,
  and PS2/GC typed equality. `timestamp` stays a raw integer: its THAW v0x28 runtime unit is not
  proven and some event timelines extend beyond their local Q/T clip. **Static authored camera
  projection shipped 2026-08-10:** one-track PLATFORM camera masters with a valid timestamp-zero
  type-1 event now attach a native perspective camera to the animated track in both glTF and
  Blender. The horizontal source value converts to vertical FOV at the engine's canonical 4:3
  aspect; Blender binds the camera to the same animated pose bone with no view-axis correction.
  `ska --format glb|blend|both` routes every skeletal/object/camera branch through the shared export
  service and defaults to GLB. Later FOV events remain JSON-only because neither path implements
  lens animation. The 347-file GC camera census pins 35 eligible projections, 312 TRS-only rigs with
  no authored FOV, 391 total FOV events, and zero non-camera FOV files. Near/far `1/100000` are an
  explicit broad PS2-derived export policy, not SKA metadata. Real PS2/GC StorySelect exports pin
  matching `0.13479553` vertical FOV in GLB plus successful skeleton-only `.blend` output.
- ✅ **Explicit THAW/legacy QbKey track binding shipped 2026-08-10.** Gameplay SKAs do not name
  their tracks, so `ska --animation-ske <source.ske> --ske <target.ske>` now takes the source rig
  explicitly and maps only exact numeric bone QbKeys. Duplicate/zero names, malformed hierarchies,
  an unmapped root, a skipped parent, or any changed mapped parent edge reject; equal bone counts never
  authorize index binding. The proven `thps7_human` 52-bone source → THUG2 `thps6_human` 50-bone
  target maps 48 tracks, drops source indices 15/16/27/28, maps 17→16, and leaves target shoulders
  15/26 in bind pose. A 330-file GC skeleton audit found 133 52-bone files but **47 distinct ordered
  QbKey identities**; canonical `thps7_human` occurs in only 29, proving count is not identity.
  Skeleton-only exports and an explicitly supplied ordinary PS2 `.iskin.ps2` already authored for
  the target skeleton use the map. The general mesh parser already routes native THAW `.skin.ps2`,
  discovers the skeleton, selects a same-stem PC/Xbox weight companion, and transfers its weights.
  The narrower `ska --skin` path does not select that THAW subformat or remap skin joint indices, so
  its supplied skin must already match `--ske`. The Animations pane now accepts `.ske`, `.ske.ps2`,
  and `.ske.ngc` source rigs as extracted files or direct entries in root/nested archives; full virtual
  paths preserve duplicate identity, and a disposable catalog keeps every required handle alive through
  parse and exact-map validation. One captured plan reaches preview, GLB, and Blender export, while
  invalid, cancelled, or superseded loads preserve the previous rig and stale queued previews are
  rejected. The real GC `global_s.apk.ngc` fixture loads 52 bones and maps 48 after catalog disposal.
  Native Xbox/PC/GameCube scene weights now have caller-explicit CLI and GUI routes. `mesh --ske` accepts
  direct/prepared/exact-stem-directory rigs; the Meshes & Characters tab keeps a parsed skeleton
  selection independently on each eligible Xbox/PC/GameCube entry from an extracted file or a direct/nested
  archive entry and snapshots it for preview, GLB, Blender, PNG, GIF, and batch work. The animation archive
  policy remains narrow while the mesh policy additionally admits `.ske.xbx`; full virtual identities and
  backend ownership survive through parse, after which only the self-contained skeleton remains. Both paths
  use the same global emitted-corner influence preflight, with
  normalized four-weight output and byte-identical rigid fallback for missing, malformed,
  incompatible, or non-unit-scale inputs. Non-worldzone entries retain scale 1 even in mixed batches,
  and a rig change cannot reuse a stale cached render. THUG2 Xbox, THAW WPC, and THAW GameCube pigeon
  fixtures each pin the exact four-joint rig, 46 vertices, and 45 triangles; GameCube direct/prepared
  GLBs are byte-identical and the Blender file reopens with one bound four-bone armature at rest. Its
  `.ske.ngc` rigs ship inside mission/worldzone archives rather than beside loose skins. The real THUG2
  `skeletons.prx` exposes 58 skeleton entries; its selected pigeon rig is byte-identical to the loose fixture
  and produces the same GLB after catalog disposal. Automatic rig inference remains outside this slice.

### ✅ STR (PS1 MDEC) long-stream drift — RESOLVED 2026-08-09
- The historical mismatch was not a VLC defect. The original demuxer copied all 2,296 bytes after
  the XA/video headers from each Mode-2 Form-1 sector, inserting 280 EDC/ECC bytes after the valid
  2,016-byte video piece. The recorded bit-16,069 divergence is exactly five bits into that first
  invalid tail.
- Commit `d13e356` already switched assembly to the XA Form bit and the correct 2,016-byte Form-1
  piece. On the audited clean Apocalypse frames, current assembled bytes match jPSXdec's full
  pipeline, and its standalone STRv2 reader reaches all 1,800 blocks when fed those bytes. The
  bundled corpus contains 323 recognized STR videos, all Form 1; no Form-2 video fixture is
  currently known.
- `MdecDecoderTests` now pins the 2,016-byte synthetic sector boundary, a multi-sector Apocalypse
  frame's exact jPSXdec assembly SHA plus the local RGB regression SHA, recursive fixture discovery,
  complete-frame counting, and explicit rejection of unsupported or incomplete frames. Direct
  preview converts such a rejected frame to opaque black instead of terminating playback, matching
  the MP4 converter's existing fail-soft behavior. The first
  yielded frame of the damaged SM2 Final `E5M6` fixture (header frame 2) is separately pinned: the
  jPSXdec standalone decoder rejects our complete assembly at the same macroblock and bit. Its
  normalized RIFF payload differs from the byte-identical Prototype/Rev1 copies in only 604 bytes
  across 30 of the first 40 sectors, confirming damaged input rather than a framing rule. There is
  no remaining STR framing backlog item.

### ⚪ PPV container — RESOLVED 2026-07-10: not a Neversoft format (out of scope)
- The *Spider-Man (2000-2-4, PSX — Prototype)* build is a **multi-game demo disc** (SCED_026.36:
  HARNESS.EXE menu + DD/GK/INFEST/MILLE/POCKET/RAYMANII/REVOLT/SYDNEY/TENCHU2/XMEN/SPIDEY/WTC).
  Every `.ppv` (incl. `WTC/FE/FE.PPV`), every `.zoo` (79), `.bfx`, `.dhs` etc. lives under `WTC/`
  = **TOCA World Touring Cars (Codemasters)** — a different developer's engine sharing the disc.
  The actual Spider-Man demo is `SPIDEY/` (CD.WAD/CD.HED — already supported).
- This is why the 2026-07-09 exe hunt found nothing: the "overlay" was never a Spider-Man
  overlay. WTC's own code ships as plain files (`WTC/WTC.EXE`, `GAME.OVL`, `TIMTRIAL.OVL` — no
  disc dump needed), though they carry no literal `BVmC`/".PPV" references either (loader likely
  resolves via .ZOO tables). Disposition: out of scope like `.bik` — Codemasters formats belong
  to Codemasters tooling. Same applies to the `.zoo`/`.bfx` census entries.

### ✅ Spider-Man TRG POWERUP placements from items.psx — SHIPPED 2026-07-23
- Source: user request 2026-07-22 ("if we're going to place objects, we should probably also see if
  the trg files mention objects directly or by filename, as I believe items.psx is used across most
  levels") + the l1a1 "?" investigation.
- Findings so far: TRG never references models by filename (0 filename strings). PLATFORM/MANIPOB
  nodes reference bank models by NAME CHECKSUM (already placed); **POWERUP nodes carry a numeric
  `pickupType`** (proto census: type 8 ×49, 15 ×25, 14 ×21, 11 ×6, 16 ×6 across 22 levels) that
  indexes a game-code item table selecting an items.psx MODEL INDEX (`CItem::InitItem("items")` +
  `mModel = N`, spidey-decomp `ob.cpp`/`shell.cpp`). Engine-proven mappings: in-world "?" marker =
  items model 5 (`Spidey_CIcon`, scale 2048 = ×0.5); web projectiles use the items region at the
  same half scale. items.psx (proto) = 6 models: 0 white wedge, 1 blue gear (web cartridge), 2
  yellow gear, 3 grey gear, 4 grey dome, 5 the "?".
- Shipped 2026-07-23 instead: `PsxItemsBankSubstitution` — bank meshes sharing a name hash with an
  items model render from the items copy (fixes the l1a1 "?" to its vivid staggered-blue pulse).
- **Table PINNED 2026-07-23** by disassembling the `CPowerUp` ctor's `switch(mType-8)` in both PSX
  binaries (Capstone via `dis_crossgame.py` in the external THPS2 decomp project; ctor found by xref'ing the
  `"items"` string + the 1.0-confidence `Spool_GetModel` anchor). The ctor stores its type arg to
  `mType` (0x38 proto / 0x34 final) then loads an items.psx mesh-name hash per case →
  `Spool_GetModel(hash, ItemsRegion)`. `Trig_CreateObject` passes the TRG node's `pickupType`
  straight in, so **TRG pickupType == ctor mType** (verified: census values map to sensible models
  — 8=web cartridge matches the user's screenshot, 11=the "?"). The tables were read directly
  from each shipped executable, resolved against items.psx, and cross-checked against the TRG corpus.
  **No per-type scale** — the ctor's
  0xDE/0xD8/0xD0 stores are spin/counter fields, not mScale; the spidey-decomp `Spidey_CIcon` ×0.5
  is a DIFFERENT class (the HUD nav icon), not the type-11 CPowerUp "?".
  - proto ctor @0x800349CC, jumptable @0x800B03A4 (mType 8..16):
    8→0x17646B0D (web cartridge/m1), 9→0xC6739C3B (yellow gear/m2), 10/12/13→default,
    11→0x7F648179 ("?"/m5), 14/15/16→0x7E74F3D4 (grey gear/m3). Census {8,11,14,15,16} 100% mapped.
  - Apr-29 proto ctor @0x8001EA70, jumptable @0x80091570 (mType 8..16, 7-mesh items.psx):
    8→0x17646B0D, 9→0xC6739C3B, 10→default, 11→0x7F648179, 12→0x12820A41 (m6), 13→0xC6739C3B,
    14/15/16→0x7E74F3D4. A hybrid (Feb's 9-case structure + Sep's 12/13 assignments). Census
    {8,11,12,13,14,15,16} 100% mapped.
  - final ctor @0x8001DE00-region, jumptable @0x80093674 (mType 8..18, 9-mesh items.psx):
    8→0x17646B0D, 9/17→default, 10&18→0xA092D785 (m7, the ubiquitous final pickup — type 18 ×210),
    11→0x7F648179, 12→0x12820A41 (m6), 13→0xC6739C3B, 14/15/16→0x7E74F3D4. Census 100% mapped.
  - **Census subset {8,11,14,15,16} is identical across ALL THREE builds** (three confirmations);
    non-census types drift gradually Feb→Apr→Sep but no type ever maps to two DIFFERENT non-default
    models. The per-build items.psx (6 / 7 / 9 meshes; m6=0x12820A41, m7=0xA092D785 presence)
    selects the right table. April-29 added to Sample/Builds 2026-07-23.
- **Placement layer SHIPPED 2026-07-23** (`PsxPowerupPlacementResolver`): POWERUP nodes render as
  items.psx pickups (translation-only — POWERUP nodes carry no angles), merged into the single items
  geometry pass in `MeshModelParser.PopulatePsxLevelObjectCompanion`; works with or without an `_o`
  bank (the bank layer swallows its own failures so a missing/malformed/unreadable bank still emits
  pickups). **POWERUP is authoritative for pickups**: bank objects whose mesh a POWERUP node already
  places are suppressed (`PsxItemsBankSubstitution.Split(suppressHashes:)`) — l1a1's bank "?" drops
  in favour of its 3 POWERUP "?" nodes; a bank pickup with no POWERUP node (the demo level lda1's
  "?", the only such case corpus-wide) still redirects to the items copy. Required a TRG parser fix:
  `ParsePowerup` now skips the node's link list before reading position (`ReadLinks`), which the old
  "read link COUNT only" code botched — POWERUP nodes with links (the "?" markers, 4-5 links each)
  had million-unit garbage coordinates. Grounded spawn semantics were completed later: Spider-Man
  grounded pickups query the level terrain and apply the engine's 128-unit hover, while the matched
  THPS/Apocalypse paths retain authored Y unless an entity is on their separate dropping path.
  Durable coverage: `PsxPowerupPlacementResolverTests`, `PsxItemsBankSubstitutionTests`,
  `TrgFileTests.Parse_SpiderManPowerupWithLinks_*`.
- **Generalized to the PS1 lineage 2026-07-24** (`MeshCompanionResolver.TryResolvePsxLevelCompanions`):
  THPS1/THPS2 get the FULL bank + PLATFORM-overlay + POWERUP stack. THPS pickup table transcribed
  verbatim from the **matched THPS2 decomp** `POWERUP.cpp` `CPowerUp::CPowerUp` (`switch(mType)`,
  no `-8`): 4/5/6/10/15 = K/S/A/T/E letters, 16/18 = tape, 21-32 = bonus/money, medals 0x664-0x666
  omitted (they spool from `skmedals`, not `items`). Letter/bonus hashes are byte-identical to THPS1's
  items.psx → one hash-keyed table serves both; `SelectTable` picks it by the 'S' letter 0x311D55D4.
  PLATFORM overlay verified coincident (THPS1 24/30, THPS2 12/17 refs at δ≈0, div 2.25). 6-12 proto
  added to Sample/Builds (9-mesh items = final table). See memory `psx_crossgame_level_objects.md`.
- **Apocalypse SHIPPED 2026-07-24** (full parity): the pickup table was reverse-engineered from
  `apocalypse_final.exe` (SLUS_003.73, no SYM) by **signature-matching against the THPS2 decomp** —
  located the "items" string + the items.psx hash-load cluster at 0x8001FEC0, read the `CPowerUp`
  ctor jump table @0x800A11EC (keyed by mType-1), and cross-checked
  against the TRG POWERUP census (types 4/5/6/10/14/15/16 = 176/281 nodes; 14/15/16 are three spin
  variants of the shared grey-gear 0x7E74F3D4; 17=plus_one region, non-items). TRG pickupType == mType,
  no per-type scale, node scale div 2.25 verified in-bounds. POWERUP + PLATFORM overlay both enabled
  (`ApplyTriggerOverlay=true`); the overlay's Apocalypse refs are mostly authored BADDY/PLATFORM spawn
  re-instances (worth a visual eyeball, one-line revert via the flag if too busy).

### 🔶 PSX level-object animation export (skeletal path; traffic snapshot shipped)
- Source: decomp contract `thps2-psx-proto docs/level_object_anim_binding.md` (2026-07-09; RunAnim/CycleAnim/CalculateAnimOrder PERFECT).
- Binding chain is fully known: item→region by filename (`Spool_FindRegion`), stream selected by the item's own `mAnim` index into the region's `pAnimFile` table (stride 8, count-prefixed — NOT stream-i→item-i), per-bone positional with parent tree from `pHierarchy` (`mapTable[bone]=parent`), cross-model retarget by name via CalculateAnimOrder. `has pAnimFile ≡ IsSuper` — animated level objects (traffic cars etc.) are CSuper instances on the same skeletal path as characters.
- Shipped 2026-08-10: `PsxPlacedTrafficResolver` handles the proven D5–DA constructor table and separate traffic `CSuper` files, first-road-node placement, initial Y offset, instance roots, skins, and embedded loop 0. Script-reachable non-startup nodes are deliberately behind a default-disabled snapshot group because trigger time, repeats, suspension, and route translation are not reconstructed. Final Downtown emits three taxi rigs (+711 triangles); San Francisco emits one van and two cable cars (+318); prototype Downtown uses the proven `taxi.psx` fallback. Distinct GLB/Blender roots and shared per-source actions are regression-pinned, and optional source failures roll back atomically.
- The former plan was based on a false premise: prototype `skdown.psx` has 836 level object records but no 0x2A/0x2C animation chunk. Traffic animation resides in separate TRG-selected super files. No animated-door fixture was found, and tag 0x45 remains a separate UI/effect path.
- ✅ **Tag 0x45 is the one NAMED animation table in the PSX format — surfaced 2026-08-19.** Its
  group header is not two opaque words but an 8-byte NUL-padded ASCII name followed by
  `u32 animCount`, then `animCount` 8-byte entries. `psxanim` printed those bytes as hex, and its
  entry path could never reach them anyway: the table ships in **mesh-less** files, so the
  post-mesh walk bailed with "No mesh data". `PsxMeshFile.TryGetChunk` now finds the chunk
  directly and `PsxAnimDumpWalker.ReadPackedName` decodes the name (falling back to raw words
  when the bytes are not a name, so a mis-framed packet stays legible). Corpus: **138 files, 474
  groups, 2,630 anims**, pinned by `PsxAnimDumpCommandTests`. Real names now visible —
  `FONTSMLL`, `SHADOW`, `SMOKE`, `ribbon`, `Buttons`, `EXPFIRE`, `FIREBALL`, `WebKnot`,
  `SpiderBa`, `RhinoBol`, `Compass`, `WebCart`, `LoadIcon`, `Reticle`, `Slime`, `SymDrop`.
  `FONTSMLL` is the same string `Font_Init` passes to `Spool_FindAnim` (`FONT.cpp:146`), which
  independently confirms the framing. **Scope**: this is the sprite/effect path only — it does
  *not* name skeletal clips, and no character `.psx` carries a 0x45 chunk.
- What's left: runtime-accurate script timing/repeated spawns/road motion, plus any other placed skeletal family once a named fixture and binding contract exist. Do not broaden the traffic snapshot into a claim of general placed-object animation support.

### ⚪ PSX 2P/HORSE "SP-instance leak" — INVESTIGATED 2026-08-20, does not reproduce
- The report: `skny_2.glb` carries the same eight `obj_barrier01` instances as `skny.glb`
  "despite `SkNY_O2` holding 2 objects", concluded to be one-player re-instances leaking through
  `PsxLevelObjectPlacementResolver`, which overlays every PLATFORM/MANIPOB node with no region
  filter. Measured, the premise is wrong on both halves.
- **`obj_barrier01` is one of `SkNY_O2`'s two objects.** Eight placements is what the engine
  produces: the two-player region shares `skny_t.trg`, so the same nodes run, and the only gate
  is whether the node's model checksum resolves in the bound bank. It does.
- **There is no spatial region filter to add.** `Trig_InitialParseTRGFile` (TRIG.cpp:3090,
  PERFECT 130/130) only chooses AUTOEXEC2 over AUTOEXEC when two players are active — already
  implemented by `PsxTrgBootScript` — and `Trig_ParseTRGFile` walks every node regardless of
  region. The engine's bank scoping is `pCurrentObjFile` alone, which the resolver already
  enforces by looking each node's checksum up in the bound bank.
- The leak is not expressible by the current code: `Resolve` returns placements keyed by BANK
  OBJECT INDEX, so every placement belongs to the bound bank by construction.
- What made it look like a leak: `dt_park_rail03` appears in both outputs, but converting each
  level with no companions shows it is LEVEL GEOMETRY in `skny.psx` and a BANK object in
  `SkNY_O2` — same name, unrelated sources. Meanwhile `skny_2` correctly drops
  `obj_ny_banks_backboard` and `obj_token01`, which are one-player-bank-only.
- Pinned by `PsxVariantBankScopeTests` so the claim is checkable rather than re-litigated.
  Re-open only with a variant where an emitted mesh is in NEITHER the bound bank nor the
  region's own geometry.

### ⚪ THUG2 precompiled `.skin.ps2` without `.iskin.ps2` — no shipped orphan demonstrated
- Re-audited 2026-08-10. The old extension census counted physical preload copies as unique unsupported assets. THUG2 PS2 contains 2,478 `.skin.ps2` copies but only 739 unique payload hashes. Every one of the 739 canonical files has a same-stem `.iskin.ps2`; all 1,739 apparent bare copies are byte-identical to one of those paired canonical skins. Archive and directory scans already prefer the higher-quality intermediate file, so every shipped unique model has a supported source.
- The 746 non-THAW-conformant entry tables must continue to reject rather than replay through `ThawPs2SkinFile`. A native THUG2 precompiled VIF decoder is now evidence-gated, not active backlog: re-open only for a genuinely unique orphan fixture or an explicit detached-copy conversion requirement.

### 🔶 N64 ROMs (THPS1/2/3 + Spider-Man) — archive/texture/mesh/embedded-animation foundation shipped
- Re-verified 2026-08-10. The old “container mapped, ERZ compression unRE'd” description is obsolete. `ErzDecoder` mechanically implements both v1 and v2 with emulator-derived SHA fixtures; `N64RomArchive` walks the master directory and reassembles stream groups; `N64AssetCarver` emits typed assets; `.z64` opens through `ArchiveFileSystem`; N64 textures and render-bank meshes route through the GUI/CLI. Corpus carve counts are pinned at 2,176 / 3,962 / 3,313 / 4,286 assets, and every render bank decodes with in-bounds indices.
- The render path also covers descriptor-bound textures, per-vertex matrix placement, alpha modes, the ROM light rig, and coplanar/semi-transparent separation. Do not reopen ERZ, `.z64` routing, “missing ROM filesystem,” or “render-bank codec” from older notes.
- Concrete residuals and completed follow-ups:
  - ✅ **Stored texture mip export — SHIPPED 2026-08-10.** The earlier `abutton` premise was false:
    format word `0x0014` is a canonical RGBA16 top plus a full-resolution aligned 4bpp auxiliary
    coverage/alpha plane, not an 8×8 mip. The parser now publishes only exact, fully consumed mip
    chains: 36/9,459 dictionary records (THPS1 7, THPS2 9, THPS3 12, Spider-Man 8), with 3–5 stored
    lower levels across RGBA16/CI4/IA4/IA8/I4/I8. The CLI, legacy conversion helper, and Texture-tab
    extraction preserve `{stem}.png` and add `{stem}_mipN.png`; preview and model embedding remain
    level zero. `N64TexFileTests` pins the corpus census and all five RGBA SHAs of a real IA8 chain.
    The 69 `0x0014` auxiliary planes are identified and reported but deliberately not applied to the
    exported alpha until a separate runtime-combine/visual oracle approves that behavior change;
  - ✅ **Nintendo Sound Tools PTR/WBK inspection — SHIPPED 2026-08-10.** The ROMs do not contain
    SGI CTL/TBL `ALBankFile` graphs. `N64SoundToolsBank` instead consumes the exact big-endian
    `N64 PtrTablesV2` descriptor graph together with its paired `N64 WaveTables` payload: checked
    file-relative wave/book/loop pointers, the unaligned final-record boundary, canonical 16-byte WBK
    packing, base-note/coarse-tune bytes, signed fine-detune workspace cells, and all required padding.
    Exact WBK magic gives the four raw wavetable leaves the typed path `audio/000.wbk.n64`, while
    every other uncompressed audio leaf remains `.bin`. `n64-audio-inspect <game.z64> -o bank.json`
    pairs the unique carved assets by content magic;
    standalone PTR input requires an explicit `--wave`. Both routes produce byte-identical schema-v1
    JSON with `sampleRate: null` and cue mapping marked unresolved. The four-ROM corpus pins 1,775
    waves / 320 loops, complete asset hashes and P/A/Z offsets, and Spider-Man's final loop ending raw
    at `D+0xCC == P`. This command remains inspection-only: it reports no inferred sample rate and does
    not execute BFX/song bytecode or join Neversoft cues. Exact initial-effect playback is exposed by
    the separate audited-ROM `n64-audio-decode --effect` route below;
  - ✅ **N64 Sound Tools ROM-global mixer profile — SHIPPED 2026-08-11.**
    `n64-audio-runtime-inspect <game.z64> -o runtime.json` is deliberately separate from PTR/WBK/BFX/SFX
    inspection and has no standalone mode. Schema v1 resolves only the four audited final ROMs using an
    exact carved-`boot.bin` SHA allowlist, NTSC country byte `rom[0x3E] == 0x45`, the clock word at
    that build's pinned raw-ROM offset, and the exact SHA of its pinned 0x160-byte raw-ROM
    `osAiSetFrequency` routine. An unknown boot or any mismatch/truncation in those pinned evidence
    regions fails before the destination directory is created. SDK `musConfig` places
    `syn_output_rate` at `+0x2C`, and the
    cartridge oracle pins the complete call chain: literal 22050 in argument 7, propagation into that
    field, libmus loading it into `a0`, and a direct call to each exact 0x160-byte libultra
    `osAiSetFrequency`. With the pinned NTSC clock 48,681,812, the routine rounds to divisor 2208, writes
    AI DACRATE 2207, and returns 22047 by integer division. The manifest calls this a
    `romGlobalMixerOutput` and publishes the country/clock/routine evidence coordinates and routine
    hash. This is not an authored per-wave or cue rate, but the exact initial-effect decoder below
    consumes it as the Sound Tools mixer basis. Existing bank schemas stay byte-identical; raw
    `n64-audio-decode --index` still requires `--sample-rate` and never guesses from the mixer;
  - ✅ **N64 ABI1 stored-wave decode — SHIPPED 2026-08-10.** `N64AdpcmDecoder` consumes the validated
    WBK slice and parsed predictor book as 9-byte frames / 16 mono samples using the signed-32 wrapping
    and saturated-history behavior of the ABI1/libultra audio-microcode runtime. Synthetic nibble,
    recurrence, saturation, and positive/negative wrap vectors plus clipped real-wave hashes distinguish
    this runtime path from Nintendo's non-bit-identical offline `vadpcm_dec` utility. The strict corpus
    dialect is pinned across 3,390,907 frames: predictors 0–3 and scales 0–12 only. The separate
    `n64-audio-decode <PTR|ROM> --index N --sample-rate Hz -o out.wav` route requires the rate from the
    caller and emits one selected stored wave once as mono PCM16; explicit PTR input also requires
    `--wave`. Its audited-ROM `--effect N` alternative selects the exact initial BFX local wave/PTR
    target, applies signed PTR base-note/fine-tune plus BFX-note pitch with Nintendo's runtime
    polynomial over the returned 22047 Hz mixer rate, and writes the stored infinite ALADPCM loop as
    a WAV `smpl` record. It rejects unknown ROMs, incomplete initial grammars, caller-supplied rates,
    finite-loop conversion, and runtime-clamped silent pitches before touching output. It does not
    resample, render envelopes, execute later bytecode, or accept a cue as its selection input;
  - ✅ **Nintendo Sound Tools BFX inspection — SHIPPED 2026-08-10.** These no-magic big-endian
    `fx_header_t` banks store signed default priorities, file-relative component offsets, opaque effect
    payloads, and an EOF-consuming u16 local-wave→PTR table. `N64SoundToolsFxBank` owns every byte and
    validates every local target against a complete PTR graph without requiring WBK audio. With no
    magic to trust, the carver emits `.bfx.n64` only when the complete asset set has exactly one fully
    parsed PTR and one full BFX match; missing, malformed, ambiguous, non-`.bin`, or colliding cases stay
    unchanged, and consumers continue to scan content rather than suffix.
    `n64-audio-fx-inspect <game.z64> -o effects.json` selects the unique structural BFX and PTR singletons;
    standalone BFX input requires explicit `--pointer`. The manifest records that binding basis because
    BFX contains no PTR identity. The schema-v3 follow-up retains the v2 nullable byte-zero binding—direct
    `81 <packed-local>` or the sole Spider-Man `95 <loop-count> 81 <packed-local>` wrapper—then resolves a
    nullable initial event only when the exact following grammar is present: `84 env[7] 9C pan A6 volume
    note<80 packed-length`. It exposes raw operands, the proven runtime pan half, `0x60` rest labeling, and
    finite versus `0x7FFF` indefinite length without inventing MIDI or duration semantics. Continuation
    classification is separate and exact: direct remaining `80`, direct `80 E2`
    with only `E2` retained as uninterpreted-after-stop, or wrapper count `0xFF` plus `96 80` as infinite
    repeat. Wrong/truncated grammar, out-of-range bindings, and every other suffix remain nullable; neither
    resolver scans later bytes or changes structural BFX acceptance. Across 13,864 carved assets the
    predicate still finds exactly four candidates and zero false positives, pinning 1,680 components/effects,
    30,626 opaque bytes, and 1,608 mappings. All 1,680 initial bindings/events classify (1,339 finite-stop,
    340 indefinite-unreachable-stop, one infinite repeat) and cover all 1,608 local waves. The manifest
    preserves every raw component byte and reports `opaqueBeyondInitialEvent`. The playback resolver now
    executes only this proven initial event/continuation boundary: all 1,680 effects select a PTR wave,
    exact signed pitch yields WAV rates 9,270/11,024/11,679/22,047 across the corpus, and all 320 stored
    loops use libaudio's `[start,end)`/`count == -1` semantics. The envelope control equations and
    finite stop times are now source- and ROM-pinned for all 14 non-flat corpus effects, but playback
    does not yet emulate naudio's squared gain, exponential ramp, and equal-power stereo pan; envelopes
    and later bytecode therefore remain unrendered.
    This is Nintendo Sound Tools BFX, not the unrelated Codemasters WTC `.bfx` family
    documented elsewhere in this file;
  - ✅ **Strict N64 raw SFX cue inspection — SHIPPED 2026-08-10.** `N64SfxCueBank` consumes zero or
    more complete 16-byte big-endian records followed by the exact `FFFFFFFF` terminator, preserves every
    raw field/hash, and rejects nonzero record padding or trailing bytes. `n64-sfx-inspect <SFX|ROM> -o
    cues.json` uses one deterministic aggregate schema for a direct bank or all strict structural matches
    carved from a ROM. The archive carver now shares the same byte-only predicate, correcting two THPS2
    tables that the old semantic note-range heuristic named `.bin`; ROM inspection still scans every asset
    instead of treating suffixes as proof. The four-ROM scan covers 13,864 assets and pins 83 banks / 3,172 records (THPS1 0,
    THPS2 14/671, THPS3 14/572, Spider-Man 55/1,929), including the valid empty THPS1 aggregate.
    Schema v3 consumes SHA-pinned compiled THPS2/3/Spider-Man alias tables and their executable
    consumers. Raw `aliasRaw` remains `u32`; THPS2/3 runtime lookup uses its low 16 bits and exports
    that normalized `lookupAlias`, while Spider-Man preserves all 32 bits. THPS2/3 table targets are
    big-endian `u16` (low 10-bit BFX index plus preserved high routing flags); Spider-Man uses a
    distinct big-endian `u32` packed-class encoding and full-word `0xFA0` no-target sentinel. A cue-bank
    identity does not prove the mutable live owner, so THPS2 remains **622 fixed targets + 34 proven
    no-play + 15 live-state choices = 671**. Twelve choices have exhaustive outcome sets; the three
    `158` records retain an unestablished other-selector outcome. THPS3 is **542 + 30 no-play = 572**.
    Spider-Man is **1,696 + 233 no-play = 1,929** with zero dynamic/out-of-range records. Typed code/data
    hashes, encodings, runtime-state layout and branches, plus the exact selected BFX/PTR source, size,
    SHA-256, and singleton-binding basis are exported.
    THPS1 has no cues. Playback meanings for every non-alias raw cue operand remain unresolved;
  - ✅ **N64 direct/compressed animation — conservative binding slice shipped 2026-08-10, exact flat-map profile added 2026-08-11.**
    The reader consumes big-endian 0x2A tables plus 24-byte big-endian `SMatrix` records and mixed-endian
    0x2C tables/channel payloads. Each direct slot is bounded by the next pool offset, sized from playback
    frames and `tween+1`, copied only to that checked size, s16-swapped, and passed to the established PSX
    direct-matrix decoder. Successful opt-in animation normally binds each emitted corner by its global
    `G_MTX` joint when render placements are unique and the interpretation is proven by coincident
    addressing, an out-of-range `objectIndex + G_MTX`, or a hierarchical super's positional part order.
    The exact Spider-Man `map` payload instead binds `objectIndex + G_MTX` and uses vertex factor k=1;
    ordinary non-profile conversion and invalid/all-failed selections retain their static path. The GUI
    Animations pane routes exact selected slots, while
    `mesh --n64-animations` explicitly requests the full eligible bank. A four-ROM CorpusFact pins 155
    animated nonempty shells / 3,259 clips and admits all 155 / 3,259: 97 shells / 802 direct clips plus
    58 / 2,457 compressed clips. Spider-Man slot 007 `docock` supplies the positional-HIER oracle: its shell is
    field-identical to PSX, all 256 referenced vertices map `G_MTX m` to PSX positional mesh `m`, and all
    43 compressed clips match across 536,820 decoded s16 samples. Flat slot 108 `map` supplies the
    relative/k=1 oracle: its 1,776-byte shell (`2712A50E…BD9`), 41,552-byte bank (`F1439FD7…65A`), and
    render-bank id 215 must all match; its PC sibling is 32,536 bytes (`75EF75D6…56B0`), agrees on all
    12 objects and 812 distinct positions, and every placement uses `G_MTX 0`. Static and animated
    positions therefore remain identical while JOINTS_0 resolves to each placing object. All 802 direct slots
    decode within their owned ranges (798 exact and four one-frame-slack
    slots at Spider-Man 145/263 clips 43/50), and seven PSX/N64 Rosetta pairs match after s16 swapping
    across 585,144 payload bytes. Real global-binding oracles include the 110-joint, 33-placement THPS2
    `sk2def` direct shell and the nonzero-placement Spider-Man slot 225 compressed shell; both GLBs pass
    Khronos with zero issues. Preview uses the existing 30 fps PSX cadence, and direct tween endings use
    the established CycleAnim wrap, as explicit export policies; N64 runtime cadence and per-clip
    loop/clamp behavior remain unproven;
  - improve incomplete bundle naming only from proven trigger/content correspondences (418/594 as last counted; that figure predates the 2026-08-13 fail-closed `_L` guard, which deliberately returns slots to numeric — Spider-Man measured 179/261 on 2026-08-14 — so re-count before quoting it), never an arbitrary first-candidate guess. Spider-Man's literal `Jameson` and `DEM4_G` outsider loads now disambiguate the sole remaining matching content occurrences; the duplicated Mysterio/firering pairs stay numeric.

---

## Census 2026-07-10 — newly surfaced items + working priorities

Full-corpus extension census (`tools/validation/support/corpus_extension_census.py`). **User-set priority
order: 1) hashes → 2) archives/containers → 3) image formats → 4) mesh formats → 5) animation
formats. NO planned support for shaders (`.shd.ngc`) or particles (`.pfx`).**

- ✅ **Priority 1 — pak type-hash identification** (DONE 2026-07-10): every observed type hash
  is now in `PakArchive.KnownTypes` (~35 added, incl. bruted `0x689028A5=.pimg`,
  `0x6290993B=.mcol`, and `0x52D95838=QbKey("unknown")` — the pak builder's fallback type for
  unclassified files; a RIFF sniff in `ExtractFiles` renames those to `.wav` when the payload is
  a WAV). Filename-hash recovery shipped alongside: `QbKeyNames.ThpgDbg.txt` (55,530
  re-hash-validated pairs from THPG's dbg/dbgq paks) + `QbKeyNames.ThawGcPaks.txt` (715 GC entry
  names proven by matching QB strings against archive key hashes; GC key rule = QbKey of the
  lowercased full path minus the last extension). The coverage audit found
  53.9% → **57.2%** named (65,878/115,205); GC
  unresolved 12,864 → 9,104. Hard limits: 40,223 LE entries are keyless (no key stored — offset
  names are all there is), and the remaining ~9k GC keys hash vocabulary that ships in no
  wordlist (gameplay-anim/CAS-part names in the skaterparts/anims apks: .ska 2,582, .img 1,970,
  .stex 1,263).
- ✅ **Priority 2 — archives/containers** (DONE 2026-07-11, commits cda9589/7008c2c/6b5388f/89ac11d/3a98f0d):
  - `.zip.wpc`/`.zip.ngc` (1,337) = QTex texture-SOURCE bundles (STORE PKZip, malformed central
    dir → `QZipArchive` local-header walker); hold original TIFF/PNG art + `debug.log`. Wired into
    `unpack`/probe/CLI/GUI. Sweep 1,337/1,337.
  - `.cut`/`.cut.ps2`/`.cut.xbx` (215) = `CFileLibrary` cutscene containers → `CutArchive`; extract
    SKA/CAM/OBA/SKE anims + SKIN/MDL/GEOM models + TEX + QB + CIF/CAS/WGT, plus a `{stem}.cif.json`
    object-binding manifest. Sweep 215/215. Cutscene anim payloads now convert (OBA bit24 skip,
    headerless SKE gate, `pre/Bits/anims` compress-table path — SKA+SKE → validator-clean GLB).
  - `.prd`/`.prf`/`.prg` (316) = German/French PRE v3 localizations, byte-identical to `.pre` — pure
    routing through `CompressedPreArchive` + full-name extraction dirs. Sweep 316/316.
  - Name harvest: `QbKeyNames.CutScenes.txt` (2,032 proven cut names) + zip-vocabulary GC pak names
    (+159); corpus pak naming 57.2% → 57.5%.
  - ✅ **`0x508AE2F2` CIF2 layout — SHIPPED** (`= QbKey("cifstruct")`, THUG2 CIF replacement). It is a
    `CStruct WriteToBuffer` stream, decoded by `QbStructBuffer` (`Core/Formats/Qb/QbStructBuffer.cs`)
    and integrated into `CutArchive`; **105/105 corpus payloads parse**, objects land in the
    `{stem}.cif.json` manifest with file cross-links. (Was "dumped raw"; the dictionary reverse-lookup
    plan is superseded.)
  - ✅ **Bare-`.cut` INTERMEDIATE animation inspection — SHIPPED 2026-08-10.** The 43 THUG authoring
    containers pair with 43 compiled `.cut.ps2` containers and 194/194 SKA members match by CUT stem
    plus TOC name checksum. `SkaIntermediateParser` consumes the version-2/3 little-endian full-float
    grammar exactly (embedded checksum/name/parent/flip skeleton, per-bone Q/T counts, 20-byte XYZW Q
    keys, and 16-byte XYZ T keys), pinning **4,588,265 Q + 6,079,925 T keys** and exact EOF across the
    corpus. The `ska` CLI emits schema-v1 `<stem>.ska.json` with raw frames/source quaternions and the
    engine-facing convention. This remains deliberately inspection-only: the embedded skeleton has no
    neutral-pose matrices, three v2 roots receive compiler-side prerotation, and some compiled
    translations wrap the signed-16 runtime range, so neither discovery nor `--ske`/`--skin` advertises
    an unproven glTF export. Four valid members omit bit29, so the supported family is described as
    INTERMEDIATE/full-float rather than universally flag-marked UNCOMPRESSED.
  - ✅ **PS2/Xbox CAS polygon-removal metadata inspection — SHIPPED 2026-08-11.**
    `CasPolyRemovalFile` accepts only explicitly typed `.cas.ps2` and `.cas.xbx`/Windows sidecars:
    little-endian version 2, `{version, removalMask, count}`, then `count × 8` bytes of
    `{mask, vertexReference}` on PS2 or `count × 12` bytes of `{mask, data0, data1}` on Xbox.
    The Xbox packed words expose the runtime-proven mesh load order and three vertex indices while
    retaining both raw words. Exact EOF, a nonnegative count, and the platform-selected stride are
    required. The `cas` CLI emits deterministic schema-v1 JSON and marks geometry application
    `notApplied`; it never infers a dialect from bare `.cas` bytes and never mutates a companion mesh.
    The loose-file oracle covers 8,134 PS2 files / 145,803 records and 4,942 Xbox/Windows files /
    44,106 records (13,076 files / 1,852,608 bytes / 189,909 records, zero failures). Its
    Sample-Builds-relative Windows paths are ordinal-sorted before slash normalization, then hashed as
    UTF-8 path + NUL + raw per-file SHA-256: `533B728E5099B292888F10EF0B10B35E92FFD4F07CF21B1EF8C9D6A998B5B7C8`;
    raw file bytes in that same order hash to
    `3FCDE1FB65DF4C1F0DC303F405767EC64281F3F5A1FF50EF673D5094DC04D019`. `CutArchive` now preserves
    the container platform suffix on CAS members; bare authoring CUTs stay bare. The retained CUT census
    pins 1,058 typed members / 561,520 bytes / 59,188 records, including 662 empty headers whose dialect
    is knowable only from the container suffix.
  - **Still open (not blockers):** applying CAS records to geometry remains unresolved because PS2
    needs the runtime DMA/ADC binding and Xbox needs companion mesh load-order/strip identity. THAW
    `.cas.ngc` uses a distinct big-endian `0x041000FE` envelope and is not accepted.
  - ✅ **Compiled PS2/Xbox WGT v1 mesh-scaling metadata inspection — SHIPPED 2026-08-11.** The retained
    THUG runtime reads a four-byte version, a signed vertex count, then exposes `3 × vertexCount`
    little-endian float weights followed by `3 × vertexCount` signed-byte bone indices to
    `SMeshScalingParameters`; the Xbox and PS2 mesh loaders consume those triples while loading the
    cutscene head. `CutsceneWeightMapFile` admits only explicit `.wgt.ps2`/`.wgt.xbx` version 1 with a
    nonnegative count, finite raw floats, and exact EOF `8 + 15 × vertexCount`. The `wgt` CLI preserves
    every raw triple in deterministic schema-v1 JSON and marks geometry application `notApplied`.
    Twelve loose files / 219,126 bytes / 14,602 vertices (eight unique payloads) form the accepted set;
    their ordinal Sample-Builds-relative path + NUL + raw per-file SHA-256 digest is
    `718F40AC62F4873ADF8BA77612568B1BFFD987C0D83EC0DBBE56B4FCCBF177AC`, and same-order raw bytes hash
    to `F08B803965E3C620BDBA34B5BDEF951960BC7586A26B8FEAF1E110BF4190B15E`. `CutArchive` now preserves
    the platform suffix on WGT members. The v1 CUT oracle pins 212 members in 52 containers /
    3,997,036 bytes / 266,356 vertices (132 PS2 plus 80 Xbox/Windows), and every payload SHA matches
    one of the eight loose v1 payloads.
  - **WGT limits (fail closed):** eight bare authoring files use `4 + 24 × vertexCount` without the
    retained compiler/consumer contract needed to claim their semantics. Four loose plus 40 CUT THUG2
    PS2 files use version 2 and exact `8 + 19 × vertexCount`; their extra leading `4 × vertexCount`
    region remains semantically unowned. Both dialects and `.wgt.ngc` are rejected. Geometry mutation
    remains separate because it needs caller-selected profile bone scales and an authoritative WGT ↔
    companion-skin vertex-order binding; the inspector never infers bone names or changes a mesh.
  - ✅ **`debug.log` texture-name side map — SHIPPED.** `ThawTextureNames.txt` carries 2,132
    compiled-texture checksum → original-art-name pairs harvested from the QTex bundles, and
    `ThawTexFile`/`NgcTexFile` use it before the general QBKey fallback. It remains deliberately
    separate from `QbKeyNames*.txt`: these identifiers are opaque build IDs, not CRC(name) pairs.
- ⚪ **Priority 3 — image formats**: `.tga` DONE 2026-07-11 — all 4 corpus TGAs verified standard
  (types 1/2 uncompressed, one 32-bit with real alpha); decoded via ImageSharp through the
  `Core/Formats/Rle/BitmapFile.cs` facade (`rle` CLI + Bitmap Converter tab), alpha preserved.
  Standard `.bmp` (3,535 files, all `BM`/BITMAPINFOHEADER) shipped in the same pass. Remaining:
  `.tim` (5 files) — standard PSX TIM headers, but they live in the multi-game demo-disc build
  (`Spider-Man (2000-2-4, PSX)`, not "Spider-Man PC" as the earlier census said) under third-party
  dirs (`DD/`, `WTC/` = TOCA) — out of scope as non-Neversoft content.
- ✅ **`.dff` — DONE 2026-08-07.** Was a routing-only gap; `.dff` now resolves through
  `MeshTypeDetector` alongside `.skn`. 477 files.
- ✅ **THPS2X `.ANIM` frontend timelines — SHIPPED 2026-08-10.** The old “Xbox-era skeletal
  animation” label was false: all 193 files live under `frontend/` and form UI timeline forests.
  `Thps2XFrontendAnimFile` parses the `Anm\0` v1 header and a deterministic recursive node grammar:
  bounded ASCII names, twelve raw base floats, one semantic-free u32, 42-byte timeline keys, nested
  nodes, and a closing screen/owner string. The uncertain u32 and u16 key fields remain raw rather
  than receiving invented meanings. Every file consumes exactly to EOF: 921 roots, 1,148 nodes,
  4,581 keys, maximum observed depth 1. `thps2x-anim` writes schema-v1 inspection JSON, preserving
  relative directories in batch mode so repeated basenames cannot overwrite. This is inspection,
  not skeletal export or a claim that the UI runtime has been reproduced.
- ✅ **`.pcm` — DONE 2026-08-07.** 2,752 files (1,376 identical on the Xbox and Windows discs).
  RIFF + Xbox ADPCM 0x0069, mono, nBlockAlign 36, wSamplesPerBlock 64, at 11025/22050/44100/48000.
  A block emits the header predictor as sample 0 then **63** nibbles — the 64th is padding;
  settled by diffing both readings against ffmpeg's `adpcm_ima_xbox` (bit-exact one way,
  mismatched the other). `Core/Formats/Audio/XboxImaAdpcm.cs` + `XboxPcmDecoder.cs`, on a new
  shared `Core/BinaryIO/RiffWaveReader.cs`.
- ✅ **`.snd` — DONE 2026-08-09.** 788 files, THUG2 Windows only. The decrypted retail
  executable exposes the complete decoder at VA `0x005F5A20`: low nibble first, canonical IMA
  tables, but the step index is updated **before** the current step lookup and the delta is
  `((step * magnitude) >> 2) + (step >> 3)`. Predictor and index start at zero and carry across
  the whole file. `nAvgBytesPerSec` is the decoded byte count, so the loader requests exactly
  `nAvgBytesPerSec / 2` samples and ignores the last high nibble for odd counts. The original x86
  routine and the clean-room implementation matched byte-for-byte on a stress vector; 350
  independently encoded PC/Xbox name pairs reach median windowed NCC 0.9906. Implemented by
  `Thug2PcSndCodec` / `Thug2PcSndDecoder`; full provenance is in
  `docs/formats/thug2-pc-snd.md`.
- ⚪ Not formats / no action: `.dep` (build path lists), `.chk` (checksum text), `.anr` (text
  anchor scripts), `.rec` replays, `.seq` ("Sequencer File" text on the DC proto), and installer
  debris. Standard `.gif`/`.jpg` now route through the Bitmap viewer and `.ogg` through Audio.
  `.zoo`/`.bfx`/`.ppv` = Codemasters WTC (see PPV entry).

---

## Done (for reference) ✅

- ✅ **Payload-bearing PS2 `.stex` standalone decode** — the earlier “raw blob needs external metadata” conclusion was false. Byte-zero owner blobs contain their texture records and decode through `ThawZoneTexFile.DecodeAllFromFile`; `FormatProbeTexture` and the Texture tab route them directly. `ThawArchiveTextureRegressionTests` pins two real nested `.stex` files by checksum, dimensions, and RGBA SHA-256. The 2026-08-09 corpus audit found all payload-bearing THAW/P8/THPG files recognizable; three 144-byte THAW owner stubs contain no texture records and correctly produce no output.
- ✅ **PSX animated-surface playback** — both previously tracked paths now ship:
  - UV wibble (2026-07-17): face bit 5 is UV scroll + per-vertex sine wibble, not an image flipbook; actual membership comes from tagged chunk 6. The exporter carries velocity/frequency/amplitude/phase with a frame-zero fallback, the viewer reproduces the native 64-sample table, and `.blend` exports build a timeline-driven UV shader. Spider-Man PC v6 correctly starts from widened face UVs in fixed 512-coordinate space and doubles only the scroll term; its legacy base-UV bytes are non-authoritative.
  - Colour pulse (2026-08-07, `a9d7c1a`; Blender follow-up 2026-08-10): frame zero remains a portable fallback; the GLB carries pre-transformed channel keys and the in-app viewer evaluates them on the shared 60 Hz timeline. A clock correction makes that timeline advance when either animation type is present instead of returning early with zero wibble meshes; the real February `l1a1_o.psx` pulse-only bank pins 6 channels, 15 pulsed primitives, 192 pulsed vertices, and zero wibble primitives. Direct `.blend` export now carries validated portable tables and byte-per-vertex POINT channel IDs into a shared Geometry Nodes evaluator that stores animated CORNER `Color`; malformed buffers/channels remain static, and additive/subtractive alpha, zero holds, accumulators, overbright keys, `fps_base`, mixed faces, and a 56-channel stress graph survive save/reopen in Blender 5.1. Blender native-time zero preserves the authored bake; later ticks use portable linear-output interpolation rather than claiming the viewer's packet-domain/nonlinear PS1-exact result.
- ✅ **THPG / Project 8 bare `.col` and `.skin` routing** — shipped 2026-08-07 (`21edfa5`). `MeshTypeDetector` recognizes bare `.col`, content-probes ambiguous `.skin`/`.mdl`, and routes `.dff`; the permissive Xbox `(1,1,1)` probe is intentionally last because many PS2-build scenes share that prefix. Routing tests pin both collision and scene cases. The underlying THPG/P8 `.col` files are ordinary version 10; the old `00 FF 00 FF` evidence was corrupt pre-offset-fix PAK extraction.
- ✅ GS-alpha export scaling (128=opaque → PNG 255=opaque) — `memory/ps2_alpha_export_scale.md` (v1.2.1). `DecodePixels(rawGsAlpha)`: export scales ×255/128, GS replay keeps raw.
- ✅ VID1 (THAW GameCube movie container) → MP4 — shipped (`vid` CLI command + Video Converter tab); the old `CLAUDE.md` "Deferred > VID" note predates it.
- ✅ **THAW `.tex.ps2` scene texture metadata** — IMPLEMENTED (confirmed 2026-07-26; the old 🔴 "Not Yet
  Implemented" note was stale). `Core/Formats/Texture/Ps2Scene/SceneTex/ThawSceneTexFile.cs` = version-6
  TEX0-metadata scan + GIF A+D CLUT/pixel decode; **DMA-REF-verified 905/905 unique textures across
  332/332 files**. Joined on entry-table `TextureChecksum` (1,325/1,329 materials direct; the 4 misses
  are mat=0/tex=0 placeholders), with a TEX0 `(TBP,CBP)` fallback join for entries whose checksum is
  absent from the companion (`ThawPs2SkinSetupMapping.AugmentTextureOverridesWithTex0Fallback`).
- ✅ **THAW PC textures (`.tex.wpc` / `.img.wpc`, 0xABADD00D)** — already shipped as
  `ThawTexFile`/`ThawImgFile` (routed via `xbxtex`); the old "Not Yet Implemented" note was stale.
  Verified 2026-07-07: **723/723 tex.wpc + 2,480/2,480 img.wpc, 0 failures, 4,472 textures**.
- ✅ **THUG2 `.scn.xbx` level scenes** — same format as `.skin`/`.mdl` (version triple 1,1,1); extension
  routing added 2026-07-07 (`9eb2680`): 192/192 files, 3,005,651 triangles, 0 validator errors.
- ✅ **PS1 `.psx` character meshes** — the "garbled body parts" claim was stale; 2026-07-07 five-build
  sweep: 490 character files, 0 real failures (non-conversions = texture-only costume files). See
  `mesh-fidelity.md`.
- ✅ Dev-artifact non-formats identified 2026-07-07 (no work needed): `.usg`/`.usg.ps2` = memory-usage
  build logs (text), Spider-Man `.tex` = hash manifests (text), `.psh` = C headers, and `.fam.*` =
  appearance config data. The old `.cas.*` grouping was incorrect: `.cas.ps2`/`.cas.xbx` are binary
  polygon-removal metadata (inspection now ships), while `.cas.ngc` is a separate unresolved envelope.
  (The 2026-07-07 claim that `.mpk.ngc` = padding stubs was wrong for 821 of
  them — they are apk companion data files; see the .apk.ngc entry above.)

## By design / won't-fix ⚪

- ⚪ **PSX texture-name → string resolution.** The PSX "texture name" array stores build-tool-assigned identifiers (e.g. `0x0000001E`), used as `TextureChecksumHashTable` keys — **not** CRC-32 name hashes and not pixel checksums. Engine analysis plus string extraction across 15 executables found 0 texture-name matches. Name resolution is not applicable to textures; don't chase it. (Mesh hashes are resolved — 81.9%.)
- ⚪ **VID (THAW GameCube movie) full decode via external APIs** — the container is documented; frame decode historically depended on external decoder APIs. VID1 now ships (see Done); no further deferral needed.
- ⚪ **`.bik` (Bink Video)** in THPG/P8 — proprietary RAD codec, out of scope.
- ⚪ **BIN / SCC / PRK** — MIPS code overlays, VSS version files, park saves. Not game asset data (`CLAUDE.md` → *Not Game Formats*).
