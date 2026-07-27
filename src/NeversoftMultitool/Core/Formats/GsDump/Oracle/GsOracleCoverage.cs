namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     How much of the capture's textured drawing the oracle actually
///     resolved. Adjudication tests assert a floor on
///     <see cref="ResolvedDrawFraction" /> so a correlation regression cannot
///     silently reduce the oracle to vacuous truth.
/// </summary>
internal sealed class GsOracleCoverage
{
    public int TexturedStateBuckets { get; init; }
    public int ResolvedStateBuckets { get; init; }
    public long TexturedDraws { get; init; }
    public long ResolvedDraws { get; init; }
    public double ResolvedDrawFraction { get; init; }
    public required List<GsOracleUnresolvedTex0> UnresolvedTex0 { get; init; }
}
