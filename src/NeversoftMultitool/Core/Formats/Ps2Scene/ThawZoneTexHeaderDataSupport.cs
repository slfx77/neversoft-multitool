using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexHeaderClutSupport;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexHeaderSourceResolver;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexTextureCache;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexVramSupport;

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

        byte[]? clut = null;
        var paletteEntries = Ps2TexPixelDecoder.GetPaletteSize(psm);
        var dataPos = 0;

        if (paletteEntries > 0)
        {
            var sourceCpsm = clutSource.HasValue
                ? InferHeaderClutSourceCpsm(
                    clutSource.Value.Entry.PaletteBytes,
                    paletteEntries,
                    GetTex0Cpsm(clutSource.Value.Entry.Tex0))
                : InferHeaderClutSourceCpsm(entry.PaletteBytes, paletteEntries, cpsm);
            var sourceClutBpp = Ps2TexPixelDecoder.GetBitsPerPixel(sourceCpsm);
            var sourceClutBytes = paletteEntries * sourceClutBpp / 8;
            if (entry.PaletteBytes < sourceClutBytes || dataPos + sourceClutBytes > slot.Length)
                return null;

            paletteScore = ScorePaletteBytes(slot.Slice(dataPos, sourceClutBytes));
            dataPos += (int)entry.PaletteBytes;

            clut = psm == Ps2TexPixelDecoder.PSMT4 && clutSource.HasValue
                ? TryReadHeaderClutBytes(fileData, dataBaseOffset, clutSource.Value, sourceClutBytes)
                : slot.Slice(0, sourceClutBytes).ToArray();
            if (clut == null)
                return null;

            var clutLayout = clutSource?.Entry.LayoutMode ?? entry.LayoutMode;
            clut = NormalizeHeaderClutBytes(
                clut,
                sourceCpsm,
                cpsm,
                psm,
                clutLayout,
                forceAutoPsmt4SlotDecode);
            if (clut == null)
                return null;
        }

        if (dataPos + pixelBytes > slot.Length)
            return null;

        var texData = PrepareHeaderPixelBytes(
            slot.Slice(dataPos, pixelBytes),
            width,
            height,
            psm,
            entry,
            dataOffsetBias,
            forceAutoPsmt4SlotDecode,
            matchedUpload);
        if (texData == null)
            return null;

        var pixels = Ps2TexPixelDecoder.DecodePixels(texData, width, height, psm, cpsm, clut);
        return pixels == null
            ? null
            : new Ps2Texture(entry.Checksum, width, height, psm, cpsm, pixels);
    }

    private static byte[]? PrepareHeaderPixelBytes(
        ReadOnlySpan<byte> rawPixelData,
        int width,
        int height,
        uint psm,
        ZoneTexHeaderEntry entry,
        int selectedBias,
        bool forceAutoPsmt4SlotDecode,
        VramUpload? matchedUpload)
    {
        if (Ps2TexPixelDecoder.GetPaletteSize(psm) == 0)
            return rawPixelData.ToArray();

        return psm switch
        {
            Ps2TexPixelDecoder.PSMT8 => PreparePsmt8HeaderPixelBytes(rawPixelData, width, height),
            Ps2TexPixelDecoder.PSMT4 => PreparePsmt4HeaderPixelBytes(
                rawPixelData,
                width,
                height,
                entry,
                selectedBias,
                forceAutoPsmt4SlotDecode,
                matchedUpload),
            _ => rawPixelData.ToArray()
        };
    }

    private static byte[]? PreparePsmt8HeaderPixelBytes(
        ReadOnlySpan<byte> rawPixelData,
        int width,
        int height)
    {
        // The extracted public slot bytes still behave like a GS-layout payload rather than
        // the runtime prepared-source buffer. Keep the public-file unswizzle path here, but
        // use the decompiled 8-bit eligibility gate instead of sharing the 4-bit table.
        if (Ps2TexSwizzle.CanConv8to32(width, height))
            return Ps2TexSwizzle.UnswizzlePsmt8(rawPixelData, width, height);

        return rawPixelData.ToArray();
    }

    private static byte[]? PreparePsmt4HeaderPixelBytes(
        ReadOnlySpan<byte> rawPixelData,
        int width,
        int height,
        ZoneTexHeaderEntry entry,
        int selectedBias,
        bool forceAutoPsmt4SlotDecode,
        VramUpload? matchedUpload)
    {
        // The decompiled high-bit family dispatch applies to the runtime prepared-source buffer,
        // but the extracted public slot bytes still line up with the older GS-layout path plus
        // a small heuristic block-permutation layer more closely. Preserve that public-file
        // baseline until the owner-blob/public-file bridge is resolved.
        var texData = matchedUpload.HasValue
            ? Ps2TexSwizzle.UnswizzlePsmt4WithUploadDpsm(rawPixelData, width, height, matchedUpload.Value.Dpsm)
            : Ps2TexSwizzle.UnswizzlePsmt4(rawPixelData, width, height);

        if (!forceAutoPsmt4SlotDecode &&
            ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotLayoutTransform(entry.LayoutMode, selectedBias))
        {
            texData = ThawZoneTexHeaderLayoutSupport.TransformPsmt4SlotBlocksForLayout(
                texData,
                width,
                height,
                entry.LayoutMode);
        }

        if (ThawZoneTexHeaderLayoutSupport.ShouldApplyPsmt4SlotTileTransform(entry, selectedBias, matchedUpload))
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

        return texData;
    }

    private static uint InferHeaderClutSourceCpsm(uint paletteBytes, int paletteEntries, uint fallbackCpsm)
    {
        var ct16Bytes = paletteEntries * (Ps2TexPixelDecoder.GetBitsPerPixel(Ps2TexPixelDecoder.PSMCT16) / 8);
        var ct32Bytes = paletteEntries * (Ps2TexPixelDecoder.GetBitsPerPixel(Ps2TexPixelDecoder.PSMCT32) / 8);

        return paletteBytes switch
        {
            var bytes when bytes == ct16Bytes => Ps2TexPixelDecoder.PSMCT16,
            var bytes when bytes == ct32Bytes => Ps2TexPixelDecoder.PSMCT32,
            _ => fallbackCpsm
        };
    }

    private static byte[]? NormalizeHeaderClutBytes(
        byte[] clutBytes,
        uint sourceCpsm,
        uint targetCpsm,
        uint psm,
        uint layoutMode,
        bool forceAutoPsmt4SlotDecode)
    {
        var normalized = ConvertHeaderClutStorage(clutBytes, sourceCpsm, targetCpsm);
        if (normalized == null)
            return null;

        if (psm == Ps2TexPixelDecoder.PSMT8)
        {
            var clutEntrySize = Ps2TexPixelDecoder.GetBitsPerPixel(targetCpsm) / 8;
            UnswizzleClutCsm1(normalized, clutEntrySize);
        }
        else if (psm == Ps2TexPixelDecoder.PSMT4)
        {
            // The decompiled runtime prepared-source path does not reorder the CLUT after
            // FUN_001e6818 binds it, but the public extracted slot bytes still behave like
            // the older packed-slot representation rather than that runtime buffer.
            normalized = forceAutoPsmt4SlotDecode
                ? targetCpsm switch
                {
                    Ps2TexPixelDecoder.PSMCT16 =>
                        ThawZoneTexHeaderLayoutSupport.ReorderClut(normalized, ClutTableT32I4, 2),
                    Ps2TexPixelDecoder.PSMCT32 =>
                        ThawZoneTexHeaderLayoutSupport.ReorderClut(normalized, ClutTableT32I4, 4),
                    _ => normalized
                }
                : ThawZoneTexHeaderClutSupport.ReorderClutPsmt4ForLayout(normalized, targetCpsm, layoutMode);
        }

        return normalized;
    }

    private static byte[]? ConvertHeaderClutStorage(byte[] clutBytes, uint sourceCpsm, uint targetCpsm)
    {
        if (sourceCpsm == targetCpsm)
            return clutBytes;

        return (sourceCpsm, targetCpsm) switch
        {
            (Ps2TexPixelDecoder.PSMCT16, Ps2TexPixelDecoder.PSMCT32) => ConvertClut16To32(clutBytes),
            (Ps2TexPixelDecoder.PSMCT32, Ps2TexPixelDecoder.PSMCT16) => ConvertClut32To16(clutBytes),
            _ => null
        };
    }

    private static byte[]? ConvertClut16To32(ReadOnlySpan<byte> clutBytes)
    {
        if ((clutBytes.Length & 1) != 0)
            return null;

        var converted = new byte[clutBytes.Length * 2];
        for (var i = 0; i < clutBytes.Length; i += 2)
        {
            var pixel = (ushort)(clutBytes[i] | (clutBytes[i + 1] << 8));
            var di = i * 2;
            converted[di] = Expand5To8(pixel & 0x1F);
            converted[di + 1] = Expand5To8((pixel >> 5) & 0x1F);
            converted[di + 2] = Expand5To8((pixel >> 10) & 0x1F);
            converted[di + 3] = 0x80;
        }

        return converted;
    }

    private static byte[]? ConvertClut32To16(ReadOnlySpan<byte> clutBytes)
    {
        if ((clutBytes.Length & 3) != 0)
            return null;

        var converted = new byte[clutBytes.Length / 2];
        for (var i = 0; i < clutBytes.Length; i += 4)
        {
            var r = (ushort)(clutBytes[i] >> 3);
            var g = (ushort)(clutBytes[i + 1] >> 3);
            var b = (ushort)(clutBytes[i + 2] >> 3);
            var pixel = (ushort)(0x8000 | r | (g << 5) | (b << 10));
            var di = i / 2;
            converted[di] = (byte)(pixel & 0xFF);
            converted[di + 1] = (byte)(pixel >> 8);
        }

        return converted;
    }

    private static byte Expand5To8(int value)
    {
        return (byte)((value << 3) | (value >> 2));
    }

}
