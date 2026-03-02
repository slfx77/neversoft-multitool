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
- **RW DFF meshes**: RenderWare 3.x DFF (Clump) files for THPS3 PS2 .SKN files → glTF (.glb). Standard non-native geometry format (version 0x0310): frame hierarchy, per-geometry vertices/normals/UVs/vertex colors, material list with texture references. 331 files, 330 converted (145,200 triangles), 279 textured. 1 file (terrorist_a.SKN) has inter-chunk padding corruption but parses gracefully (0 triangles). Texture embedding via companion `.tex` TXD files with extension-stripping name match. Shared `RwChunkReader` utilities with RW TXD parser. CLI: `rwdff <input> [-o output] [-t] [--tex path] [-v]`. GUI: integrated in Mesh Converter tab.
- **RW BSP levels**: RenderWare 3.x World (BSP) files for THPS3 PS2 level geometry → glTF (.glb). Chunk type 0x000B, version 0x0310. BSP tree structure: World → MaterialList(0x08) + PlaneSection(0x0A) binary tree → AtomicSection(0x09) leaves. AtomicSection layout: header(12B: matBase+numTris+numVerts) + bbox(24B) + positions(N×12) + 8-byte gap + normals(N×4 packed i8 if NORMALS) + colors(N×4 RGBA if PRELIT) + UV0(N×8) + UV1(N×8 if TEXTURED2) + triangles(M×8: matId:u16, v0:u16, v1:u16, v2:u16). Format flags: 0xC9 common (tristrip+prelit+modulate+textured2), 0xD9 adds normals. 38 files, 37 converted (656,316 triangles), 1 empty (Ware_Test10.bsp). Shared MaterialList with per-section matListWindowBase offset. Texture embedding via companion `.tex` TXD files. Shared `RwChunkReader` utilities with RW DFF/TXD parsers. CLI: `rwbsp <input> [-o output] [-t] [--tex path] [-v]`. GUI: integrated in Mesh Converter tab.
- **COL collision meshes**: Neversoft collision files (.col.xbx, .col.wpc, .col.ps2) for THUG/THUG2/THAW → glTF (.glb). Binary format (versions 9-10): file header (32B) + per-object headers (64B each) + vertex data (fixed-point 6B or float 12B) + intensity data (1B per vert) + face data (small 8B or large 10B). Fixed-point vertices: 3×u16 (×0.0625 + bbox_min). Faces: small (flags:u16 + terrain:u16 + 3×u8 indices + pad) or large (flags:u16 + terrain:u16 + 3×u16 indices). Intensity values mapped to grayscale vertex colors. 957 files in THUG2 Xbox, 942 converted (15 empty, 0 objects), 1,343,749 total triangles, 0 glTF errors. Reference: io_thps_scene import_thug2.py (denetii/io_thps_scene). CLI: `col <input> [-o output] [-v]`. GUI: integrated in Mesh Converter tab.
- **Cross-platform skeleton (.ske)**: Neversoft cross-platform skeleton format (THPS4/THUG/THUG2). Two sub-formats: THPS4 (checksum + numBones + 3 name tables, no neutral poses, size = 8 + N×12) and THUG/THUG2 (checksum + version + flags + numBones + names + neutral poses, size = 16 + N×44). 154 files across 3 games (34 THPS4, 62 THUG, 58 THUG2). Auto-discovered for .skin.ps2 files via CompanionSearch. THPS4 .ske files are the ONLY skeleton format available (no .ske.ps2), enabling bone hierarchy export for all 34 THPS4 skinned meshes. THUG/THUG2 .ske files provide cross-platform alternative to .ske.ps2.
- **PS2 MDL/SKIN meshes**: Native PS2 scene files (.mdl.ps2, .skin.ps2, .iskin.ps2) → glTF (.glb) with vertex colors, normals, UV coordinates, and material references. Version triples: THPS4 (3,4,1), THUG (5,6,1), THUG2 (6,6,1). 2,299 standard files across 3 games (574 THPS4, 812 THUG, 913 THUG2). THPS4 uses full float-precision vertices (40B non-skinned, 64B skinned). THUG/THUG2 non-skinned uses floats with packed sint16 normals (32B); skinned uses packed sint16 for positions/UVs/normals (28B). Triangle strips encoded via ADC bits per-vertex (0x8000 = strip restart). Packed normals: 2×sint16, nz=sqrt(1-nx²-ny²), Z sign from LSB of nx. Skinned position scale: SUB_INCH_PRECISION=16.0 (÷16). Skinned UV scale: ÷4096. Materials are single-pass (not multi-pass like cross-platform format). VC wibble has phase field in ALL versions (THUG source bug omits it). THUG2 shader_id is always 4 bytes (no conditional specular color). 739 THUG2 .skin.ps2 files with version (1,N,M) are pre-compiled VIF/DMA rendering chains (bones pre-baked, lighting pre-computed, UVs confirmed via numerical cross-reference); detected and gracefully skipped since matching .iskin.ps2 files provide higher-quality data. Reference: THUG source (scene.cpp/mesh.cpp/material.cpp), validated via `tools/ps2_scene_trace.py`.
- **PS2 GEOM meshes**: Pre-compiled CGeomNode rendering trees (.geom.ps2) → glTF (.glb). VIF opcode parser extracts vertex data from embedded DMA chains. 1,800 files across 3 games (603 THPS4, 933 THUG, 264 THUG2), 1,798 converted (2 empty fireball.geom.ps2 particle placeholders). Texture embedding: THUG/THUG2 use CGeomNode.texture_checksum at +0x44 directly (932/933 THUG, 263/264 THUG2 textured). **THPS4 texture resolution via VRAM simulation**: CGeomNode.texture_checksum=0 for ALL THPS4 leaves; textures identified by GS TEX0_1 register values in DMA chain VU1 input (UNPACK V4_32 GIF tag + UNPACK V3_32 register writes). TEX0 contains TBP (VRAM texture base pointer) and CBP (CLUT base pointer). `Ps2VramAllocator` simulates PS2 GS VRAM allocation from TEX files (replicating texture.cpp LoadTextureGroup) to build (TBP,CBP)→checksum mapping. 602/603 THPS4 files textured (7,931 textures embedded, 4,727 VRAM mapping entries). Validated via `tools/ps2_geom_trace.py`, `tools/ps2_geom_gscontext.py`, `tools/ps2_vram_sim.py`.
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

