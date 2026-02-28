namespace NeversoftMultitool.Core.Formats.Psx;

public enum ColorFormat
{
    Argb1555,
    Rgb565,
    Argb4444
}

public static class ColorHelpers
{
    // ARGB1555: alpha is either 0 or 128
    private static readonly ColorParams Argb1555Params = new(
        0x7C00, 0x3E0, 0x1F, 0x8000,
        31, 31, 31, 1,
        15, 10, 5);

    // RGB565: no translucent
    private static readonly ColorParams Rgb565Params = new(
        0xF800, 0x7E0, 0x1F, 0,
        31, 63, 31, 0,
        16, 11, 5);

    // ARGB4444: translucent alpha 0-255
    private static readonly ColorParams Argb4444Params = new(
        0xF00, 0xF0, 0xF, 0xF000,
        15, 15, 15, 15,
        12, 8, 4);

    /// <summary>
    ///     Converts a 16-bit PS1 color to RGBA bytes.
    /// </summary>
    public static void Ps1To32Bpp(ushort color, Span<byte> rgba)
    {
        var r = color & 0x1F;
        var g = (color >> 5) & 0x1F;
        var b = (color >> 10) & 0x1F;

        // Fully transparent (magenta)
        if (r == 31 && g == 0 && b == 31)
        {
            rgba[0] = 0;
            rgba[1] = 0;
            rgba[2] = 0;
            rgba[3] = 0;
            return;
        }

        rgba[0] = (byte)(r * 255 / 31);
        rgba[1] = (byte)(g * 255 / 31);
        rgba[2] = (byte)(b * 255 / 31);
        rgba[3] = 255;
    }

    /// <summary>
    ///     Gets the color format parameters for a 16-bit texture.
    /// </summary>
    public static ColorFormat Get16BppColorFormat(uint pixelFormat)
    {
        var formatBits = pixelFormat & 0xF;
        return formatBits switch
        {
            0 => ColorFormat.Argb1555,
            1 => ColorFormat.Rgb565,
            _ => ColorFormat.Argb4444
        };
    }

    /// <summary>
    ///     Converts a 16-bit color to RGBA bytes.
    /// </summary>
    public static void Convert16BppTo32Bpp(ushort color, ColorFormat format, Span<byte> rgba)
    {
        var p = format switch
        {
            ColorFormat.Argb1555 => Argb1555Params,
            ColorFormat.Rgb565 => Rgb565Params,
            ColorFormat.Argb4444 => Argb4444Params,
            _ => Argb1555Params
        };

        var r = (color & p.RedMask) >> p.RedShift;
        var g = (color & p.GreenMask) >> p.GreenShift;
        var b = color & p.BlueMask;
        var a = (color & p.AlphaMask) >> p.AlphaShift;

        rgba[0] = (byte)(r * 255 / p.RedMax);
        rgba[1] = (byte)(g * 255 / p.GreenMax);
        rgba[2] = (byte)(b * 255 / p.BlueMax);
        rgba[3] = p.AlphaMax == 0 ? (byte)255 : (byte)(a * 255 / p.AlphaMax);
    }

    /// <summary>
    ///     Converts a 16-bit texture buffer to a flat RGBA byte array.
    /// </summary>
    public static byte[] Convert16BitTextureToRgba(uint pixelFormat, int width, int height, ushort[] textureBuffer)
    {
        var format = Get16BppColorFormat(pixelFormat);
        var pixels = new byte[width * height * 4];
        Span<byte> rgba = stackalloc byte[4];

        for (var i = 0; i < textureBuffer.Length && i < width * height; i++)
        {
            Convert16BppTo32Bpp(textureBuffer[i], format, rgba);
            var offset = i * 4;
            pixels[offset] = rgba[0];
            pixels[offset + 1] = rgba[1];
            pixels[offset + 2] = rgba[2];
            pixels[offset + 3] = rgba[3];
        }

        return pixels;
    }

    /// <summary>
    ///     Fixes the pixel data of a texture read by the IO THPS scene code.
    ///     Converts from a list-of-lists pixel format to a flat RGBA byte array.
    /// </summary>
    public static byte[] FixPixelData(int width, int height, byte[] rawPixels)
    {
        // rawPixels is a flat array of [R,G,B,A, R,G,B,A, ...] in row-major order
        // We need to: reverse each row, shift right by 1 pixel, shift rows down by 1, shift column 0 up by 1

        // Step 1: Build initial image with reversed rows
        var image = new byte[height * width * 4];
        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                // Reverse the column order within each row
                var srcIndex = (row * width + (width - 1 - col)) * 4;
                var dstIndex = (row * width + col) * 4;
                if (srcIndex + 3 < rawPixels.Length)
                {
                    image[dstIndex] = rawPixels[srcIndex];
                    image[dstIndex + 1] = rawPixels[srcIndex + 1];
                    image[dstIndex + 2] = rawPixels[srcIndex + 2];
                    image[dstIndex + 3] = rawPixels[srcIndex + 3];
                }
            }
        }

        // Step 2: Shift each row right by 1 pixel
        var shifted = new byte[height * width * 4];
        for (var row = 0; row < height; row++)
        {
            var rowStart = row * width * 4;
            // Last pixel wraps to first position
            Array.Copy(image, rowStart + (width - 1) * 4, shifted, rowStart, 4);
            // Copy rest shifted right
            Array.Copy(image, rowStart, shifted, rowStart + 4, (width - 1) * 4);
        }

        // Step 3: Shift all rows down by 1 (last row wraps to first)
        var shiftedDown = new byte[height * width * 4];
        Array.Copy(shifted, (height - 1) * width * 4, shiftedDown, 0, width * 4);
        Array.Copy(shifted, 0, shiftedDown, width * 4, (height - 1) * width * 4);

        // Step 4: Shift column 0 up by 1 (first pixel wraps to last)
        var result = new byte[height * width * 4];
        Array.Copy(shiftedDown, result, shiftedDown.Length);
        // Extract column 0
        var columnData = new byte[height * 4];
        for (var row = 0; row < height; row++)
        {
            Array.Copy(result, row * width * 4, columnData, row * 4, 4);
        }

        // Shift column up by 1 (row N gets row N+1, last gets first)
        for (var row = 0; row < height; row++)
        {
            var srcRow = (row + 1) % height;
            Array.Copy(columnData, srcRow * 4, result, row * width * 4, 4);
        }

        return result;
    }

    private sealed record ColorParams(
        int RedMask,
        int GreenMask,
        int BlueMask,
        int AlphaMask,
        int RedMax,
        int GreenMax,
        int BlueMax,
        int AlphaMax,
        int AlphaShift,
        int RedShift,
        int GreenShift);
}
