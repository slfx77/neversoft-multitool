using NeversoftMultitool.Core.Formats.Texture.Psx;

namespace NeversoftMultitool.Core.Formats.Texture.Ps1;

public static class Ps1TextureDecoder
{
    /// <summary>
    ///     Alpha markers used by mesh-only PS1 decoding to retain the GPU
    ///     palette state until the face's ABE/ABR state is known. Ordinary
    ///     texture extraction does not emit these markers.
    /// </summary>
    internal const byte RuntimeOpaqueAlpha = 253;

    internal const byte RuntimeSemiTransparencyAlpha = 254;

    /// <summary>
    ///     Extracts a 4-bit (16-color) texture from a PSX file.
    /// </summary>
    public static byte[]? Extract4BitTexture(BinaryReader reader, PsxTextureHeader header,
        List<PsxPalette> palette4Bit,
        bool preserveRuntimeSemiTransparency = false)
    {
        if (header.Width <= 0 || header.Height <= 0)
            return null;

        var padWidth = (header.Width + 0x3) & ~0x3;
        padWidth >>= 1;
        var realLen = padWidth * header.Height + GetPaddingAmount(header, padWidth);
        var palIndices = reader.ReadBytes(realLen);
        if (palIndices.Length != realLen)
            return null;

        // Find matching palette
        foreach (var pal in palette4Bit)
        {
            if (pal.TexId != header.TexId) continue;

            var pixels = new byte[header.Width * header.Height * 4];
            var runtimeAlpha = preserveRuntimeSemiTransparency
                ? BuildRuntimePaletteAlpha(pal.ColorData)
                : null;
            Span<byte> rgba = stackalloc byte[4];

            for (var y = 0; y < header.Height; y++)
            {
                for (var x = 0; x < header.Width; x++)
                {
                    var byteIndex = y * padWidth + (x >> 1);
                    var colorIndex = (palIndices[byteIndex] >> ((x & 0x1) * 4)) & 0xF;
                    var color = pal.ColorData[colorIndex];

                    ColorHelpers.Ps1To32Bpp(
                        color,
                        rgba,
                        runtimeAlpha == null);
                    if (runtimeAlpha != null)
                        ApplyRuntimePaletteAlpha(rgba, runtimeAlpha[colorIndex]);

                    // Note: Python uses pixels[y * width - x] which wraps around
                    var pixelIndex = y * header.Width - x;
                    if (pixelIndex < 0) pixelIndex += header.Width * header.Height;
                    var offset = pixelIndex * 4;

                    pixels[offset] = rgba[0];
                    pixels[offset + 1] = rgba[1];
                    pixels[offset + 2] = rgba[2];
                    pixels[offset + 3] = rgba[3];
                }
            }

            return pixels;
        }

        return null;
    }

    /// <summary>
    ///     Extracts an 8-bit (256-color) texture from a PSX file.
    /// </summary>
    public static byte[]? Extract8BitTexture(BinaryReader reader, PsxTextureHeader header,
        List<PsxPalette> palette8Bit,
        bool preserveRuntimeSemiTransparency = false)
    {
        if (header.Width <= 0 || header.Height <= 0)
            return null;

        var padWidth = (header.Width + 0x1) & ~0x1;
        var realLen = padWidth * header.Height + GetPaddingAmount(header, padWidth);
        var palIndices = reader.ReadBytes(realLen);
        if (palIndices.Length != realLen)
            return null;

        // Find matching palette
        foreach (var pal in palette8Bit)
        {
            if (pal.TexId != header.TexId) continue;

            var pixels = new byte[header.Width * header.Height * 4];
            var runtimeAlpha = preserveRuntimeSemiTransparency
                ? BuildRuntimePaletteAlpha(pal.ColorData)
                : null;
            Span<byte> rgba = stackalloc byte[4];

            for (var y = 0; y < header.Height; y++)
            {
                for (var x = 0; x < header.Width; x++)
                {
                    var colorIndex = palIndices[y * padWidth + x] & 0xFF;
                    var color = pal.ColorData[colorIndex];

                    ColorHelpers.Ps1To32Bpp(
                        color,
                        rgba,
                        runtimeAlpha == null);
                    if (runtimeAlpha != null)
                        ApplyRuntimePaletteAlpha(rgba, runtimeAlpha[colorIndex]);

                    // Note: Python uses pixels[y * width - x] which wraps around
                    var pixelIndex = y * header.Width - x;
                    if (pixelIndex < 0) pixelIndex += header.Width * header.Height;
                    var offset = pixelIndex * 4;

                    pixels[offset] = rgba[0];
                    pixels[offset + 1] = rgba[1];
                    pixels[offset + 2] = rgba[2];
                    pixels[offset + 3] = rgba[3];
                }
            }

            return pixels;
        }

        return null;
    }

    private static byte[] BuildRuntimePaletteAlpha(ushort[] colors)
    {
        // Pal_LoadPalette (THPS2 decomp, PERFECT match @0x8004BC24) uses the
        // first 0x7C1F entry as the runtime transparent key. Shipped art also
        // repeats that same key colour in otherwise-unused CLUT slots and then
        // indexes those slots for cutout coverage (LDA3's billboard palette
        // uses five such entries). Preserve the asset-level colour-key
        // convention for every exact key entry; limiting alpha=0 to the first
        // one exposes the repeated entries as large magenta fields.
        //
        // Ordinary entries after the first key, entries already carrying bit
        // 15, and raw black before the key receive STP; if no key exists every
        // entry receives STP. A textured primitive only blends STP texels when
        // its ABE bit is set, so the state must survive texture decoding rather
        // than becoming uniform PNG transparency. The editor-only
        // TransparentPalForEditor override is deliberately not modeled by this
        // gameplay/viewer path.
        var alpha = new byte[colors.Length];
        var foundTransparent = false;
        for (var i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            var isMagentaKey = (color & 0x7FFF) == 0x7C1F;
            if (isMagentaKey)
            {
                alpha[i] = 0;
                foundTransparent = true;
                continue;
            }

            var hasStp = (color & 0x8000) != 0;
            if (!foundTransparent)
            {
                // Before a key, Pal_LoadPalette only forces STP on raw black.
                hasStp |= color == 0;
            }
            else
            {
                // Every ordinary entry after the first key is forced to STP.
                hasStp = true;
            }

            alpha[i] = hasStp ? RuntimeSemiTransparencyAlpha : RuntimeOpaqueAlpha;
        }

        if (!foundTransparent)
            Array.Fill(alpha, RuntimeSemiTransparencyAlpha);

        return alpha;
    }

    private static void ApplyRuntimePaletteAlpha(Span<byte> rgba, byte alpha)
    {
        rgba[3] = alpha;
        if (alpha == 0)
            rgba[..3].Clear();
    }

    private static int GetPaddingAmount(PsxTextureHeader header, int padWidth)
    {
        if (header.Height % 2 != 0)
        {
            return padWidth % 4 != 0 ? 2 : 0;
        }

        return 0;
    }
}
