namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Machine-readable "what the game actually did" facts extracted from one
///     PCSX2 .gs replay (written as <c>{stem}.gsoracle.json</c> when a zone
///     texture catalog is supplied): per catalog-resolved texture checksum,
///     the GS blend/test state buckets it was drawn under and their
///     frame-global draw ordering. Consumed as committed goldens by the
///     converter adjudication tests — the bridge that lets THAW blend-mode
///     and draw-order claims be VERIFIED against hardware truth instead of
///     merely implemented.
/// </summary>
internal sealed class GsOracleReport
{
    public required string Capture { get; init; }
    public required string Serial { get; init; }
    public uint Crc { get; init; }
    public long TotalDraws { get; init; }
    public required GsOracleCoverage Coverage { get; init; }
    public required List<GsOracleTextureFacts> Textures { get; init; }
}
