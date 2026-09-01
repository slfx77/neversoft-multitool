# GBA Tony Hawk support matrix

This page records what Neversoft Multitool can extract from the seven Tony
Hawk GBA cartridges in the local corpus. For THPS2 through American Sk8land,
the visible level is pre-baked isometric tile art; the skateable 3D world is a
separate parametric collision surface. A textured GLB combines those two
assets, but it is not evidence that the visible art was originally polygonal.
Downhill Jam is the exception: its separate Visual Impact engine stores the
visible perspective course as indexed, textured triangles.

**Decoded / tested** below means that the native structures close across the
applicable local retail-ROM corpus and regression tests pin corpus counts plus
representative decoded output. **Approximate** identifies presentation whose
source transform has not been found. **Unresolved** means that no parser or
export route is claimed.

## Capability matrix

| Game | Visible tile/course art | Collision / 3D level mesh | Rider models | GAX samples and music |
|---|---|---|---|---|
| Tony Hawk's Pro Skater 2 | **Decoded / tested:** nine full-colour 8bpp isometric surfaces, palettes and tile-detail views. | **Decoded / tested:** cartridge height functions produce the shape-aware collision surface and textured GLB. The stored art origin gives exact art/collision registration. | **Decoded / tested:** shared 266-face skater, 15 character palettes, 4,772 full-pose morph frames and 221 clip slots; static and selected/all non-empty animation exports are available. | **Decoded / tested:** sparse signed-PCM8 banks and 11 sequenced songs. This generation also has byte-exact reference-render and emulator-correlation evidence. |
| Tony Hawk's Pro Skater 3 | **Decoded / tested:** nine full-colour 8bpp tile surfaces and palettes. | **Decoded with offline-state caveat:** all nine grids and their cartridge height functions parse. Cells driven by live scene/player state are explicitly labelled and use the deterministic empty-scene contribution offline. The art origin is unresolved, so textured-mesh UV registration is **approximate and centred**; collision quads are never removed from that preview. | **Unresolved:** the THPS2 model complex is absent and THPS3's own rider container has not been identified. | **Decoded / tested:** sparse signed-PCM8 banks and 14 sequenced songs; structurally validated, not yet byte-compared with emulator audio. |
| Tony Hawk's Pro Skater 4 | **Decoded / tested:** eight full-colour mixed 4bpp/8bpp tile surfaces and palettes. The one-bit asset is exported separately as an occlusion mask. | **Decoded / tested geometry:** all eight parametric collision grids execute from the ROM. The art origin is unresolved, so textured-mesh UV registration is **approximate and centred** without geometry culling. | **Unresolved:** the THPS2 model complex is absent and this game's rider container has not been identified. | **Decoded / tested:** sparse unsigned-PCM8 banks and 10 sequenced songs; structurally validated, not yet byte-compared with emulator audio. |
| Tony Hawk's Underground | **Decoded / tested:** ten full-colour mixed 4bpp/8bpp tile surfaces and palettes, plus separately named occlusion masks. | **Decoded / tested geometry:** all ten parametric collision grids execute from the ROM; textured-mesh UV registration is **approximate and centred**. | **Unresolved:** the THPS2 model complex is absent and this game's rider container has not been identified. | **Decoded / tested:** sparse unsigned-PCM8 banks and seven sequenced songs; structurally validated, not yet byte-compared with emulator audio. |
| Tony Hawk's Underground 2 | **Decoded / tested:** seven full-colour mixed 4bpp/8bpp tile surfaces and palettes, plus separately named occlusion masks. | **Decoded / tested geometry:** all seven parametric collision grids execute from the ROM; textured-mesh UV registration is **approximate and centred**. | **Unresolved:** a loose header-like sequence does not close as a model complex, so no rider parser is claimed. | **Decoded / tested:** sparse unsigned-PCM8 banks and six sequenced songs; structurally validated, not yet byte-compared with emulator audio. |
| Tony Hawk's American Sk8land | **Decoded / tested:** twelve full-colour mixed 4bpp/8bpp tile surfaces and palettes, plus separately named occlusion masks. | **Decoded / tested geometry:** all twelve parametric collision grids execute from the ROM; textured-mesh UV registration is **approximate and centred**. | **Unresolved:** the THPS2 model complex is absent and this game's rider container has not been identified. | **Decoded / tested:** sparse unsigned-PCM8 banks and nine sequenced songs; structurally validated, not yet byte-compared with emulator audio. |
| Tony Hawk's Downhill Jam | **Decoded / tested:** all 11 self-relative perspective course packages close structurally. Complete indexed triangle banks parse; every non-degenerate triangle exports with exact flat palette fills, texture pages and the engine's special 64x64 page-zero sampling. Authored zero-area records are omitted from GLB. The 16-byte placed-object records remain undecoded and are not exported. | **Decoded / tested source paths:** all chunk-referenced sequential collision polylines and both stored road-edge arrays parse. Collision GLBs widen every exact polyline and each available edge into narrow viewer ribbons, then pair equal-length edge arrays into a road-envelope viewer proxy; these triangles are not claimed as an authored collision mesh. Course 06 has unequal edge counts and course 10 has only one edge, so neither receives a guessed road strip. | **Partial:** 24 rider variants can be assembled into posed static GLBs from a 94-clip, 13-part pose directory. Animated GLB tracks, packed normals and palette/ramp binding remain unresolved; group colours are diagnostic. | **Decoded / tested:** sparse unsigned-PCM8 banks and 11 sequenced songs; structurally validated, not yet byte-compared with emulator audio. |

