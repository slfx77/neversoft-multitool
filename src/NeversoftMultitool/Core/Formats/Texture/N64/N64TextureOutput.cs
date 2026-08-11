namespace NeversoftMultitool.Core.Formats.Texture.N64;

/// <summary>
///     Writes the decoded levels that were actually stored in an N64 texture
///     record. Level zero keeps the historical output path; additional levels
///     use a stable <c>_mipN</c> suffix beside it.
/// </summary>
public static class N64TextureOutput
{
    /// <summary>
    ///     Decodes <paramref name="inputPath" /> and writes every stored level
    ///     to <paramref name="outputDir" />. The first returned path is the
    ///     backwards-compatible <c>{stem}.png</c> output.
    /// </summary>
    public static IReadOnlyList<string> ConvertToPngLevels(string inputPath, string outputDir)
    {
        var texture = N64TexFile.Decode(File.ReadAllBytes(inputPath));
        // Carved records are slot-prefixed and therefore already unique. Keep
        // the original ConvertToPng stem rule rather than using the embedded
        // art name, which can legitimately repeat in a dictionary.
        var stem = Path.GetFileName(inputPath).Split('.')[0];
        return WritePngLevels(texture, Path.Combine(outputDir, $"{stem}.png"));
    }

    /// <summary>
    ///     Writes level zero to <paramref name="topLevelPath" /> and each
    ///     additional stored level to <c>{stem}_mip{level}.png</c>. Returns the
    ///     paths in level order, with the legacy top-level path first.
    /// </summary>
    public static IReadOnlyList<string> WritePngLevels(
        N64TexFile.N64Texture texture,
        string topLevelPath)
    {
        var directory = Path.GetDirectoryName(topLevelPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        BinaryIO.ImageWriter.WritePng(topLevelPath, texture.Width, texture.Height, texture.Rgba);

        var paths = new List<string>(texture.MipLevels.Count) { topLevelPath };
        foreach (var mip in texture.MipLevels
                     .Where(static level => level.Level > 0)
                     .OrderBy(static level => level.Level))
        {
            var mipPath = BuildMipPath(topLevelPath, mip.Level);
            BinaryIO.ImageWriter.WritePng(mipPath, mip.Width, mip.Height, mip.Rgba);
            paths.Add(mipPath);
        }

        return paths;
    }

    internal static string BuildMipPath(string topLevelPath, int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        var directory = Path.GetDirectoryName(topLevelPath);
        var stem = Path.GetFileNameWithoutExtension(topLevelPath);
        var extension = Path.GetExtension(topLevelPath);
        var fileName = $"{stem}_mip{level}{extension}";
        return string.IsNullOrEmpty(directory) ? fileName : Path.Combine(directory, fileName);
    }
}
