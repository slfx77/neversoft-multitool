# Neversoft Multitool

.NET 10.0 tool for extracting and converting assets from Neversoft Entertainment game files (1998-2001 era). Features a WinUI 3 GUI on Windows and a cross-platform CLI.

## Supported Formats

### Textures

| Format        | Description                                                                | Games                           |
| ------------- | -------------------------------------------------------------------------- | ------------------------------- |
| PSX (PS1)     | 4-bit and 8-bit paletted textures → PNG                                    | All PS1 titles                  |
| PSX (Xbox/DC) | 16-bit PowerVR textures (twiddled, VQ, rectangle) → PNG/DDS                | THPS2X, Spider-Man DC, THPS2 DC |
| PVR           | Standalone Dreamcast GBIX+PVRT textures (ARGB1555, RGB565, ARGB4444) → PNG | THPS2 DC, Spider-Man DC         |
| RLE / BMR     | Neversoft's custom RLE-compressed bitmaps → PNG/BMP                        | All titles                      |

### Archives

| Format    | Description                                       | Games                   |
| --------- | ------------------------------------------------- | ----------------------- |
| WAD + HED | Paired archive/index format                       | Apocalypse, THPS series |
| PKR3      | Compressed archive format                         | Spider-Man PC           |
| PRE       | Simple flat archive format                        | THPS1 PS1, THPS2 PS1/DC |
| DDX       | Xbox texture archives containing DDS files        | THPS2X                  |
| BON       | Dreamcast v1 (PVR → PNG) and Xbox v3/v4 (raw DDS) | THPS2 DC, THPS2X        |

### Audio

| Format | Description                                   | Games                   |
| ------ | --------------------------------------------- | ----------------------- |
| XA     | PS1 ADPCM audio (sectored and raw) → WAV      | All PS1 titles          |
| VAB    | PS1 sound bank (multi-sample) → WAV           | All PS1 titles          |
| ADX    | CRI Middleware audio → WAV                    | THPS2 DC, Spider-Man DC |
| KAT    | Dreamcast audio soundbank (ADPCM + PCM) → WAV | THPS2 DC, Spider-Man DC |

### 3D Models

| Format | Description                                                                         | Games  |
| ------ | ----------------------------------------------------------------------------------- | ------ |
| DDM    | Xbox level geometry → glTF (.glb) with materials, vertex colors, texture references | THPS2X |

### Tested Games

- Apocalypse (PS1, 1998)
- Spider-Man (PS1/Dreamcast/PC, 2000-2001)
- Spider-Man 2: Enter Electro (PS1, 2001)
- Tony Hawk's Pro Skater (PS1, 1999)
- Tony Hawk's Pro Skater 2 (PS1/Dreamcast, 2000)
- Tony Hawk's Pro Skater 2X (Xbox, 2001)

## Requirements

