namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     The glyph-index orderings <c>Font::getCharIndex</c> selects between, transcribed from
///     the matched THPS2 PSX decomp (<c>src/FONTTOOLS.cpp:667-768</c>).
/// </summary>
/// <remarks>
///     <para>
///         <b>The mode is runtime state, not file data.</b> <c>Font::CharMap</c> is a public
///         field the constructor zeroes and game code assigns; nothing in a <c>.fnt</c> records
///         which ordering its author used. These tables are therefore published as candidates
///         and are never inferred from the file.
///     </para>
///     <para>
///         The orderings are also not stable across builds. The March-2000 THPS2 prototype's
///         <c>player/s2bio.fnt</c> has 74 glyphs and maps index 48 to <c>a</c>, exactly as
///         decompiled; retail THPS2's <c>s2bio.fnt</c> has 94, places <c>_</c> at 48, shifts
///         lowercase to 49-74 and adds accented glyphs at 75-93. Same game, same filename,
///         different ordering — which is why a caller must state the mode rather than have one
///         guessed for it.
///     </para>
/// </remarks>
public static class FntCharacterMap
{
    /// <summary>Punctuation following the letters and digits in <see cref="Mode.Standard" />.</summary>
    private const string StandardPunctuation = "?!:.,-%+'#/$";

    /// <summary>A <c>Font::CharMap</c> value.</summary>
    public enum Mode
    {
        /// <summary>
        ///     <c>CharMap == 0</c>: <c>A-Z</c> at 0-25 (upper and lower case both map here),
        ///     <c>0-9</c> at 26-35, then <c>? ! : . , - % + ' # / $</c> at 36-47.
        /// </summary>
        Standard = 0,

        /// <summary><c>CharMap == 1</c>: <c>0-9</c> at 0-9 and <c>:</c> at 10.</summary>
        Numeric = 1,

        /// <summary>
        ///     <c>CharMap == 2</c>: <see cref="Standard" /> plus <c>a-z</c> at 48-73, so upper
        ///     and lower case are distinct glyphs.
        /// </summary>
        LowercaseFirst = 2
    }

    /// <summary>Highest glyph index the mode assigns a character to.</summary>
    public static int HighestMappedIndex(Mode mode)
    {
        return mode switch
        {
            Mode.Standard => 47,
            Mode.Numeric => 10,
            Mode.LowercaseFirst => 73,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown character map mode.")
        };
    }

    /// <summary>
    ///     True when the mode assigns a character to every glyph in a font of
    ///     <paramref name="glyphCount" /> glyphs, leaving none unlabelled.
    /// </summary>
    public static bool MapsEveryGlyph(Mode mode, int glyphCount)
    {
        return glyphCount <= HighestMappedIndex(mode) + 1;
    }

    /// <summary>
    ///     The character this mode assigns to <paramref name="index" />, or <see langword="null" />
    ///     when the mode assigns none.
    /// </summary>
    public static string? Character(Mode mode, int index)
    {
        if (index < 0)
            return null;

        return mode switch
        {
            Mode.Numeric => index switch
            {
                < 10 => ((char)('0' + index)).ToString(),
                10 => ":",
                _ => null
            },
            Mode.LowercaseFirst when index is >= 48 and <= 73 =>
                ((char)('a' + (index - 48))).ToString(),
            Mode.Standard or Mode.LowercaseFirst => index switch
            {
                < 26 => ((char)('A' + index)).ToString(),
                < 36 => ((char)('0' + (index - 26))).ToString(),
                < 48 => StandardPunctuation[index - 36].ToString(),
                _ => null
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown character map mode.")
        };
    }

    /// <summary>The characters this mode assigns to glyphs 0..<paramref name="glyphCount" />-1.</summary>
    public static string?[] Characters(Mode mode, int glyphCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(glyphCount);

        var characters = new string?[glyphCount];
        for (var i = 0; i < glyphCount; i++)
            characters[i] = Character(mode, i);

        return characters;
    }
}
