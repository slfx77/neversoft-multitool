# Non-GBA corpus robustness

Audited 2026-09-02 against **50 non-GBA staged build directories and 623,478
extracted/loose files** under `Sample/Builds`. The seven GBA builds are deliberately
excluded because they are being audited separately.

This page tracks end-user outcomes, not merely whether a header can be parsed.
The end goal is **C in every applicable capability for every game**; collectables and gameplay
objects are deliberately the last layer, after dependable scene composition and collision space.

- **Complete / C** means the present corpus family is content-gated, usable through the
  shared preview or conversion path, and protected by a full-family/full-corpus gate where
  the corpus permits one.
- **Partial / P** means useful output exists, but a named fidelity, binding, composition, or
  coverage gap remains.
- **Inspect / I** means structure is preserved as JSON/metadata but cannot yet produce the
  requested rendered result.
- **Open / O** means there is no dependable user-facing route. Deliberately malformed,
  truncated, foreign, or authored-empty files are correct rejections rather than parser
  failures.

## End-goal status

| Capability | Status | What is solid | What still prevents end-goal coverage |
| --- | --- | --- | --- |
| Texture and bitmap viewing | **Complete for the present corpus** | PS1/DC/N64/PS2/Xbox/PC/DS/GC/Wii/PSP/X360/PS3 texture families, including all 880 THPS4 PC `*img.dat`, all 668 PSP level containers, and all 3,570 non-empty PS3 `FACECAA7` dictionaries; RLE/BMR/ZLB and standard bitmap families | No decoder-coverage gap; authored material/texture binding on late scenes is tracked under meshes and levels |
| Video viewing | **Complete for the present corpus** | STR, SFD, PSS, Bink, `.bik.xen`, complete PMF A/V, VID1, TGR, and SMO share preview/conversion paths | Only deliberately rejected incomplete/non-video STR candidates; these are not valid movies |
| Audio extraction/conversion | **Complete for direct audio; N64 static cue targets closed, live controls partial** | XA/VAB/VAG/ADX/KAT/SFX/PCM/SND/PSS/VID, WAV/WMA, DS SWAV/STRM/HWAS, PSP AT3 and PMF ATRAC3+, Wii DSP, DEE/SMO Bink-DCT, FSB3 MP3/XMA1, THAW paired XMA banks, late `.wav.ps3/.wav.xen` carriers, exact initial N64 BFX→PTR pitch/rate/stored-loop playback, and executable-pinned THPS2/3/Spider-Man cue→BFX maps with exact bank provenance | N64 cue operands other than alias and BFX envelopes are not rendered; 15 THPS2 records require live runtime state, and three retain an unestablished selector outcome |
| Static meshes | **Broad, uneven on late ports** | PS1, N64, DDM, RenderWare, native PS2/Xbox/PC (including every THPS4 delimiter-free scene/model DAT), THAW GC, DS, PSP rigid GE plus all 668 PSP static worlds, and useful THAW/P8/PG X360/P8 PS3 families | Most DHJ/PG Wii scenes; PSP level dynamic-object records; PG PS3 topology; late skin/material binding |
| Skeletal animation | **Broad through THAW; structural later** | PS1 lineage, THPS3 runtime-proven composition, THPS4 (including all PC `*ska.dat`)/THUG/THUG2/THAW SKA/SKE, evidence-gated N64 clips, DS clips, and bounded P8/PG key streams | PSP `.ska.psp`; native PSP/X360/PS3 mesh/rig joins; later key-stream parsing has not been visually validated against native rigs |
| Levels and sky/background | **Mixed** | PS1 composition, N64 static worlds, authored THPS2X DDM sky composition, authored THPS3 PS2 BSP main/sky/backdrop composition, PS2 native levels, THPS4 PC `Levels.qb` sky/main/shell composition, THAW PS2 worldzones/TOD/sky, DS model sets, THAW GC, strict Remix/P8 PSP runtime-manifest subsets, and small proven Wii subsets | Full Wii scenes; ambiguous PSP editor/mission/global layers; PG PS3 topology; systematic sky/background composition outside proven families |
| Standalone collision rendering | **Strong** | Little-endian COL v8/v9/v10, big-endian X360 v10, THPS4 PC `*col.dat`, proof-bound THAW GC `.col.ngc`, DS collision worlds, exact PSX/Dreamcast/Spider-Man Windows inline level surfaces, and THPS3 BSP per-triangle collision | Remaining THAW GC and Wii positions are external without an exact compatible owner; no dependable PS3 collision family is present |
| Render-scene + collision overlay | **Partial and opt-in** | Exact PSX/Dreamcast/Spider-Man Windows inline levels, THPS3 BSPs, same-owner PS2 GEOM and Xbox/Windows SCN, delimiter-free THPS4 PC DAT, proof-bound THAW GC loose/typed-PAK families, and all 24 authored THPS2X DDM/PSX main-level families can be shown/exported as a translucent overlay | It is not a general level matcher; unregistered PSX residue, non-level THPS2X pairs, structurally incompatible NGC families, PS3, PSP, cross-owner hash/offset pairs, and unmatched X360 scenes are deliberately excluded |
| Collectables and level objects | **Intentionally last / partial** | Strong PS1 object banks, trigger placements, pickups, and bounded traffic snapshots; THAW PS2 props/QB placement | A systematic per-game object/collectable layer for every other level family |

