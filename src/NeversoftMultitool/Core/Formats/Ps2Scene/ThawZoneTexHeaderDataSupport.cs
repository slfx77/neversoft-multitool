using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexCoreDecoder;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexHeaderSourceResolver;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexTextureCache;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexHeaderDataSupport
{
    internal static Ps2Texture? DecodeBestHeaderDataSlot(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry,
        IReadOnlyDictionary<ZoneTexHeaderEntry, HeaderClutSourceContext>? sameCbpClutSources = null)
    {
        var candidate = SelectHeaderDataSlotCandidate(fileData, uploads, dataBaseOffset, dataOffsetBias, entry);
        if (!candidate.HasValue)
            return null;
        if (sameCbpClutSources == null || !sameCbpClutSources.TryGetValue(entry, out var clutSource))
            return candidate.Value.Texture;
        if (clutSource.Entry == entry && clutSource.Bias == candidate.Value.Bias)
            return candidate.Value.Texture;

        return DecodeFromHeaderDataSlot(
            fileData,
            uploads,
            dataBaseOffset,
            candidate.Value.Bias,
            entry,
            out _,
            false,
            clutSource);
    }

    internal static int? SelectHeaderDataSlotBias(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry)
    {
        var candidate = SelectHeaderDataSlotCandidate(fileData, [], dataBaseOffset, dataOffsetBias, entry);
        return candidate?.Bias;
    }

    internal static HeaderDataSlotCandidate? SelectHeaderDataSlotCandidate(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry)
    {
        if (dataOffsetBias <= 0)
            return DecodeHeaderDataSlotCandidate(fileData, uploads, dataBaseOffset, 0, entry);

        VramUpload? matchedUpload = null;
        if (FindUploadIndexForOffset(uploads, entry.UploadOffset) is var uploadIndex && uploadIndex.HasValue)
            matchedUpload = uploads[uploadIndex.Value];

        // Zero-offset entries still point at the literal slot base. For non-zero offsets, the files
        // mix both conventions, so try both starts and pick the one whose palette bytes look more
        // like authored CLUT data rather than misaligned pixel/index payload.
        int[] candidateBiases = entry.DataOffset == 0
            ? [0, dataOffsetBias]
            : [dataOffsetBias, 0];

        HeaderDataSlotCandidate? bestCandidate = null;
        foreach (var candidateBias in candidateBiases.Distinct())
        {
            var candidate = DecodeHeaderDataSlotCandidate(fileData, uploads, dataBaseOffset, candidateBias, entry);
            if (candidate == null)
                continue;

            if (bestCandidate == null || candidate.Value.Score > bestCandidate.Value.Score)
                bestCandidate = candidate;
        }

        if (bestCandidate.HasValue
            && bestCandidate.Value.Bias == 0
            && ThawZoneTexHeaderLayoutSupport.ShouldPreferPsmt4BiasedAutoSlotCandidate(entry, dataOffsetBias, matchedUpload)
            && DecodeHeaderDataSlotCandidate(fileData, uploads, dataBaseOffset, dataOffsetBias, entry, true) is
                { } biasedCandidate)
            return biasedCandidate;

        // Bias=32 selected but nobias consistently wins for this PSMT4 128×128 layout subgroup
        if (bestCandidate.HasValue
            && bestCandidate.Value.Bias > 0
            && ThawZoneTexHeaderLayoutSupport.ShouldPreferNobiasForBias32Bucket(entry, dataOffsetBias, matchedUpload)
            && DecodeHeaderDataSlotCandidate(fileData, uploads, dataBaseOffset, 0, entry) is { } nobiasCandidate)
            return nobiasCandidate;

        return bestCandidate;
    }

    internal static HeaderDataSlotCandidate? DecodeHeaderDataSlotCandidate(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry,
        bool forceAutoPsmt4SlotDecode = false)
    {
        var texture = DecodeFromHeaderDataSlot(fileData, uploads, dataBaseOffset, dataOffsetBias, entry,
            out var paletteScore, forceAutoPsmt4SlotDecode);
        return texture?.Pixels == null
            ? null
            : new HeaderDataSlotCandidate(texture, paletteScore, dataOffsetBias);
    }

    internal static Ps2Texture? DecodeFromHeaderDataSlot(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry)
    {
        return DecodeFromHeaderDataSlot(fileData, [], dataBaseOffset, dataOffsetBias, entry, out _, false);
    }

    internal static Ps2Texture? DecodeFromHeaderDataSlot(
        ReadOnlySpan<byte> fileData,
        IReadOnlyList<VramUpload> uploads,
        int dataBaseOffset,
        int dataOffsetBias,
        ZoneTexHeaderEntry entry,
        out double paletteScore,
        bool forceAutoPsmt4SlotDecode,
        HeaderClutSourceContext? clutSource = null)
    {
        paletteScore = double.NegativeInfinity;
        var tex0 = entry.Tex0;
        var psm = (uint)((tex0 >> 20) & 0x3F);
        var cpsm = (uint)((tex0 >> 51) & 0xF);
        var width = 1 << (int)((tex0 >> 26) & 0xF);
        var height = 1 << (int)((tex0 >> 30) & 0xF);

        if (!Ps2TexPixelDecoder.IsValidPsm(psm))
            return null;

        var slotOffset = (long)dataBaseOffset + dataOffsetBias + entry.DataOffset;
        var slotLength = (long)entry.PaletteBytes + entry.DataSize;
        if (slotOffset < 0 || slotLength <= 0 || slotOffset + slotLength > fileData.Length)
            return null;

        var slot = fileData.Slice((int)slotOffset, (int)slotLength);
        var inferredPixelBytes = width * height * Ps2TexPixelDecoder.GetBitsPerPixel(psm) / 8;
        var pixelBytes = entry.BasePixelBytes != 0 ? (int)entry.BasePixelBytes : inferredPixelBytes;
        if (pixelBytes <= 0)
            return null;

        VramUpload? matchedUpload = null;
        if (FindUploadIndexForOffset(uploads, entry.UploadOffset) is var uploadIndex && uploadIndex.HasValue)
            matchedUpload = uploads[uploadIndex.Value];

        var useAutoPsmt4SlotDecode = forceAutoPsmt4SlotDecode;

        byte[]? clut = null;
        var paletteEntries = Ps2TexPixelDecoder.GetPaletteSize(psm);
        var dataPos = 0;

        if (paletteEntries > 0)
        {
            var clutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(cpsm);
            var clutBytes = paletteEntries * clutBpp / 8;
            if (entry.PaletteBytes < clutBytes || dataPos + clutBytes > slot.Length)
                return null;

            paletteScore = ScorePaletteBytes(slot.Slice(dataPos, clutBytes));
            dataPos += (int)entry.PaletteBytes;

            clut = psm == Ps2TexPixelDecoder.PSMT4 && clutSource.HasValue
                ? TryReadHeaderClutBytes(fileData, dataBaseOffset, clutSource.Value, clutBytes)
                : slot.Slice(0, clutBytes).ToArray();
            if (clut == null)
                return null;

            if (psm == Ps2TexPixelDecoder.PSMT8)
            {
                UnswizzleClutCsm1(clut, clutBpp / 8);
            }
            else if (psm == Ps2TexPixelDecoder.PSMT4)
            {
                var clutLayout = clutSource?.Entry.LayoutMode ?? entry.LayoutMode;
                clut = useAutoPsmt4SlotDecode
                    ? cpsm switch
                    {
                        Ps2TexPixelDecoder.PSMCT16 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 2),
                        Ps2TexPixelDecoder.PSMCT32 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 4),
                        _ => clut
                    }
                    : ReorderClutPsmt4ForLayout(clut, cpsm, clutLayout);
            }
        }

        if (dataPos + pixelBytes > slot.Length)
            return null;

        var texData = slot.Slice(dataPos, pixelBytes);
        if (psm == Ps2TexPixelDecoder.PSMT8 && width >= height)
            texData = Ps2TexSwizzle.UnswizzlePsmt8(texData, width, height);
        else if (psm == Ps2TexPixelDecoder.PSMT4)
        {
            var unswizzled = matchedUpload.HasValue
                ? Ps2TexSwizzle.UnswizzlePsmt4WithUploadDpsm(texData, width, height, matchedUpload.Value.Dpsm)
                : Ps2TexSwizzle.UnswizzlePsmt4(texData, width, height);
            texData = !useAutoPsmt4SlotDecode && ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotLayoutTransform(entry.LayoutMode, dataOffsetBias)
                ? ThawZoneTexHeaderLayoutSupport.TransformPsmt4SlotBlocksForLayout(unswizzled, width, height, entry.LayoutMode)
                : unswizzled;

            if (ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotTileTransform(entry, dataOffsetBias, matchedUpload))
            {
                texData = ThawZoneTexHeaderLayoutSupport.TransformPsmt4LinearBlocks(
                    texData,
                    width,
                    height,
                    16,
                    16,
                    Layout02000005TilePermutation,
                    0x05);
            }
        }

        var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        return pixels == null
            ? null
            : new Ps2Texture(entry.Checksum, width, height, psm, cpsm, pixels);
    }

    internal static double ScorePaletteBytes(ReadOnlySpan<byte> paletteBytes)
    {
        if (paletteBytes.IsEmpty)
            return 0;

        Span<int> histogram = stackalloc int[256];
        foreach (var value in paletteBytes)
            histogram[value]++;

        double entropy = 0;
        foreach (var count in histogram)
        {
            if (count == 0)
                continue;

            var probability = (double)count / paletteBytes.Length;
            entropy -= probability * Math.Log2(probability);
        }

        return entropy;
    }

    internal static Dictionary<ZoneTexHeaderEntry, HeaderClutSourceContext> BuildSameCbpClutSourceMap(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        int dataOffsetBias,
        IReadOnlyList<ZoneTexHeaderEntry> headerEntries)
    {
        var contexts = new List<HeaderClutEntropyContext>(headerEntries.Count);
        foreach (var entry in headerEntries)
        {
            var bias = SelectHeaderDataSlotBias(fileData, dataBaseOffset, dataOffsetBias, entry) ?? 0;
            contexts.Add(new HeaderClutEntropyContext(
                entry,
                bias,
                ComputeHeaderClutEntropy(fileData, dataBaseOffset, entry, bias)));
        }

        var map = new Dictionary<ZoneTexHeaderEntry, HeaderClutSourceContext>();
        foreach (var group in contexts.GroupBy(context =>
                     (GetTex0Cbp(context.Entry.Tex0), GetTex0Cpsm(context.Entry.Tex0))))
        {
            var orderedGroup = group.OrderBy(static context => context.Entry.DataOffset).ToList();
            if (orderedGroup.Count <= 1)
                continue;

            var bestEntropy = orderedGroup
                .OrderByDescending(static context => context.ClutEntropy)
                .ThenBy(static context => context.Entry.DataOffset)
                .First();

            foreach (var context in orderedGroup)
            {
                if (!ShouldPreferSameCbpEntropyClut(context.Entry))
                    continue;

                map[context.Entry] = new HeaderClutSourceContext(bestEntropy.Entry, bestEntropy.Bias);
            }
        }

        return map;
    }

    internal static bool ShouldPreferSameCbpEntropyClut(ZoneTexHeaderEntry entry)
    {
        if (GetTex0Psm(entry.Tex0) != Ps2TexPixelDecoder.PSMT4)
            return false;

        return entry.LayoutMode == 0x02000005;
    }

    internal static double ComputeHeaderClutEntropy(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        ZoneTexHeaderEntry entry,
        int selectedBias)
    {
        if (GetTex0Psm(entry.Tex0) != Ps2TexPixelDecoder.PSMT4)
            return double.NegativeInfinity;

        var cpsm = GetTex0Cpsm(entry.Tex0);
        var clutBytes = 16 * Ps2TexPixelDecoder.GetBitsPerPixel(cpsm) / 8;
        if (entry.PaletteBytes < clutBytes)
            return double.NegativeInfinity;

        var slotOffset = (long)dataBaseOffset + selectedBias + entry.DataOffset;
        if (slotOffset < 0 || slotOffset + clutBytes > fileData.Length)
            return double.NegativeInfinity;

        return ScorePaletteBytes(fileData.Slice((int)slotOffset, clutBytes));
    }

    internal static byte[]? TryReadHeaderClutBytes(
        ReadOnlySpan<byte> fileData,
        int dataBaseOffset,
        HeaderClutSourceContext clutSource,
        int clutBytes)
    {
        var slotOffset = (long)dataBaseOffset + clutSource.Bias + clutSource.Entry.DataOffset;
        if (slotOffset < 0 || slotOffset + clutBytes > fileData.Length)
            return null;
        if (clutSource.Entry.PaletteBytes < clutBytes)
            return null;

        return fileData.Slice((int)slotOffset, clutBytes).ToArray();
    }

    internal static byte[] ReorderClutPsmt4ForLayout(byte[] clut, uint cpsm, uint layoutMode)
    {
        if (layoutMode == 0x02000001 && cpsm == Ps2TexPixelDecoder.PSMCT16)
            return clut;
        if (layoutMode == 0x02000005 && cpsm == Ps2TexPixelDecoder.PSMCT32)
            return clut;

        return cpsm switch
        {
            // Packed slot data stores CT16 CLUTs as 16 compact entries rather than the
            // full 32-halfword GS block, so the logical reorder matches the CT32 16-entry permutation.
            Ps2TexPixelDecoder.PSMCT16 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 2),
            Ps2TexPixelDecoder.PSMCT32 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 4),
            _ => clut
        };
    }
}
