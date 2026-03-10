using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

/// <summary>
///     Parser for THAW PS2 .skin.ps2 files (pre-compiled VIF/DMA rendering chains).
///     Also handles THUG2 pre-compiled .skin.ps2 files that have no .iskin.ps2 companion.
///     Binary layout: 32B header + object table (8B×N) + entry table (64B×M) + gap chunks + DMA/VIF data.
///     FLUSH+DIRECT pairs mark material/setup changes inside the rendering chain, not true mesh starts.
///     The VIF stream before the first FLUSH+DIRECT pair can already contain vertex batches for the
///     first material. THAW parsing now routes through a replay layer that ports the needed VIF/VU1
///     state handling into C#: STCYCL/BASE/OFFSET/ITOP/STMOD/STMASK, UNPACK addressing, double
///     buffering, and batch snapshots at MSCAL/MSCNT. Mesh extraction still consumes the decoded
///     interleaved vertex payload plus post-batch kick words, but it does so from replay batches
///     rather than from a separate bespoke VIF walk.
///
///     Also handles PAK-extracted .skin files from THAW PS2 level _main PAKs.
///     These have the same VIF vertex format but a different preamble (no 32B header/entry table).
///     Material info is extracted from GS register writes (TEX0_1, ALPHA_1) in DIRECT VIF blocks.
/// </summary>
public static class ThawPs2SkinFile
{
    /// <summary>
    ///     Detect pre-compiled VIF/DMA .skin.ps2 (THAW or THUG2).
    ///     Works with partial data (32-byte header from FormatProbe) or full file data.
    ///     When fileSize is provided, it's used for size validation instead of data.Length.
    /// </summary>
    public static bool IsThawPs2Skin(byte[] data, long fileSize = 0)
    {
        if (data.Length < 32) return false;
        if (fileSize == 0) fileSize = data.Length;

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes1 = BitConverter.ToUInt32(data, 4);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);

        // Standard PS2 scene version triples: (3,4,1), (5,6,1), (6,6,1)
        if (numObjects is 3 or 5 or 6 && totalMeshes1 is 4 or 6 && totalMeshes2 == 1)
            return false;

        // Sanity: numObjects small, meshes reasonable
        if (numObjects == 0 || numObjects > 20) return false;
        if (totalMeshes2 == 0 || totalMeshes2 > 500) return false;
        if (totalMeshes1 > totalMeshes2) return false;

        // dataSize + 16 must not exceed file size (allow CAS trailing data)
        if (dataSize + 16 > fileSize) return false;

        // Bounding sphere floats at 0x10 must be valid
        var bsR = BitConverter.ToSingle(data, 0x1C);
        if (float.IsNaN(bsR) || float.IsInfinity(bsR) || bsR <= 0) return false;

        // Entry table must fit within file (only check with full data)
        if (data.Length > 32)
        {
            var entryTableEnd = 32 + numObjects * 8 + totalMeshes2 * 64;
            if (entryTableEnd > fileSize) return false;
        }

