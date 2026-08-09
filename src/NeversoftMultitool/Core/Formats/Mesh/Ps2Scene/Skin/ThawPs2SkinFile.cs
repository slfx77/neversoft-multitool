using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

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
///     Also handles PAK-extracted .skin files from THAW PS2 level _main PAKs.
///     These have the same VIF vertex format but a different preamble (no 32B header/entry table).
///     Material info is extracted from GS register writes (TEX0_1, ALPHA_1) in DIRECT VIF blocks.
/// </summary>
public static class ThawPs2SkinFile
{
    public static bool IsThawPs2Skin(byte[] data, long fileSize = 0)
    {
        if (data.Length < 32) return false;
        if (fileSize == 0) fileSize = data.Length;

        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes1 = BitConverter.ToUInt32(data, 4);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);

        if (numObjects is 3 or 5 or 6 && totalMeshes1 is 4 or 6 && totalMeshes2 == 1)
            return false;

        if (numObjects == 0 || numObjects > 20) return false;
        if (totalMeshes2 == 0 || totalMeshes2 > 500) return false;
        if (totalMeshes1 > totalMeshes2) return false;
        if (dataSize + 16 > fileSize) return false;

        var bsR = BitConverter.ToSingle(data, 0x1C);
        if (float.IsNaN(bsR) || float.IsInfinity(bsR) || bsR <= 0) return false;

