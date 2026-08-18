using NeversoftMultitool.Core.Formats.Texture;

namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     Renders <c>.fnt</c> glyphs to RGBA and packs them into a single sheet.
/// </summary>
/// <remarks>
///     <para>
///         Packing walks the glyphs in stored order and wraps onto a new shelf when the next
///         one would overflow <see cref="DefaultMaxWidth" />, so the sheet reads in glyph-index
///         order and every cell's rectangle is published in the manifest rather than implied by
///         a grid rule.
///     </para>
///     <para>
///         Transparency is by CLUT <i>value</i>: an entry of <c>0x0000</c> is the PS1 GPU's
///         "not drawn" texel and is the only transparency these fonts use — measured across the
///         382 paletted corpus files, every one carries at least one <c>0x0000</c> entry, and
///         while two Spider-Man <c>sp_fnt01</c> copies do hold a magenta entry (the key the PSX
///         texture path looks for) no glyph anywhere in the corpus references one, so it is dead
///         palette data and leaving the magenta key off cannot change a rendered pixel. Bit 15
///         (STP) is set on 91% of entries and is deliberately ignored: <c>Font::draw</c> issues
///         the glyph's main pass with <c>Transparent = 0</c>, so the PS1 never consults STP for
///         it, and the aliased/shadow passes that do are runtime state rather than authored art.
///     </para>
/// </remarks>
public static class FntAtlasBuilder
{
    /// <summary>Shelf width the packer wraps at, widened if a single glyph exceeds it.</summary>
    public const int DefaultMaxWidth = 512;

    /// <summary>Transparent gutter around and between cells, in pixels.</summary>
    public const int DefaultPadding = 1;

    /// <summary>Packs every glyph of <paramref name="font" /> into one RGBA sheet.</summary>
    public static FntAtlas Build(FntFile font, int maxWidth = DefaultMaxWidth, int padding = DefaultPadding)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentOutOfRangeException.ThrowIfNegative(padding);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxWidth);

        var widest = font.Glyphs.Max(static glyph => glyph.Width);
        var atlasWidth = Math.Max(maxWidth, widest + 2 * padding);

        var cells = new FntAtlasCell[font.Glyphs.Length];
        var x = padding;
        var y = padding;
        var shelfHeight = 0;

        foreach (var glyph in font.Glyphs)
        {
            if (x > padding && x + glyph.Width + padding > atlasWidth)
            {
                x = padding;
                y += shelfHeight + padding;
                shelfHeight = 0;
            }

            cells[glyph.Index] = new FntAtlasCell(glyph.Index, x, y, glyph.Width, glyph.Height);
            x += glyph.Width + padding;
            shelfHeight = Math.Max(shelfHeight, glyph.Height);
        }

        var atlasHeight = y + shelfHeight + padding;
        var byteCount = (long)atlasWidth * atlasHeight * 4;
        if (byteCount > Array.MaxLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxWidth),
                $"A {atlasWidth}x{atlasHeight} atlas needs {byteCount} bytes, beyond the maximum array size.");
        }

        var rgba = new byte[byteCount];

        foreach (var glyph in font.Glyphs)
        {
            var cell = cells[glyph.Index];
            Blit(rgba, atlasWidth, cell.X, cell.Y, RenderGlyph(font, glyph), glyph.Width, glyph.Height);
        }

        return new FntAtlas(rgba, atlasWidth, atlasHeight, padding, cells);
    }

    /// <summary>Renders one glyph to a tightly packed RGBA buffer.</summary>
    public static byte[] RenderGlyph(FntFile font, FntGlyph glyph)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(glyph);

        var rgba = new byte[glyph.Width * glyph.Height * 4];
        Span<byte> pixel = stackalloc byte[4];
        for (var i = 0; i < glyph.Pixels.Length; i++)
        {
            ToRgba(font, glyph.Pixels[i], pixel);
            pixel.CopyTo(rgba.AsSpan(i * 4, 4));
        }

        return rgba;
    }

    /// <summary>Converts one stored 4-bit pixel value to RGBA.</summary>
    public static void ToRgba(FntFile font, byte value, Span<byte> rgba)
    {
        ArgumentNullException.ThrowIfNull(font);

        if (!font.HasPalette)
        {
            // No CLUT ships with this layout, so the 4-bit value is coverage. White keeps the
            // colour decision with the consumer instead of inventing one the file never stated.
            rgba[0] = 255;
            rgba[1] = 255;
            rgba[2] = 255;
            rgba[3] = (byte)(value * 255 / 15);
            return;
        }

        var entry = font.Palette[value];
        if (entry == 0)
        {
            rgba.Clear();
            return;
        }

        ColorHelpers.Ps1To32Bpp(entry, rgba, treatMagentaAsTransparent: false);
    }

    private static void Blit(
        byte[] destination,
        int destinationWidth,
        int destinationX,
        int destinationY,
        byte[] source,
        int sourceWidth,
        int sourceHeight)
    {
        for (var row = 0; row < sourceHeight; row++)
        {
            var sourceOffset = row * sourceWidth * 4;
            var destinationOffset = ((destinationY + row) * destinationWidth + destinationX) * 4;
            Array.Copy(source, sourceOffset, destination, destinationOffset, sourceWidth * 4);
        }
    }
}