The shortest honest summary is: **media decoding is close; authored scene composition is
not**. A corpus-complete decoder does not imply that a late-generation scene has its
materials, skeleton, animation, sky, collision, and gameplay objects joined into one view.

## Platform and engine matrix

Legend: **C** complete for the present corpus family, **P** partial with a named limitation,
**I** inspect only, **O** open, and **—** no separate relevant layer. “Overlay” means a combined
render-scene/collision view, not the standalone COL viewer.

| Platform / engine family | Anim | Mesh | Level + sky | Collision render | Overlay | Audio | Texture / bitmap | Video | Objects |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| PS1: Apocalypse, THPS1-4, Spider-Man/EE | C | C | C | C¹ | C¹ | C | C | C² | P |
| Spider-Man Windows (PSX-derived) | C | C | P¹ | C¹ | C¹ | C | C | C | P |
| N64: THPS1-3, Spider-Man | P | C | P | P | — | C¹⁵ | C | — | P |
| Dreamcast: THPS2, Spider-Man | P | P | P | C¹⁶ | C¹⁶ | C | C | C | O |
| THPS2X / early Xbox | I³ | C | C³ | P³ | C³ | C | C | C | P |
| THPS3 PS2 / RenderWare | C | C | C¹⁷ | C¹⁷ | C¹⁷ | C | C | C | P |
| Native PS2: THPS4 through Proving Ground | C | C | P | C | P⁴ | C | C | C | P |
| THPS4 Windows / Aspyr DAT | C⁵ | C⁵ | C⁵ | C⁵ | C⁵ | C⁵ | C⁵ | C⁵ | O |
| THUG2/THAW Xbox and Windows | C | C | P | C | P⁴ | C | C | C | P |
| THAW GameCube | C | C | P | P⁶ | P⁶ | C | C | C | P |
| DS: American Sk8land, DHJ, Proving Ground | P⁷ | C | P | C | O | C | C | C | P |
| Wii: DHJ, Proving Ground | P | P⁸ | P⁸ | I⁶ | O | C | C | C | O |
| PSP: THUG2 Remix, Project 8 | P⁹ | P⁹ | P¹⁰ | C | O | C¹¹ | C¹⁰ | C¹¹ | O |
| Xbox 360: THAW, Project 8, Proving Ground | P¹² | P¹² | P | C | O⁴ | C | C | C | O |
| PS3: Project 8, Proving Ground | P¹² | P¹³ | P¹³ | O | O | C | C¹⁴ | C | O |

Notes:

