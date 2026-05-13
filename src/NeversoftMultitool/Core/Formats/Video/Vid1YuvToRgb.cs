namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     YUV 4:2:0 planar → RGB24 (row-major, top-down) conversion using
///     BT.601 studio-range coefficients.
/// </summary>
internal static class Vid1YuvToRgb
{
    private static readonly int[] YContribution = BuildContributionTable(static value => 298 * (value - 16));
    private static readonly int[] CbBlueContribution = BuildContributionTable(static value => 516 * (value - 128));
    private static readonly int[] CbGreenContribution = BuildContributionTable(static value => -100 * (value - 128));
    private static readonly int[] CrRedContribution = BuildContributionTable(static value => 409 * (value - 128));
    private static readonly int[] CrGreenContribution = BuildContributionTable(static value => -208 * (value - 128));

    /// <summary>
    ///     Convert YUV 4:2:0 planes to a row-major RGB24 buffer.
    ///     Chroma is upsampled by pixel-replicate (nearest-neighbor).
    /// </summary>
    public static byte[] Convert(
        byte[] lumaPlane, byte[] cbPlane, byte[] crPlane,
        int width, int height)
    {
        var rgb = new byte[width * height * 3];
        ConvertToRgb(lumaPlane, cbPlane, crPlane, width, height, rgb);
        return rgb;
    }

    public static void ConvertToRgb(
        byte[] lumaPlane, byte[] cbPlane, byte[] crPlane,
        int width, int height,
        Span<byte> rgb)
    {
        if (rgb.Length < width * height * 3)
            throw new ArgumentException("RGB destination is too small", nameof(rgb));

        var chromaWidth = width >> 1;

        for (var y = 0; y < height; y += 2)
        {
            var hasSecondRow = y + 1 < height;
            var chromaRow = (y >> 1) * chromaWidth;
            var lumaRow0 = y * width;
            var lumaRow1 = lumaRow0 + width;
            var rgbRow0 = lumaRow0 * 3;
            var rgbRow1 = rgbRow0 + width * 3;

            for (var x = 0; x < width; x += 2)
            {
                var chromaIndex = chromaRow + (x >> 1);
                var cb = cbPlane[chromaIndex];
                var cr = crPlane[chromaIndex];

                var redBias = CrRedContribution[cr] + 128;
                var greenBias = CbGreenContribution[cb] + CrGreenContribution[cr] + 128;
                var blueBias = CbBlueContribution[cb] + 128;

                WriteRgbPixel(rgb, rgbRow0 + x * 3, YContribution[lumaPlane[lumaRow0 + x]], redBias, greenBias,
                    blueBias);

                if (x + 1 < width)
                {
                    WriteRgbPixel(
                        rgb,
                        rgbRow0 + (x + 1) * 3,
                        YContribution[lumaPlane[lumaRow0 + x + 1]],
                        redBias,
                        greenBias,
                        blueBias);
                }

                if (!hasSecondRow)
                    continue;

                WriteRgbPixel(rgb, rgbRow1 + x * 3, YContribution[lumaPlane[lumaRow1 + x]], redBias, greenBias,
                    blueBias);

                if (x + 1 < width)
                {
                    WriteRgbPixel(
                        rgb,
                        rgbRow1 + (x + 1) * 3,
                        YContribution[lumaPlane[lumaRow1 + x + 1]],
                        redBias,
                        greenBias,
                        blueBias);
                }
            }
        }
    }

    public static void ConvertToBgra(
        byte[] lumaPlane, byte[] cbPlane, byte[] crPlane,
        int width, int height,
        Span<byte> bgra)
    {
        if (bgra.Length < width * height * 4)
            throw new ArgumentException("BGRA destination is too small", nameof(bgra));

        var chromaWidth = width >> 1;

        for (var y = 0; y < height; y += 2)
        {
            var hasSecondRow = y + 1 < height;
            var chromaRow = (y >> 1) * chromaWidth;
            var lumaRow0 = y * width;
            var lumaRow1 = lumaRow0 + width;
            var bgraRow0 = lumaRow0 * 4;
            var bgraRow1 = bgraRow0 + width * 4;

            for (var x = 0; x < width; x += 2)
            {
                var chromaIndex = chromaRow + (x >> 1);
                var cb = cbPlane[chromaIndex];
                var cr = crPlane[chromaIndex];

                var redBias = CrRedContribution[cr] + 128;
                var greenBias = CbGreenContribution[cb] + CrGreenContribution[cr] + 128;
                var blueBias = CbBlueContribution[cb] + 128;

                WriteBgraPixel(bgra, bgraRow0 + x * 4, YContribution[lumaPlane[lumaRow0 + x]], redBias, greenBias,
                    blueBias);

                if (x + 1 < width)
                {
                    WriteBgraPixel(
                        bgra,
                        bgraRow0 + (x + 1) * 4,
                        YContribution[lumaPlane[lumaRow0 + x + 1]],
                        redBias,
                        greenBias,
                        blueBias);
                }

                if (!hasSecondRow)
                    continue;

                WriteBgraPixel(bgra, bgraRow1 + x * 4, YContribution[lumaPlane[lumaRow1 + x]], redBias, greenBias,
                    blueBias);

                if (x + 1 < width)
                {
                    WriteBgraPixel(
                        bgra,
                        bgraRow1 + (x + 1) * 4,
                        YContribution[lumaPlane[lumaRow1 + x + 1]],
                        redBias,
                        greenBias,
                        blueBias);
                }
            }
        }
    }

    private static int[] BuildContributionTable(Func<int, int> transform)
    {
        var table = new int[256];
        for (var i = 0; i < table.Length; i++)
            table[i] = transform(i);

        return table;
    }

    private static void WriteRgbPixel(Span<byte> rgb, int offset, int yContribution, int redBias, int greenBias,
        int blueBias)
    {
        rgb[offset] = ClampByte((yContribution + redBias) >> 8);
        rgb[offset + 1] = ClampByte((yContribution + greenBias) >> 8);
        rgb[offset + 2] = ClampByte((yContribution + blueBias) >> 8);
    }

    private static void WriteBgraPixel(Span<byte> bgra, int offset, int yContribution, int redBias, int greenBias,
        int blueBias)
    {
        bgra[offset] = ClampByte((yContribution + blueBias) >> 8);
        bgra[offset + 1] = ClampByte((yContribution + greenBias) >> 8);
        bgra[offset + 2] = ClampByte((yContribution + redBias) >> 8);
        bgra[offset + 3] = 0xFF;
    }

    private static byte ClampByte(int value)
    {
        if ((uint)value <= byte.MaxValue)
            return (byte)value;

        return value < 0 ? byte.MinValue : byte.MaxValue;
    }
}
