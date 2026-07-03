# Neversoft Multitool

.NET 10.0 tool for extracting and converting assets from Neversoft Entertainment game files, spanning the PS1, Dreamcast, Xbox, GameCube, PC, and PS2 eras (1998–2007). Features a WinUI 3 GUI on Windows and a cross-platform CLI that share the same format decoders.

## Supported Formats

### Textures

| Format        | Description                                                                     | Games                                   |
| ------------- | ------------------------------------------------------------------------------- | --------------------------------------- |
| PSX (PS1)     | 4-bit / 8-bit paletted textures → PNG                                           | All PS1 titles                          |
| PSX (Xbox/DC) | 16-bit PowerVR textures (twiddled, VQ, rectangle) → PNG/DDS                     | THPS2X, Spider-Man DC, THPS2 DC         |
| PVR           | Standalone Dreamcast GBIX+PVRT textures (ARGB1555, RGB565, ARGB4444) → PNG/DDS  | THPS2 DC, Spider-Man DC                 |
| RLE / BMR     | Neversoft custom RLE bitmaps — RGBA5551 (PS1) + BMP-wrapped 24-bit RGB (DC)     | All titles                              |
| PS2 TEX / IMG | Version-tagged GS textures (PSMCT32/16, PSMT8/4, CLUT swizzle) → PNG            | THPS4, THUG, THUG2, THAW                |
| RW TXD        | RenderWare 3.x Texture Dictionaries (PS2-native rasters) → PNG                  | THPS3 PS2                               |
| Xbox TEX / IMG| DXT1/DXT5, paletted, and raw BGRA textures → PNG                                | THUG2 Xbox/PC                           |
| NGC TEX       | GameCube texture dictionaries → PNG                                             | THAW GameCube                           |

### Archives

| Format    | Description                                                        | Games                             |
| --------- | ----------------------------------------------------------------- | --------------------------------- |
| WAD + HED | Paired archive/index format                                       | Apocalypse, THPS series           |
| PKR3      | Compressed archive format                                         | Spider-Man PC                     |
| PRE       | Simple flat archive format (plain + compressed PRE3/PRX)          | THPS1 PS1, THPS2 PS1/DC, THPS3+   |
| DDX       | Xbox texture archives containing DDS files                        | THPS2X                            |
| BON       | Dreamcast v1 (PVR → PNG) and Xbox v3/v4 (raw DDS)                 | THPS2 DC, THPS2X                  |
| PAK       | Neversoft PAK archives (+ companion .pab data)                    | THUG2, THAW, Guitar Hero (PS2)    |

The **Game Unpacker** recursively extracts every archive under a directory in one pass — including nested archives (a WAD containing PREs, a PAK inside a WAD) — reproducing the game's on-disc directory tree.

### Audio

| Format | Description                                                     | Games                        |
| ------ | -------------------------------------------------------------- | ---------------------------- |
| XA     | PS1 ADPCM audio (sectored and raw) → WAV                       | All PS1 titles               |
| VAB    | PS1 sound bank (multi-sample) → WAV                            | All PS1 titles               |
| VAG    | PS2 SPU-ADPCM (headered + headerless) → WAV                    | THPS3+ PS2                    |
| PSS    | Headerless SPU-ADPCM → WAV                                     | Spider-Man PC, THPS3+ PS2     |
| ADX    | CRI Middleware audio → WAV                                     | THPS2 DC, Spider-Man DC       |
| KAT    | Dreamcast audio soundbank (ADPCM + PCM) → WAV                  | THPS2 DC, Spider-Man DC       |
| SFX    | Dreamcast cue banks (resolves companion KAT/VAB samples) → WAV | THPS2 DC, Spider-Man DC       |

The **Audio Converter** offers in-app playback with a seekable timeline for the whole file or individual bank samples.

### 3D Models & Animation

