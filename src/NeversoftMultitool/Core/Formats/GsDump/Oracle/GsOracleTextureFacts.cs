namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Every GS state bucket the game drew with while sampling one catalog
///     texture (correlated runtime TEX0 → THAW zone-texture checksum). Facts
///     are OBSERVATIONS: the set of blend modes a converter emits for this
///     texture must be CONTAINED in the observed set, never asserted equal —
///     a checksum can legitimately be drawn under several states (scene +
///     HUD + skybox reuse).
/// </summary>
internal sealed class GsOracleTextureFacts
{
    public required uint Checksum { get; init; }
    public required List<string> Tex0Values { get; init; }
    public required List<GsOracleStateBucket> StateBuckets { get; init; }
    public long TotalDraws { get; init; }
    public long TotalPixelsWritten { get; init; }
}
