# Neversoft Multitool

.NET 10.0 tool for extracting, inspecting, converting, and previewing Neversoft-era game assets across PS1, N64, Dreamcast, GBA, Xbox, GameCube, PS2, Windows, DS, Wii, PSP, Xbox 360, and PS3 releases (1998–2007). The WinUI 3 GUI and cross-platform CLI share the same content-gated format decoders.

For the current per-platform end-goal audit—including late handheld, Wii, Xbox 360, and PS3
coverage—see [Non-GBA corpus robustness](docs/corpus-robustness.md).

## Supported Formats

### Textures

| Format        | Description                                                                     | Games                                   |
| ------------- | ------------------------------------------------------------------------------- | --------------------------------------- |
| PSX (PS1)     | 4-bit / 8-bit paletted textures → PNG                                           | All PS1 titles                          |
| PSX (Xbox/DC) | 16-bit PowerVR textures (twiddled, VQ, rectangle) → PNG/DDS                     | THPS2X, Spider-Man DC, THPS2 DC         |
| PVR           | Standalone Dreamcast GBIX+PVRT textures (ARGB1555, RGB565, ARGB4444) → PNG/DDS  | THPS2 DC, Spider-Man DC                 |
| RLE / BMR / ZLB | Neversoft RLE bitmaps — RGBA5551 (PS1), BMP-wrapped 24-bit RGB (DC), gzip ZLB | All titles                              |
| BMP / TGA / TIFF / PNG / JPEG / GIF | Standard bitmaps as shipped on disc (alpha preserved; TIFF mip chains exported) → PNG | THPS1/2, THPS3+, Spider-Man             |
| PS2 TEX / IMG | Version-tagged GS textures (PSMCT32/16, PSMT8/4, CLUT swizzle) → PNG            | THPS4, THUG, THUG2, THAW                |
| RW TXD        | RenderWare 3.x Texture Dictionaries (PS2-native rasters) → PNG                  | THPS3 PS2                               |
| Xbox/PC TEX / IMG | DXT1/DXT5, paletted, and raw BGRA textures → PNG; strict delimiter-free `*tex.dat` dictionaries and `*img.dat` surfaces | THUG2 Xbox/PC, THPS4 PC |
| N64 TEX / IMG | Carved texture records, palettes, and stored mip levels → PNG                    | THPS1–3, Spider-Man N64                 |
| DS texture banks | Indexed/direct-colour texture and palette companions → PNG                  | American Sk8land, DHJ, Proving Ground   |
| NGC/Wii TEX, STEX, IMG | GX C8/CMPR/RGBA8 dictionaries and padded IMG records → PNG            | THAW GameCube, DHJ/PG Wii               |
| PSP IMG       | PSP image records, including padded-linear crops → PNG                            | THUG2 Remix, Project 8                  |
| X360/PS3 TEX / IMG | `FACECAA7`, Xenon tiled DXT/DXN, and proof-bound PS3 VRAM companions (all non-empty corpus dictionaries) → PNG | THAW, Project 8, Proving Ground |

### Archives

| Format    | Description                                                        | Games                             |
| --------- | ----------------------------------------------------------------- | --------------------------------- |
| WAD + HED | Paired archive/index format                                       | Apocalypse, THPS series           |
| PKR3      | Compressed archive format                                         | Spider-Man PC                     |
| PRE       | Simple flat archive format (plain + compressed PRE3/PRX)          | THPS1 PS1, THPS2 PS1/DC, THPS3+   |
| DDX       | Xbox texture archives containing DDS files                        | THPS2X                            |
| BON       | Dreamcast v1 (PVR → PNG) and Xbox v3/v4 (raw DDS)                 | THPS2 DC, THPS2X                  |
| PAK       | Neversoft PAK archives (+ companion .pab data)                    | THUG2, THAW, Guitar Hero (PS2)    |
| Disc images | ISO9660 / Xbox XDVDFS / GameCube GCM filesystems from .iso, .bin+.cue, .img+.ccd, and Dreamcast .gdi dumps; PS1 STR/XA streams extract losslessly (2336-byte sectors) and CD audio tracks extract as WAV | All platforms |