1. PSX-lineage games ray-cast the level SModel rather than loading a duplicate collision mesh.
   Dedicated collision mode re-emits both visible and loader-invisible faces, grouped by the
   distinct raw collision halfword rather than the PSX GPU/render word. The runtime transforms the
   serialized vertex words directly—including sprite-type vertices—and rejects collision function
   class one before reading its indices. Identity requires exact THPS/Apocalypse level-owner
   evidence, an exact v2.x TRG `SpoolEnv` registration, or a valid same-stem v2.x TRG plus the VAB
   marker retained by six legacy PS1/Dreamcast `*_g` packages; arbitrary props/characters and remote archive evidence are
   not promoted. In particular, Apocalypse's bare `death`/`war` payloads are `SpoolIn` actors.
   Across the 20 audited PSX/Dreamcast/Spider-Man Windows builds, **670 physical sources / 535
   per-build level stems** emit **2,182,243** triangles from 2,661,969 declared triangles: 478,027
   triangles belong to the runtime-skipped class, 1,699 participating triangles are geometric
   degenerates, and zero participating references are unresolved. This includes 1,396,461 visible
   plus 126,347 loader-invisible face instances, with 243 authored collision-flag values and 177 on
   runtime-participating faces. Any rejected face record, invalid object reference, or invalid
   participating vertex reference makes the whole collision view fail closed; skipped-class indices
   are deliberately never dereferenced. The PS1 subset is 567 sources / 442 stems / 1,858,481
   emitted triangles. Spider-Man Windows contributes 67 exact registered environments / 163,702
   emitted triangles; its unregistered `zArt_G` payload is excluded. That port still has no dedicated
   full-build composition/sky sweep, so its level + sky grade remains partial.
2. Of 335 loose STR candidates, 323 are accepted; the 12 rejects are incomplete or non-video
   payloads and are pinned as such.
3. THPS2X `.ANIM` files are frontend timeline forests, not skeletal animation. DDM level
   composition now reads the exact `<level>_t.trg` `BackgroundCreate` registrations: all 500/500
   authored commands across 24 level families resolve to 25 unique background objects. Twenty
   families author sky geometry and four author none. Sky layers retain authored paint order,
   backdrop colour, placement anchor, and camera-lock semantics. The corpus also has 104 exact-stem
   DDM/PSX structural pairs, all with non-super PSX revision 6, but only those same 24 complete
   authored-level families are promoted to overlay support. They contain 19,527 collision
   objects/meshes and 328,442 structurally valid faces (306,154 visible plus 22,288 hidden
   collision-only), emitted as 485,549 non-degenerate overlay triangles; 89 other declared face
   records fail structural gates. Directly viewing a `.psx` remains partial collision rendering
   because the ordinary display path intentionally omits the hidden collision-only faces.
4. The overlay resolver is deliberately opt-in and exact: same owner/directory and same stem,
   with byte-order/content validation. The loose corpus has **3,365/3,620** `.geom.ps2` files with
   an exact `.col.ps2` companion and **90/192** `.scn.xbx` files with an exact `.col.xbx`
   companion, plus **29/29** THPS4 PC delimiter-free `*scn.dat` → `*col.dat` pairs. Many of the
   broad PS2/Xbox counts are object assets, so they are not claims of level coverage; THPS4's 13
   authored main levels are separately corpus-gated with render composition and overlay. There
   are no exact loose `.scn.xen`/`.col.xen` pairs in the measured corpus. NGC is admitted only by
   the stronger external-pool proof in note 6; PS3 and hashed `.mdl.xen` pairings remain excluded
   rather than guessed.
