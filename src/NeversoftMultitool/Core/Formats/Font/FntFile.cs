using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     PS1-era Neversoft <c>.fnt</c> bitmap font: a glyph metric table, an optional
///     16-entry CLUT, and 4bpp glyph bitmaps.
/// </summary>
/// <remarks>
///     <para>
///         The layout is transcribed from <c>Font::Font(unsigned char *)</c> in the matched
///         THPS2 PSX decomp (<c>src/FONTTOOLS.cpp</c>), which is the engine's only reader:
///     </para>
///     <code>
///     +0                        u32 glyphCount
///     +4 + 16*i                 { u32 widthUnits, i32 height, i32 baseline, i32 advanceWidth }
///     +4 + 16*glyphCount        16 x u16 CLUT (PS1 15-bit BGR, bit 15 = STP)
///     +36 + 16*glyphCount       glyph pixels, 4bpp low-nibble-first,
///                               pixel width = widthUnits*4, row stride = widthUnits*2 bytes,
///                               per-glyph size = 2*(widthUnits*height) + 2*((widthUnits*height) &amp; 1)
///     </code>
///     <para>
///         The trailing two bytes on an odd <c>widthUnits*height</c> are the loader's own
///         rounding (<c>oddPixelPadding</c>), not a row pad — rows are unpadded.
///     </para>
///     <para>
///         Parsing is strict: a candidate is accepted only when the metric table, palette and
///         every glyph's pixel run consume the file exactly. That exactness is what separates
///         the two layouts and rejects unrelated formats sharing the <c>.fnt</c> extension
///         (THAW and THPS3-PS2 both ship one). See <see cref="FntLayout" />.
///     </para>
/// </remarks>
public sealed class FntFile
{
    /// <summary>Entries in an embedded CLUT, when the layout carries one.</summary>
    public const int PaletteEntryCount = 16;

    private const int PalettedRecordSize = 16;
    private const int IntensityRecordSize = 12;
    private const int PaletteByteCount = PaletteEntryCount * 2;

    // Generous next to the corpus (glyphCount 10-94, widthUnits 1-10, height 1-40) but tight
    // enough that the size arithmetic below cannot overflow.
    private const int MaxGlyphCount = 4096;
    private const int MaxDimensionUnits = 4096;

    /// <summary>Which serialized layout this file uses.</summary>
    public required FntLayout Layout { get; init; }

    /// <summary>The glyph table, in stored order.</summary>
    public required FntGlyph[] Glyphs { get; init; }

    /// <summary>
    ///     The embedded 16-entry CLUT, or empty for
    ///     <see cref="FntLayout.IntensityWithoutPalette" />, which ships none.
    /// </summary>
    public required ushort[] Palette { get; init; }

    /// <summary>Total bytes consumed, which always equals the source length.</summary>
    public required int SerializedSize { get; init; }

    /// <summary>Uppercase SHA-256 of the whole source file.</summary>
    public required string SerializedSha256 { get; init; }

    /// <summary>True when the pixel values index <see cref="Palette" /> rather than being intensities.</summary>
    public bool HasPalette => Layout == FntLayout.PalettedWithAdvance;

    /// <summary>
    ///     Reads a <c>.fnt</c>, trying the canonical paletted layout first and the reduced
    ///     intensity layout second.
    /// </summary>
    /// <exception cref="InvalidDataException">The data matches neither layout exactly.</exception>
    public static FntFile Parse(ReadOnlySpan<byte> data)
    {
        if (TryParse(data, out var font))
            return font;

        throw new InvalidDataException(
            "Not a Neversoft bitmap font: no glyph table and pixel run consumes the file exactly.");
    }

    /// <summary>Reads a <c>.fnt</c>, returning false when the data is not one.</summary>
    public static bool TryParse(ReadOnlySpan<byte> data, out FntFile font)
    {
        if (TryParseLayout(data, FntLayout.PalettedWithAdvance, out font))
            return true;

        return TryParseLayout(data, FntLayout.IntensityWithoutPalette, out font);
    }

    /// <summary>Structural check that does not decode pixels.</summary>
    public static bool IsFnt(ReadOnlySpan<byte> data)
    {
        return Measure(data, FntLayout.PalettedWithAdvance, out _)
               || Measure(data, FntLayout.IntensityWithoutPalette, out _);
    }

