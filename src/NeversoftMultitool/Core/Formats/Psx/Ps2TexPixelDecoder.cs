namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Decodes PS2 GS pixel data (PSMCT32/24/16, PSMT8/4) to RGBA8888 with CLUT lookup
///     and alpha fixup. Used by Ps2TexFile for TEX/IMG texture extraction.
///     Format constants and helpers from THUG source: Gfx/NGPS/NX/gs.h.
/// </summary>
internal static class Ps2TexPixelDecoder
{
    // PS2 GS Pixel Storage Modes (from gs.h)
    internal const uint PSMCT32 = 0x00;
    internal const uint PSMCT24 = 0x01;
    internal const uint PSMCT16 = 0x02;
    internal const uint PSMT8 = 0x13;
    internal const uint PSMT4 = 0x14;

    /// <summary>
    ///     Decodes raw pixel data to RGBA8888, then flips vertically (PS2 stores bottom-up).
    /// </summary>
    internal static byte[]? DecodePixels(ReadOnlySpan<byte> texData, int width, int height,
        uint psm, uint cpsm, byte[]? clut)
    {
        var pixels = new byte[width * height * 4];

        switch (psm)
        {
            case PSMCT32:
                DecodePsmct32(texData, pixels, width, height);
                break;
            case PSMCT24:
                DecodePsmct24(texData, pixels, width, height);
                break;
            case PSMCT16:
                DecodePsmct16(texData, pixels, width, height);
                break;
            case PSMT8:
                if (clut == null) return null;
                DecodePsmt8(texData, pixels, width, height, clut, cpsm);
                break;
            case PSMT4:
                if (clut == null) return null;
                DecodePsmt4(texData, pixels, width, height, clut, cpsm);
                break;
            default:
                return null;
        }

        // PS2 textures are stored bottom-up (sprite.cpp: m_flags |= mINVERTED)
        FlipVertical(pixels, width, height);

        // If every pixel has alpha=0 after decoding, the texture doesn't use alpha --
        // set all to 255 so it doesn't appear fully transparent in PNG output
        FixAllZeroAlpha(pixels);

        return pixels;
    }

    /// <summary>
    ///     Returns the number of palette entries for the given pixel storage mode.
    /// </summary>
    internal static int GetPaletteSize(uint psm)
    {
        return psm switch
        {
            PSMT8 => 256,
            PSMT4 => 16,
            _ => 0
        };
    }

    /// <summary>
    ///     Returns the bits per pixel for the given pixel storage mode.
    /// </summary>
    internal static int GetBitsPerPixel(uint psm)
    {
        return psm switch
        {
            PSMCT32 => 32,
            PSMCT24 => 24,
            PSMCT16 => 16,
            PSMT8 => 8,
            PSMT4 => 4,
            _ => 0
        };
    }

    /// <summary>
    ///     Returns true if the pixel storage mode is a recognized PS2 GS format.
    /// </summary>
    internal static bool IsValidPsm(uint psm)
    {
        return psm is PSMCT32 or PSMCT24 or PSMCT16 or PSMT8 or PSMT4;
    }

    /// <summary>
    ///     Returns a human-readable description of a pixel storage mode.
    /// </summary>
    internal static string DescribePsm(uint psm)
    {
        return psm switch
        {
            PSMCT32 => "32bpp RGBA",
            PSMCT24 => "24bpp RGB",
            PSMCT16 => "16bpp RGBA5551",
            PSMT8 => "8-bit indexed",
            PSMT4 => "4-bit indexed",
            _ => $"Unknown (0x{psm:X2})"
        };
    }