5. THPS4 PC's delimiter-free families now all route through strict payload gates: **601/601
   `*tex.dat`, 880/880 `*img.dat`, 601/601 `*col.dat`, 420/420 `*skin.dat`, 152/152 `*mdl.dat`,
   29/29 `*scn.dat`, and 1,966/1,966 `*ska.dat`**. `data/scripts/Levels.qb`, not filename
   resemblance, supplies the 13 authored sky/main joins and two editor shells; this preserves the
   Motox → Hof_Sky exception and excludes unused Can_Sky/Pink_Sky residue. The static scene files
   retain materials, hierarchy placement, textures, and exact EOF; SKA uses the port's shipped
   2,048-entry Q/T compression tables and reaches the shared animation export IR. Media adds 3,612
   DEE, 47 SMO, 506 WAV, and 27 TGR files.
6. NGC collision topology, BSP metadata, and intensities parse, but vertex positions live
   outside the collision file. THUG decompilation shows `mp_raw_vert_pos` binding to the loaded
   render scene's position pool, so THAW GC now renders only a proof-bound subset. Loose ownership
   requires one exact same-directory stem; hash-named entries require one typed COL and one typed
   scene in the selected PAK directory. Both then require matching object count/checksum/order,
   exactly one static-or-skin pool with `totalVerts` positions, finite coordinates, valid face
   ranges, object and scene bounds (the audited static and skin winners both require the format's
   1/32-unit collision/render precision), and real non-degenerate geometry. The canonical loose
   census accepts **210/722** (23 static MDL and 187
   skin), declines 495 incompatible non-empty families, and records 17 authored-empty families.
   The 680 PAK-expanded typed-entry copies yield 225 accepted, 289 declined, and 166 empty. No
   cross-directory/name proximity, size-only choice, or fake coordinates are used; standalone
   collision fails closed and optional scene overlay fails open. Wii stays inspection-only because
   its scene dialect/ownership is not proved by this THAW-specific gate.
7. Sk8land has 77 models with 11,156 applicable clips. DHJ has 322 applicable bindings but 121
   currently bake; Proving Ground has 467 applicable bindings but 131 bake, chiefly because
   singular joint transforms are refused rather than approximated.
8. The THAW GameCube layout safely covers only **11/392** DHJ Wii and **56/1,199** Proving Ground
   Wii scene candidates. Those subsets are useful, but full Wii scene and sky support remains
   partial.
9. PSP GE parsing exports 9,509 rigid meshes. Weight bytes validate, but there is no `.ske`
   bone-index join; **1,608 `.ska.psp`** files remain unsupported. Animation is partial rather
   than open because other admitted PSP/legacy clips can be structurally read, not because a
   faithful animated PSP character view exists.
10. All **668/668** Remix/P8 `.psp_level` files consume exactly and emit their static GE world plus
    embedded T4/T8 textures: Remix 80 / 80,327,774 bytes; P8 Final and Rev1 each 294 / 61,488,910.
    Across the three builds this is 1,785,387 strips, 8,646,324 vertices, and 5,075,550 theoretical
    triangles. The trailing 64-byte dynamic-object records remain deliberately omitted.
    Composition is evidence-gated by the same build's `levels.qb`, each structure's exact wrapper,
    and the PSP `load_level` branch—not `_sky`, basename, or proximity. Remix uniquely resolves 42
    main variants (21 single-player + 21 `_net`): 40 compose an authored sky and both Mainmenu
    variants explicitly have none. Its five editor structures author five sky/shell alternatives,
    but one shared `sk5ed` main has no unique theme, so all stay standalone; `Default_Sky` likewise
    has no packaged `Default` main. Each P8 build uniquely resolves 40 world-zone variants (20 + 20):
    36 compose a sky and `z_mainmenu`/`z_training` author none. Missions, SFX layers, `global`,
    `z_world`, and navmesh residue remain standalone because no explicit unique main ownership was
    proved. Composed documents retain runtime sky→main order, independent material/texture namespaces,
    and camera-locked sky metadata; missing/ambiguous/malformed optional composition falls back safely.
11. All 334 PSP PMFs pass the strict PSMF demux. Exactly 333 carry ATRAC3+ private-stream audio,
    whose 568/752-byte frames are wrapped as OMA and decode to WAV or AAC-in-MP4; `ICON1.PMF` is
    the sole authored video-only file and is muxed with explicit `-an`.
