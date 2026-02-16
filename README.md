# Neversoft Multitool

.NET 10.0 tool for extracting and converting assets from Neversoft Entertainment game files (1998-2001 era). Features a WinUI 3 GUI on Windows and a cross-platform CLI.

## Supported Formats

| Format | Description | Status |
|--------|------------|--------|
| PSX (PS1) | 4-bit and 8-bit paletted textures | Supported |
| PSX (Xbox/DC) | 16-bit PowerVR textures (twiddled, VQ, rectangle) | Supported |
| RLE / BMR | Neversoft's custom RLE-compressed bitmaps | Supported |
| WAD + HED | Paired archive/index format (Apocalypse, THPS) | Supported |
| PKR3 | Compressed archive format (Spider-Man PC) | Supported |
| PRE | Archive format (THPS series) | Planned |

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

The GUI provides tabs for each format type with drag-and-drop file selection and batch processing.

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

#### Convert RLE/BMR Bitmaps

```bash
NeversoftMultitool rle <directory> [-o output] [-w 512] [-v]
```

- `<directory>` - Path to directory containing .rle/.bmr files
- `-o, --output` - Output directory (default: `output`)
- `-w, --width` - Image width in pixels (default: 512)
- `-v, --verbose` - Show per-file details

#### Extract Archives

```bash
NeversoftMultitool archive <file> [-o output] [-v]
```

- `<file>` - Path to archive file (.wad, .pkr, or .pre)
- `-o, --output` - Output directory (default: `output`)
- `-v, --verbose` - Show per-file extraction progress

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
      Psx/                 # PSX texture extraction + decoding
      Rle/                 # RLE/BMR bitmap conversion
      Archives/            # WAD, PKR, PRE extraction
  CLI/                     # Command-line interface
  App/                     # WinUI 3 GUI (Windows only)
```