    /// <summary>
    ///     Flips RGBA pixel buffer vertically (swap top and bottom rows).
    /// </summary>
    private static void FlipVertical(byte[] pixels, int width, int height)
    {
        var rowBytes = width * 4;
        var temp = new byte[rowBytes];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            var topOff = top * rowBytes;
            var botOff = bottom * rowBytes;
            Buffer.BlockCopy(pixels, topOff, temp, 0, rowBytes);
            Buffer.BlockCopy(pixels, botOff, pixels, topOff, rowBytes);
            Buffer.BlockCopy(temp, 0, pixels, botOff, rowBytes);
        }
    }

    /// <summary>
    ///     If all alpha values in the buffer are 0, sets all to 255.
    ///     Handles textures that don't use alpha (PS2 material controls blending, not texture alpha).
    /// </summary>
    private static void FixAllZeroAlpha(byte[] pixels)
    {
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 0)
                return; // found non-zero alpha, texture uses alpha channel
        }

        // All alpha = 0 -> force opaque
        for (var i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
    }

    private static void DecodePsmct32(ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 4;
            var di = i * 4;
            dst[di] = src[si]; // R
            dst[di + 1] = src[si + 1]; // G
            dst[di + 2] = src[si + 2]; // B
            dst[di + 3] = ScaleAlpha(src[si + 3]); // A: PS2 0-128 -> 0-255
        }
    }

    private static void DecodePsmct24(ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 3;
            var di = i * 4;
            dst[di] = src[si]; // R
            dst[di + 1] = src[si + 1]; // G
            dst[di + 2] = src[si + 2]; // B
            dst[di + 3] = 255; // A: fully opaque
        }
    }

    private static void DecodePsmct16(ReadOnlySpan<byte> src, byte[] dst, int width, int height)
    {
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 2;
            var pixel = (ushort)(src[si] | (src[si + 1] << 8));
            var di = i * 4;
            // RGB555: xBBBBBGGGGGRRRRR -- alpha bit ignored, always opaque
            // (PS2 GS uses material/register alpha, not per-texel alpha in 16-bit mode)
            dst[di] = (byte)(((pixel & 0x1F) << 3) | ((pixel & 0x1F) >> 2)); // R
            dst[di + 1] = (byte)((((pixel >> 5) & 0x1F) << 3) | (((pixel >> 5) & 0x1F) >> 2)); // G
            dst[di + 2] = (byte)((((pixel >> 10) & 0x1F) << 3) | (((pixel >> 10) & 0x1F) >> 2)); // B
            dst[di + 3] = 255; // A: always opaque
        }
    }

    private static void DecodePsmt8(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut, uint cpsm)
    {
        var clutBpp = GetBitsPerPixel(cpsm) / 8;

        for (var i = 0; i < width * height; i++)
        {
            var colorIndex = src[i];
            var di = i * 4;
            ReadClutEntry(clut, colorIndex, clutBpp, cpsm, dst, di);
        }
    }

    private static void DecodePsmt4(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut, uint cpsm)
    {
        var clutBpp = GetBitsPerPixel(cpsm) / 8;

        for (var i = 0; i < width * height; i++)
        {
            var byteIndex = i >> 1;
            var colorIndex = (i & 1) == 0
                ? src[byteIndex] & 0x0F // low nibble first
                : (src[byteIndex] >> 4) & 0x0F; // high nibble
            var di = i * 4;
            ReadClutEntry(clut, colorIndex, clutBpp, cpsm, dst, di);
        }
    }

    private static void ReadClutEntry(byte[] clut, int index, int clutBpp, uint cpsm, byte[] dst, int di)
    {
        var ci = index * clutBpp;
        if (ci + clutBpp > clut.Length)
        {
            dst[di] = dst[di + 1] = dst[di + 2] = 0;
            dst[di + 3] = 255;
            return;
        }

        if (cpsm == PSMCT32)
        {
            dst[di] = clut[ci]; // R
            dst[di + 1] = clut[ci + 1]; // G
            dst[di + 2] = clut[ci + 2]; // B
            dst[di + 3] = ScaleAlpha(clut[ci + 3]);
        }
        else // PSMCT16
        {
            // Alpha bit ignored for 16-bit CLUT -- always opaque
            // (p_NxTexture.cpp: new_color.a = 0x80 when converting 16->32 bit CLUT)
            var pixel = (ushort)(clut[ci] | (clut[ci + 1] << 8));
            dst[di] = (byte)(((pixel & 0x1F) << 3) | ((pixel & 0x1F) >> 2));
            dst[di + 1] = (byte)((((pixel >> 5) & 0x1F) << 3) | (((pixel >> 5) & 0x1F) >> 2));
            dst[di + 2] = (byte)((((pixel >> 10) & 0x1F) << 3) | (((pixel >> 10) & 0x1F) >> 2));
            dst[di + 3] = 255;
        }
    }

    /// <summary>
    ///     PS2 GS alpha is 0-128 (128 = opaque). Scale to 0-255.
    /// </summary>
    private static byte ScaleAlpha(byte gsAlpha)
    {
        return (byte)Math.Min(gsAlpha * 255 / 128, 255);
    }
}