12. P8/PG next-gen SKA key streams parse under strict section and track boundaries across
    **44,649/44,649 files**: P8 X360 9,467, P8 PS3 9,467, PG X360 8,641, and PG PS3 17,074.
    This is structural key-stream parsing only—not native skeleton/mesh binding or visual motion
    validation. Later scene meshes likewise remain rigid/untextured.
13. Project 8 PS3 has a useful validated scene subset. Proving Ground PS3 topology is
    intentionally rejected after locality tests proved that superficially validator-clean
    output rendered as shattered triangles.
14. PS3 IMG accepts 2,851/2,853 descriptors; the two rejects are physically truncated payloads.
    Separately, every one of the next-gen TEX corpus's **3,570 non-empty PS3 dictionaries / 23,090
    records** has decoded pixels. Exact named/type-hash twins cover 2,388 / 15,573. Same-build
    byte-identical dictionaries cover 1,083 / 6,577 only when all eligible exact-length owners have
    byte-identical payloads. Authoritative raw PAK ownership covers the last 99 P8 dictionaries / 940
    records: 19 / 91 exact-name entries and 80 / 849 collision-renamed/indexed entries. Indexed pairing
    requires the complete typed populations to agree on count, preserved order, name CRC,
    collision-neutral logical stem, and declared payload size; 877/877 independently named controls
    confirm the ordinal invariant with zero counterexamples. Raw, wrapped, and PAB-backed archives
    share the archive reader. Mutually inconsistent exact-name spellings, conflicting content
    payloads, short owners, cross-build matches, duplicate descriptor keys, incomplete populations,
    and metadata mismatches are rejected rather than guessed.
15. N64 direct effect playback is complete for the four audited final ROMs: all 1,680 BFX effects
    resolve through 1,775 PTR waves at the exact returned 22047 Hz mixer rate, applying signed
    base-note/fine-tune and effect-note pitch with Nintendo's runtime polynomial. All 320 stored
    ALADPCM loops are infinite; 359 effects reference 314 distinct loop waves and emit exact WAV
    `smpl` bounds when selected.
    Static cue targets are closed for every audited bank; mutable runtime ownership is not guessed.
    THPS2 therefore has **622 fixed BFX targets + 34 executable-proven no-play sentinels + 15
    live-state choices = 671 records**. Twelve choices (`F4`, `13C`, `156`, and `157`) have exhaustive
    target/no-play outcome sets. The three `158` records retain an unestablished outcome when their
    guards pass with a selector outside the three proven cases; a cue-bank path/hash does not prove
    which owner is live at playback time. THPS3 is **542 targets + 30 no-play = 572**. Spider-Man's independently pinned
    482-entry big-endian `u32` table supplies **1,696 targets + 233 full-word `0xFA0` no-play =
    1,929**, preserving its packed class bits separately from the effect index. THPS1 has no cue
    banks. Raw `aliasRaw` remains `u32`; THPS2/3 lookup masks it to the low 16 bits while Spider-Man
    uses the full 32 bits, and schema v3 exports the resulting `lookupAlias`. The schema also records
    typed code/data evidence, exact selected BFX/PTR source/size/SHA-256 and binding basis, runtime
    state layouts, exhaustive branch alternatives, and unestablished outcomes. Raw cue operands other
    than alias, BFX envelopes, and later dynamic BFX pitch/handle changes are preserved but not rendered.
16. THPS2 and Spider-Man Dreamcast use the same PSX-derived collision contract as note 1. Their
    exact subset is **36 sources / 26 per-build level stems / 160,060 emitted triangles** from
    210,931 declared: 50,793 are runtime-skipped and 78 participating triangles are degenerate. The
    default render remains unchanged; collision is a default-off overlay which adds the
    loader-invisible surface and preserves its raw classification.
