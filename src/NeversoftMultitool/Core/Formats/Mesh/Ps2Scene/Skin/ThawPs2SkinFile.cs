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

        if (data.Length > 32)
        {
            var entryTableEnd = 32 + numObjects * 8 + totalMeshes2 * 64;
            if (entryTableEnd > fileSize) return false;
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
        var (vifStart, setupStarts) = ThawPs2SkinVifLayout.ResolveThawSetupBoundaries(data, entryTableEnd, vifEnd);
        var replayBatches = ThawPs2ReplayEngine.ReplayBatches(data, vifStart, vifEnd, setupStarts);

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

        return ThawReplayKickExtractor.ExtractKicks(replayEntries, mappingInfo.SetupEntryIndices, replayBatches);
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

    public static Scene.Ps2Scene ParsePakSkin(byte[] data)
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

        var materialMap = new Dictionary<uint, Ps2Material>();
        var setupMaterialChecksums = new uint[setupStarts.Count];

        for (var setupIndex = 0; setupIndex < setupStarts.Count; setupIndex++)
        {
            var directOffset = VifNextCode(data, setupStarts[setupIndex], data.Length);
            var gsRegisters = ThawPs2SkinSetupMapping.ParseDirectBlockRegisters(data, directOffset);
            var materialChecksum = gsRegisters.Tex0Cbp;
            setupMaterialChecksums[setupIndex] = materialChecksum;

            if (!materialMap.ContainsKey(materialChecksum))
            {
                materialMap[materialChecksum] = new Ps2Material
                {
                    Checksum = materialChecksum,
                    TextureChecksum = gsRegisters.Tex0Cbp,
                    RegAlpha = gsRegisters.Alpha1,
                    Flags = 0,
                    AlphaRef = gsRegisters.AlphaRef
                };
            }
        }

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
            Materials = [.. materialMap.Values],
            MeshGroups = meshGroups
        };
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
}