        return true;
    }

    public static Ps2Scene Parse(string filePath) => Parse(
        File.ReadAllBytes(filePath),
        TryLoadCompanionTexData(filePath));

    /// <summary>
    ///     Parse a THAW PS2 .skin.ps2 file. When companionTexData is provided,
    ///     uses DIRECT block TEX0 register values to correctly map VIF sections
    ///     to entry table materials (the VIF rendering order doesn't match the
    ///     entry table order).
    /// </summary>
    public static Ps2Scene Parse(byte[] data, byte[]? companionTexData = null)
    {
        // Read header and entry table for material/grouping info
        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);
        var (_, setupStarts) = ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        ms.Position = 0x20;

        for (var i = 0; i < numObjects; i++)
            r.ReadBytes(8);

        var entries = new EntryRecord[totalMeshes2];
        for (var i = 0; i < totalMeshes2; i++)
            entries[i] = ReadEntry(r);

        var mappingInfo = BuildSetupMappingInfo(data, companionTexData, entries, setupStarts);
        var entryTextureOverrides = mappingInfo.EntryTextureOverrides;
        var entryAlphaRefs = mappingInfo.EntryAlphaRefs;

        // Build materials from the raw entry table, applying DIRECT-derived overrides after
        // remapping section order back onto the actual entry indices.
        var materialMap = new Dictionary<uint, Ps2Material>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var texCk = entry.TextureChecksum;

            if (entryTextureOverrides != null && entryTextureOverrides.TryGetValue(i, out var directTexCk))
                texCk = directTexCk;

            if (!materialMap.ContainsKey(entry.MaterialChecksum))
            {
                var alphaRef = 0;
                if (entryAlphaRefs != null && entryAlphaRefs.TryGetValue(i, out var directAlphaRef))
                    alphaRef = directAlphaRef;

                materialMap[entry.MaterialChecksum] = new Ps2Material
                {
                    Checksum = entry.MaterialChecksum,
                    TextureChecksum = texCk,
                    RegAlpha = entry.GsAlphaLow | ((ulong)entry.GsAlphaHigh << 32),
                    Flags = entry.MaterialFlags,
                    AlphaRef = alphaRef
                };
            }
        }

        // Extract meshes via replay kick path (full VIF/VU1 simulation)
        var kicks = ReplayExtractKicks(data, companionTexData);

        // Group kick meshes by owner object checksum
        var groupMap = new Dictionary<uint, List<Ps2Mesh>>();
        foreach (var kick in kicks)
        {
            var ownerCk = kick.EntryIndex < entries.Length
                ? entries[kick.EntryIndex].OwnerObjectChecksum
                : 0u;

            if (!groupMap.TryGetValue(ownerCk, out var list))
            {
                list = [];
                groupMap[ownerCk] = list;
            }

            foreach (var mesh in kick.Meshes)
            {
                if (mesh.Vertices.Length >= 3)
                    list.Add(mesh);
            }
        }

        var meshGroups = groupMap.Select(kv => new Ps2MeshGroup
        {
            Checksum = kv.Key,
            Meshes = kv.Value
        }).ToList();

        return new Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [.. materialMap.Values],
            MeshGroups = meshGroups
        };
    }

    internal static IReadOnlyList<ThawReplayBatch> ReplayBatches(byte[] data)
    {
        if (!IsThawPs2Skin(data, data.Length))
            return [];

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);
        var (vifStart, setupStarts) = ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);
        return ThawPs2ReplayEngine.ReplayBatches(data, vifStart, vifEnd, setupStarts);
    }

    internal static IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> ReplayExtractKicks(
        byte[] data, byte[]? companionTexData = null)
    {
        if (!IsThawPs2Skin(data, data.Length))
            return [];

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);
        var (vifStart, setupStarts) = ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);
        var replayBatches = ThawPs2ReplayEngine.ReplayBatches(data, vifStart, vifEnd, setupStarts);

        using var ms = new MemoryStream(data);
        using var r = new BinaryReader(ms);
        r.ReadBytes(32);
        for (var i = 0; i < numObjects; i++)
            r.ReadBytes(8);

        var entries = new EntryRecord[totalMeshes2];
        for (var i = 0; i < totalMeshes2; i++)
            entries[i] = ReadEntry(r);

        var mappingInfo = BuildSetupMappingInfo(data, companionTexData, entries, setupStarts);
        var replayEntries = new (uint MaterialChecksum, bool HasVertexColors)[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            replayEntries[i] = (entries[i].MaterialChecksum, entries[i].HasVertexColors);

        return ThawReplayKickExtractor.ExtractKicks(replayEntries, mappingInfo.SetupEntryIndices, replayBatches);
    }

    private static EntryRecord ReadEntry(BinaryReader r)
    {
        r.ReadUInt32(); // materialEntrySize
        var materialChecksum = r.ReadUInt32();
        var materialFlags = r.ReadUInt32();
        r.ReadUInt32(); // flags2
        var gsAlphaLow = r.ReadUInt32();
        var gsAlphaHigh = r.ReadUInt32();
        r.ReadUInt32(); // grassFlag
        r.ReadUInt32(); // reserved1
        var textureChecksum = r.ReadUInt32();
        r.ReadUInt32(); // texturePassFlag
        r.ReadUInt32(); // reserved2
        var ownerObjectChecksum = r.ReadUInt32();
        r.ReadUInt32(); // packedStcycl
        var vertexColorMask = r.ReadUInt32();
        r.ReadUInt32(); // reserved3
        r.ReadUInt32(); // reserved4

        return new EntryRecord
        {
            MaterialChecksum = materialChecksum,
            MaterialFlags = materialFlags,
            GsAlphaLow = gsAlphaLow,
            GsAlphaHigh = gsAlphaHigh,
            TextureChecksum = textureChecksum,
            OwnerObjectChecksum = ownerObjectChecksum,
            HasVertexColors = vertexColorMask != 0
        };
    }

    /// <summary>
    ///     Detect PAK-extracted .skin files from THAW PS2 level _main PAK archives.
    ///     These files contain VIF/DMA rendering chains but have a variable-length preamble
    ///     instead of the standard 32B ThawPs2SkinFile header.
    ///     Detection: NOT a known format + contains FLUSH+DIRECT VIF opcode pair in first 8KB.
    /// </summary>
    public static bool IsPakSkin(byte[] data)
    {
        if (data.Length < 256) return false;

        // Must NOT be a known format
        if (IsThawPs2Skin(data, data.Length)) return false;
        if (Ps2SceneFile.IsPs2Scene(data)) return false;

        // Must contain a FLUSH (0x11000000) followed by DIRECT in the first 8KB
        var searchEnd = Math.Min(data.Length, 8192);
        for (var i = 0; i < searchEnd - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != 0x11000000)
                continue;

            // Verify DIRECT (0x50/0x51) follows the FLUSH
            var next = VifNextCode(data, i, data.Length);
            if (next + 4 <= data.Length && (data[next + 3] & 0x7F) is 0x50 or 0x51)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     Parse a PAK-extracted .skin file from THAW PS2 level _main PAK archives.
    ///     These files have a variable-length preamble before VIF data. Material info
    ///     is extracted from GS registers (TEX0_1, ALPHA_1) in each setup block's DIRECT block.
    /// </summary>
    public static Ps2Scene ParsePakSkin(byte[] data)
    {
        var emptyScene = new Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [],
            MeshGroups = []
        };

        // Find VIF data start by scanning for first FLUSH opcode
        var vifStart = -1;
        for (var i = 0; i < data.Length - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) == 0x11000000)
            {
                vifStart = i;
                break;
            }
        }

        if (vifStart < 0)
            return emptyScene;

        // Find all setup boundaries (FLUSH+DIRECT pairs)
        var setupStarts = FindSetupBoundaries(data, vifStart, data.Length);
        if (setupStarts.Count == 0)
            return emptyScene;

        // Build material info from GS registers in each setup's DIRECT block
        var materialMap = new Dictionary<uint, Ps2Material>();
        var setupMaterialChecksums = new uint[setupStarts.Count];

        for (var setupIdx = 0; setupIdx < setupStarts.Count; setupIdx++)
        {
            var directOffset = VifNextCode(data, setupStarts[setupIdx], data.Length);
            var gsRegs = ParseDirectBlockRegisters(data, directOffset);
            var matChecksum = gsRegs.Tex0Cbp;
            setupMaterialChecksums[setupIdx] = matChecksum;

            if (!materialMap.ContainsKey(matChecksum))
            {
                materialMap[matChecksum] = new Ps2Material
                {
                    Checksum = matChecksum,
                    TextureChecksum = gsRegs.Tex0Cbp,
                    RegAlpha = gsRegs.Alpha1,
                    Flags = 0,
                    AlphaRef = gsRegs.AlphaRef
                };
            }
        }

        // Run replay engine on the VIF data
        var replayBatches = ThawPs2ReplayEngine.ReplayBatches(data, vifStart, data.Length, setupStarts);

        // Build entry-like tuples from GS-derived material checksums
        var replayEntries = new (uint MaterialChecksum, bool HasVertexColors)[setupStarts.Count];
        for (var i = 0; i < setupStarts.Count; i++)
            replayEntries[i] = (setupMaterialChecksums[i], false);

        // Extract kicks via replay path
        var setupEntryIndices = new int[setupStarts.Count + 1];
        if (setupEntryIndices.Length > 0)
            setupEntryIndices[0] = 0;
        for (var i = 0; i < setupStarts.Count; i++)
            setupEntryIndices[i + 1] = i;

        var kicks = ThawReplayKickExtractor.ExtractKicks(replayEntries, setupEntryIndices, replayBatches);

        // Collect all meshes from kicks
        var allMeshes = new List<Ps2Mesh>();
        foreach (var kick in kicks)
        {
            foreach (var mesh in kick.Meshes)
            {
                if (mesh.Vertices.Length >= 3)
                    allMeshes.Add(mesh);
            }
        }

        var meshGroups = new List<Ps2MeshGroup>();
        if (allMeshes.Count > 0)
        {
            meshGroups.Add(new Ps2MeshGroup
            {
                Checksum = 0,
                Meshes = allMeshes
            });
        }

        return new Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [.. materialMap.Values],
            MeshGroups = meshGroups
        };
    }

    /// <summary>
    ///     Parse GS register writes from a DIRECT VIF block.
    ///     DIRECT blocks contain GIF A+D register writes that set up GS state (textures, blending).
    ///     Layout: 4-byte DIRECT header → 16-byte GIF tag → N×16-byte register writes.
    /// </summary>
    private static GsRegisters ParseDirectBlockRegisters(byte[] data, int directOffset)
    {
        var result = new GsRegisters();

        if (directOffset + 4 > data.Length) return result;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directOffset));
        var gifStart = directOffset + 4;
        var gifEnd = gifStart + (qwc << 4);
        if (gifEnd > data.Length || qwc == 0) return result;

        // Read GIF tag (128 bits)
        if (gifStart + 16 > data.Length) return result;
        var gifLo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart));
        var nloop = (int)(gifLo & 0x7FFF);
        var flg = (int)((gifLo >> 58) & 3);
        var nreg = (int)((gifLo >> 60) & 0xF);
        if (nreg == 0) nreg = 16;
        var gifHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart + 8));

        // Expect PACKED mode (FLG=0), single register descriptor A+D (0x0E)
        if (flg != 0 || nreg != 1 || (gifHi & 0xFF) != 0x0E) return result;

        // Parse A+D register writes
        for (var i = 0; i < nloop; i++)
        {
            var off = gifStart + 16 + i * 16;
            if (off + 16 > data.Length) break;

            var dataVal = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            var regHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8));
            var regAddr = (int)(regHi & 0xFF);

            switch (regAddr)
            {
                case 0x06: // TEX0_1 — extract CBP (bits 37-50) as texture identifier
                    result.Tex0Cbp = (uint)((dataVal >> 37) & 0x3FFF);
                    break;
                case 0x42: // ALPHA_1
                    result.Alpha1 = dataVal;
                    break;
                case 0x47: // TEST_1
                    if ((dataVal & 1) != 0)
                        result.AlphaRef = (byte)((dataVal >> 4) & 0xFF);
                    break;
            }
        }

        return result;
    }

    private readonly record struct SetupMappingInfo(
        int[] SetupEntryIndices,
        Dictionary<int, uint>? EntryTextureOverrides,
        Dictionary<int, int>? EntryAlphaRefs);

    private static SetupMappingInfo BuildSetupMappingInfo(
        byte[] data,
        byte[]? companionTexData,
        IReadOnlyList<EntryRecord> entries,
        IReadOnlyList<int> setupStarts)
    {
        var rawDirectOffsets = FindRawDirectOffsets(data);
        var setupSectionAlphaRefs = BuildSectionAlphaRefMapping(data, setupStarts);

        if (HasLeadingRawDirectPreamble(rawDirectOffsets, setupStarts))
        {
            var rawSectionTextures = BuildSectionTextureMapping(data, companionTexData, rawDirectOffsets);
            if (rawSectionTextures != null && rawSectionTextures.Count > 0)
            {
                var rawSectionEntryIndices = BuildSectionEntryIndices(entries, rawDirectOffsets.Count, rawSectionTextures);
                var setupEntryIndices = BuildDirectAlignedSetupEntryIndices(
                    rawSectionEntryIndices,
                    setupStarts.Count + 1,
                    entries.Count);
                var rawSectionAlphaRefs = BuildSectionAlphaRefMapping(data, rawDirectOffsets);
                var entryAlphaRefs = BuildEntryAlphaRefOverrides(rawSectionAlphaRefs, setupEntryIndices, 0);
                return new SetupMappingInfo(setupEntryIndices, null, entryAlphaRefs);
            }
        }

        if (RawDirectOffsetsMatchSetupStarts(rawDirectOffsets, setupStarts))
        {
            var sectionTextures = BuildSectionTextureMapping(data, companionTexData, setupStarts);
            var setupEntryIndices = BuildSetupEntryIndices(entries, setupStarts.Count, sectionTextures, setupSectionAlphaRefs);
            var entryTextureOverrides = BuildEntryTextureOverrides(sectionTextures, setupEntryIndices, 1);
            var entryAlphaRefs = BuildEntryAlphaRefOverrides(setupSectionAlphaRefs, setupEntryIndices, 1);
            return new SetupMappingInfo(setupEntryIndices, entryTextureOverrides, entryAlphaRefs);
        }

        var identitySetupEntryIndices = BuildIdentitySetupEntryIndices(setupStarts.Count + 1, entries.Count);
        var identityAlphaRefs = BuildEntryAlphaRefOverrides(setupSectionAlphaRefs, identitySetupEntryIndices, 1);
        return new SetupMappingInfo(identitySetupEntryIndices, null, identityAlphaRefs);
    }

    /// <summary>
    ///     Build a mapping from DIRECT block index to TEST_1 alpha-reference values.
    ///     THAW skin entry records do not carry Aref, but the DIRECT setup blocks do.
    /// </summary>
    private static Dictionary<int, int>? BuildSectionAlphaRefMapping(
        byte[] data,
        IReadOnlyList<int> directOffsets)
    {
        if (directOffsets.Count == 0)
            return null;

        var mapping = new Dictionary<int, int>();
        for (var sectionIdx = 0; sectionIdx < directOffsets.Count; sectionIdx++)
        {
            var regs = ParseDirectBlockRegisters(data, directOffsets[sectionIdx]);
            if (regs.AlphaRef > 0)
                mapping[sectionIdx] = regs.AlphaRef;
        }

        return mapping.Count > 0 ? mapping : null;
    }

    /// <summary>
    ///     Build a mapping from DIRECT block index to texture checksum using DIRECT TEX0 values
    ///     matched against companion .tex.ps2 data.
    /// </summary>
    private static Dictionary<int, uint>? BuildSectionTextureMapping(
        byte[] data,
        byte[]? companionTexData,
        IReadOnlyList<int> directOffsets)
    {
        if (companionTexData is null) return null;
        if (directOffsets.Count == 0) return null;

        var tbpCbpToChecksum = BuildTbpCbpMap(companionTexData);
        if (tbpCbpToChecksum.Count == 0) return null;

        var mapping = new Dictionary<int, uint>();
        for (var sectionIdx = 0; sectionIdx < directOffsets.Count; sectionIdx++)
        {
            var tex0 = ExtractTex0FromDirect(data, directOffsets[sectionIdx]);
            if (tex0 is null) continue;

            var (tbp, cbp) = tex0.Value;
            if (tbpCbpToChecksum.TryGetValue((tbp, cbp), out var texChecksum))
                mapping[sectionIdx] = texChecksum;
        }

        return mapping.Count > 0 ? mapping : null;
    }

    private static bool RawDirectOffsetsMatchSetupStarts(
        IReadOnlyList<int> rawDirectOffsets,
        IReadOnlyList<int> setupStarts)
    {
        if (rawDirectOffsets.Count != setupStarts.Count)
            return false;

        for (var i = 0; i < rawDirectOffsets.Count; i++)
        {
            if (rawDirectOffsets[i] != setupStarts[i])
                return false;
        }

        return true;
    }

    private static bool HasLeadingRawDirectPreamble(
        IReadOnlyList<int> rawDirectOffsets,
        IReadOnlyList<int> setupStarts)
    {
        if (rawDirectOffsets.Count != setupStarts.Count + 1)
            return false;

        for (var i = 0; i < setupStarts.Count; i++)
        {
            if (rawDirectOffsets[i + 1] != setupStarts[i])
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Extract TEX0 TBP and CBP values from a DIRECT VIF block's GIF A+D register writes.
    /// </summary>
    private static (uint Tbp, uint Cbp)? ExtractTex0FromDirect(byte[] data, int directOffset)
    {
        if (directOffset + 4 > data.Length) return null;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directOffset));
        var gifStart = directOffset + 4;
        var gifEnd = gifStart + (qwc << 4);
        if (gifEnd > data.Length || qwc == 0) return null;
        if (gifStart + 16 > data.Length) return null;

        var gifLo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart));
        var nloop = (int)(gifLo & 0x7FFF);
        var flg = (int)((gifLo >> 58) & 3);
        var nreg = (int)((gifLo >> 60) & 0xF);
        if (nreg == 0) nreg = 16;
        var gifHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart + 8));

        // Expect PACKED mode (FLG=0), single register descriptor A+D (0x0E)
        if (flg != 0 || nreg != 1 || (gifHi & 0xFF) != 0x0E) return null;

        for (var i = 0; i < nloop; i++)
        {
            var off = gifStart + 16 + i * 16;
            if (off + 16 > data.Length) break;
            var dataVal = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off));
            var regHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(off + 8));
            if ((regHi & 0xFF) == 0x06) // TEX0_1
            {
                var tbp = (uint)(dataVal & 0x3FFF);
                var cbp = (uint)((dataVal >> 37) & 0x3FFF);
                return (tbp, cbp);
            }
        }

        return null;
    }

    /// <summary>
    ///     Build (TBP,CBP) → texture checksum mapping from companion .tex.ps2 metadata.
    ///     Scans for TEX0 register values at 8-byte intervals with valid PSM/TBP/TBW fields.
    ///     Texture checksum is at TEX0_offset - 0x10 (same layout as ThawSceneTexFile.ScanTex0Entries).
    /// </summary>
    private static Dictionary<(uint, uint), uint> BuildTbpCbpMap(byte[] texData)
    {
        var map = new Dictionary<(uint, uint), uint>();
        if (texData.Length < 0x40) return map;

        var version = BitConverter.ToUInt16(texData, 0);
        if (version != 6) return map;

        var off1 = (int)BitConverter.ToUInt32(texData, 8);
        if (off1 <= 0x40 || off1 >= texData.Length) return map;

        for (var off = 0x40; off + 8 <= off1; off += 8)
        {
            var val = BitConverter.ToUInt64(texData, off);
            var tbp = (uint)(val & 0x3FFF);
            var tbw = (uint)((val >> 14) & 0x3F);
            var psm = (uint)((val >> 20) & 0x3F);
            var tw = (int)((val >> 26) & 0xF);
            var th = (int)((val >> 30) & 0xF);

            if (!Ps2TexPixelDecoder.IsValidPsm(psm)) continue;
            if (tw < 1 || tw > 10 || th < 1 || th > 10) continue;
            if (tbp < 0x2BC0 || tbw < 1) continue;

            var ckOff = off - 0x10;
            if (ckOff < 0x40) continue;
            var checksum = BitConverter.ToUInt32(texData, ckOff);
            if (checksum <= 0xFFFF) continue;

            var cbp = (uint)((val >> 37) & 0x3FFF);
            map[(tbp, cbp)] = checksum;
        }

        return map;
    }

    /// <summary>
    ///     Build a replay-setup to entry-table mapping.
    ///     Replay batches use setup index 0 for preamble, then 1..N for DIRECT setup sections 0..N-1.
    ///     Some skins (e.g. sec_jimbo) permute the entry table relative to DIRECT setup order, so
    ///     we resolve the DIRECT section sequence back onto raw entry indices using TEX0-derived textures.
    /// </summary>
    private static int[] BuildSetupEntryIndices(
        IReadOnlyList<EntryRecord> entries,
        int setupCount,
        IReadOnlyDictionary<int, uint>? sectionTextures,
        IReadOnlyDictionary<int, int>? sectionAlphaRefs)
    {
        if (entries.Count == 0)
            return [];

        var sectionCount = setupCount;
        if (sectionTextures != null && sectionTextures.Count > 0)
            sectionCount = Math.Max(sectionCount, sectionTextures.Keys.Max() + 1);
        if (sectionAlphaRefs != null && sectionAlphaRefs.Count > 0)
            sectionCount = Math.Max(sectionCount, sectionAlphaRefs.Keys.Max() + 1);

        var sectionEntryIndices = BuildSectionEntryIndices(entries, sectionCount, sectionTextures);
        var setupEntryIndices = new int[sectionCount + 1];
        setupEntryIndices[0] = 0;

        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            var fallbackEntryIndex = Math.Min(sectionIndex + 1, entries.Count - 1);
            var entryIndex = sectionEntryIndices[sectionIndex];
            setupEntryIndices[sectionIndex + 1] = entryIndex >= 0
                ? entryIndex
                : fallbackEntryIndex;
        }

        return setupEntryIndices;
    }

    private static int[] BuildIdentitySetupEntryIndices(int setupSlotCount, int entryCount)
    {
        if (entryCount == 0 || setupSlotCount <= 0)
            return [];

        var setupEntryIndices = new int[setupSlotCount];
        for (var setupIndex = 0; setupIndex < setupSlotCount; setupIndex++)
            setupEntryIndices[setupIndex] = Math.Min(setupIndex, entryCount - 1);

        return setupEntryIndices;
    }

    private static int[] BuildDirectAlignedSetupEntryIndices(
        IReadOnlyList<int> sectionEntryIndices,
        int setupSlotCount,
        int entryCount)
    {
        if (entryCount == 0 || setupSlotCount <= 0)
            return [];

        var setupEntryIndices = new int[setupSlotCount];
        for (var setupIndex = 0; setupIndex < setupSlotCount; setupIndex++)
        {
            if ((uint)setupIndex < (uint)sectionEntryIndices.Count)
            {
                var entryIndex = sectionEntryIndices[setupIndex];
                if ((uint)entryIndex < (uint)entryCount)
                {
                    setupEntryIndices[setupIndex] = entryIndex;
                    continue;
                }
            }

            setupEntryIndices[setupIndex] = Math.Min(setupIndex, entryCount - 1);
        }

        return setupEntryIndices;
    }

    private static int[] BuildSectionEntryIndices(
        IReadOnlyList<EntryRecord> entries,
        int sectionCount,
        IReadOnlyDictionary<int, uint>? sectionTextures)
    {
        var sectionEntryIndices = Enumerable.Repeat(-1, sectionCount).ToArray();
        if (entries.Count == 0)
            return sectionEntryIndices;

        if (sectionTextures is null || sectionTextures.Count == 0)
        {
            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
                sectionEntryIndices[sectionIndex] = Math.Min(sectionIndex + 1, entries.Count - 1);

            return sectionEntryIndices;
        }

        var entryAssigned = new bool[entries.Count];
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            if (!sectionTextures.TryGetValue(sectionIndex, out var sectionTexture))
                continue;

            var entryIndex = ResolveSectionEntryIndex(entries, sectionTexture, sectionIndex, entryAssigned);
            if (entryIndex < 0)
                continue;

            sectionEntryIndices[sectionIndex] = entryIndex;
            entryAssigned[entryIndex] = true;
        }

        // Preserve original order for unresolved sections while keeping the mapping unique.
        for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
        {
            if (sectionEntryIndices[sectionIndex] >= 0)
                continue;

            if (sectionIndex < entries.Count && !entryAssigned[sectionIndex])
            {
                sectionEntryIndices[sectionIndex] = sectionIndex;
                entryAssigned[sectionIndex] = true;
                continue;
            }

            var fallbackIndex = Array.FindIndex(entryAssigned, assigned => !assigned);
            if (fallbackIndex >= 0)
            {
                sectionEntryIndices[sectionIndex] = fallbackIndex;
                entryAssigned[fallbackIndex] = true;
            }
        }

        return sectionEntryIndices;
    }

    private static Dictionary<int, uint>? BuildEntryTextureOverrides(
        IReadOnlyDictionary<int, uint>? sectionTextures,
        IReadOnlyList<int> setupEntryIndices,
        int setupIndexBase)
    {
        if (sectionTextures is null || sectionTextures.Count == 0)
            return null;

        var overrides = new Dictionary<int, uint>();
        foreach (var (sectionIndex, textureChecksum) in sectionTextures)
        {
            var setupIndex = sectionIndex + setupIndexBase;
            if ((uint)setupIndex >= (uint)setupEntryIndices.Count)
                continue;

            overrides[setupEntryIndices[setupIndex]] = textureChecksum;
        }

        return overrides.Count > 0 ? overrides : null;
    }

    private static Dictionary<int, int>? BuildEntryAlphaRefOverrides(
        IReadOnlyDictionary<int, int>? sectionAlphaRefs,
        IReadOnlyList<int> setupEntryIndices,
        int setupIndexBase)
    {
        if (sectionAlphaRefs is null || sectionAlphaRefs.Count == 0)
            return null;

        var overrides = new Dictionary<int, int>();
        foreach (var (sectionIndex, alphaRef) in sectionAlphaRefs)
        {
            var setupIndex = sectionIndex + setupIndexBase;
            if ((uint)setupIndex >= (uint)setupEntryIndices.Count)
                continue;

            overrides[setupEntryIndices[setupIndex]] = alphaRef;
        }

        return overrides.Count > 0 ? overrides : null;
    }

    private static byte[]? TryLoadCompanionTexData(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (dir == null)
            return null;

        var stem = Path.GetFileName(filePath);
        if (stem.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".skin.ps2".Length];
        else if (stem.EndsWith(".mdl.ps2", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".mdl.ps2".Length];
        else if (stem.EndsWith(".iskin.ps2", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".iskin.ps2".Length];
        else
            stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(stem));

        var texPath = CompanionSearch.FindCompanion(dir, stem, [".tex.ps2"], ["TEX", "Textures"]);
        return texPath != null && File.Exists(texPath)
            ? File.ReadAllBytes(texPath)
            : null;
    }

    private static int ResolveSectionEntryIndex(
        IReadOnlyList<EntryRecord> entries,
        uint sectionTexture,
        int sectionIndex,
        IReadOnlyList<bool> entryAssigned)
    {
        var bestIndex = -1;
        var bestDistance = int.MaxValue;

        for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
        {
            if (entryAssigned[entryIndex] || entries[entryIndex].TextureChecksum != sectionTexture)
                continue;

            if (entryIndex == sectionIndex)
                return entryIndex;

            var distance = Math.Abs(entryIndex - sectionIndex);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = entryIndex;
            }
        }

        return bestIndex;
    }

    /// <summary>
    ///     Resolve the real THAW VIF-chain lower bound plus command-stepped setup boundaries.
    ///     Some files have descriptor/gap data between the entry table and the first DMA/VIF chain.
    ///     If the stepped scan from the header-derived lower bound finds nothing, fall back to the
    ///     first raw FLUSH+DIRECT pair as the chain lower bound and rescan from there.
    /// </summary>
    private static (int VifStart, List<int> SetupStarts) ResolveThawSetupBoundaries(
        byte[] data,
        int searchStart,
        int searchEnd)
    {
        var setupStarts = FindSetupBoundaries(data, searchStart, searchEnd);
        var rawFlushOffsets = FindRawSetupBoundaryFlushOffsets(data, searchStart, searchEnd);
        if (rawFlushOffsets.Count == 0)
            return (searchStart, setupStarts);

        var fallbackVifStart = rawFlushOffsets[0] + 4;
        var needsFallbackStart = setupStarts.Count == 0 || fallbackVifStart < setupStarts[0];
        if (!needsFallbackStart)
            return (searchStart, setupStarts);

        var rescannedSetupStarts = FindSetupBoundaries(data, fallbackVifStart, searchEnd);
        if (rescannedSetupStarts.Count > 0)
            return (fallbackVifStart, rescannedSetupStarts);

        var rawDirectOffsets = rawFlushOffsets
            .Select(flushOffset => flushOffset + 4)
            .ToList();
        return (fallbackVifStart, rawDirectOffsets);
    }

    /// <summary>
    ///     Find all material/setup boundary positions by scanning for FLUSH+DIRECT VIF opcode pairs.
    ///     These mark GS register setup changes inside the rendering chain. The first material can
    ///     begin in the VIF preamble before the first FLUSH+DIRECT pair, so callers must keep the
    ///     preamble range if present. Step the stream with VIF semantics instead of raw dword scans:
    ///     large UNPACK payloads can contain byte patterns that look like FLUSH/DIRECT but are only
    ///     uploaded VU1 data, not real VIF commands.
    /// </summary>
    private static List<int> FindSetupBoundaries(byte[] data, int searchStart, int searchEnd)
    {
        var setupStarts = new List<int>();
        var position = searchStart;
        var previousWasFlush = false;

        while (position + 4 <= searchEnd && position + 4 <= data.Length)
        {
            var command = data[position + 3] & 0x7F;
            if (command is 0x10 or 0x11)
            {
                previousWasFlush = true;
            }
            else if (command is 0x50 or 0x51)
            {
                if (previousWasFlush)
                    setupStarts.Add(position);

                previousWasFlush = false;
            }
            else
            {
                previousWasFlush = false;
            }

            position = VifNextCode(data, position, searchEnd);
        }

        return setupStarts;
    }

    private static List<int> FindRawSetupBoundaryFlushOffsets(byte[] data, int searchStart, int searchEnd)
    {
        var flushOffsets = new List<int>();
        for (var offset = searchStart; offset + 8 <= searchEnd && offset + 8 <= data.Length; offset += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (word is not (0x10000000 or 0x11000000))
                continue;

            var nextCommand = data[offset + 7] & 0x7F;
            if (nextCommand is 0x50 or 0x51)
                flushOffsets.Add(offset);
        }

        return flushOffsets;
    }

    private static List<int> FindRawDirectOffsets(byte[] data)
    {
        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);

        var directOffsets = new List<int>();
        for (var offset = entryTableEnd; offset + 8 <= vifEnd && offset + 8 <= data.Length; offset += 4)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (word is not (0x10000000 or 0x11000000))
                continue;

            var nextCommand = data[offset + 7] & 0x7F;
            if (nextCommand is 0x50 or 0x51)
                directOffsets.Add(offset + 4);
        }

        return directOffsets;
    }

    /// <summary>Advance past one VIF opcode. Port of vif::NextCode from vif.cpp.</summary>
    internal static int VifNextCode(byte[] data, int offset, int end)
    {
        if (offset >= end || offset + 4 > data.Length)
            return end;

        var cmd = data[offset + 3];

        if ((cmd & 0x60) != 0x60)
        {
            return (cmd & 0x7F) switch
            {
                0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 or 0x07
                    or 0x10 or 0x11 or 0x13 or 0x14 or 0x15 or 0x17 => offset + 4,
                0x20 => offset + 8, // STMASK
                0x30 or 0x31 => offset + 20, // STROW/STCOL
                0x4A => offset + (data[offset + 2] << 3) + 4, // MPG
                0x50 or 0x51 => // DIRECT/DIRECTHL
                    offset + (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) << 4) + 4,
                _ => end
            };
        }

        // UNPACK
        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[offset + 2];
        var dimension = vn + 1;
        var bitLength = 32 >> vl;
        var dataSize = ((bitLength * dimension * num + 31) >> 5) << 2;
        return offset + 4 + dataSize;
    }

    private struct EntryRecord
    {
        public uint MaterialChecksum;
        public uint MaterialFlags;
        public uint GsAlphaLow;
        public uint GsAlphaHigh;
        public uint TextureChecksum;
        public uint OwnerObjectChecksum;
        public bool HasVertexColors;
    }

    private struct GsRegisters
    {
        public uint Tex0Cbp;  // CLUT base pointer — unique per texture binding
        public ulong Alpha1;  // GS blend mode register
        public byte AlphaRef; // TEST_1 AREF when alpha test is enabled
    }
}