17. THPS3 PS2 composition is read from the shipping `SKATE3/Scripts/levels.qb`, never inferred from
    filenames. Its 13 single-player mains resolve to 11 authored sky BSPs and 11 unambiguous backdrop
    colours. Foundry and Warehouse explicitly author no sky; Tutorials maps `Tut.bsp` to the exceptional
    `Sk3Ed_Bch_Sky.bsp`. Sky primitives are camera-locked and retain a separate TXD/material namespace.
    Collision uses the same BSP topology, but the Neversoft atomic extension stores a version-6
    side table with one little-endian `u16` flag per triangle. Of 43 BSPs, 39 non-empty runtime
    worlds have a complete usable table and emit **771,579** overlay triangles from 772,002 source
    triangles across 394 raw flag values; 423 geometric degenerates are omitted. Three DCC/source
    BSPs correctly lack the runtime extension and `Ware_Test10.bsp` is authored-empty. Main-level
    composition overlays only the main world, never its camera-locked sky, and default output is
    unchanged.

## Quantitative gates for the current frontier

- **Standard web bitmaps:** 4,455/4,477 non-GBA PNG/JPEG/GIF files identify and fully decode.
  The remaining 22 are zero-byte Proving Ground Wii PNG entries and are explicitly rejected.
  In-place conversion reserves source PNG names and TIFF-derived `_mipN` names so authored files
  cannot be overwritten.
- **THPS4 PC delimiter-free DAT:** 601/601 `*tex.dat` files consume exactly and expose 8,332 RGBA
  textures across 38,093 exact-size mips; one is an authored-empty eight-byte dictionary. All
  880/880 `*img.dat` files decode: 841 P8 surfaces (16- or 256-colour, including 103 padded
  layouts) and 39 BGRA32 surfaces, covering 34,553,888 stored bytes and 12,133,382 pixels. The
  independent collision gate accepts 601/601 v8 `*col.dat` files: 11,701 objects, 646,877 vertices,
  and 669,796 faces. It validates the post-face BSP graph (90,386 internal nodes and 102,087 leaves),
  proves that every object face is reachable through the 1,557,770 object-local references, and
  consumes every file exactly through EOF. Static scene parsing accepts all 420 skins, 152 models,
  and 29 level/sky/shell scenes: 601 files / 48,967,304 bytes. Animation accepts all
  1,966 SKAs (72,368 bone tracks, 599,567 quaternion keys, 139,026 translation keys, and 1,096
  custom keys) through the shared export IR.
- **Next-gen textures:** 12,335 `FACECAA7` files / 90,477 records, including DXN/BC5. All
  embedded X360 allocations pass exact 4 KiB, logical-size, and non-aliasing gates. All 3,570
  non-empty PS3 dictionaries / 23,090 records decode through the proof-bound source breakdown in
  note 14; zero remain metadata-only. Separately:
  13,712/13,712 `.img.xen`; 2,851/2,853 `.img.ps3`; 12,127/12,127 Wii `.img.ngc`; and
  PSP IMG covers 4,515 THUG2 Remix files plus 3,141 in each P8 Final/Rev1 build (6,282 P8;
  **10,797 build-tree files total**).
- **Scenes and rigid meshes:** THPS4 PC's 420 skins, 152 models, and 29 scenes consume exactly:
  48,967,304 bytes, 11,014 materials, 10,431 sectors, 33,310 meshes, 1,306,185 source vertices,
  and 2,071,700 strip indices; all 29 scenes
  populate 726,332 nondegenerate triangles. Its 13 authored main levels compose sky/main/shell in
  script order with per-scene texture dictionaries and camera-locked sky metadata. Separately,
  4,242/4,242 THAW GameCube scene files parse under strict attribute/range validation. PSP has
  9,509 supported rigid wrappers (6,894,277 vertices); 543 authored-empty/name-collision wrappers
  are rejected. Its separate `.psp_level` route consumes all 668 static worlds exactly: 1,785,387
  strips, 8,646,324 vertices, 5,075,550 theoretical triangles, and decoded embedded textures.
