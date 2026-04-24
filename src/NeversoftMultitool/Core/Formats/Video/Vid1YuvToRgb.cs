namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     YUV 4:2:0 planar → RGB24 (row-major, top-down) conversion using
///     BT.601 studio-range coefficients.
/// </summary>
internal static class Vid1YuvToRgb
{
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

        var chromaWidth = width / 2;

        for (var y = 0; y < height; y++)
        {
            var cyRow = y >> 1;
            var lumaRow = y * width;
            var rgbRow = lumaRow * 3;

            for (var x = 0; x < width; x++)
            {
                var cxCol = x >> 1;
                int yVal = lumaPlane[lumaRow + x];
                int cb = cbPlane[(cyRow * chromaWidth) + cxCol] - 128;
                int cr = crPlane[(cyRow * chromaWidth) + cxCol] - 128;

                // BT.601 studio-range:
                //   C = Y - 16, D = Cb - 128, E = Cr - 128
                //   R = (298*C + 409*E + 128) >> 8
                //   G = (298*C - 100*D - 208*E + 128) >> 8
                //   B = (298*C + 516*D + 128) >> 8
                // This maps encoded black (Y=16, Cb/Cr=128) to RGB 0.
                var c = yVal - 16;
                var r = ((298 * c) + (409 * cr) + 128) >> 8;
                var g = ((298 * c) - (100 * cb) - (208 * cr) + 128) >> 8;
                var b = ((298 * c) + (516 * cb) + 128) >> 8;

                rgb[rgbRow + (x * 3)] = ClampByte(r);
                rgb[rgbRow + (x * 3) + 1] = ClampByte(g);
                rgb[rgbRow + (x * 3) + 2] = ClampByte(b);
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

        var chromaWidth = width / 2;

        for (var y = 0; y < height; y++)
        {
            var cyRow = y >> 1;
            var lumaRow = y * width;
            var bgraRow = lumaRow * 4;

            for (var x = 0; x < width; x++)
            {
                var cxCol = x >> 1;
                int yVal = lumaPlane[lumaRow + x];
                int cb = cbPlane[(cyRow * chromaWidth) + cxCol] - 128;
                int cr = crPlane[(cyRow * chromaWidth) + cxCol] - 128;

                var c = yVal - 16;
                var r = ((298 * c) + (409 * cr) + 128) >> 8;
                var g = ((298 * c) - (100 * cb) - (208 * cr) + 128) >> 8;
                var b = ((298 * c) + (516 * cb) + 128) >> 8;

                var offset = bgraRow + (x * 4);
                bgra[offset] = ClampByte(b);
                bgra[offset + 1] = ClampByte(g);
                bgra[offset + 2] = ClampByte(r);
                bgra[offset + 3] = 0xFF;
            }
        }
    }

    private static byte ClampByte(int value)
    {
        if (value < 0) return 0;
        if (value > 255) return 255;
        return (byte)value;
    }
}