The **Game Unpacker** recursively extracts every archive under a directory in one pass — including nested archives (a disc image containing WADs, a WAD containing PREs, a PAK inside a WAD) — reproducing the game's on-disc directory tree.

### Audio

| Format | Description                                                     | Games                        |
| ------ | -------------------------------------------------------------- | ---------------------------- |
| XA     | PS1 ADPCM audio (sectored and raw) → WAV                       | All PS1 titles               |
| VAB    | PS1 sound bank (multi-sample) → WAV                            | All PS1 titles               |
| VAG    | PS2 SPU-ADPCM (headered + headerless; music streams decode as stereo 48 kHz) → WAV | THPS3+ PS2                    |
| PSS    | Headerless SPU-ADPCM → WAV                                     | Spider-Man PC, THPS3+ PS2     |
| ADX    | CRI Middleware audio → WAV                                     | THPS2 DC, Spider-Man DC       |
| KAT    | Dreamcast audio soundbank (ADPCM + PCM) → WAV                  | THPS2 DC, Spider-Man DC       |
| SFX    | Dreamcast cue banks (resolves companion KAT/VAB samples) → WAV | THPS2 DC, Spider-Man DC       |
| PCM    | Xbox IMA ADPCM sound effects → WAV                             | THUG2 Xbox / Windows          |
| SND    | THUG2 PC continuous 4-bit IMA sound effects → WAV              | THUG2 Windows                 |
| DEE / audio-only SMO | Strict Bink-DCT soundtrack carriers → PCM16 WAV | THPS4 Windows                 |
| SWAV / STRM / HWAS | Nintendo DS PCM/ADPCM streams → WAV                | American Sk8land, DHJ, PG     |
| AT3    | PSP ATRAC3 streams → WAV (ffmpeg)                              | THUG2 Remix, Project 8        |
| PMF audio | Strict PSMF private-stream ATRAC3+ demux → WAV (ffmpeg)       | THUG2 Remix, Project 8        |
| DSP / audio-only VID1 | Nintendo DSP-ADPCM streams → WAV               | THAW GameCube, DHJ/PG Wii     |
| FSB3 / XMA DAT+WAD | Named MP3/XMA1 bank streams → raw audio or WAV     | THAW/P8/PG Xbox 360 and PS3   |
| WAV.PS3 / WAV.XEN | Content-gated raw MP3, one-stream FSB3, and RIFF/XMA1 carriers → WAV | Project 8, Proving Ground |
| WAV / WMA | Content-gated pass-through or PCM16 conversion              | Later Windows builds          |
| N64 PTR/WBK | Nintendo Sound Tools metadata → inspection JSON; selected stored wave → mono PCM16 WAV at an explicit caller rate | THPS1–3, Spider-Man N64 |
| N64 BFX | Exact initial-effect BFX→PTR selection, signed base-note/fine-tune plus BFX-note pitch, audited 22047 Hz mixer basis, and stored infinite ALADPCM loop → PCM16 WAV with `smpl`; raw envelopes and later bytecode are not rendered | THPS1–3, Spider-Man N64 finals |
| N64 runtime profile | Audited final-ROM Sound Tools global mixer/output profile → schema-v1 inspection JSON and exact initial-effect playback basis (not an authored per-wave or cue rate) | THPS1–3, Spider-Man N64 finals |
| N64 SFX cues | Strict raw 16-byte cue records; executable-pinned THPS2/3/Spider-Man alias→BFX maps with exact BFX/PTR provenance, runtime-width-correct raw/lookup aliases, fail-closed THPS2 live-state alternatives, and executable-proven no-target sentinels | THPS1–3, Spider-Man N64 |

The **Audio Converter** offers in-app playback with a seekable timeline for the whole file or individual bank samples.
Recursive batches keep unique output names unchanged and add a stable path-derived suffix only when output names would otherwise collide.

### 3D Models & Animation