        return data.Length <= 32 || EntryTableIsValid(data, fileSize, numObjects, totalMeshes2);
    }

    /// <summary>
    ///     THUG2 pre-compiled skins share the THAW header shape but their 64-byte
    ///     records are laid out differently: the u32 at entry+44 (THAW's owner-object
    ///     checksum) holds unrelated data there. In genuine THAW skins every entry's
    ///     owner is either 0 (unowned) or one of the object-table checksums —
    ///     validated corpus-wide at 332/332 THAW accepts versus 746 THUG2 rejects.
    /// </summary>
    private static bool EntryTableIsValid(byte[] data, long fileSize, uint numObjects, uint totalMeshes2)
    {
        var entryTableEnd = 32 + numObjects * 8 + totalMeshes2 * 64;
        if (entryTableEnd > fileSize) return false;

        // Header-only prefix (FormatProbeMesh) — table not in the buffer, can't validate.
        if (entryTableEnd > data.Length) return true;

        Span<uint> objectChecksums = stackalloc uint[(int)numObjects];
        for (var i = 0; i < numObjects; i++)
            objectChecksums[i] = BitConverter.ToUInt32(data, 0x20 + i * 8);

        var entryBase = 0x20 + (int)numObjects * 8;
        for (var i = 0; i < totalMeshes2; i++)
        {
            var owner = BitConverter.ToUInt32(data, entryBase + i * 64 + 44);
            if (owner != 0 && !objectChecksums.Contains(owner))
                return false;
        }

        return true;
    }

    public static Scene.Ps2Scene Parse(string filePath)
    {
        return Parse(
            File.ReadAllBytes(filePath),
            ThawPs2SkinSetupMapping.TryLoadCompanionTexData(filePath));
    }

    public static Scene.Ps2Scene Parse(byte[] data, byte[]? companionTexData = null)
    {
        var numObjects = BitConverter.ToUInt32(data, 0);
        var totalMeshes2 = BitConverter.ToUInt32(data, 8);
        var dataSize = BitConverter.ToUInt32(data, 12);
        var entryTableEnd = (int)(32 + numObjects * 8 + totalMeshes2 * 64);
        var vifEnd = (int)Math.Min(dataSize + 16, data.Length);
        var (_, setupStarts) = ThawPs2SkinVifLayout.ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        ms.Position = 0x20;

        for (var i = 0; i < numObjects; i++)
            reader.ReadBytes(8);

        var entries = new EntryRecord[totalMeshes2];
        for (var i = 0; i < totalMeshes2; i++)
            entries[i] = ReadEntry(reader);

        var mappingInfo = ThawPs2SkinSetupMapping.BuildSetupMappingInfo(data, companionTexData, entries, setupStarts);
        var entryTextureOverrides = mappingInfo.EntryTextureOverrides;
        var entryAlphaRefs = mappingInfo.EntryAlphaRefs;

        var materialMap = new Dictionary<uint, Ps2Material>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var textureChecksum = entry.TextureChecksum;

            if (entryTextureOverrides != null && entryTextureOverrides.TryGetValue(i, out var directTexChecksum))
                textureChecksum = directTexChecksum;

            if (!materialMap.ContainsKey(entry.MaterialChecksum))
            {
                var alphaRef = 0;
                if (entryAlphaRefs != null && entryAlphaRefs.TryGetValue(i, out var directAlphaRef))
                    alphaRef = directAlphaRef;

                materialMap[entry.MaterialChecksum] = new Ps2Material
                {
                    Checksum = entry.MaterialChecksum,
                    TextureChecksum = textureChecksum,
                    RegAlpha = entry.GsAlphaLow | ((ulong)entry.GsAlphaHigh << 32),
                    Flags = entry.MaterialFlags,
                    AlphaRef = alphaRef
                };
            }
        }

        var kicks = ReplayExtractKicks(data, companionTexData);
        var groupMap = new Dictionary<uint, List<Ps2Mesh>>();
        foreach (var kick in kicks)
        {
            var ownerChecksum = kick.EntryIndex < entries.Length
                ? entries[kick.EntryIndex].OwnerObjectChecksum
                : 0u;

            if (!groupMap.TryGetValue(ownerChecksum, out var list))
            {
                list = [];
                groupMap[ownerChecksum] = list;
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

        return new Scene.Ps2Scene
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
        var (vifStart, setupStarts) = ThawPs2SkinVifLayout.ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);
        var positionScale = ThpgPositionUnwrapper.UsesQ412Positions(data, entryTableEnd, vifEnd)
            ? ThawPs2ReplayVertexDecoder.Q412PositionScale
            : ThawPs2ReplayVertexDecoder.DefaultPositionScale;
        return ThawPs2ReplayEngine.ReplayBatches(data, vifStart, vifEnd, setupStarts, positionScale);
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
        var (vifStart, setupStarts) = ThawPs2SkinVifLayout.ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);

        // Proving Ground stores positions in Q4.12 fixed-point form. Decode at
        // that finer scale, then unwrap after kick extraction.
        var usesQ412 = ThpgPositionUnwrapper.UsesQ412Positions(data, entryTableEnd, vifEnd);
        var positionScale = usesQ412
            ? ThawPs2ReplayVertexDecoder.Q412PositionScale
            : ThawPs2ReplayVertexDecoder.DefaultPositionScale;
        var replayBatches = ThawPs2ReplayEngine.ReplayBatches(data, vifStart, vifEnd, setupStarts, positionScale);

        using var ms = new MemoryStream(data);
        using var reader = new BinaryReader(ms);
        reader.ReadBytes(32);
        for (var i = 0; i < numObjects; i++)
            reader.ReadBytes(8);

        var entries = new EntryRecord[totalMeshes2];
        for (var i = 0; i < totalMeshes2; i++)
            entries[i] = ReadEntry(reader);

        var mappingInfo = ThawPs2SkinSetupMapping.BuildSetupMappingInfo(data, companionTexData, entries, setupStarts);
        var replayEntries = new (uint MaterialChecksum, bool HasVertexColors)[entries.Length];
        for (var i = 0; i < entries.Length; i++)
            replayEntries[i] = (entries[i].MaterialChecksum, entries[i].HasVertexColors);

        var kicks = ThawReplayKickExtractor.ExtractKicks(replayEntries, mappingInfo.SetupEntryIndices, replayBatches);
        if (usesQ412)
            UnwrapQ412Kicks(data, kicks);

        return kicks;
    }

    /// <summary>
    ///     Maps each kick's meshes to the per-section bbox record covering its VIF
    ///     offset, then reconstructs the Q4.12 absolute bands (Proving Ground skins).
    /// </summary>
    private static void UnwrapQ412Kicks(byte[] data, IReadOnlyList<ThawReplayKickExtractor.ExtractedKick> kicks)
    {
        var sections = ThpgPositionUnwrapper.ReadSectionBounds(data);
        var allMeshes = new List<Ps2Mesh>();
        var sectionOfMesh = new List<int>();
        foreach (var kick in kicks)
        {
            var sectionIndex = FindSectionIndex(sections, kick.FirstCommandOffset);
            foreach (var mesh in kick.Meshes)
            {
                allMeshes.Add(mesh);
                sectionOfMesh.Add(sectionIndex);
            }
        }

        ThpgPositionUnwrapper.Unwrap(allMeshes, data, sectionOfMesh, sections);
    }

    private static int FindSectionIndex(List<ThpgPositionUnwrapper.SectionBounds> sections, int vifOffset)
    {
        var sectionIndex = -1;
        for (var s = 0; s < sections.Count && sections[s].VifOffset <= vifOffset; s++)
            sectionIndex = s;
        return sectionIndex;
    }

    public static bool IsPakSkin(byte[] data)
    {
        if (data.Length < 256) return false;
        if (IsThawPs2Skin(data, data.Length)) return false;
        if (Ps2SceneFile.IsPs2Scene(data)) return false;

        var searchEnd = Math.Min(data.Length, 8192);
        for (var i = 0; i < searchEnd - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != 0x11000000)
                continue;

            var next = VifNextCode(data, i, data.Length);
            if (next + 4 <= data.Length && (data[next + 3] & 0x7F) is 0x50 or 0x51)
                return true;
        }

        return false;
    }

    public static Scene.Ps2Scene ParsePakSkin(
        byte[] data,
        Ps2Tex0ChecksumResolver? tex0Resolver = null)
    {
        var emptyScene = new Scene.Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [],
            MeshGroups = []
        };

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

        var setupStarts = ThawPs2SkinVifLayout.FindSetupBoundaries(data, vifStart, data.Length);
        if (setupStarts.Count == 0)
            return emptyScene;

        var setupRegisters = new GsRegisters[setupStarts.Count];

        for (var setupIndex = 0; setupIndex < setupStarts.Count; setupIndex++)
        {
            var directOffset = VifNextCode(data, setupStarts[setupIndex], data.Length);
            setupRegisters[setupIndex] = ThawPs2SkinSetupMapping.ParseDirectBlockRegisters(data, directOffset);
        }

        var (materials, setupMaterialChecksums) = BuildPakSkinMaterials(setupRegisters, tex0Resolver);

        var replayBatches = ThawPs2ReplayEngine.ReplayBatches(data, vifStart, data.Length, setupStarts);

        var replayEntries = new (uint MaterialChecksum, bool HasVertexColors)[setupStarts.Count];
        for (var i = 0; i < setupStarts.Count; i++)
            replayEntries[i] = (setupMaterialChecksums[i], false);

        var setupEntryIndices = new int[setupStarts.Count + 1];
        if (setupEntryIndices.Length > 0)
            setupEntryIndices[0] = 0;
        for (var i = 0; i < setupStarts.Count; i++)
            setupEntryIndices[i + 1] = i;

        var kicks = ThawReplayKickExtractor.ExtractKicks(replayEntries, setupEntryIndices, replayBatches);

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

        return new Scene.Ps2Scene
        {
            MaterialVersion = 0,
            MeshVersion = 0,
            VertexVersion = 0,
            Materials = [.. materials],
            MeshGroups = meshGroups
        };
    }

    internal static (IReadOnlyList<Ps2Material> Materials, uint[] SetupMaterialChecksums)
        BuildPakSkinMaterials(
            IReadOnlyList<GsRegisters> setupRegisters,
            Ps2Tex0ChecksumResolver? tex0Resolver = null)
    {
        var materialChecksums = new Dictionary<PakSkinMaterialKey, uint>();
        var materials = new List<Ps2Material>();
        var setupMaterialChecksums = new uint[setupRegisters.Count];

        for (var setupIndex = 0; setupIndex < setupRegisters.Count; setupIndex++)
        {
            var gsRegisters = setupRegisters[setupIndex];
            var textureChecksum = tex0Resolver?.Invoke(gsRegisters.Tex0, 0) ?? 0;
            if (textureChecksum == 0)
                textureChecksum = gsRegisters.Tex0Cbp;

            var key = new PakSkinMaterialKey(
                gsRegisters.Tex0,
                textureChecksum,
                gsRegisters.Alpha1,
                gsRegisters.AlphaRef);

            if (!materialChecksums.TryGetValue(key, out var materialChecksum))
            {
                materialChecksum = checked((uint)(materialChecksums.Count + 1));
                materialChecksums.Add(key, materialChecksum);
                materials.Add(new Ps2Material
                {
                    Checksum = materialChecksum,
                    TextureChecksum = textureChecksum,
                    RegAlpha = gsRegisters.Alpha1,
                    Flags = 0,
                    AlphaRef = gsRegisters.AlphaRef
                });
            }

            setupMaterialChecksums[setupIndex] = materialChecksum;
        }

        return (materials, setupMaterialChecksums);
    }

    internal static int VifNextCode(byte[] data, int offset, int end)
    {
        return ThawPs2SkinVifLayout.VifNextCode(data, offset, end);
    }

    private static EntryRecord ReadEntry(BinaryReader reader)
    {
        reader.ReadUInt32();
        var materialChecksum = reader.ReadUInt32();
        var materialFlags = reader.ReadUInt32();
        reader.ReadUInt32();
        var gsAlphaLow = reader.ReadUInt32();
        var gsAlphaHigh = reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        var textureChecksum = reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        var ownerObjectChecksum = reader.ReadUInt32();
        reader.ReadUInt32();
        var vertexColorMask = reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

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

    private readonly record struct PakSkinMaterialKey(
        ulong Tex0,
        uint TextureChecksum,
        ulong RegAlpha,
        byte AlphaRef);
}