- **PSP authored sky:** Remix resolves exactly 42 normal main variants from the shipping legacy-QB
  manifest (40 with sky, two explicit no-sky); each P8 build resolves exactly 40 world-zone variants
  from its sectioned-QB manifest (36 with sky, four no-sky). Real Remix and P8 compositions export
  textured GLBs with independent sky/main namespaces and camera-locked skies. Editor alternatives,
  missions, SFX/global/world layers, missing companions, and malformed manifests are adversarially
  gated and remain standalone.
- **THPS2X authored sky:** 500/500 `BackgroundCreate` registrations resolve without ambiguity to
  25 unique DDM background objects across 24 level families. Twenty levels compose sky meshes;
  the other four author no sky object. A real multi-layer gate pins camera-locking, backdrop colour,
  placement anchor, and six-layer authored paint order.
- **THPS3 PS2 authored sky:** the exact shipping master list yields 13 single-player mains: Foundry,
  Canada, Rio, Suburbia, Airport, Skater Island, Los Angeles, Tokyo, Cruise Ship, Warehouse, Burnside,
  Roswell, and Tutorials. Exactly 11 have same-build, uniquely resolved authored sky BSPs; Foundry and
  Warehouse intentionally have none, and Tutorials uses `Sk3Ed_Bch_Sky` rather than a basename match.
  Eleven have unambiguous authored backdrop colours (Airport's only colour is multiplayer-only and
  Warehouse authors none). All 13 pass the composed viewer/export gate with independent main/sky
  texture providers and material windows; a real Tutorials composition also exports to GLB.
- **Later SKA:** all 44,649 P8/PG X360/PS3 files pass the structural wrapper, section-table,
  size-table, and bounded key-stream gate. One P8 track has 66,790 quaternion keys, confirming
  that the stored u16 total is a low word. This still does not prove source-rig binding or visual
  animation fidelity.
- **THPS4 PC audio:** 3,612/3,612 DEE files (57,784,660 bytes, 116,881 Bink frames) satisfy the
  strict BIKi/Bink-DCT profile and every file decodes through ffmpeg. All 47 SMO soundtrack
  carriers satisfy their separate stereo profile and route directly to WAV as well as through
  the video path.
- **PSP PMF audio:** 333/334 PMFs contain exact private-stream ATRAC3+ frames and decode through
  ffmpeg after strict OMA wrapping; the remaining file is explicitly video-only. Corpus gates cover
  both 568- and 752-byte layouts and the complete former 1,325-frame failure case.
- **Late compound audio:** all 6,534 files classify exactly: 3,759 `.wav.ps3` (3,530 raw MP3 +
  229 one-stream FSB3) and 2,775 `.wav.xen` RIFF/XMA1. Each of the 27 authored
  codec/rate/channel/loop layouts has a real ffmpeg decode gate.
- **N64 effect audio:** four audited final ROMs expose 1,680/1,680 exactly resolved BFX effects
  over 1,775 PTR waves. The corpus gate decodes every selected wave, checks exact effective and
  integer WAV rates, validates all stored loop bounds, and pins one emitted loop `smpl` record and
  PCM SHA-256 per game. Cue inspection separately pins 83 banks / 3,172 records, records the exact
  selected BFX/PTR bytes behind each effect-index domain, and fails closed outside the compiled
  THPS2/3/Spider-Man alias ranges; the inspector does not accept THPS2 live state and therefore
  leaves those executable branches as the alternatives described in note 15.
- **Large audio banks:** 12 FSB3 banks expose 22,454 streams (5,418 MP3 + 17,036 XMA1). THAW
  X360's two paired DAT/WAD banks expose another 3,703 XMA1 streams and tile all 516,990,976 WAD
  bytes exactly. Wii's 6,578/6,578 extensionless DSP streams validate and decode.
