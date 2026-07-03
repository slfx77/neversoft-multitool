namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

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
    ///     By default alpha is rescaled from the GS convention (128 = fully opaque) to the
    ///     standard PNG/glTF 0-255 range. Pass <paramref name="rawGsAlpha" /> = true to keep
    ///     the raw GS alpha byte instead — required by the GS replay pipeline whose blend
    ///     math divides by 128 per PS2 GS spec.
    /// </summary>
    internal static byte[]? DecodePixels(
        ReadOnlySpan<byte> texData,
        int width,
        int height,
        uint psm,
        uint cpsm,
        byte[]? clut,
        bool flipVertical = true,
        bool fixAllZeroAlpha = true,
        ulong? texa = null,
        bool rawGsAlpha = false)
    {
        var pixels = new byte[width * height * 4];

        switch (psm)
        {
            case PSMCT32:
                DecodePsmct32(texData, pixels, width, height, rawGsAlpha);
                break;
            case PSMCT24:
                DecodePsmct24(texData, pixels, width, height, texa, rawGsAlpha);
                break;
            case PSMCT16:
                DecodePsmct16(texData, pixels, width, height, texa, rawGsAlpha);
                break;
            case PSMT8:
                if (clut == null) return null;
                DecodePsmt8(texData, pixels, width, height, clut, cpsm, texa, rawGsAlpha);
                break;
            case PSMT4:
                if (clut == null) return null;
                DecodePsmt4(texData, pixels, width, height, clut, cpsm, texa, rawGsAlpha);
                break;
            default:
                return null;
        }

        if (flipVertical)
            FlipVertical(pixels, width, height);

        // If every pixel has alpha=0 after decoding, the texture doesn't use alpha --
        // set all to 255 so it doesn't appear fully transparent in PNG output
        if (fixAllZeroAlpha)
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

    private static void DecodePsmct32(ReadOnlySpan<byte> src, byte[] dst, int width, int height, bool rawGsAlpha)
    {
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 4;
            var di = i * 4;
            dst[di] = src[si]; // R
            dst[di + 1] = src[si + 1]; // G
            dst[di + 2] = src[si + 2]; // B
            dst[di + 3] = ScaleAlpha(src[si + 3], rawGsAlpha); // A: PS2 0-128 -> 0-255
        }
    }

    private static void DecodePsmct24(ReadOnlySpan<byte> src, byte[] dst, int width, int height, ulong? texa,
        bool rawGsAlpha)
    {
        // PSMCT24 has no native alpha. PS2 GS provides it via TEXA.TA0 (a raw 8-bit byte
        // where 128 = nominal full = blend factor 1.0). With AEM=1, fully-black RGB pixels
        // get alpha=0 instead. Default to 128 if TEXA isn't supplied (matches "opaque" intent).
        var ta0 = ScaleAlpha(texa.HasValue ? (byte)(texa.Value & 0xFF) : (byte)128, rawGsAlpha);
        var aem = texa.HasValue && ((texa.Value >> 15) & 1) != 0;
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 3;
            var di = i * 4;
            var r = src[si];
            var g = src[si + 1];
            var b = src[si + 2];
            dst[di] = r;
            dst[di + 1] = g;
            dst[di + 2] = b;
            dst[di + 3] = aem && r == 0 && g == 0 && b == 0 ? (byte)0 : ta0;
        }
    }

    private static void DecodePsmct16(ReadOnlySpan<byte> src, byte[] dst, int width, int height, ulong? texa,
        bool rawGsAlpha)
    {
        for (var i = 0; i < width * height; i++)
        {
            var si = i * 2;
            var pixel = (ushort)(src[si] | (src[si + 1] << 8));
            var di = i * 4;
            // RGB5551: the high bit selects TEXA.TA0/TA1 during live GS sampling.
            dst[di] = (byte)(((pixel & 0x1F) << 3) | ((pixel & 0x1F) >> 2)); // R
            dst[di + 1] = (byte)((((pixel >> 5) & 0x1F) << 3) | (((pixel >> 5) & 0x1F) >> 2)); // G
            dst[di + 2] = (byte)((((pixel >> 10) & 0x1F) << 3) | (((pixel >> 10) & 0x1F) >> 2)); // B
            dst[di + 3] = texa.HasValue ? ExpandTexaAlpha(pixel, texa.Value, rawGsAlpha) : (byte)255;
        }
    }

    private static void DecodePsmt8(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut, uint cpsm, ulong? texa, bool rawGsAlpha)
    {
        var clutBpp = GetBitsPerPixel(cpsm) / 8;

        for (var i = 0; i < width * height; i++)
        {
            var colorIndex = src[i];
            var di = i * 4;
            ReadClutEntry(clut, colorIndex, clutBpp, cpsm, dst, di, texa, rawGsAlpha);
        }
    }

    private static void DecodePsmt4(ReadOnlySpan<byte> src, byte[] dst, int width, int height,
        byte[] clut, uint cpsm, ulong? texa, bool rawGsAlpha)
    {
        var clutBpp = GetBitsPerPixel(cpsm) / 8;

        for (var i = 0; i < width * height; i++)
        {
            var byteIndex = i >> 1;
            var colorIndex = (i & 1) == 0
                ? src[byteIndex] & 0x0F // low nibble first
                : (src[byteIndex] >> 4) & 0x0F; // high nibble
            var di = i * 4;
            ReadClutEntry(clut, colorIndex, clutBpp, cpsm, dst, di, texa, rawGsAlpha);
        }
    }

    private static void ReadClutEntry(byte[] clut, int index, int clutBpp, uint cpsm, byte[] dst, int di, ulong? texa,
        bool rawGsAlpha)
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
            dst[di + 3] = ScaleAlpha(clut[ci + 3], rawGsAlpha);
        }
        else // PSMCT16
        {
            // RGB5551: the high bit selects TEXA.TA0/TA1 during live GS sampling.
            var pixel = (ushort)(clut[ci] | (clut[ci + 1] << 8));
            dst[di] = (byte)(((pixel & 0x1F) << 3) | ((pixel & 0x1F) >> 2));
            dst[di + 1] = (byte)((((pixel >> 5) & 0x1F) << 3) | (((pixel >> 5) & 0x1F) >> 2));
            dst[di + 2] = (byte)((((pixel >> 10) & 0x1F) << 3) | (((pixel >> 10) & 0x1F) >> 2));
            dst[di + 3] = texa.HasValue ? ExpandTexaAlpha(pixel, texa.Value, rawGsAlpha) : (byte)255;
        }
    }

    /// <summary>
    ///     Expand a PSMCT16-encoded RGB5551 pixel's 1-bit alpha into a full byte via the
    ///     TEXA register: TA0 for alpha-bit=0, TA1 for alpha-bit=1, with the AEM rule
    ///     forcing alpha=0 when AEM=1 and the pixel is fully black with alpha-bit=0. Used
    ///     by both texture decode (PSMCT16 textures + PSMCT16 CLUTs) and framebuffer Cd
    ///     reads (PSMCT16/16S framebuffers sampled during ABE blending). Defaults to raw
    ///     GS alpha because the framebuffer-read callers feed GS blend math (/128); the
    ///     texture-decode paths pass their caller's <c>rawGsAlpha</c> explicitly.
    /// </summary>
    internal static byte ExpandTexaAlpha(ushort pixel, ulong texa, bool rawGsAlpha = true)
    {
        var alphaBitSet = (pixel & 0x8000) != 0;
        var gsAlpha = alphaBitSet ? (byte)((texa >> 32) & 0xFF) : (byte)(texa & 0xFF);
        var aem = ((texa >> 15) & 1) != 0;
        if (aem && !alphaBitSet && (pixel & 0x7FFF) == 0)
            gsAlpha = 0;

        return ScaleAlpha(gsAlpha, rawGsAlpha);
    }

    /// <summary>
    ///     PS2 GS stores alpha as a raw 8-bit byte where 128 = nominal full (blend factor
    ///     1.0, matching PCSX2's
    ///     <see href="../../../../Sample/pcsx2/pcsx2/GS/GSLocalMemory.h">WritePixel32</see>
    ///     which stores the raw byte). The GS replay pipeline needs that raw byte because
    ///     its blend math uses <c>alpha / 128</c> per PS2 GS spec; PNG/glTF export needs
    ///     the 0-255 convention (255 = opaque), so it rescales via <c>alpha * 255 / 128</c>.
    /// </summary>
    private static byte ScaleAlpha(byte gsAlpha, bool rawGsAlpha)
    {
        return rawGsAlpha ? gsAlpha : (byte)Math.Min(gsAlpha * 255 / 128, 255);
    }
}
