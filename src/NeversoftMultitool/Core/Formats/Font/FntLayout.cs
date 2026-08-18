namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     Serialized layout of a PS1-era Neversoft <c>.fnt</c> bitmap font.
/// </summary>
/// <remarks>
///     Both layouts are accepted only on an exact end-of-file match, which is what keeps
///     them mutually exclusive: across the 443-file corpus no file satisfies both, 382
///     satisfy <see cref="PalettedWithAdvance" />, one satisfies <see cref="CompactWithoutPalette" />,
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
    ///     — no advance width — and no embedded CLUT. The prototype loader does not read this
    ///     shape, so everything about it is measured rather than transcribed.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Corpus: one file, THPS2 Dreamcast <c>LEVSEL.FNT</c>. Established by exact
    ///         end-of-file plus a per-pixel render check.
    ///     </para>
    ///     <para>
    ///         <b>Its 4bpp pixels are HIGH nibble first</b>, the opposite of the PS1 layout.
    ///         That is measured, not assumed: decoding low-first punches holes through every
    ///         stroke, and two independent continuity metrics (adjacent-luminance jumps and
    ///         contiguous bright runs) invert on this file alone while agreeing on low-first
    ///         for all 382 paletted files. Because the rule rests on a single file, a second
    ///         palette-less font must be measured rather than assumed to match.
    ///     </para>
    ///     <para>
    ///         What the pixel values <i>mean</i> is deliberately not claimed. With no CLUT in
    ///         the file and no decompiled Dreamcast loader, coverage and palette-index readings
    ///         are indistinguishable from the bytes, so the exporter treats them as coverage and
    ///         says so rather than naming the layout after an unproven semantic.
    ///     </para>
    /// </remarks>
    CompactWithoutPalette
}
