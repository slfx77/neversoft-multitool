# Neversoft Multitool - AI Assistant Instructions

## Project Overview

.NET 10.0 application for extracting and converting assets from Neversoft game files (PS1, Dreamcast, Xbox, PC). Features WinUI 3 GUI (Windows) and cross-platform CLI. Ported from Python/PyQt6.

Supported formats:

- **PSX textures**: 4-bit/8-bit paletted (PS1), 16-bit PowerVR (twiddled, VQ, rectangle) — PS1, Dreamcast, Xbox
- **PVR textures**: Standalone Dreamcast GBIX+PVRT textures (ARGB1555, RGB565, ARGB4444; twiddled, VQ, rectangle)
- **RLE/BMR bitmaps**: Neversoft's custom RLE compression — RGBA5551 (PS1) and BMP-wrapped 24-bit RGB (Dreamcast)
- **WAD+HED archives**: Paired archive/index format used in Apocalypse, THPS series
- **PKR3 archives**: Compressed archive format used in Spider-Man PC
- **PRE archives**: Simple flat archive format used in THPS1 (PS1), THPS2 (PS1, Dreamcast)
- **DDX archives**: Xbox texture archives containing DDS files (THPS2X)
- **BON archives**: Dreamcast v1 (PVR textures → PNG) and Xbox v3/v4 (raw DDS extraction)
- **Audio**: XA (PS1 ADPCM), VAB (PS1 sound banks), ADX (CRI Middleware), KAT (Dreamcast soundbanks) → WAV
- **DDM meshes**: Xbox 3D level geometry → glTF (.glb) with materials, vertex colors, and texture references (partial — individual meshes work, level layouts produce incorrect results)

## Build Commands

```bash
# Build GUI + CLI (multi-target)
dotnet build src/NeversoftMultitool/NeversoftMultitool.csproj

# Run tests (use exe directly; VSTest adapter has testhost issues with xunit.v3)
dotnet build tests/NeversoftMultitool.Tests/NeversoftMultitool.Tests.csproj
tests/NeversoftMultitool.Tests/bin/Debug/net10.0/NeversoftMultitool.Tests.exe
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
│       ├── Audio/           # ADX, XA, VAB, KAT audio decoding
│       ├── Mesh/            # DDM mesh extraction → glTF
│       └── Archives/        # WAD, PKR, PRE, DDX, BON extraction
├── CLI/                     # Command-line interface
├── App/                     # WinUI 3 GUI (Windows only)
│   ├── MainWindow.xaml      # NavigationView with tabs
│   └── Tabs/                # PsxTextureTab, RleBitmapTab, ArchiveExtractorTab, AudioConverterTab, HashReviewerTab
tests/
├── TestData/                # Game files for integration testing
├── NeversoftMultitool.Tests/
│   ├── GoldenFiles/         # Python-generated reference output
│   └── Core/Formats/        # Unit + integration tests
```

## Code Style

- File-scoped namespaces: `namespace Foo;`
- Private fields: `_camelCase`
- Nullable reference types: Enabled
- Primary constructors where appropriate
- SixLabors.ImageSharp for PNG output (Rgba32 for RGBA, Rgb24 for RGB)
- SharpGLTF.Toolkit for glTF 2.0 mesh output (MeshBuilder with VertexPositionNormal + VertexColor1Texture1)

## Sample Data

`Sample/Builds/` contains 14 game builds organized by `Game (Date, Console)` with files sorted into format subdirectories (PSX/, WAD/, RLE/, PKR/, etc.). Conversion artifacts (.png, .bmp, .dds, .wav, .mp4) have been cleaned.

## Deferred Items

### Format Support — Well-Understood (Documentable)

- **STR**: PS1 MDEC video streams — present in most PS1 builds
- **SFD**: Sofdec video (CRI Middleware) — Dreamcast only (35 in THPS2 DC, 28 in Spider-Man DC). Well-documented format.
- **TRG**: Level trigger/script files — `_TRG` magic, versions 2.0 (THPS/Apocalypse) / 2.1 (Spider-Man). 311 files across all platforms. Contains spawn points, camera paths, rail definitions, trick objects, goals, embedded bytecode scripts. Documented: [JayFoxRox/thps2-tools](https://github.com/JayFoxRox/thps2-tools), [Vadru93/THPS2X-Formats](https://github.com/Vadru93/THPS2X-Formats), [krystalgamer/spidey-tools](https://github.com/krystalgamer/spidey-tools).
- **BET**: Beat detection maps for THPS2X music — 18 Xbox-only files. Pre-computed rhythm events (timestamp + intensity + channel) for syncing visual effects to music. Trivially simple format (uint16 count + 6-byte records). No existing documentation.

### Not Game Formats

- **SCC**: Microsoft Visual SourceSafe `vssver.scc` version tracking files. Development artifacts accidentally shipped on disc. No game data.
- **BIN**: Generic file extension used for PS1 MIPS code overlays (game logic, menu screens), THPS2 data tables (cretex.bin, tricks.bin), and Dreamcast bootstrap executables. ~89% compiled machine code. Not suitable for asset extraction. See [spidey-decomp](https://github.com/krystalgamer/spidey-decomp) for Spider-Man module decompilation.

### Research & Improvements

- **DDM level layout**: DDM mesh converter produces correct individual meshes but incorrect results for level layouts (multi-mesh world-space assembly). Needs investigation.
- **QBKey pipeline tool**: `tools/qbkey_pipeline/` — unified C CLI for hash resolution. Resolves 4,004 of 69K QBKey hashes (5.8%) via dictionary matching + brute-force. Needs review and integration into main project workflow. Compile (basic): `clang -O3 -D_CRT_SECURE_NO_WARNINGS -o qbkey_pipeline.exe qbkey_pipeline.c`. Add `-fopenmp` for multithreaded CPU brute-force. Add `-DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120 -I"<CUDA>/include" -L"<CUDA>/lib/x64" -lOpenCL` for GPU brute-force. Subcommands: `collect-hashes`, `collect-names`, `match`, `brute` (CPU), `brute-gpu` (GPU/OpenCL), `filter`, `prefilter`, `candidates`.
- **PowerVR format improvements**: DDS output with mip level preservation is implemented for formats 0x200 (twiddled+mip) and 0x400 (VQ+mip). Remaining: texture atlas conversion, research GIMP's newly added PowerVR support
- **PSX OBJ mesh export**: Export 3D geometry from PSX model files as OBJ format. Mesh name hashes can be resolved using the confirmed reflected CRC-32 algorithm (polynomial 0xEDB88320, init 0xFFFFFFFF, no final XOR, lowercase input). Body part naming confirmed for Apocalypse; THPS naming convention still unknown.
