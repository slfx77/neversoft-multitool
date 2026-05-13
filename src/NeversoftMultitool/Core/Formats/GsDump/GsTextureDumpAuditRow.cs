namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsTextureDumpAuditRow
{
    public required string Key { get; init; }
    public string? Path { get; set; }
    public required string Source { get; init; }
    public string? SourceKey { get; init; }
    public required string Tex0 { get; init; }
    public required string Texa { get; init; }
    public uint ContentHash { get; init; }
    public uint? SourceChecksum { get; init; }
    public uint Tbp { get; init; }
    public uint Tbw { get; init; }
    public uint Psm { get; init; }
    public int TextureWidth { get; init; }
    public int TextureHeight { get; init; }
    public int RegionX { get; init; }
    public int RegionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public uint Tcc { get; init; }
    public uint Tfx { get; init; }
    public uint Cbp { get; init; }
    public uint Cpsm { get; init; }
    public uint Csm { get; init; }
    public uint Csa { get; init; }
    public uint Cld { get; init; }
    public bool AllAlphaZero { get; init; }
}