- **Video:** 3,962 recognized/accepted corpus inputs span Bink, `.bik.xen`, PMF, VID1, PSS, SFD,
  TGR, SMO, and valid STR carriers; this is not a count of every file bearing a video-like suffix.
  Twelve of 335 loose STR candidates are pinned invalid/incomplete, while 14 PPV and five VLC files
  are foreign-game payloads and stay excluded. Output naming is collision-safe and MP4 writes are
  staged and validated.
- **Standalone collision:** 11,265 little-endian v8/v9/v10 files render; the sole named reject is
  THPS4 PS2's legacy v1 `canada.col.ps2`. THPS4 v8 accounts for 1,007 files, 12,523 objects,
  699,488 vertices, and 748,848 faces. Separately, **764/764 big-endian THAW X360 v10** files
  render (32,034 objects, 1,268,567 vertices, 996,233 faces), as do the 601 THPS4 PC DAT files
  above. THAW GameCube additionally renders the exact external-pool subset in note 6: 210/722
  canonical loose families plus 225/680 typed PAK-entry copies; incompatible and authored-empty
  families remain deliberate rejections.
- **Inline collision surfaces:** 670 exact PSX/Dreamcast/Spider-Man Windows environment sources
  across 535 per-build level families emit 2,182,243 of 2,661,969 declared triangles, including
  126,347 loader-invisible faces and 177 runtime-participating raw collision-flag values. The engine
  rejects 478,027 triangles by face class before reading indices; 1,699 participating degenerates
  are omitted and zero participating references are unresolved. THPS3
  has 39/43 complete non-empty runtime BSPs: 771,579 of 772,002 triangles emit across 394 flag
  values, with 423 geometric degenerates omitted. Missing face records, partial BSP sectors,
  invalid indices, remote archive evidence, and actor/object payloads fail closed.
- **Paired collision overlay:** exact-stem discovery covers 3,365 loose PS2 GEOM pairs and 90
  loose Xbox SCN pairs, plus all 29 THPS4 PC scene DATs. All 13 authored THPS4 main levels compose
  a render scene and v8 collision overlay; representative real PS2, Windows, and THPS4 pairs export.
  THPS2X has 104 exact DDM/PSX pairs, but only 24 carry both authored main-level markers and are
  enabled. Their 22,993,532 collision bytes contain 19,527 objects/meshes and 328,442 retained faces;
  visible and hidden collision-only faces together emit 485,549 non-degenerate triangles. Malformed,
  ambiguous, wrong-endian/version, remote-directory, non-level, and unsupported-platform candidates
  fail closed. The THAW GC subset uses the same proof for a default-off translucent overlay; a real
  pigeon fixture adds 45 collision triangles without changing default scene output. The inline
  PSX-lineage and THPS3 surfaces use this same default-off viewer/export route without inventing
  a companion file; GLB extras and Blender manifests preserve their collision-group boundaries.
- **NGC collision topology and binding:** THAW GC topology is 1,402/1,402, while the renderable
  exact-owner subset is quantified in note 6. Proving Ground Wii is 671/672 inspection-only; the sole
  reject has inverted scene bounds and is treated as an authored broken/stale record.

## Ordered remaining work

1. Render the proven N64 BFX envelope/control timeline without conflating a logical Sound Tools
   render with the native mixer's squared gain, exponential ramps, and equal-power stereo pan; keep
   deriving executable consumers for the non-alias raw cue fields.
2. Join PSP and next-gen weights, bone palettes, skeletons, and animation rigs, validate motion
   visually against native references, and bind decoded textures/materials.
3. Derive full Wii scene layouts, the PSP level-container dynamic-object records, and Proving Ground
   PS3 topology before enabling them.
4. Extend PSP editor/mission/global ownership and bring sky/background composition to every remaining
   level family only where runtime evidence makes the join unique.
5. Broaden the remaining NGC/Wii collision families only where same-space position ownership is
   proved. Do not infer companions from proximity, size, or basename alone across directories.
6. Add collectables and gameplay objects last, using trigger/QB/runtime evidence rather than
   proximity guesses.