| Format          | Description                                                                | Games                        |
| --------------- | ------------------------------------------------------------------------- | ---------------------------- |
| PSX mesh        | PS1/Dreamcast/Spider-Man Windows geometry → glTF with vertex colours; exact registered level surfaces offer opt-in collision flags + loader-invisible faces | THPS1–4, Spider-Man, Apoc.   |
| DDM             | Xbox level geometry → glTF with materials, textures, lights, PSX placement, TRG-authored sky/background composition, and opt-in PSX collision overlay | THPS2X |
| RW DFF          | RenderWare 3.x skinned meshes (.SKN) → glTF with skeleton                  | THPS3 PS2                    |
| RW BSP          | RenderWare 3.x world/level geometry → glTF; exact `levels.qb` main/sky/background composition and opt-in per-triangle collision view | THPS3 PS2 |
| COL             | Collision v8/v9/v10 (`.col.ps2/.xbx/.wpc/.xen`, bare, strict THPS4 PC `*col.dat`, and proof-bound THAW `.col.ngc`) → glTF | THPS4 through Proving Ground |
| Xbox/PC/NGC MDL / SKIN | Native scene meshes → glTF; strict THPS4 PC delimiter-free skin/model/scene DAT; opt-in weights with explicit CLI or per-entry GUI skeleton selection | THPS4, THUG2, THAW |
| PS2 MDL / SKIN  | Native PS2 scene meshes (incl. `.iskin.ps2`) → glTF                       | THPS4, THUG, THUG2          |
| PS2 GEOM        | Pre-compiled CGeomNode render trees (`.geom.ps2`) → glTF                  | THPS4, THUG, THUG2          |
| THAW skin/zone  | Pre-compiled VIF/DMA skins + worldzone level PAKs → glTF                  | THAW PS2                    |
| DS model sets   | Geometry, textures, skeletons, clips, and collision worlds → glTF          | American Sk8land, DHJ, PG   |
| PSP GE mesh     | Rigid PSP display-list meshes (`.skin/.mdl/.geom.psp`) → glTF              | THUG2 Remix, Project 8      |
| PSP level       | Strict `.psp_level` static worlds + embedded textures → glTF; same-build runtime-manifest sky/main composition where ownership is unique | THUG2 Remix, Project 8 |
| X360/PS3 scene  | Big-endian THAW/P8/PG scene geometry and PS3 VRAM companions → glTF        | THAW, Project 8, PG¹        |
| CAS removal metadata | Strict PS2/Xbox v2 polygon-removal sidecars → inspection JSON (not applied to geometry) | THPS4, THUG, THUG2 |
| WGT scaling metadata | Strict compiled PS2/Xbox v1 cutscene-head weight maps → inspection JSON (not applied to geometry) | THUG, THUG2 |
| N64 model       | Carved shell + render bank → glTF; evidence-gated global/relative 0x2A/0x2C animation via the Animations pane or `mesh --n64-animations` | THPS1–3, Spider-Man N64 |
| SKE / SKA       | Derived little-endian skeleton/animation families → glTF/Blender; strict THPS4 PC `*ska.dat`, THAW cameras, exact source-rig mapping, and bare-CUT inspection | THPS4–Proving Ground |
| Next-gen SKA    | Strict P8/PG X360/PS3 wrapper, section, size, and bounded key-stream parsing (not native rig binding or visual-motion validation) | Project 8, Proving Ground |
| PSX animation   | PS1 character skeletal animation → animated glTF (.glb / .gif)            | THPS1/2, Spider-Man, Apoc.   |

¹ Project 8 PS3 has a validated scene subset; Proving Ground PS3 topology remains deliberately disabled because its currently decoded indices produce shattered geometry.

Mesh conversion writes glTF (.glb), and — where a Blender helper is configured — Blender (.blend) scenes. Skinned meshes export with joints, weights, and inverse-bind matrices on the families whose rig binding is derived; later PSP/X360/PS3 geometry is still rigid. PSX Blender scenes also preserve authored frame-zero colour and play validated portable colour-pulse channels through a shared Geometry Nodes graph. The **Meshes & Characters** tab renders models and plays back animations in-app with a play/pause/seek transport, exports animated GLB, Blender scenes, or GIF renders, and can render any previewed model to PNG stills or animated GIF with the built-in headless rasterizer.