The THPS4-through-Sk8land collision corpus covers 37 levels, 68,179 grid
cells and 2,555 referenced height-object slots. No referenced height function
is skipped. THPS3 covers 15,756 cells; its runtime-dependent cells retain their
authored base height while the unavailable live-object contribution is sampled
as an empty scene.

## Practical routes

Render the authored 2D level surfaces directly from a THPS2-through-Sk8land
ROM:

```text
NeversoftMultitool gba-level game.gba --output level-images
```

- THPS2 writes the full-colour surface as `level_NN_colour.png`, collision as
  `level_NN_iso.png`, the exactly registered overlay as
  `level_NN_overlay.png`, plus tile-detail and palette views.
- THPS3 writes `level_NN.png`, `level_NN_collision.png` and
  `level_NN_palette.png`.
- THPS4 through Sk8land write visible `level_NN.png`, decoded
  `level_NN_collision.png`, source palette, and the explicitly named
  `level_NN_occlusion.png`.
- Downhill Jam is detected by the same command and instead writes complete
  `course_NN.glb` visual meshes plus `course_NN_collision.glb` source-path
  viewer proxies. Use the dedicated command below with `--index N` for one
  course or `--no-collision` to omit the proxy GLBs.

The dedicated Downhill Jam spelling exposes the same course exporter:

```text
NeversoftMultitool gba-dhj-level downhill-jam.gba --output dhj-courses
```

The collision GLBs preserve every referenced sequential polyline and each
available road edge. Their narrow ribbons exist only because common GLB viewers
ignore line primitives. A paired edge strip is emitted only when both authored
arrays have exactly equal counts; course 06 is 661/635 and course 10 ends its
missing right edge with `0xCDCD`.

Carve a supported Vicarious Visions ROM, then export its collision levels to
GLB through the common mesh converter:

```text
NeversoftMultitool archive game.gba --output carved
NeversoftMultitool mesh carved/levels --output level-glb
```

The carve keeps `rom.gbarom` beside the `.lvl.gba` records because their art,
collision objects and executable height functions still point into the ROM.
THPS2 additionally creates `.chr.gba` roster records in `carved/models`:

```text
NeversoftMultitool mesh carved/models/00_tony_hawk.chr.gba --output rider-glb
NeversoftMultitool mesh carved/models/00_tony_hawk.chr.gba --output animated-rider --gba-animation 0
```

Use `--gba-animations` to export every non-empty THPS2 clip. Downhill Jam has a
separate, direct posed-rider route and is not accepted by the Vicarious Visions
level carver:

```text
NeversoftMultitool gba-dhj-model downhill-jam.gba --output dhj-riders
NeversoftMultitool gba-dhj-model downhill-jam.gba --output one-pose --index 19 --clip 79 --frame 0
```

Audio is CLI-only for all seven games:

```text
NeversoftMultitool gba-audio game.gba --output samples
NeversoftMultitool gba-music game.gba --output songs
```

In the desktop app, the **Archive Extractor** recognizes THPS2 through
Sk8land and emits the same carved records. Open the extracted directory in the
mesh browser: `.lvl.gba` entries appear in **Levels**, where the 2D view opens
on authored art and the layer picker exposes available collision/overlay views;
the 3D view and export use the collision mesh. THPS2 `.chr.gba` entries appear
under **Meshes & Characters** and can use the animation panel. GAX audio and the
Downhill Jam course/rider exporters currently have dedicated CLI routes rather
than desktop-tab integration.

## Evidence and implementation

- Visible art: [`GbaLevelImages.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaLevelImages.cs),
  [`GbaThps3LevelArt.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaThps3LevelArt.cs)
  and [`GbaLaterLevelArt.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaLaterLevelArt.cs).
- Collision and GLB construction:
  [`GbaCollisionSurface.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaCollisionSurface.cs),
  [`GbaThps3CollisionSurface.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaThps3CollisionSurface.cs),
  [`GbaLaterCollisionSurface.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaLaterCollisionSurface.cs)
  and [`GbaLevelGeometryWriter.cs`](../../src/NeversoftMultitool/Core/Formats/Mesh/Conversion/GbaLevelGeometryWriter.cs).
- Downhill Jam course parsing and GLB construction:
  [`GbaDhjCourse.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaDhjCourse.cs)
  and [`GbaDhjCourseGeometryWriter.cs`](../../src/NeversoftMultitool/Core/Formats/Mesh/Conversion/GbaDhjCourseGeometryWriter.cs).
- Rider implementations:
  [`GbaSkaterModel.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaSkaterModel.cs)
  and [`GbaDhjModel.cs`](../../src/NeversoftMultitool/Core/Formats/Gba/GbaDhjModel.cs).
- Audio layouts, corpus counts and fidelity limits are detailed in
  [GBA GAX audio support](gba-gax-audio.md).
- The binary evidence trail and rejected false leads are retained in the
  [GBA investigation record](../../tools/research/gba-3d/FINDINGS.md).
