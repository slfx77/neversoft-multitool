namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Un-swizzle algorithms for PS2 GS VRAM tiled pixel layouts (PSMT8, PSMT4).
///     THUG2+ build tools pre-apply Conv8to32/Conv4to32/Conv4to16 swizzle to paletted texture data,
///     storing it in PS2 GS VRAM page layout in the file. These methods reverse that transformation.
///     Ported from THUG source: Gfx/NGPS/NX/texturemem.cpp.
/// </summary>
internal static class Ps2TexSwizzle
{
    private static readonly Dictionary<(int, int), int[]> Conv8to32Cache = [];
    private static readonly Dictionary<(int, int), int[]> Conv4to32Cache = [];
    private static readonly Dictionary<(int, int), int[]> Conv4to16Cache = [];
    private static readonly Lock CacheLock = new();

    internal static byte[] UnswizzlePsmt8(ReadOnlySpan<byte> swizzled, int width, int height)
    {
        var mapping = GetOrBuildMapping(
            Conv8to32Cache,
            width,
            height,
            Ps2TexSwizzlePageMappingBuilder.BuildConv8to32Mapping);
        var output = new byte[width * height];

        for (var filePos = 0; filePos < mapping.Length && filePos < swizzled.Length; filePos++)
        {
            var linearPos = mapping[filePos];
            if (linearPos >= 0 && linearPos < output.Length)
                output[linearPos] = swizzled[filePos];
        }

        return output;
    }

    internal static byte[] UnswizzlePsmt4(ReadOnlySpan<byte> swizzled, int width, int height)
    {
        int[] mapping;
        if (CanConv4to32(width, height))
            mapping = GetOrBuildMapping(
                Conv4to32Cache,
                width,
                height,
                Ps2TexSwizzlePageMappingBuilder.BuildConv4to32Mapping);
        else if (CanConv4to16(width, height))
            mapping = GetOrBuildMapping(
                Conv4to16Cache,
                width,
                height,
                Ps2TexSwizzleVramMappingBuilder.BuildConv4to16Mapping);
        else
            return swizzled.ToArray();

        return ApplyNibbleMapping(swizzled, mapping, width * height);
    }

    internal static byte[] UnswizzlePsmt4WithUploadDpsm(ReadOnlySpan<byte> swizzled, int width, int height,
        uint uploadDpsm)
    {
        int[] mapping;
        if (uploadDpsm == Ps2TexPixelDecoder.PSMCT16 && CanConv4to16(width, height))
            mapping = GetOrBuildMapping(
                Conv4to16Cache,
                width,
                height,
                Ps2TexSwizzleVramMappingBuilder.BuildConv4to16Mapping);
        else if (CanConv4to32(width, height))
            mapping = GetOrBuildMapping(
                Conv4to32Cache,
                width,
                height,
                Ps2TexSwizzlePageMappingBuilder.BuildConv4to32Mapping);
        else if (CanConv4to16(width, height))
            mapping = GetOrBuildMapping(
                Conv4to16Cache,
                width,
                height,
                Ps2TexSwizzleVramMappingBuilder.BuildConv4to16Mapping);
        else
            return swizzled.ToArray();

        return ApplyNibbleMapping(swizzled, mapping, width * height);
    }

    internal static bool CanConv8to32(int width, int height)
    {
        return Ps2TexSwizzlePageMappingBuilder.CanConv8to32(width, height);
    }

    private static int[] BuildConv8to32Mapping(int width, int height)
    {
        return Ps2TexSwizzlePageMappingBuilder.BuildConv8to32Mapping(width, height);
    }

    private static int[] BuildConv4to32Mapping(int width, int height)
    {
        return Ps2TexSwizzlePageMappingBuilder.BuildConv4to32Mapping(width, height);
    }

    private static int[] BuildConv4to16Mapping(int width, int height)
    {
        return Ps2TexSwizzleVramMappingBuilder.BuildConv4to16Mapping(width, height);
    }

    private static bool CanConv4to32(int width, int height)
    {
        return CanConv8to32(width, height);
    }

    private static bool CanConv4to16(int width, int height)
    {
        return Ps2TexSwizzleVramMappingBuilder.CanConv4to16(width, height);
    }

    private static byte[] ApplyNibbleMapping(ReadOnlySpan<byte> swizzled, int[] mapping, int totalNibbles)
    {
        var output = new byte[totalNibbles / 2];

        for (var outNibble = 0; outNibble < mapping.Length; outNibble++)
        {
            var linearNibble = mapping[outNibble];
            if (linearNibble < 0 || linearNibble >= totalNibbles)
                continue;

            var swizzledByte = outNibble / 2;
            var swizzledShift = (outNibble & 1) * 4;
            if (swizzledByte >= swizzled.Length)
                continue;
            var value = (swizzled[swizzledByte] >> swizzledShift) & 0x0F;

            var outputByte = linearNibble / 2;
            var outputShift = (linearNibble & 1) * 4;
            output[outputByte] = (byte)((output[outputByte] & ~(0x0F << outputShift)) | (value << outputShift));
        }

        return output;
    }

    private static int[] GetOrBuildMapping(
        Dictionary<(int, int), int[]> cache,
        int width,
        int height,
        Func<int, int, int[]> buildMapping)
    {
        var key = (width, height);
        lock (CacheLock)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
        }

        var mapping = buildMapping(width, height);

        lock (CacheLock)
        {
            cache.TryAdd(key, mapping);
            return cache[key];
        }
    }
}