    private static bool TryParseLayout(ReadOnlySpan<byte> data, FntLayout layout, out FntFile font)
    {
        font = null!;
        if (!Measure(data, layout, out var records))
            return false;

        var recordSize = RecordSize(layout);
        var glyphCount = records.Length;
        var pixelOffset = 4 + recordSize * glyphCount
                          + (layout == FntLayout.PalettedWithAdvance ? PaletteByteCount : 0);

        var palette = new ushort[layout == FntLayout.PalettedWithAdvance ? PaletteEntryCount : 0];
        for (var i = 0; i < palette.Length; i++)
        {
            palette[i] = BinaryPrimitives.ReadUInt16LittleEndian(
                data.Slice(4 + recordSize * glyphCount + i * 2, 2));
        }

        var glyphs = new FntGlyph[glyphCount];
        var offset = pixelOffset;
        for (var i = 0; i < glyphCount; i++)
        {
            var record = records[i];
            var length = GlyphByteLength(record.WidthUnits, record.Height);
            glyphs[i] = new FntGlyph
            {
                Index = i,
                Width = record.WidthUnits * 4,
                Height = record.Height,
                Baseline = record.Baseline,
                AdvanceWidth = layout == FntLayout.PalettedWithAdvance ? record.AdvanceWidth : null,
                WidthUnits = record.WidthUnits,
                PixelDataOffset = offset,
                PixelDataLength = length,
                Pixels = DecodePixels(data.Slice(offset, length), record.WidthUnits, record.Height)
            };
            offset += length;
        }

        font = new FntFile
        {
            Layout = layout,
            Glyphs = glyphs,
            Palette = palette,
            SerializedSize = data.Length,
            SerializedSha256 = Convert.ToHexString(SHA256.HashData(data))
        };
        return true;
    }

    /// <summary>
    ///     Validates the header and metric table against <paramref name="layout" /> and requires
    ///     the resulting pixel run to end exactly at EOF.
    /// </summary>
    private static bool Measure(ReadOnlySpan<byte> data, FntLayout layout, out GlyphRecord[] records)
    {
        records = [];
        if (data.Length < 4)
            return false;

        var glyphCount = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (glyphCount == 0 || glyphCount > MaxGlyphCount)
            return false;

        var recordSize = RecordSize(layout);
        var count = (int)glyphCount;
        var tableEnd = 4L + (long)recordSize * count;
        var pixelStart = tableEnd + (layout == FntLayout.PalettedWithAdvance ? PaletteByteCount : 0);
        if (pixelStart > data.Length)
            return false;

        var parsed = new GlyphRecord[count];
        var total = pixelStart;
        for (var i = 0; i < count; i++)
        {
            var record = data.Slice(4 + recordSize * i, recordSize);
            var widthUnits = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var height = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
            var baseline = BinaryPrimitives.ReadInt32LittleEndian(record[8..]);
            var advance = layout == FntLayout.PalettedWithAdvance
                ? BinaryPrimitives.ReadInt32LittleEndian(record[12..])
                : 0;

            if (widthUnits == 0 || widthUnits > MaxDimensionUnits)
                return false;
            if (height <= 0 || height > MaxDimensionUnits)
                return false;

            parsed[i] = new GlyphRecord((int)widthUnits, height, baseline, advance);
            total += GlyphByteLength((int)widthUnits, height);
            if (total > data.Length)
                return false;
        }

        if (total != data.Length)
            return false;

        records = parsed;
        return true;
    }

    private static int RecordSize(FntLayout layout)
    {
        return layout == FntLayout.PalettedWithAdvance ? PalettedRecordSize : IntensityRecordSize;
    }

    /// <summary>
    ///     Bytes one glyph occupies, reproducing the loader's <c>oddPixelPadding</c> tail
    ///     (<c>FONTTOOLS.cpp:281-285</c>). The parity is taken from the pixel count
    ///     <i>before</i> it is doubled — <c>oddPixelPadding = (pixelCount &amp; 1) &lt;&lt; 1</c>
    ///     then <c>pixelCount = (pixelCount &lt;&lt; 1) + oddPixelPadding</c> — so reading the
    ///     parity after the shift would never pad at all.
    /// </summary>
    private static int GlyphByteLength(int widthUnits, int height)
    {
        var pixels = widthUnits * height;
        return (pixels << 1) + ((pixels & 1) << 1);
    }

    /// <summary>Unpacks 4bpp pixels, low nibble first, into one byte per pixel.</summary>
    private static byte[] DecodePixels(ReadOnlySpan<byte> source, int widthUnits, int height)
    {
        var width = widthUnits * 4;
        var stride = widthUnits * 2;
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var rowStart = y * stride;
            var destRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var value = source[rowStart + (x >> 1)];
                pixels[destRow + x] = (byte)((x & 1) == 0 ? value & 0x0F : value >> 4);
            }
        }

        return pixels;
    }

    private readonly record struct GlyphRecord(int WidthUnits, int Height, int Baseline, int AdvanceWidth);
}
