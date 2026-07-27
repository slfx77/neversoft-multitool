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
    public int Width { get; init; }
    public int Height { get; init; }
    public int RegionX { get; init; }
    public int RegionY { get; init; }
    public uint Psm { get; init; }
    public uint Cpsm { get; init; }
    public uint Csa { get; init; }
    public string? Notes { get; init; }
}
