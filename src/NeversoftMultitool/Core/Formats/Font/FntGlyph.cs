namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     One glyph of a PS1-era Neversoft <c>.fnt</c> bitmap font, exactly as serialized.
/// </summary>
/// <remarks>
///     <see cref="Pixels" /> holds one 4-bit value per pixel in row-major order. For
///     <see cref="FntLayout.PalettedWithAdvance" /> those are CLUT indices; for
///     <see cref="FntLayout.CompactWithoutPalette" /> no CLUT ships, so what they denote is
///     unproven and the exporter renders them as coverage.
/// </remarks>
public sealed record FntGlyph
{
    /// <summary>Zero-based position in the file's glyph table.</summary>
    public required int Index { get; init; }

    /// <summary>Glyph bitmap width in pixels — always <see cref="WidthUnits" /> * 4.</summary>
    public required int Width { get; init; }

    /// <summary>Glyph bitmap height in rows.</summary>
    public required int Height { get; init; }

    /// <summary>
    ///     Rows above the text baseline. Signed: the corpus contains -1 and -2, which
    ///     place the whole glyph below the baseline.
    /// </summary>
    public required int Baseline { get; init; }

    /// <summary>
    ///     Pen advance in pixels. Absent from <see cref="FntLayout.CompactWithoutPalette" />
    ///     files, where it is reported as <see langword="null" /> rather than invented.
    /// </summary>
    public required int? AdvanceWidth { get; init; }

    /// <summary>Raw stored width field; the pixel width is four times this.</summary>
    public required int WidthUnits { get; init; }

    /// <summary>Absolute byte offset of this glyph's pixels within the source file.</summary>
    public required int PixelDataOffset { get; init; }

    /// <summary>
    ///     Bytes this glyph consumes, including the two-byte tail the loader adds when
    ///     <see cref="WidthUnits" /> * <see cref="Height" /> is odd.
    /// </summary>
    public required int PixelDataLength { get; init; }

    /// <summary>One 4-bit value per pixel, row-major, length <see cref="Width" /> * <see cref="Height" />.</summary>
    public required byte[] Pixels { get; init; }
}
