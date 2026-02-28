namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Result of extracting textures from a single PSX file.
/// </summary>
public sealed class PsxExtractionResult
{
    public int TotalTextures { get; set; }
    public int TexturesWritten { get; set; }
    public int PlaceholdersSkipped { get; set; }
    public bool Success => TotalTextures > 0 && TexturesWritten == TotalTextures;
    public bool Skipped => TotalTextures == 0;
    public string? ErrorMessage { get; set; }
}

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

/// <summary>
///     Options controlling ancillary output formats (DDS, mip atlas) during texture extraction.
/// </summary>
internal readonly record struct OutputOptions(bool WriteDds, bool WriteMipAtlas);
