using NeversoftMultitool.Core.Formats.Psx;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexFile;
using static NeversoftMultitool.Core.Formats.Ps2Scene.ThawZoneTexHeaderSourceResolver;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawZoneTexHeaderClutSupport
{
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
            var bias = ThawZoneTexHeaderDataSupport.SelectHeaderDataSlotBias(fileData, dataBaseOffset, dataOffsetBias, entry) ?? 0;
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

            foreach (var entry in orderedGroup.Select(static context => context.Entry))
            {
                if (!ShouldPreferSameCbpEntropyClut(entry))
                    continue;

                map[entry] = new HeaderClutSourceContext(bestEntropy.Entry, bestEntropy.Bias);
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

    internal static byte[]? TryReadNearestPsmt4UploadClutBytes(
        IReadOnlyList<VramUpload> uploads,
        ulong tex0,
        int? matchedUploadIndex,
        int clutBytes)
    {
        if (!matchedUploadIndex.HasValue)
            return null;

        var cbp = GetTex0Cbp(tex0);
        var cpsm = GetTex0Cpsm(tex0);
        for (var i = matchedUploadIndex.Value - 1; i >= 0; i--)
        {
            var upload = uploads[i];
            if (upload.Dbp != cbp || upload.Dpsm != cpsm)
                continue;

            if (upload.Width != 8 || upload.Height != 2 || upload.PixelData.Length < clutBytes)
                continue;

            return upload.PixelData.AsSpan(0, clutBytes).ToArray();
        }

        return null;
    }

    internal static byte[] ReorderClutPsmt4ForLayout(byte[] clut, uint cpsm, uint layoutMode)
    {
        if (layoutMode == 0x02000001 && cpsm == Ps2TexPixelDecoder.PSMCT16)
            return clut;
        if (layoutMode == 0x02000005 && cpsm == Ps2TexPixelDecoder.PSMCT32)
            return clut;

        return cpsm switch
        {
            Ps2TexPixelDecoder.PSMCT16 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 2),
            Ps2TexPixelDecoder.PSMCT32 => ThawZoneTexHeaderLayoutSupport.ReorderClut(clut, ClutTableT32I4, 4),
            _ => clut
        };
    }
}
