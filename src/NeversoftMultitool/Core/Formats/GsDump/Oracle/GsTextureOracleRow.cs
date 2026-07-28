namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     One replay-decoded texture (the game's real uploaded texels,
///     unswizzled by the GS replay at runtime TEX0) compared against the
///     converter's own decode of the same checksum. Classification values:
///     <c>Match</c>, <c>QuantizationOnly</c> (≤ 5-bit-step differences),
///     <c>AlphaProtocolDiff</c> (RGB agrees, alpha conventions diverge —
///     TEXA/CSA expansion), <c>Divergent</c> (a real decode defect lead, with
///     the exact TEX0/TEXA/region attached), <c>NotComparable</c> (dimension
///     or data mismatch).
/// </summary>
internal sealed class GsTextureOracleRow
{
    public required uint Checksum { get; init; }
    public required string Tex0 { get; init; }
    public required string Texa { get; init; }
    public required string Source { get; init; }
    public required string Classification { get; init; }
    public double RgbMae { get; init; }
    public double AlphaMae { get; init; }

    /// <summary>
    ///     Best same-size catalog content match found for a Divergent dump
    ///     (null when no sweep ran). When it differs from Checksum, the slot
    ///     held a different asset than the TBP attribution assumed.
    /// </summary>
    public uint? BestMatchChecksum { get; init; }

    public double BestMatchRgbMae { get; init; } = -1;
    public int Width { get; init; }
    public int Height { get; init; }
    public int RegionX { get; init; }
    public int RegionY { get; init; }
    public uint Psm { get; init; }
    public uint Cpsm { get; init; }
    public uint Csa { get; init; }
    public string? Notes { get; init; }
}
