namespace NeversoftMultitool.Core.Formats.Psx;

public static class MortonCurve
{
    /// <summary>
    ///     Calculates the Morton index for a texture.
    /// </summary>
    public static int Morton(int index, int textureWidth, int textureHeight)
    {
        var xBitPosition = 1;
        var yBitPosition = 1;
        var bitMask = index;
        var interleavedX = 0;
        var interleavedY = 0;

        var msbWidth = MostSignificantBit(textureWidth);
        var msbHeight = MostSignificantBit(textureHeight);
        var iterations = Math.Max(msbWidth, msbHeight) + 1;

        for (var i = 0; i < iterations; i++)
        {
            interleavedX += xBitPosition * (bitMask & 1);
            bitMask >>= 1;
            xBitPosition *= 2;

            interleavedY += yBitPosition * (bitMask & 1);
            bitMask >>= 1;
            yBitPosition *= 2;
        }

        return interleavedY * textureWidth + interleavedX;
    }

    /// <summary>
    ///     Standard 2D Z-order curve interleave.
    ///     Replaces pymorton.interleave(x, y) for VQ textures.
    /// </summary>
    public static int Interleave(int x, int y)
    {
        var result = 0;
        for (var i = 0; i < 16; i++)
        {
            result |= ((x >> i) & 1) << (2 * i);
            result |= ((y >> i) & 1) << (2 * i + 1);
        }

        return result;
    }

    private static int MostSignificantBit(int n)
    {
        if (n <= 0) return 0;
        var bits = 0;
        while (n > 1)
        {
            n >>= 1;
            bits++;
        }

        return bits;
    }
}
