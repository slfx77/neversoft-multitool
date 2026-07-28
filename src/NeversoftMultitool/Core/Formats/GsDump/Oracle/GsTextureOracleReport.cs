namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Zone-texture decode ground truth for one capture (written as
///     <c>{stem}.texoracle.json</c>): every catalog-resolvable texture the
///     game uploaded, compared replay-decode vs converter-decode. Divergent
///     rows are concrete zone-TEX decode-bug leads; the adjudication tests
///     assert none appear outside the documented allowlist.
/// </summary>
internal sealed class GsTextureOracleReport
{
    public required string Capture { get; init; }
    public int Compared { get; init; }
    public int Matches { get; init; }
    public int QuantizationOnly { get; init; }
    public int AlphaProtocolDiffs { get; init; }
    public int Divergent { get; init; }
    public int SlotReuseSuspects { get; init; }
    public int AttributionMismatches { get; init; }
    public int ForeignContent { get; init; }
    public int NotComparable { get; init; }
    public required List<GsTextureOracleRow> Rows { get; init; }
}