## glTF Validation

After converting meshes to .glb, **always validate the output** using the bundled glTF validator:

```bash
# Validate a single file
tools/gltf_validator_bin/gltf_validator.exe <file.glb>

# Validate a directory (recursive)
tools/gltf_validator_bin/gltf_validator.exe <directory>

# JSON report to stdout (parse with Python for material/texture/mesh counts)
tools/gltf_validator_bin/gltf_validator.exe -o <file.glb>
```

Key things to check: 0 errors, 0 warnings. `UNUSED_OBJECT` infos on `TEXCOORD_0` indicate meshes with UV coordinates but no texture assigned to the material — expected for untextured geometry, but a bug if textures should be present.

For quick programmatic checks, parse the GLB JSON directly (first chunk after 12-byte header):
```python
import struct, json
with open('file.glb', 'rb') as f:
    f.read(12)  # skip magic/version/length
    clen, _ = struct.unpack('<II', f.read(8))
    d = json.loads(f.read(clen).decode().rstrip('\x00'))
    print(f"Materials: {len(d.get('materials',[]))}, Textures: {len(d.get('textures',[]))}")
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
│       ├── Ps2Scene/        # PS2 MDL/SKIN mesh extraction → glTF
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

- `Gfx/NGPS/NX/texture.cpp` — PS2 TEX/IMG binary format: version-tagged groups, GS pixel modes (PSMCT32/24/16, PSMT8/4), CLUT CSM1 swizzle. VRAM allocation: LoadTextureGroup() lines 377-691 (sequential TBP with 8KB alignment, CBP from end, cache optimization +16 for odd small textures)
- `Gfx/NGPS/NX/nx_init.cpp` — VramBufferBase=0x2BC0 (line 230), VramGroupSize=0x0A20, VramToggle=0x1E20
- `Gfx/NGPS/NX/scene.cpp` — PS2 scene loading: material/mesh/vert version triplet, material import, mesh groups
- `Gfx/NGPS/NX/mesh.cpp` — PS2 mesh vertex data: VU1 DMA list packing, UV/color/normal formats
- `Gfx/NGPS/NX/mesh.h` — sMesh struct, mesh flags (TEXTURE, COLOURS, NORMALS, ST16, SKINNED, etc.)
- `Gfx/NGPS/NX/material.h` — sMaterial struct, material flags
- `Gfx/NGPS/NX/render.h` — SUB_INCH_PRECISION=16.0f (vertex position scale for sint16→float conversion)
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
  - **GHIDRA decompilation pipeline** (`tools/ghidra/thps2-psx-proto/`): Decompiles PSX functions from THPS2 PSX Prototype (SLUS_900.86 + MAIN.SYM, 3,566 PSY-Q symbols). `ImportSymHeadless.java` (pre-script: reads SYM directly, imports 8,652 struct definitions, 2,759 typedefs, 3,568 typed globals, 1,611 function signatures; loads PSY-Q 4.60 SDK GDT; sets GP=0x800D7CA0), `DecompilePsxFunctions.java` (post-script: decompiles 41 targeted functions by address), `run_decompile.sh` (driver). Output: `output/psx_decompiled.c` (4,305 lines, 41 functions with typed struct access). Parallel TRG pipeline in `tools/ghidra/thps2-trg-proto/`. Available binaries: 14 PS1 executables (Apocalypse through SM2:EE), 2 Dreamcast, 1 PC — only THPS2 protos (Mar/Jun 2000) have SYM files; others require signature-based function matching. `tools/ghidra/thps2-trg-proto/output/symbols.txt` contains all 3,566 symbol names with addresses.
  - **GHIDRA headless string extraction**: `tools/ghidra/ExtractStrings.java` is a GHIDRA Java postScript that extracts analyzed strings from game executables. Performs proper disassembly and data-flow analysis via three tiers: defined strings, symbol names, and data section scanning. Run via `tools/ghidra/run_extraction.sh [builds_path] [output_dir]` which processes 16 executables across PS1 (MIPS), PC (x86), Dreamcast (SH-2 fallback), and Xbox (x86). Output feeds into `qbkey import <combined> --psx-dir <path>`. Requires GHIDRA 11+ and Java 17+; set `GHIDRA_HOME` env var (default: `C:/tools/ghidra_12.0.2_PUBLIC`). Extracted 25,462 candidate names from 15 executables; 0 texture matches (names don't correspond to original PS1 texture naming convention).
- **Audio preview**: In-tab playback controls in Audio Converter tab — play/pause/stop, seekable slider, position display, temp file caching. Converts selected file/sample to WAV via Windows MediaPlayer API. Supports ADX/XA (whole file) and VAB/KAT (individual samples).

### Not Yet Implemented

- **Unpack Tab (GUI)**: Recursive extraction tab for game root directories. Pipeline: extract disc image → extract WAD/HED archives → extract PRE archives → ready for conversion. PS2 games (THPS4/THUG/THUG2) store assets inside WAD archives on disc (e.g. SKATE4.WAD). The extracted WAD layout uses semantic directories: `Models/` (per-entity bundles with .skin.ps2 + .tex.ps2 + .geom.ps2 together), `Skeletons/` (flat), `Levels/`, `Textures/`, `pre/` (compressed PRE archives with additional assets). `SampleGenerator --extract-only` can extract PS2 ISOs + WADs to Research/Builds for testing. `CompanionSearch` handles both this layout and the extension-sorted Sample layout.
- **THAW (.skin.ps2 meshes)**: THAW uses a different .skin.ps2 binary format than THUG2. Version triples read as garbage values (e.g., 65536,2496,1792), indicating structural incompatibility with Ps2SceneFile. THAW .tex.ps2 = QB script/dialogue data (version 256, not textures). THAW .pak.ps2 (1,732 files) = opaque level bundles (not traditional archives). No .geom.ps2 files exist. Current support: WAD/HED extraction only (~30% coverage). Requires Ghidra decompilation of THAW executable to identify the binary format.
- **RW DFF skinning**: Skin PLG (0x0116) present in THPS3 .SKN geometry Extensions but currently skipped. Initial output is rigid mesh only — skinned characters render as static poses without bone deformation. Frame hierarchy is preserved.

### Research & Improvements

- **PowerVR format improvements**: DDS output with mip level preservation is implemented for formats 0x200 (twiddled+mip) and 0x400 (VQ+mip). Mip atlas PNG (`_mips.png`) output renders all mip levels into a single image for visual verification. GIMP PVR research: `Sample/gimp/plug-ins/common/file-pvr.c` supports additional format types (Small VQ, CLUT4/8, stride, etc.) but none of these appear in any Neversoft game files — scan of 20,726 textures across 1,041 files found zero unsupported format codes. Current six format types (0x100, 0x200, 0x300, 0x400, 0x900, 0xD00) provide complete coverage.
- **PSX mesh conversion**: Parser (`Core/Formats/Psx/PsxMeshFile.cs`), glTF writer (`PsxGltfWriter.cs`), CLI (`CLI/PsxMeshCommand.cs`), and GUI tab (`App/Tabs/MeshConverterTab.xaml`) exist. Level geometry (`_g.psx`) works; **character models produce wrong geometry** (garbled/misaligned body parts). No community tool implements a catchall across all format variants.
- **THPS2 PSX matching decompilation** (`tools/ghidra/thps2-psx-proto/`): **Full matching decomp project** — goal is 1:1 binary reproduction, not just a format reference. 682/1455 perfect matches across 67 TUs. Compiler: GCC 2.91.66-psx (EGCS 1.1.2), flags: `-O2 -fno-strength-reduce -G8 -msoft-float -mno-abicalls`. Build/match scripts: `wmatch.sh`, `match_combined.sh`, `match_all.sh`. See `MATCHING_REFERENCE.md` for per-function ceiling analysis, C-source techniques, and per-TU completion status. Ghidra reference decompilation: 41 functions with full type-aware output (`output/psx_decompiled.c`).
