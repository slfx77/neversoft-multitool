namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     Serialized layout of a PS1-era Neversoft <c>.fnt</c> bitmap font.
/// </summary>
/// <remarks>
///     Both layouts are accepted only on an exact end-of-file match, which is what keeps
///     them mutually exclusive: across the 443-file corpus no file satisfies both, 382
///     satisfy <see cref="PalettedWithAdvance" />, one satisfies <see cref="IntensityWithoutPalette" />,
///     and the 60 THAW / THPS3-PS2 files (genuinely different formats) satisfy neither.
/// </remarks>
public enum FntLayout
{
    /// <summary>
    ///     The canonical layout read by <c>Font::Font(unsigned char *)</c> in the matched
    ///     THPS2 PSX decomp (<c>src/FONTTOOLS.cpp</c>):
    ///     <c>u32 glyphCount</c>, then <c>glyphCount</c> 16-byte records
    ///     <c>{ u32 widthUnits, i32 height, i32 baseline, i32 advanceWidth }</c>,
    ///     then a 16-entry <c>u16</c> CLUT, then 4bpp glyph pixels.
    /// </summary>
    PalettedWithAdvance,

    /// <summary>
    ///     A reduced layout with 12-byte records <c>{ u32 widthUnits, i32 height, i32 baseline }</c>
    ///     — no advance width — and no embedded CLUT, so the 4-bit pixel values are an
    ///     intensity ramp rather than palette indices. The prototype loader does not read this
    ///     shape; it is established by exact end-of-file plus rendering (the glyphs decode to
    ///     legible letters in the same order). Corpus: one file, THPS2 Dreamcast <c>LEVSEL.FNT</c>.
    /// </summary>
    IntensityWithoutPalette
}
