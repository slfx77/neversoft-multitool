namespace NeversoftMultitool.Core.Formats.Texture.Psx;

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
