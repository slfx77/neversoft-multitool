namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Represents a color palette entry for PS1 textures.
/// </summary>
public sealed class PsxPalette
{
    public required uint TexId { get; init; }
    public required ushort[] ColorData { get; init; }
}