| Format          | Description                                                                | Games                        |
| --------------- | ------------------------------------------------------------------------- | ---------------------------- |
| PSX mesh        | PS1 level geometry (`_g.psx`) → glTF (.glb) with vertex colors            | THPS1/2, Spider-Man, Apoc.   |
| DDM             | Xbox level geometry → glTF with materials, textures, lights, PSX placement | THPS2X                      |
| RW DFF          | RenderWare 3.x skinned meshes (.SKN) → glTF with skeleton                  | THPS3 PS2                    |
| RW BSP          | RenderWare 3.x world/level geometry → glTF                                 | THPS3 PS2                    |
| COL             | Collision meshes (.col.xbx/.wpc/.ps2) → glTF                              | THUG, THUG2, THAW           |
| Xbox MDL / SKIN | Native Xbox/PC scene meshes → glTF                                         | THUG2, THAW                 |
| PS2 MDL / SKIN  | Native PS2 scene meshes (incl. `.iskin.ps2`) → glTF                       | THPS4, THUG, THUG2          |
| PS2 GEOM        | Pre-compiled CGeomNode render trees (`.geom.ps2`) → glTF                  | THPS4, THUG, THUG2          |
| THAW skin/zone  | Pre-compiled VIF/DMA skins + worldzone level PAKs → glTF                  | THAW PS2                    |
| SKE / SKA       | Cross-platform skeletons + bone animations → glTF animation tracks        | THPS4, THUG, THUG2          |
| PSX animation   | PS1 character skeletal animation → animated glTF (.glb / .gif)            | THPS1/2, Spider-Man, Apoc.   |

Mesh conversion writes glTF (.glb), and — where a Blender helper is configured — Blender (.blend) scenes. Skinned meshes export with joints, weights, and inverse-bind matrices; the **Character Preview** tab renders models and plays back animations in-app.

### Scripts & Levels

| Format | Description                                                                  | Games                     |
| ------ | --------------------------------------------------------------------------- | ------------------------- |
| TRG    | Level trigger/script files → JSON (spawns, camera paths, rails, bytecode)   | Apocalypse, Spider-Man, THPS |
| QB     | Compiled Neversoft game scripts (`.qb`) → decompiled `.q` source           | THPS3–THUG2               |

### Video

