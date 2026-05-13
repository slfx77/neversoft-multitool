namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsTex0CorrelationRow
{
    public required string Tex0 { get; init; }
    public long XyzWrites { get; init; }
    public uint? TextureChecksum { get; init; }
    public string? ResolutionMode { get; init; }
}
