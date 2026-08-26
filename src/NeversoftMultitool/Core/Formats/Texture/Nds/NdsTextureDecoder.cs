namespace NeversoftMultitool.Core.Formats.Texture.Nds;

/// <summary>
///     Decodes a Nintendo DS GX texture to RGBA8. The DS palette entry is BGR555
///     with bit 15 unused, so a channel scales 5 → 8 bits as
///     <c>(v &lt;&lt; 3) | (v &gt;&gt; 2)</c>.
///
///     Transparency has two sources and they are NOT the same thing: the paletted
///     formats take it from TEXIMAGE_PARAM bit 29 ("colour 0 transparent"), while
///     A3I5/A5I3 carry per-texel alpha in the high bits of each byte and Direct16
///     uses bit 15 of the texel. Only the formats the corpus actually ships are
///     decoded; <see cref="NdsTextureFormat.Compressed4X4" /> is rejected rather
///     than approximated, because its palette-index block lives in a second data
///     slot that the bank does not carry — and no shipped texture uses it.
/// </summary>
public static class NdsTextureDecoder
{
    /// <summary>Decodes to a tightly-packed RGBA byte array (width * height * 4).</summary>
    public static byte[] Decode(NdsTextureEntry entry, ReadOnlySpan<byte> texels)
    {
        if (entry.Format == NdsTextureFormat.Compressed4X4)
        {
            throw new InvalidDataException(
                "DS 4x4-compressed textures need a second palette-index block the bank does not carry.");
        }

        if (texels.Length < entry.PixelBytes)
        {
            throw new InvalidDataException(
                $"Texture {entry.PixelId:x8} needs {entry.PixelBytes} texel bytes but only {texels.Length} were supplied.");
        }

        // Rows are stored BOTTOM-UP, the same way this studio's GameCube art is
        // (see NgcTexFile). Decoding in storage order puts a car on its roof and a
        // face on its chin — visible the moment a texture with a recognisable
        // subject or any lettering is looked at, and invisible to any statistical
        // check, since a flipped image is exactly as spatially coherent.
        var rgba = new byte[entry.Width * entry.Height * 4];
        var transparent = TransparentIndices(entry);
        for (var y = 0; y < entry.Height; y++)
        {
            var destinationRow = (entry.Height - 1 - y) * entry.Width;
            for (var x = 0; x < entry.Width; x++)
            {
                WriteTexel(entry, texels, y * entry.Width + x,
                    rgba.AsSpan((destinationRow + x) * 4), transparent);
            }
        }

        return rgba;
    }

    /// <summary>
    ///     Which palette indices are holes. Index 0 when TEXIMAGE_PARAM says so —
    ///     and, when the key parked there is a saturated magenta-class colour,
    ///     also any entry that is a near-duplicate of it: the art tools quantised
    ///     frond/foliage sources drawn on a key-colour canvas into palettes that
    ///     carry the key AGAIN at other slots (a Sk8land palm holds rgb(248,0,248)
    ///     at 0, 8 AND 9), and the few edge texels using those render as key-colour
    ///     speckles. The magenta gate keeps the rule away from art whose entry 0
    ///     merely happens to match a legitimate colour — measured, every
    ///     colour-0-transparent palette in all three carts has a magenta-class
    ///     entry 0, and the rule touches 17 textures / 64 entries corpus-wide.
    /// </summary>
    private static uint TransparentIndices(NdsTextureEntry entry)
    {
        if (!entry.Color0Transparent || entry.Palette.Length == 0)
            return 0;

        var mask = 1u;
        var key = entry.Palette[0];
        int r0 = (key & 31) << 3, g0 = ((key >> 5) & 31) << 3, b0 = ((key >> 10) & 31) << 3;
        if (r0 < 200 || b0 < 200 || g0 > 100)
            return mask;

        for (var i = 1; i < Math.Min(entry.Palette.Length, 32); i++)
        {
            var c = entry.Palette[i];
            int r = (c & 31) << 3, g = ((c >> 5) & 31) << 3, b = ((c >> 10) & 31) << 3;
            if (Math.Abs(r - r0) <= 24 && Math.Abs(g - g0) <= 24 && Math.Abs(b - b0) <= 24)
                mask |= 1u << i;
        }

        return mask;
    }

    private static void WriteTexel(
        NdsTextureEntry entry, ReadOnlySpan<byte> texels, int i, Span<byte> dest, uint transparent)
    {
        switch (entry.Format)
        {
            case NdsTextureFormat.Direct16:
            {
                var v = (ushort)(texels[i * 2] | (texels[i * 2 + 1] << 8));
                Bgr555(v, dest);
                dest[3] = (v & 0x8000) != 0 ? (byte)255 : (byte)0;
                return;
            }

            case NdsTextureFormat.A3I5:
                Paletted(entry, texels[i] & 0x1F, dest, Expand((texels[i] >> 5) & 7, 3));
                return;

            case NdsTextureFormat.A5I3:
                Paletted(entry, texels[i] & 0x07, dest, Expand((texels[i] >> 3) & 31, 5));
                return;

            case NdsTextureFormat.Palette256:
                PalettedIndexed(entry, texels[i], dest, transparent);
                return;

            case NdsTextureFormat.Palette16:
            {
                var b = texels[i / 2];
                PalettedIndexed(entry, (i & 1) == 0 ? b & 0x0F : b >> 4, dest, transparent);
                return;
            }

            case NdsTextureFormat.Palette4:
            {
                var b = texels[i / 4];
                PalettedIndexed(entry, (b >> ((i & 3) * 2)) & 3, dest, transparent);
                return;
            }

            default:
                throw new InvalidDataException($"Unsupported DS texture format {entry.Format}.");
        }
    }

    /// <summary>Paletted formats: holes per <see cref="TransparentIndices" />.</summary>
    private static void PalettedIndexed(
        NdsTextureEntry entry, int index, Span<byte> dest, uint transparent)
    {
        var hole = index < 32 && (transparent & (1u << index)) != 0;
        Paletted(entry, index, dest, hole ? (byte)0 : (byte)255);
    }

    private static void Paletted(NdsTextureEntry entry, int index, Span<byte> dest, byte alpha)
    {
        if ((uint)index < (uint)entry.Palette.Length)
            Bgr555(entry.Palette[index], dest);
        dest[3] = alpha;
    }

    private static void Bgr555(ushort value, Span<byte> dest)
    {
        dest[0] = Scale5To8(value & 31);
        dest[1] = Scale5To8((value >> 5) & 31);
        dest[2] = Scale5To8((value >> 10) & 31);
    }

    private static byte Scale5To8(int v)
    {
        return (byte)((v << 3) | (v >> 2));
    }

    /// <summary>Expands an n-bit alpha field to 8 bits.</summary>
    private static byte Expand(int value, int bits)
    {
        var max = (1 << bits) - 1;
        return (byte)(value * 255 / max);
    }
}
