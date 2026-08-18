namespace NeversoftMultitool;

internal enum TextureFileFormat
{
    Psx,
    Ps2Tex,
    NgcTex,
    Pvr,
    XbxTex,
    XbxImg,

    /// <summary>Carved N64 .tex.n64 dictionary records (one texture per file).</summary>
    N64Tex,

    /// <summary>PS1-era Neversoft .fnt bitmap fonts (one row per glyph).</summary>
    Fnt
}
