using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Psx;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawPs2SkinSetupMapping
{
    internal static GsRegisters ParseDirectBlockRegisters(byte[] data, int directOffset)
    {
        var result = new GsRegisters();
        if (directOffset + 4 > data.Length)
            return result;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directOffset));
        var gifStart = directOffset + 4;
        var gifEnd = gifStart + (qwc << 4);
        if (gifEnd > data.Length || qwc == 0 || gifStart + 16 > data.Length)
            return result;

        var gifLo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart));
        var nloop = (int)(gifLo & 0x7FFF);
        var flg = (int)((gifLo >> 58) & 3);
        var nreg = (int)((gifLo >> 60) & 0xF);
        if (nreg == 0) nreg = 16;
        var gifHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart + 8));
        if (flg != 0 || nreg != 1 || (gifHi & 0xFF) != 0x0E)
            return result;

        for (var i = 0; i < nloop; i++)
        {
            var offset = gifStart + 16 + i * 16;
            if (offset + 16 > data.Length)
                break;

            var dataVal = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            var regHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8));
            var regAddr = (int)(regHi & 0xFF);

            switch (regAddr)
            {
                case 0x06:
                    result.Tex0Cbp = (uint)((dataVal >> 37) & 0x3FFF);
                    break;
                case 0x42:
                    result.Alpha1 = dataVal;
                    break;
                case 0x47:
                    if ((dataVal & 1) != 0)
                        result.AlphaRef = (byte)((dataVal >> 4) & 0xFF);
                    break;
            }
        }

        return result;
    }

    internal static SetupMappingInfo BuildSetupMappingInfo(
        byte[] data,
        byte[]? companionTexData,
        IReadOnlyList<EntryRecord> entries,
        IReadOnlyList<int> setupStarts)
    {
        var rawDirectOffsets = ThawPs2SkinVifLayout.FindRawDirectOffsets(data);
        var setupSectionAlphaRefs = BuildSectionAlphaRefMapping(data, setupStarts);

        if (HasLeadingRawDirectPreamble(rawDirectOffsets, setupStarts))
        {
            var rawSectionTextures = BuildSectionTextureMapping(data, companionTexData, rawDirectOffsets);
            if (rawSectionTextures != null && rawSectionTextures.Count > 0)
            {
                var rawSectionEntryIndices =
                    BuildSectionEntryIndices(entries, rawDirectOffsets.Count, rawSectionTextures);
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
            var setupEntryIndices =
                BuildSetupEntryIndices(entries, setupStarts.Count, sectionTextures, setupSectionAlphaRefs);
            var entryTextureOverrides = BuildEntryTextureOverrides(sectionTextures, setupEntryIndices, 1);
            var entryAlphaRefs = BuildEntryAlphaRefOverrides(setupSectionAlphaRefs, setupEntryIndices, 1);
            return new SetupMappingInfo(setupEntryIndices, entryTextureOverrides, entryAlphaRefs);
        }

        var identitySetupEntryIndices = BuildIdentitySetupEntryIndices(setupStarts.Count + 1, entries.Count);
        var identityAlphaRefs = BuildEntryAlphaRefOverrides(setupSectionAlphaRefs, identitySetupEntryIndices, 1);
        return new SetupMappingInfo(identitySetupEntryIndices, null, identityAlphaRefs);
    }

    internal static byte[]? TryLoadCompanionTexData(string filePath)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (directory == null)
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

        var texPath = CompanionSearch.FindCompanion(directory, stem, [".tex.ps2"], ["TEX", "Textures"]);
        return texPath != null && File.Exists(texPath)
            ? File.ReadAllBytes(texPath)
            : null;
    }

    private static Dictionary<int, int>? BuildSectionAlphaRefMapping(
        byte[] data,
        IReadOnlyList<int> directOffsets)
    {
        if (directOffsets.Count == 0)
            return null;

        var mapping = new Dictionary<int, int>();
        for (var sectionIndex = 0; sectionIndex < directOffsets.Count; sectionIndex++)
        {
            var regs = ParseDirectBlockRegisters(data, directOffsets[sectionIndex]);
            if (regs.AlphaRef > 0)
                mapping[sectionIndex] = regs.AlphaRef;
        }

        return mapping.Count > 0 ? mapping : null;
    }

    private static Dictionary<int, uint>? BuildSectionTextureMapping(
        byte[] data,
        byte[]? companionTexData,
        IReadOnlyList<int> directOffsets)
    {
        if (companionTexData is null || directOffsets.Count == 0)
            return null;

        var tbpCbpToChecksum = BuildTbpCbpMap(companionTexData);
        if (tbpCbpToChecksum.Count == 0)
            return null;

        var mapping = new Dictionary<int, uint>();
        for (var sectionIndex = 0; sectionIndex < directOffsets.Count; sectionIndex++)
        {
            var tex0 = ExtractTex0FromDirect(data, directOffsets[sectionIndex]);
            if (tex0 is null)
                continue;

            var (tbp, cbp) = tex0.Value;
            if (tbpCbpToChecksum.TryGetValue((tbp, cbp), out var textureChecksum))
                mapping[sectionIndex] = textureChecksum;
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

    private static (uint Tbp, uint Cbp)? ExtractTex0FromDirect(byte[] data, int directOffset)
    {
        if (directOffset + 4 > data.Length)
            return null;

        var qwc = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(directOffset));
        var gifStart = directOffset + 4;
        var gifEnd = gifStart + (qwc << 4);
        if (gifEnd > data.Length || qwc == 0 || gifStart + 16 > data.Length)
            return null;

        var gifLo = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart));
        var nloop = (int)(gifLo & 0x7FFF);
        var flg = (int)((gifLo >> 58) & 3);
        var nreg = (int)((gifLo >> 60) & 0xF);
        if (nreg == 0) nreg = 16;
        var gifHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(gifStart + 8));
        if (flg != 0 || nreg != 1 || (gifHi & 0xFF) != 0x0E)
            return null;

        for (var i = 0; i < nloop; i++)
        {
            var offset = gifStart + 16 + i * 16;
            if (offset + 16 > data.Length)
                break;

            var dataVal = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            var regHi = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset + 8));
            if ((regHi & 0xFF) == 0x06)
            {
                var tbp = (uint)(dataVal & 0x3FFF);
                var cbp = (uint)((dataVal >> 37) & 0x3FFF);
                return (tbp, cbp);
            }
        }

        return null;
    }

    private static Dictionary<(uint, uint), uint> BuildTbpCbpMap(byte[] texData)
    {
        var map = new Dictionary<(uint, uint), uint>();
        if (texData.Length < 0x40)
            return map;

        var version = BitConverter.ToUInt16(texData, 0);
        if (version != 6)
            return map;

        var off1 = (int)BitConverter.ToUInt32(texData, 8);
        if (off1 <= 0x40 || off1 >= texData.Length)
            return map;

        for (var off = 0x40; off + 8 <= off1; off += 8)
        {
            var value = BitConverter.ToUInt64(texData, off);
            var tbp = (uint)(value & 0x3FFF);
            var tbw = (uint)((value >> 14) & 0x3F);
            var psm = (uint)((value >> 20) & 0x3F);
            var tw = (int)((value >> 26) & 0xF);
            var th = (int)((value >> 30) & 0xF);

            if (!Ps2TexPixelDecoder.IsValidPsm(psm)) continue;
            if (tw < 1 || tw > 10 || th < 1 || th > 10) continue;
            if (tbp < 0x2BC0 || tbw < 1) continue;

            var checksumOffset = off - 0x10;
            if (checksumOffset < 0x40) continue;
            var checksum = BitConverter.ToUInt32(texData, checksumOffset);
            if (checksum <= 0xFFFF) continue;

            var cbp = (uint)((value >> 37) & 0x3FFF);
            map[(tbp, cbp)] = checksum;
        }

        return map;
    }

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
}
