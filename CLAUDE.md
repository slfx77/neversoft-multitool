# Neversoft Multitool - AI Assistant Instructions

## Project Overview

.NET 10.0 application for extracting and converting assets from Neversoft game files (PS1, Dreamcast, Xbox, PC). Features WinUI 3 GUI (Windows) and cross-platform CLI. Ported from Python/PyQt6.

Supported formats:

- **PSX files**: Multi-purpose format containing textures + optional 3D mesh geometry. File naming conventions: `*_g.psx` = level geometry, `*_l.psx` = texture library (paired with `_g`), `*_o.psx` = level objects, no suffix = character/creature/item model. Textures: 4-bit/8-bit paletted (PS1), 16-bit PowerVR (twiddled, VQ, rectangle) — PS1, Dreamcast, Xbox. Meshes: → glTF (.glb) with vertex colors, normals, and texture references. **Format versions** (0x03, 0x04, 0x06) are unreliable — prototype builds may share a version ID but differ structurally. Engine timeline: Apocalypse (1998) → THPS1 (1999) → Spider-Man (2000) → THPS2 (2000) → SM2:EE (2001)
- **PVR textures**: Standalone Dreamcast GBIX+PVRT textures (ARGB1555, RGB565, ARGB4444; twiddled, VQ, rectangle)
- **RLE/BMR bitmaps**: Neversoft's custom RLE compression — RGBA5551 (PS1) and BMP-wrapped 24-bit RGB (Dreamcast)
- **WAD+HED archives**: Paired archive/index format used in Apocalypse, THPS series
- **PKR3 archives**: Compressed archive format used in Spider-Man PC
- **PRE archives**: Simple flat archive format used in THPS1 (PS1), THPS2 (PS1, Dreamcast)
- **DDX archives**: Xbox texture archives containing DDS files (THPS2X)
- **BON archives**: Dreamcast v1 (PVR textures → PNG) and Xbox v3/v4 (raw DDS extraction)
- **PS2 TEX/IMG textures**: Neversoft PS2 texture format (THPS4/THUG/THUG2). Version-tagged groups, GS pixel modes (PSMCT32/16, PSMT8/4), CLUT CSM1 swizzle, Conv4to32/Conv4to16 un-swizzle. 47,154 textures across 2,485 files → PNG.
- **RW TXD textures**: RenderWare 3.x Texture Dictionary files (THPS3 PS2). Chunk-based container (version 0x0310), PS2-native rasters with linear pixel data (version 0). Formats: PSMT4, PSMT8, PSMCT32, PSMCT16S. 4,547 textures across 390 files → PNG. Routed automatically via `Ps2TexFile.Parse()` when first u32 = 0x0016.
- **Audio**: XA (PS1 ADPCM), VAB (PS1 sound banks), VAG (PS2 SPU-ADPCM, headered + headerless), PSS (headerless SPU-ADPCM, same as VAG), ADX (CRI Middleware), KAT (Dreamcast soundbanks) → WAV
- **DDM meshes**: Xbox 3D level geometry → glTF (.glb) with materials, vertex colors, texture references, and .lit lights. DDM vertices are in local/object space. Level assembly uses PSX layout files for world-space placement: each PSX object entry provides a 20.12 fixed-point position (raw/4096 → float), matched to DDM objects via CRC-32 hash lookup. Coordinate mapping confirmed via THPS2X Xbox decompilation: `world_pos = psx_int32 / 4096`, no axis negation; glTF output uses `(-X, -Y, +Z)` matching vertex conversion. Level DDMs produce three .glb files: `{name}_level.glb`, `{name}_objects.glb`, `{name}.glb` (combined). Standalone DDMs (no PSX companion) produce one .glb file.
- **TRG triggers**: Level trigger/script files → JSON — spawn points, camera paths, rail networks, enemy spawns, command lists, bytecode scripts. Versions 2.0 (Apocalypse/THPS) and 2.1 (Spider-Man)
- **SFD video**: CRI Sofdec video (Dreamcast) → MP4 via ffmpeg. MPEG-PS container with MPEG-1 video + ADX audio. Requires ffmpeg on PATH.
- **STR video**: PS1 MDEC video streams → MP4 via pure C# decoder + ffmpeg encoding. 2336-byte CD-ROM sectors with interleaved XA-ADPCM audio. Variable resolutions (320×240, 320×192, 96×64). Present in Apocalypse, Spider-Man, THPS1/2 (PS1). Audio extracted via XaDecoder, raw RGB24 frames piped to ffmpeg stdin.
- **Video tab** (GUI): Unified Video Conversion tab handles both SFD and STR files with full playback preview via MediaPlayerElement.

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
│       ├── Psx/             # PSX texture extraction + mesh geometry → glTF
│       ├── Rle/             # RLE/BMR bitmap conversion
│       ├── Audio/           # ADX, XA, VAB, VAG, KAT audio decoding
│       ├── Mesh/            # DDM mesh extraction → glTF
│       ├── Trg/             # TRG level trigger/script parsing → JSON
│       ├── Video/           # SFD (Sofdec) + STR (PS1 MDEC) video conversion
│       └── Archives/        # WAD, PKR, PRE, DDX, BON extraction
├── CLI/                     # Command-line interface
├── App/                     # WinUI 3 GUI (Windows only)
│   ├── MainWindow.xaml      # NavigationView with tabs
│   └── Tabs/                # PsxTextureTab, RleBitmapTab, ArchiveExtractorTab, AudioConverterTab, SfdConverterTab, HashReviewerTab
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