- [.NET 10.0 SDK](https://dotnet.microsoft.com/download)
- Windows 10 version 1903+ (for GUI mode)
- CLI mode works on Windows, Linux, and macOS

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

The GUI provides tabs for PSX textures, RLE bitmaps, archives, audio conversion, and hash review with batch processing support.

### CLI Mode

```bash
# On Windows (from GUI build), use --no-gui or a subcommand to enter CLI mode
# On any platform (from CLI build):
dotnet run --project src/NeversoftMultitool -f net10.0 -- <command> [options]
```

#### Extract PSX Textures

```bash
NeversoftMultitool psx <directory> [-o output] [--subdirs] [-v]
```

- `<directory>` - Path to directory containing .psx files
- `-o, --output` - Output directory (default: `output`)
- `--subdirs` - Create subdirectories per .psx file
- `-v, --verbose` - Show per-file details

#### Convert PVR Textures

```bash
NeversoftMultitool pvr <input> [-o output] [-v]
```

- `<input>` - Path to a .pvr file or directory containing .pvr files
- `-o, --output` - Output directory (default: `TestOutput`)
- `-v, --verbose` - Show per-file details

#### Convert RLE/BMR Bitmaps

```bash
NeversoftMultitool rle <directory> [-o output] [-w width] [-v]
```

- `<directory>` - Path to directory containing .rle/.bmr files
- `-o, --output` - Output directory (default: `output`)
- `-w, --width` - Image width in pixels (default: auto-detect)
- `-v, --verbose` - Show per-file details

#### Extract Archives

```bash
NeversoftMultitool archive <file> [-o output] [-v]
```

- `<file>` - Path to archive file (.wad, .pkr, .pre, .ddx, or .bon)
- `-o, --output` - Output directory (default: `TestOutput`)
- `-v, --verbose` - Show per-file extraction progress

#### Convert DDM Meshes

```bash
NeversoftMultitool ddm <directory> [-o output] [-t textures] [-v]
```

- `<directory>` - Path to directory containing .ddm files
- `-o, --output` - Output directory (default: `TestOutput/DDM`)
- `-t, --textures` - Path to directory with extracted DDX textures (PNG) for material binding
- `-v, --verbose` - Show per-file details

Level DDMs are automatically paired with companion `_o` (object) DDMs and `.psx` files for world-space placement.

#### Convert Audio Files

```bash
NeversoftMultitool audio <directory> [-o output] [-r sample-rate] [-v]
```

- `<directory>` - Path to directory containing audio files (.adx, .xa, .vab, .kat)
- `-o, --output` - Output directory (default: `TestOutput`)
- `-r, --sample-rate` - Sample rate for VAB output (default: 11025)
- `-v, --verbose` - Show per-file details

## Architecture

The project uses multi-targeting to produce both a cross-platform CLI and a Windows GUI from a single codebase:

- **`net10.0`** - Cross-platform CLI using System.CommandLine + Spectre.Console
- **`net10.0-windows10.0.19041.0`** - WinUI 3 GUI with Mica backdrop

Shared format logic lives in `Core/` and is used by both targets. GUI code in `App/` is excluded from cross-platform builds via conditional compilation (`#if WINDOWS_GUI`).

```
src/NeversoftMultitool/
  Core/                    # Shared format logic
    BinaryIO/              # BinaryReader extensions, ImageWriter
    Formats/
      Psx/                 # PSX/PVR texture extraction + decoding
      Rle/                 # RLE/BMR bitmap conversion
      Archives/            # WAD, PKR, PRE, DDX, BON extraction
      Audio/               # ADX, XA, VAB, KAT audio decoding
      Mesh/                # DDM mesh extraction → glTF
  CLI/                     # Command-line interface
  App/                     # WinUI 3 GUI (Windows only)
```

## Code Guardrails

- C# files should stay under a soft 500-line limit. Existing exceptions are tracked in repo-policy tests and should be reduced over time instead of adding new ones.
- `partial class` usage should stay limited to UI XAML code-behind and cases where source generation requires it, such as `[GeneratedRegex]`.

## Acknowledgements

This project contains code derived from or informed by:

- [io_thps_scene](https://github.com/denetii/io_thps_scene) — Blender plugin for Tony Hawk's Pro Skater formats, used as reference for PSX model/texture parsing.
- [psx_extractor](https://github.com/krystalgamer/spidey-tools/tree/master/psx_extractor) — Spider-Man PC PSX extractor, used as reference for 16-bit texture decoding.
- [Rawtex](https://zenhax.com/viewtopic.php?t=7099) — Multipurpose raw texture converter, used as reference for PowerVR palette type handling.
- [RLE-GIMP-Plugin](https://github.com/Daniel-McCarthy/RLE-GIMP-Plugin) — GIMP plugin for Neversoft RLE/BMR files, used as reference for the PS1 RLE format.
- [jPSXdec](https://github.com/m35/jpsxdec) — PlayStation 1 media decoder/converter in Java, used as reference for XA ADPCM audio decoding.
- [KAT2WAV](https://github.com/DCxDemo/KAT2WAV) — Dreamcast KAT soundbank extractor, used as reference for KAT format understanding.
- [Hed-Extract](https://github.com/Daniel-McCarthy/Hed-Extract) — PSP Tony Hawk's Project 8 HED/WAD extractor/packer, used as format reference for HED archive extraction.
- [thps2-tools](https://github.com/JayFoxRox/thps2-tools) — THPS2 WAD/HED extraction script, used as reference for WAD archive format.
- [NxTools](https://gitgud.io/Fretworks/NxTools) — Blender plugin for Neversoft game assets, used as format reference for Xbox/THAW scene and texture parsing.
- [Queen-Bee](https://github.com/Nanook/Queen-Bee) — Guitar Hero/Tony Hawk PAK/QB editor, used as reference for PAK archive entry format.
- [librw](https://github.com/aap/librw) — RenderWare engine re-implementation, used as reference for RW TXD texture dictionary format.

### Previous Versions

This tool was originally two separate Python/PyQt5 tools:

- [Neversoft Bitmap Converter](https://github.com/slfx77/neversoft_bitmap_converter)
- [PSX Texture Extractor](https://github.com/slfx77/psx_texture_extractor)
