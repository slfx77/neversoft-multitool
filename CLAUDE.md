# Neversoft Multitool - AI Assistant Instructions

## Project Overview

.NET 10.0 application for extracting and converting assets from Neversoft game files (PS1, Dreamcast, Xbox, PC). Features WinUI 3 GUI (Windows) and cross-platform CLI. Ported from Python/PyQt6.

Supported formats:
- **PSX textures**: 4-bit/8-bit paletted (PS1), 16-bit PowerVR (twiddled, VQ, rectangle)
- **RLE/BMR bitmaps**: Neversoft's custom RLE compression with RGBA5551 colors
- **WAD+HED archives**: Paired archive/index format used in Apocalypse, THPS series
- **PKR3 archives**: Compressed archive format used in Spider-Man PC

## Build Commands

```bash
# Build GUI + CLI (multi-target)
dotnet build src/NeversoftMultitool/NeversoftMultitool.csproj

# Run tests (use exe directly; VSTest adapter has testhost issues with xunit.v3)
dotnet build tests/NeversoftMultitool.Tests/NeversoftMultitool.Tests.csproj
tests/NeversoftMultitool.Tests/bin/Debug/net10.0/NeversoftMultitool.Tests.exe

# Generate golden reference files (requires Python + pypng + pymorton)
cd neversoft_multitool && python generate_golden_files.py
```

## Architecture

### Multi-targeting

- `net10.0`: Cross-platform CLI (System.CommandLine + Spectre.Console)
- `net10.0-windows10.0.19041.0`: WinUI 3 GUI with Mica backdrop
- GUI code is in `App/` and excluded from cross-platform builds via `#if WINDOWS_GUI`

### Key Directories

```
src/NeversoftMultitool/
├── Core/                    # Shared format logic (both CLI + GUI)
│   ├── BinaryIO/            # BinaryReader extensions, ImageWriter
│   └── Formats/
│       ├── Psx/             # PSX texture extraction
│       ├── Rle/             # RLE/BMR bitmap conversion
│       └── Archives/        # WAD, PKR, PRE extraction
├── CLI/                     # Command-line interface
├── App/                     # WinUI 3 GUI (Windows only)
│   ├── MainWindow.xaml      # NavigationView with tabs
│   └── Tabs/                # PsxTextureTab, RleBitmapTab, ArchiveExtractorTab
tests/
├── TestData/                # Game files for integration testing
├── NeversoftMultitool.Tests/
│   ├── GoldenFiles/         # Python-generated reference output
│   └── Core/Formats/        # Unit + integration tests
neversoft_multitool/         # Original Python source (reference)
```

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Primary constructors where appropriate
- SixLabors.ImageSharp for PNG output (Rgba32 for RGBA, Rgb24 for RGB)

## Sample Data

`Sample/Builds/` contains 14 game builds organized by `Game (Date, Console)` with files sorted into format subdirectories (PSX/, WAD/, RLE/, PKR/, etc.). Conversion artifacts (.png, .bmp, .dds, .wav, .mp4) have been cleaned.

## Deferred Items

### Format Support — Currently Stubbed
- **PRE archive format**: Stub only (`PreArchive.cs` throws `NotSupportedException`)
- **PVR-T Xbox texture support**: `extract_textures` returns error for PVR-T textures (0xFFFFFFFF marker)
- **Dreamcast RLE format**: Different encoding than PS1 RLE
- **RLE width auto-detection**: Currently requires user to specify width (default 512)

### Format Support — Well-Understood (Documentable)
- **VAB**: PS1 sound bank format — present in most PS1 builds
- **XA**: PS1 ADPCM audio — present in most PS1 builds
- **STR**: PS1 MDEC video streams — present in most PS1 builds

### Format Support — Research Needed
- **TRG**: Scripts? Present in most builds across all platforms
- **ADX**: CRI Middleware audio (Dreamcast) — 25 files in THPS2 DC, 1 in Spider-Man DC
- **BET**: Unknown — Xbox only, 18 files in THPS2X
- **BON**: Unknown — Dreamcast (39 in THPS2 DC) + Xbox (64 in THPS2X)
- **DDM**: Unknown — Xbox only, 104 files in THPS2X
- **BIN**: Catch-all binary — present across many builds, hard to identify
- **KAT**: Unknown — Dreamcast only (58 in Spider-Man DC, 15 in THPS2 DC)
- **PVR (Dreamcast)**: Different header than Xbox PVR? Can't be opened in GIMP — 223 files in THPS2 DC
- **SCC**: Unknown — Dreamcast (6 in THPS2 DC) + Xbox (32 in THPS2X)
- **SFD**: Sofdec video (CRI Middleware) — Dreamcast only (35 in THPS2 DC, 28 in Spider-Man DC)

### Research & Improvements
- **THPS2 HED/WAD filename hashing**: Reverse the hash algorithm used for filenames in THPS2 HED/WAD files to properly extract filenames without hardcoding them like other publicly available extractors do
- **PowerVR format improvements**: Research GIMP's newly added PowerVR support to improve our own format handling. The current implementation was essentially brute-force reversed and things like texture atlas conversion and mip level preservation aren't properly handled. Consider DDS output as an alternative to PNG since DDS is more readily supported on Windows, while retaining PNG capability for documentation
