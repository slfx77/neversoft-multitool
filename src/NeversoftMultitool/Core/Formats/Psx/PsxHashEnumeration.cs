namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     All name hashes and extended header names from a PSX file.
/// </summary>
public sealed class PsxHashEnumeration
{
    public required uint[] MeshNameHashes { get; init; }
    public required uint[] TextureNameHashes { get; init; }
    public string[]? DetailTextureNames { get; init; }
    public string[]? CubemapNames { get; init; }
}