| Format | Description                                            | Games                        |
| ------ | ----------------------------------------------------- | ---------------------------- |
| SFD    | CRI Sofdec (MPEG-1 + ADX) → MP4 (requires ffmpeg)     | Dreamcast titles             |
| STR    | PS1 MDEC video streams → MP4 (pure C# decoder)        | Apocalypse, Spider-Man, THPS1/2 |
| VID1   | THAW GameCube movie container (MPEG-4) → MP4          | THAW GameCube                |

Both SFD and STR play back in-app in the **Video Converter** tab.

### Tested Games

- Apocalypse (PS1, 1998)
- Tony Hawk's Pro Skater (PS1, 1999)
- Spider-Man (PS1 / Dreamcast / PC, 2000–2001)
- Tony Hawk's Pro Skater 2 (PS1 / Dreamcast, 2000)
- Spider-Man 2: Enter Electro (PS1, 2001)
- Tony Hawk's Pro Skater 2X (Xbox, 2001)
- Tony Hawk's Pro Skater 3 (PS2, 2001)
- Tony Hawk's Pro Skater 4 (PS2, 2002)
- Tony Hawk's Underground (PS2, 2003)
- Tony Hawk's Underground 2 (PS2 / Xbox / Windows, 2004)
- Tony Hawk's American Wasteland (PS2 / GameCube / PC, 2005–2006)
- Tony Hawk's Project 8 (PS2, 2006)
- Tony Hawk's Proving Ground (PS2, 2007)

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Windows 10 version 1903+ (for GUI mode)
- CLI mode works on Windows, Linux, and macOS
- [ffmpeg](https://ffmpeg.org/) on `PATH` for SFD/VID1 video conversion

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

- **Textures** — PSX, PVR, PS2 TEX/IMG, RW TXD, Xbox TEX, and NGC textures → PNG
- **RLE / BMR** — Neversoft RLE bitmaps with auto width detection
- **Archive Extractor** — WAD, PKR, PRE, DDX, BON, PAK
- **Game Unpacker** — recursive extraction of every archive in a game directory
- **Audio Converter** — all audio formats with in-app playback
- **Video Converter** — SFD, STR, and VID1 with playback preview
- **Mesh Converter** — every mesh format above → glTF/Blender, with texture and skeleton binding
- **Character Preview** — model + animation preview
- **Hash Reviewer** — QBKey hash → name resolution review
- **Script Decompiler** — TRG triggers → JSON and QB scripts → `.q` source

### CLI Mode

```bash
# On Windows (from GUI build), use --no-gui or a subcommand to enter CLI mode
# On any platform (from CLI build):
dotnet run --project src/NeversoftMultitool -f net10.0 -- <command> [options]
```

Every command takes an input file or directory, an `-o/--output` directory, and `-v/--verbose`. Run any command with `--help` for its full options.

| Command    | Purpose                                                      |
| ---------- | ----------------------------------------------------------- |
| `psx`      | Extract textures from PS1 PSX model files                   |
| `pvr`      | Convert Dreamcast PVR textures → PNG                        |
| `ps2tex`   | Convert PS2 TEX/IMG and RW TXD textures → PNG               |
| `xbxtex`   | Convert Xbox/PC TEX/IMG textures → PNG                      |
| `ngctex`   | Convert GameCube texture dictionaries → PNG                 |
| `rle`      | Convert RLE/BMR/ZLB bitmaps → PNG                           |
| `archive`  | Extract a WAD/PKR/PRE/PRX/DDX/BON/PAK archive               |
| `unpack`   | Recursively extract every archive under a directory         |
| `audio`    | Convert ADX/XA/VAB/VAG/KAT/SFX/PSS/VID audio → WAV          |
| `sfd`      | Convert SFD (Sofdec) / PSS video → MP4                      |
| `str`      | Convert PS1 MDEC (STR) video → MP4                          |
| `vid`      | Convert THAW GameCube VID1 video → MP4                      |
| `mesh`     | Auto-detect and convert any supported mesh → glTF/Blender   |
| `ddm`      | Convert DDM level meshes → glTF (with PSX placement)        |
| `psx-mesh` | Convert PS1 PSX model files → glTF/Blender                  |
| `rwdff`    | Convert RenderWare DFF (.SKN) meshes → glTF/Blender         |
| `rwbsp`    | Convert RenderWare BSP levels → glTF/Blender                |
| `col`      | Convert collision (.col) meshes → glTF/Blender              |
| `ska`      | Export SKA skeletal animations → glTF                       |
| `psx-anim-export` | Export a PS1 character `.psx` as an animated `.glb`  |
| `trg`      | Parse TRG trigger/script files → JSON                      |
| `qb`       | Decompile compiled QB scripts → `.q` source                |
| `qbkey`    | QBKey hash utilities (cross-reference, import)             |
| `glb-render` / `glb-gif` | Render `.glb` files to PNG / animated GIF     |

#### Examples

```bash
# Extract every archive in a game directory (nested archives included)
NeversoftMultitool unpack "path/to/game" -v

# Convert a THAW PS2 character to glTF (textures + skeleton auto-discovered)
NeversoftMultitool mesh skater_muska.skin.ps2 -o out

# Convert a directory of audio files to WAV
NeversoftMultitool audio "path/to/sounds" -o out

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
      Psx/                 # PSX texture extraction + PS1 mesh geometry
      Rle/                 # RLE/BMR bitmap conversion
      Archives/            # WAD, PKR, PRE, DDX, BON, PAK extraction
      Audio/               # XA, VAB, VAG, ADX, KAT, SFX, PSS decoding
      Texture/             # PS2 TEX, RW TXD, Xbox TEX, NGC decoding
      Mesh/                # PSX/DDM/RW/COL/PS2/Xbox meshes → glTF
      Animation/           # SKA + PSX skeletal animation
      Trg/                 # TRG level trigger/script parsing
      Qb/                  # QB compiled-script decompilation
      Video/               # SFD, STR, VID1 video conversion
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