Collision also renders independently. The **Levels** tab and `mesh --collision-overlay` offer a
default-off translucent overlay when a scene owns proven inline collision topology (a proof-bound
PSX-lineage environment role or complete THPS3 runtime BSP flags), or when a same-owner, exact-stem
PS2 GEOM, Xbox/Windows SCN, delimiter-free THPS4 PC scene/COL, proof-bound THAW GameCube scene/COL,
or authored THPS2X DDM/PSX pair passes its collision parser and platform gate. GameCube `.col.ngc`
stores no positions, so its
standalone and overlay routes require an exact loose stem or a unique same-directory typed PAK peer,
matching object checksums/order, an exact static-or-skin position count, valid face ranges and bounds,
and real non-degenerate triangles. The THPS2X route is restricted to the 24 complete main-level families identified by
both exact `_o.ddm` and `_t.trg` markers, and includes the PSX payload's hidden collision-only faces;
the other 80 structural DDM/PSX pairs are not promoted as levels. THPS4 PC main levels also compose
the exact sky and optional editor shell authored by `Levels.qb`. PSX-lineage collision identity uses
parsed TRG `SpoolEnv` registrations plus a narrow legacy-console VAB compatibility marker; it never
inherits a remote archive directory's object bank. Collision uses serialized vertex coordinates,
omits the runtime's unconditional non-collision face class, and preserves the collision halfword
separately from the GPU/render flags.
THPS3 similarly re-emits the BSP surface grouped by the complete runtime collision plugin. Both GLB
extras and Blender manifests retain those classification boundaries after their shared translucent
material is merged. This intentionally does not guess PS3, PSP, cross-owner hashes/offsets, or
unmatched X360 pairings.

PSP `.psp_level` selection applies the same evidence standard: same-build `levels.qb` structures and
the shipping PSP `load_level` branch prove 42 Remix and 40-per-build Project 8 main variants, with
camera-locked skies on 40 and 36 respectively. Ambiguous Remix editor themes and Project 8
mission/global/zone overlays remain standalone rather than being joined by basename.

### Scripts & Levels

| Format | Description                                                                  | Games                     |
| ------ | --------------------------------------------------------------------------- | ------------------------- |
| TRG    | Level trigger/script files → JSON (spawns, camera paths, rails, bytecode)   | Apocalypse, Spider-Man, THPS |
| QB     | Compiled Neversoft game scripts (`.qb`) → decompiled `.q` source           | THPS3–THUG2               |
| ANIM   | Frontend UI timeline forests (`.ANIM`) → inspection JSON (not skeletal)    | THPS2X                    |

### Video