## Analysis & Diagnostic Scripts

When writing analysis or diagnostic scripts (Python, shell, etc.), **always create them as reusable files in the `tools/` directory** rather than running inline code via the console. This ensures scripts are preserved, versioned, and can be re-run later. Use descriptive names and include usage comments at the top of each script.

## Sample Data

`Sample/Builds/` contains 14 game builds organized by `Game (Date, Console)` with files sorted into format subdirectories (PSX/, WAD/, RLE/, PKR/, etc.). Conversion artifacts (.png, .bmp, .dds, .wav, .mp4) have been cleaned.

## THUG Source Code Reference

`Sample/thug/Code/` contains the THUG (Tony Hawk's Underground) source code, which provides exact binary format specifications for THPS4+ era PS2 formats:

- `Gfx/NGPS/NX/texture.cpp` — PS2 TEX/IMG binary format: version-tagged groups, GS pixel modes (PSMCT32/24/16, PSMT8/4), CLUT CSM1 swizzle
- `Gfx/NGPS/NX/scene.cpp` — PS2 scene loading: material/mesh/vert version triplet, material import, mesh groups
- `Gfx/NGPS/NX/mesh.cpp` — PS2 mesh vertex data: VU1 DMA list packing, UV/color/normal formats
- `Gfx/NGPS/NX/mesh.h` — sMesh struct, mesh flags (TEXTURE, COLOURS, NORMALS, ST16, SKINNED, etc.)
- `Gfx/NGPS/NX/material.h` — sMaterial struct, material flags
- `Gfx/BonedAnim.cpp` — SKA animation format: header, compression flags, Q48 table lookup
- `Gfx/Skeleton.cpp` — SKE bone hierarchy, neutral pose, inverse matrices
- `Gel/Music/Ngps/Pcm/pcm.h` — VAG header struct (VAGp magic, version, dataSize, sampleFreq, name)
- `Sys/File/PRE.h` — PRE archive format (used in THPS3+ for bundled file loading)

## Deferred Items

### Not Game Formats / Not Archives

- **SCC**: Microsoft Visual SourceSafe `vssver.scc` version tracking files. Development artifacts accidentally shipped on disc. No game data.
- **BIN**: Generic file extension used for PS1 MIPS code overlays (game logic, menu screens), THPS2 data tables (cretex.bin, tricks.bin), and Dreamcast bootstrap executables. ~89% compiled machine code. Not suitable for asset extraction. See [spidey-decomp](https://github.com/krystalgamer/spidey-decomp) for Spider-Man module decompilation.
- **PAK (THAW)**: `.pak.ps2` files are opaque level data bundles, NOT traditional archives. No entry table, no magic number, contain embedded text config data mixed with binary blobs. No PAK parser needed. Note: nxtools `pak.py` (GHPak) is Guitar Hero-specific, not applicable to THPS/THAW.
- **PRK**: Custom park save data (8-20KB files named `custom1-20.prk`). Not archives.
- **THAW .tex.ps2**: Despite the `.tex` extension, these are QB script/dialogue data files, NOT texture files. Contain ASCII text (script commands, dialogue strings) interspersed with binary headers.

### In Progress (Not Yet Bug-Free)

- **TRG triggers**: Parser (`Core/Formats/Trg/`), CLI command (`CLI/TrgCommand.cs`), GUI viewer tab (`App/Tabs/TrgViewerTab.xaml`), and tests exist. Versions 2.0 (Apocalypse/THPS) and 2.1 (Spider-Man).
- **QBKey name resolution**: `QbKeyNames.txt` embedded resource contains 17,650 hash→name mappings loaded at startup into `FrozenDictionary` (via partial class `QbKey.KnownNames.cs`). Format: `name=0xHASH` per line. Discovered via `qbkey cross-ref` matching DDM plaintext names against PSX QBKey hashes across 104 THPS2X Xbox file pairs, plus archive filename scanning. Coverage: **mesh hashes 81.9% (19,336/23,611)**. Texture identifiers are build-tool-assigned IDs (not name hashes), so name resolution does not apply to them.
  - **Hash algorithm**: PS1/DC/Xbox-era Neversoft games use **case-sensitive** reflected CRC-32 (polynomial 0xEDB88320, init 0xFFFFFFFF, no final XOR, **no lowercasing**). This was confirmed by verifying DDM checksum fields match case-sensitive CRC-32 of mixed-case mesh names (e.g. `Itm_Bonus01`). `QbKey.Hash()` is case-sensitive; `QbKey.HashLower()` provides the THUG+ era lowercase variant. The `io_thps_scene` Blender addon documents the same split: `crc32b_from_string()` (no lowercase) for THPS1/2 vs `crc_from_string()` (lowercase) for THUG+.
  - **Texture identifiers (NOT name hashes)**: The PSX "texture name" array stores opaque identifiers used as keys in `TextureChecksumHashTable` (a 512-bucket hash table, decompiled from THPS2 proto). `ProcessNewPSX` reads each identifier, looks it up via `Spool_FindTextureEntry__FUi`, then **replaces the value in-place with a pointer** to the texture entry struct. Values include small numbers like `0x0000001E` (30) that cannot be CRC-32 of any string, ruling out name hashing. Same identifier appears across files with different texture dimensions (e.g. `0x3FB86DC4`: hawk2.PSX 32x64 vs hawk2b.PSX 32x32), proving they're not pixel-data checksums either. The `TexId` field in the per-texture header is actually a **palette hash** (passed to `Pal_FindPaletteEntry__FUi`), not a texture name. These are build-tool-assigned identifiers; name resolution is not applicable. See `tools/ghidra/thps2-psx-proto/output/psx_decompiled.c` for full decompilation.
  - `qbkey cross-ref <ddm-dir> <psx-dir>` — cross-reference DDM vs PSX hashes. `--export path.txt` merges discovered + existing mappings. `--scan-archives <builds-path>` scans WAD/PRE/BON/PKR/DDX archive filenames and hashes them against PSX pools.
  - `qbkey import <names-file> [--psx-dir <path>]` — import candidate names from external sources (e.g. C pipeline output), hash with QBKey, check against PSX hashes, optionally export merged dictionary.
  - **Texture hash diagnostic**: `tools/texture_diagnostic.py` — Python diagnostic script for analyzing PSX texture/mesh hash populations and testing alternative hash algorithms. Modes: default (file pair comparison), `--all` (batch all 104 pairs), `--analyze-hashes` (hash distribution analysis), `--real-hashes` (float vs real hash separation), `--test-algorithms` (test 4 CRC variants against all candidate names), `--dump-ghidra` (inspect GHIDRA extraction results).
  - External C pipeline tool in `tools/qbkey_pipeline/` provides `collect-names` (BON/DDX/DDM/PRE/PKR + executable string scraping), `match`, `brute`, and `brute-gpu` subcommands. Compile: `clang -O3 -D_CRT_SECURE_NO_WARNINGS -o qbkey_pipeline.exe qbkey_pipeline.c`. Add `-fopenmp` for multithreaded CPU brute-force. Add `-DHAS_OPENCL -DCL_TARGET_OPENCL_VERSION=120 -I"<CUDA>/include" -L"<CUDA>/lib/x64" -lOpenCL` for GPU brute-force.
  - **GHIDRA decompilation pipeline** (`tools/ghidra/thps2-psx-proto/`): Decompiles PSX model/texture functions from THPS2 PSX Prototype (SLUS_900.86 + MAIN.SYM, 3,566 PSY-Q symbols). `ApplyPsyqSymbols.java` (pre-script: applies symbols, sets GP=0x800D7CA0), `DecompilePsxFunctions.java` (post-script: decompiles targeted functions by address), `run_decompile.sh` (driver). Output: `output/psx_decompiled.c`. Parallel TRG pipeline in `tools/ghidra/thps2-trg-proto/`. Available binaries: 14 PS1 executables (Apocalypse through SM2:EE), 2 Dreamcast, 1 PC — only THPS2 protos (Mar/Jun 2000) have SYM files; others require signature-based function matching. `tools/ghidra/thps2-trg-proto/output/symbols.txt` contains all 3,566 symbol names with addresses.
  - **GHIDRA headless string extraction**: `tools/ghidra/ExtractStrings.java` is a GHIDRA Java postScript that extracts analyzed strings from game executables. Performs proper disassembly and data-flow analysis via three tiers: defined strings, symbol names, and data section scanning. Run via `tools/ghidra/run_extraction.sh [builds_path] [output_dir]` which processes 16 executables across PS1 (MIPS), PC (x86), Dreamcast (SH-2 fallback), and Xbox (x86). Output feeds into `qbkey import <combined> --psx-dir <path>`. Requires GHIDRA 11+ and Java 17+; set `GHIDRA_HOME` env var (default: `C:/tools/ghidra_12.0.2_PUBLIC`). Extracted 25,462 candidate names from 15 executables; 0 texture matches (names don't correspond to original PS1 texture naming convention).
- **Audio preview**: In-tab playback controls in Audio Converter tab — play/pause/stop, seekable slider, position display, temp file caching. Converts selected file/sample to WAV via Windows MediaPlayer API. Supports ADX/XA (whole file) and VAB/KAT (individual samples).

### Research & Improvements

- **PowerVR format improvements**: DDS output with mip level preservation is implemented for formats 0x200 (twiddled+mip) and 0x400 (VQ+mip). Mip atlas PNG (`_mips.png`) output renders all mip levels into a single image for visual verification. GIMP PVR research: `Sample/gimp/plug-ins/common/file-pvr.c` supports additional format types (Small VQ, CLUT4/8, stride, etc.) but none of these appear in any Neversoft game files — scan of 20,726 textures across 1,041 files found zero unsupported format codes. Current six format types (0x100, 0x200, 0x300, 0x400, 0x900, 0xD00) provide complete coverage.
- **PSX mesh conversion**: Parser (`Core/Formats/Psx/PsxMeshFile.cs`), glTF writer (`PsxGltfWriter.cs`), CLI (`CLI/PsxMeshCommand.cs`), and GUI tab (`App/Tabs/MeshConverterTab.xaml`) exist. Level geometry (`_g.psx`) works; **character models produce wrong geometry** (garbled/misaligned body parts). No community tool implements a catchall across all format variants — Ghidra decompilation of game binaries is the sole authoritative reference. Current decompilation: 25 functions from THPS2 PSX Prototype (2000-03-29) with MAIN.SYM symbols (`tools/ghidra/thps2-psx-proto/output/psx_decompiled.c`). Key functions: `M3dInit_ParsePSX__Fi` (runtime mesh processing), `ProcessNewPSX__Fi` (file loading pipeline), `CreatePSX__14LevelGeneratorPib` (format construction). Vertex type semantics need verification against decompilation — psxprev interpretations (type 1=joint anchor, type 2=stitched) may not match Neversoft's actual behavior (`M3dInit_ParsePSX` shows type 2 as sprite-scale, not stitching). PSY-Q C++ mangled symbol names encode parameter types (e.g. `__FP6CSuper` = takes `CSuper*`; key types: `CSuper`=character class, `SVECTOR`=vertex struct, `SModel`=model struct, `Item`=renderable object). Mesh name hashes resolved via `QbKey.Hash()` (case-sensitive CRC-32, 81.9% coverage via QbKeyNames.txt).
