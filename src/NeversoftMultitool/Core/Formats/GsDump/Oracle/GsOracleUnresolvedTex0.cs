namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     A textured runtime TEX0 the catalog could not resolve to a checksum —
///     kept so coverage regressions in the TEX0↔checksum correlation are
///     visible instead of silently hollowing the oracle.
/// </summary>
internal sealed class GsOracleUnresolvedTex0
{
    public required string Tex0 { get; init; }
    public long Draws { get; init; }
    public long PixelsWritten { get; init; }
}