| Format | Description                                            | Games                        |
| ------ | ----------------------------------------------------- | ---------------------------- |
| SFD / PSS | CRI Sofdec and PS2 program streams → MP4 (ffmpeg) | Dreamcast and PS2 titles     |
| STR    | PS1 MDEC video streams → MP4 (pure C# decoder)        | Apocalypse, Spider-Man, THPS1/2 |
| VID1   | GameCube/Wii movie and audio-only containers → MP4/WAV | THAW GameCube, DHJ/PG Wii   |
| BIK / BIK.XEN | Bink 1/2 movies → MP4 (ffmpeg)                | Windows and Xbox 360 builds  |
| TGR / SMO | Content-gated THPS4 PC Bink movie/soundtrack carriers → MP4 | THPS4 Windows       |
| PMF    | Strict PSP PSMF video + ATRAC3+ audio → MP4; video-only streams remain explicit | THUG2 Remix, Project 8 PSP |

All video formats play back in-app in the **Video Converter** tab — STR and VID1 stream directly through their native decoders (with seek support); SFD/PSS/BIK/PMF/TGR/SMO convert on first preview and cache on disk so later previews start instantly.

### Tested Games

- Apocalypse (PS1)
- Spider-Man (PS1 / N64 / Dreamcast / Windows) and Spider-Man 2: Enter Electro (PS1)
- Tony Hawk's Pro Skater 1–4 (PS1 / N64 / Dreamcast / PS2 / Windows, as applicable)
- Tony Hawk's Pro Skater 2X (Xbox) and Tony Hawk's Underground (PS2)
- Tony Hawk's Underground 2 / Remix (PS2 / Xbox / Windows / PSP)
- Tony Hawk's American Wasteland (PS2 / GameCube / Windows / Xbox 360)
- Tony Hawk's American Sk8land (DS) and Tony Hawk's Downhill Jam (DS / Wii)
- Tony Hawk's Project 8 (PS2 / PSP / Xbox 360 / PS3)
- Tony Hawk's Proving Ground (PS2 / DS / Wii / Xbox 360 / PS3)

The robustness audit includes finals, prototypes, demos, and revisions where present. A listed game is a corpus target, not a claim that every end-goal layer is complete; see the linked matrix for the remaining per-platform gaps.

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Windows 10 version 1809+ (for GUI mode)
- CLI mode works on Windows, Linux, and macOS
- [ffmpeg](https://ffmpeg.org/) on `PATH` for compressed video plus WMA, ATRAC3, Bink-DCT, MP3, and XMA audio conversion

## Build

```bash
# Build (GUI + CLI)
dotnet build src/NeversoftMultitool/NeversoftMultitool.csproj

# Build and run tests
dotnet build tests/NeversoftMultitool.Tests/NeversoftMultitool.Tests.csproj
tests/NeversoftMultitool.Tests/bin/Debug/net10.0/NeversoftMultitool.Tests.exe
```

## Usage

### GUI Mode (Windows)

Run the executable with no arguments to launch the WinUI 3 interface:

```bash
dotnet run --project src/NeversoftMultitool -f net10.0-windows10.0.19041.0
```

The GUI is organized into tabs with batch-processing support:

- **Textures** — PSX/PVR, N64, PS2/RW, Xbox/PC, DS, GameCube/Wii, PSP, Xbox 360, and PS3 texture families → PNG, with zoomable preview (fit / 100% + pan, optional pixel-perfect integer scaling); folder and archive scans run in the background with progress, and archives are browsed *through* their nested archives
- **Bitmap Converter** — Neversoft RLE/BMR/ZLB, N64 fullscreen images, and standard BMP/TGA/TIFF/PNG/JPEG/GIF → PNG with zoomable preview; source PNGs are never rewritten in place
- **Archive Extractor** — WAD, PKR, PRE, DDX, BON, PAK, and disc images (ISO / BIN+CUE / IMG+CCD / GDI)
- **Game Unpacker** — recursive extraction of every archive in a game directory (disc images included)
- **Audio Converter** — all listed audio families, including PSP PMF ATRAC3+, THPS4 PC DEE/SMO, late `.wav.ps3`/`.wav.xen`, multi-stream FSB3/XMA banks, and content-gated extensionless Wii DSP, with in-app playback; VAB banks auto-detect their tone-table sample rate
- **Video Converter** — SFD, PSS, BIK/BIK.XEN, PMF, TGR, SMO, STR, and VID1 with playback preview, per-file conversion checkboxes, collision-safe output names, and recursive folder scans; VID1 seeks resume from decode anchors instead of re-decoding from frame 0
- **Levels** — world-scale preview and export, with separate default-off controls for showing and exporting a collision companion only where the conservative exact-owner/structural resolver supports it
- **Meshes & Characters** — every mesh format above → glTF/Blender in one tab: batch conversion/rendering over checked files, a three.js 3D preview with camera presets (perspective / true orthographic / isometric / trimetric, named views, 90° snap) plus **Orbit / Fly / Walk** control modes (WASD + Q/E, Shift/Ctrl speed, mouse-look; levels start in Fly and F toggles Walk, other models toggle Fly/Orbit), and an Animations pane for skinned characters (bone-count-filtered discovery, exact source-rig selection from extracted files or direct/nested archive entries, animated preview, GLB/Blender/GIF export). Eligible Xbox/PC/GameCube skinned scenes also expose a per-entry manual skeleton choice from an extracted file or a direct/nested archive entry; the parsed rig is captured independently for preview, batch export, PNG, and GIF, while default or incompatible selections remain rigid. Archives over 2 GB open fine, and meshes inside nested archives (SKATE3.WAD's level PREs) scan directly
- **Script Decompiler** — TRG triggers → JSON and QB scripts → `.q` source, in a three-pane file / node / detail layout
- **Hash Reviewer** — QBKey hash → name resolution review

### CLI Mode

```bash
# On Windows (from GUI build), use --no-gui or a subcommand to enter CLI mode
# On any platform (from CLI build):
dotnet run --project src/NeversoftMultitool -f net10.0 -- <command> [options]
```

Most conversion commands take an input file or directory plus `-o/--output`; inspection commands may instead write one JSON file, and verbosity is command-specific. Run any command with `--help` for its exact options.

| Command    | Purpose                                                      |
| ---------- | ----------------------------------------------------------- |
| `psx`      | Extract textures from PS1 PSX model files                   |
| `pvr`      | Convert Dreamcast PVR textures → PNG                        |
| `ps2tex`   | Convert PS2 TEX/IMG, PSP IMG, and RW TXD textures → PNG      |
| `xbxtex`   | Convert Xbox/PC/X360/PS3 TEX/IMG textures → PNG              |
| `ngctex`   | Convert GameCube/Wii TEX/STEX/IMG textures → PNG             |
| `n64tex`   | Convert carved N64 TEX/IMG records and stored mip levels → PNG |
| `rle`      | Convert RLE/BMR/ZLB and standard bitmaps → PNG              |
| `archive`  | Extract a WAD/PKR/PRE/PRX/DDX/BON/PAK archive or disc image (ISO/CUE/GDI/IMG) |
| `cas`      | Inspect typed PS2/Xbox CAS polygon-removal metadata → schema-v1 JSON (no geometry changes) |
| `wgt`      | Inspect compiled PS2/Xbox WGT v1 mesh-scaling metadata → schema-v1 JSON (no geometry changes) |
| `unpack`   | Recursively extract every archive under a directory         |
| `audio`    | Convert supported bank, console, handheld, and standard audio → raw/WAV |
| `n64-audio-inspect` | Inspect a paired N64 Sound Tools PTR/WBK bank → JSON |
| `n64-audio-fx-inspect` | Inspect a Sound Tools BFX bank, its conservative initial event, and local-wave→PTR bindings → JSON |
| `n64-audio-runtime-inspect` | Inspect an audited ROM-global Sound Tools mixer/output profile → JSON |
| `n64-sfx-inspect` | Inspect raw N64 cue banks and, for pinned THPS2/3/Spider-Man ROMs, resolve static compiled alias→BFX mappings while preserving live-state alternatives → schema-v3 aggregate JSON |
| `n64-audio-decode` | Decode a raw stored wave at a caller rate, or an audited ROM effect with exact initial BFX→PTR pitch/rate/loop semantics → mono PCM16 WAV |
| `sfd`      | Convert SFD/PSS/BIK/PMF/TGR/SMO video → MP4                 |
| `str`      | Convert PS1 MDEC (STR) video → MP4                          |
| `vid`      | Convert THAW GameCube VID1 video → MP4                      |
| `mesh`     | Auto-detect and convert any supported mesh → glTF/Blender; `.col.ngc` uses a proof-bound render pool, and `--collision-overlay` opts into exact-owner collision composition |
| `ddm`      | Convert DDM level meshes → glTF (PSX placement + authored sky) |
| `psx-mesh` | Convert PS1 PSX model files → glTF/Blender                  |
| `psx-mesh-dump` | Dump PS1 mesh parse diagnostics → JSON              |
| `rwdff`    | Convert RenderWare DFF (.SKN) meshes → glTF/Blender         |
| `rwbsp`    | Convert RenderWare BSP levels → glTF/Blender                |
| `col`      | Convert little- or big-endian collision v8/v9/v10 → glTF/Blender |
| `ngccol`   | Inspect GameCube `.col.ngc` topology/BSP/intensity metadata → JSON (positions are external) |
| `ska`      | Parse compiled SKA; export derived rig families → glTF/Blender (`--format`; exact cross-rig binding via `--animation-ske`); inspect bare-CUT SKA → JSON |
| `psxanim`  | Probe a PS1 character `.psx` for animation data             |
| `psx-anim-export` | Export a PS1 character `.psx` as an animated `.glb`  |
| `psx-anim-trace` | Trace PS1 animation bone transforms against an exporter or GLB |
| `psx-anim-survey` | Survey PS1 files by version and animation-table layout |
| `trg`      | Parse TRG trigger/script files → JSON                      |
| `qb`       | Decompile compiled QB scripts → `.q` source                |
| `qbkey`    | QBKey hash utilities (cross-reference, import)             |
| `glb-render` / `glb-gif` | Render `.glb` files to PNG / animated GIF     |
| `gsdump`   | Audit raw PCSX2 GS dumps and compare against screenshot PNGs |
| `thps2x-anim` | Inspect THPS2X frontend UI `.ANIM` timelines → JSON     |

#### Examples

```bash
# Extract every archive in a game directory (nested archives included)
NeversoftMultitool unpack "path/to/game" -v

# Extract a disc image (PS1 bin+cue, Dreamcast gdi, PS2/Xbox/GC iso, ...)
NeversoftMultitool archive "Tony Hawk's Pro Skater (USA).cue" -o out

# Convert a THAW PS2 character to glTF (textures + skeleton auto-discovered)
NeversoftMultitool mesh skater_muska.skin.ps2 -o out

# Convert a directory of audio files to WAV
NeversoftMultitool audio "path/to/sounds" -o out

# Inspect the unique Sound Tools bank in an N64 ROM (the bank alone has no playback rate/cue ownership)
NeversoftMultitool n64-audio-inspect game.z64 -o bank.json

# Inspect the separate ROM-global mixer/output profile for an evidence-matched audited ROM
# (22050 requested → divisor 2208 / DACRATE 2207 → 22047 returned; not a wave rate)
NeversoftMultitool n64-audio-runtime-inspect game.z64 -o runtime.json

# Resolution is fail-closed: boot SHA, country byte, pinned NTSC clock, and the
# exact raw-ROM osAiSetFrequency routine must all match the audited build.

# Inspect an explicitly paired standalone bank
NeversoftMultitool n64-audio-inspect bank.ptr.n64 --wave 000.wbk.n64 -o bank.json

# Inspect the unique Sound Tools BFX/PTR singletons and conservative initial-event metadata
NeversoftMultitool n64-audio-fx-inspect game.z64 -o effects.json

# A uniquely validated carved BFX is named .bfx.n64; standalone input still needs its PTR index space
NeversoftMultitool n64-audio-fx-inspect effects.bfx.n64 --pointer bank.ptr.n64 -o effects.json

# Inspect every strict raw cue bank in a ROM; pinned THPS2/3/Spider-Man builds also report exact compiled aliases
NeversoftMultitool n64-sfx-inspect game.z64 -o cues.json

# Inspect one extracted raw cue table with the same aggregate schema
NeversoftMultitool n64-sfx-inspect cue.sfx.n64 -o cues.json

# Decode effect 4 from an audited final ROM using its exact initial wave, 22047 Hz mixer,
# signed note/fine-tune pitch, and stored infinite-loop metadata (`smpl` when present)
NeversoftMultitool n64-audio-decode game.z64 --effect 4 -o effect4.wav

# Or decode stored wave 221 once; 32000 Hz is caller-supplied WAV playback metadata
NeversoftMultitool n64-audio-decode game.z64 --index 221 --sample-rate 32000 -o wave221.wav

# Standalone PTR input still requires its explicit WBK payload
NeversoftMultitool n64-audio-decode bank.ptr.n64 --wave 000.wbk.n64 --index 221 --sample-rate 32000 -o wave221.wav

# Inspect GameCube collision metadata; no vertex positions are stored in this file
NeversoftMultitool ngccol models -o collision-json -v

# Render a THAW GameCube COL only when its exact scene owner proves the external position pool
NeversoftMultitool mesh anl_pigeon.col.ngc -o collision-glb

# Add that same proven surface to the scene (off by default)
NeversoftMultitool mesh anl_pigeon.skin.ngc -o scene-glb --collision-overlay

# Inspect PS2/Xbox polygon-removal sidecars without applying them to geometry
NeversoftMultitool cas models -o cas-json -v

# Inspect compiled cutscene-head scaling weights without applying them to geometry
NeversoftMultitool wgt models -o wgt-json -v

# Decompile a compiled script
NeversoftMultitool qb level.qb -o out
```

## Architecture

The project uses multi-targeting to produce both a cross-platform CLI and a Windows GUI from a single codebase:

- **`net10.0`** — Cross-platform CLI using System.CommandLine + Spectre.Console
- **`net10.0-windows10.0.19041.0`** — WinUI 3 GUI with Mica backdrop

Shared format logic lives in `Core/` and is used by both targets. GUI code in `App/` is excluded from cross-platform builds via conditional compilation (`#if WINDOWS_GUI`).

```
src/NeversoftMultitool/
  Core/                    # Shared format logic
    BinaryIO/              # BinaryReader extensions, ImageWriter
    Formats/
      Texture/             # PSX, PVR, PS2 TEX, RW TXD, Xbox TEX, NGC decoding
      Rle/                 # RLE/BMR bitmap conversion
      Archives/            # WAD, PKR, PRE, DDX, BON, PAK extraction
      ArchiveFs/           # Read-only filesystem over any archive (nested opens)
      DiscImage/           # ISO9660/XDVDFS/GCM disc images (iso, cue, ccd, gdi)
      Audio/               # XA, VAB, VAG, ADX, KAT, SFX, PCM, SND, PSS decoding
      Mesh/                # PSX/DDM/RW/PS2/Xbox meshes → glTF
      Collision/           # COL collision meshes → glTF
      Animation/           # SKA + PSX skeletal animation
      Trg/                 # TRG level trigger/script parsing
      Qb/                  # QB compiled-script decompilation
      Video/               # SFD, STR video conversion
      Vid1/                # THAW GameCube VID1 decoder
      GsDump/              # Software GS replay (PCSX2 .gs validation)
  CLI/                     # Command-line interface
  App/                     # WinUI 3 GUI (Windows only)
    Tabs/                  # One tab per format family
```

## Code Guardrails

- C# files should stay under a soft 500-line limit. Existing exceptions are tracked in repo-policy tests and should be reduced over time instead of adding new ones.
- `partial class` usage should stay limited to UI XAML code-behind and cases where source generation requires it, such as `[GeneratedRegex]`.

## Acknowledgements

This project contains code derived from or informed by:

- [io_thps_scene](https://github.com/denetii/io_thps_scene) — Blender plugin for Tony Hawk's Pro Skater formats, used as reference for PSX/PS2 model, collision, and texture parsing.
- [psx_extractor](https://github.com/krystalgamer/spidey-tools/tree/master/psx_extractor) — Spider-Man PC PSX extractor, used as reference for 16-bit texture decoding.
- [Rawtex](https://zenhax.com/viewtopic.php?t=7099) — Multipurpose raw texture converter, used as reference for PowerVR palette type handling.
- [RLE-GIMP-Plugin](https://github.com/Daniel-McCarthy/RLE-GIMP-Plugin) — GIMP plugin for Neversoft RLE/BMR files, used as reference for the PS1 RLE format.
- [jPSXdec](https://github.com/m35/jpsxdec) — PlayStation 1 media decoder/converter in Java, used as reference for XA ADPCM and MDEC video decoding.
- [KAT2WAV](https://github.com/DCxDemo/KAT2WAV) — Dreamcast KAT soundbank extractor, used as reference for KAT format understanding.
- [Hed-Extract](https://github.com/Daniel-McCarthy/Hed-Extract) — PSP Tony Hawk's Project 8 HED/WAD extractor/packer, used as format reference for HED archive extraction.
- [thps2-tools](https://github.com/JayFoxRox/thps2-tools) — THPS2 WAD/HED extraction script, used as reference for WAD archive format.
- [NxTools](https://gitgud.io/Fretworks/NxTools) — Blender plugin for Neversoft game assets, used as format reference for Xbox/THAW scene and texture parsing.
- [Queen-Bee](https://github.com/Nanook/Queen-Bee) — Guitar Hero/Tony Hawk PAK/QB editor, used as reference for PAK archive entry format.
- [librw](https://github.com/aap/librw) — RenderWare engine re-implementation, used as reference for RW TXD/DFF/BSP formats.
- [PCSX2](https://github.com/PCSX2/pcsx2) — PlayStation 2 emulator, used as reference for GS pixel-format, swizzle, and blend semantics.

### Previous Versions

This tool was originally two separate Python/PyQt5 tools:

- [Neversoft Bitmap Converter](https://github.com/slfx77/neversoft_bitmap_converter)
- [PSX Texture Extractor](https://github.com/slfx77/psx_texture_extractor)
