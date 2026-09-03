namespace NeversoftMultitool;

internal enum TextureFileFormat
{
    Psx,
    Ps2Tex,
    NgcTex,
    Pvr,
    XbxTex,
    XbxImg,

    /// <summary>Delimiter-free THPS4 Aspyr <c>*img.dat</c> loose image.</summary>
    Thps4PcDatImg,

    /// <summary>FACECAA7 Xbox 360 / PS3 texture dictionary.</summary>
    NextGenTex,

    /// <summary>128-byte PS3 IMG descriptor paired with an IMV/VRAM payload.</summary>
    Ps3Img,

    /// <summary>Carved N64 .tex.n64 dictionary records (one texture per file).</summary>
    N64Tex,

    /// <summary>PS1-era Neversoft .fnt bitmap fonts (one row per glyph).</summary>
    Fnt,

    /// <summary>Full-screen BIOS-LZ77 images scanned from a GBA ROM (one row per screen).</summary>
    GbaImage,

    /// <summary>
    ///     A Vicarious Visions DS texture bank (one row per record). The pixels live
    ///     in sibling container entries the bank names, so a row only resolves while
    ///     the cart or its GOB is open.
    /// </summary>
    NdsTextureBank
}
