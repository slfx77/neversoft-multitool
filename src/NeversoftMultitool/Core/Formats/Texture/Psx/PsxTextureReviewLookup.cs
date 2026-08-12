namespace NeversoftMultitool.Core.Formats.Texture.Psx;

/// <summary>
///     Resolves Hash Reviewer texture previews from legacy basename-only source
///     references without letting filesystem enumeration order select between
///     conflicting assets.
/// </summary>
internal static class PsxTextureReviewLookup
{
    public static (byte[] Rgba, int Width, int Height)? TryExtractFromAllFiles(
        string buildsDir,
        IReadOnlyList<string> fileNames,
        uint targetHash,
        List<string> diagnostics)
    {
        var files = FindPsxFiles(buildsDir, fileNames);
        if (files.Count == 0)
        {
            diagnostics.Insert(0, $"No PSX files found for: {string.Join(", ", fileNames)}");
            return null;
        }

        (byte[] Rgba, int Width, int Height)? accepted = null;
        var successfulFiles = new List<string>();
        var hasConflict = false;
        foreach (var psxPath in files)
        {
            var result = PsxLibrary.ExtractTextureByHash(psxPath, targetHash, diagnostics);
            if (result == null)
                continue;

            successfulFiles.Add(GetDisplayPath(buildsDir, psxPath));
            if (accepted == null)
            {
                accepted = result;
                continue;
            }

            hasConflict |= !Matches(accepted.Value, result.Value);
        }

        if (hasConflict)
        {
            diagnostics.Insert(
                0,
                $"Texture hash 0x{targetHash:X8} has conflicting previews in " +
                $"{successfulFiles.Count} PSX files: {string.Join("; ", successfulFiles)}");
            return null;
        }

        return accepted;
    }

    private static IReadOnlyList<string> FindPsxFiles(
        string buildsDir,
        IReadOnlyList<string> fileNames)
    {
        var matches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fileName in fileNames)
        {
            try
            {
                matches.UnionWith(Directory.EnumerateFiles(
                    buildsDir,
                    fileName,
                    SearchOption.AllDirectories));
            }
            catch (Exception ex) when (ex is IOException
                                           or UnauthorizedAccessException
                                           or ArgumentException
                                           or System.Security.SecurityException)
            {
                // Preserve the reviewer's best-effort lookup behavior. Other
                // source references may still resolve successfully.
            }
        }

        return matches
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
    }

    private static bool Matches(
        (byte[] Rgba, int Width, int Height) first,
        (byte[] Rgba, int Width, int Height) second)
    {
        return first.Width == second.Width
               && first.Height == second.Height
               && first.Rgba.AsSpan().SequenceEqual(second.Rgba);
    }

    private static string GetDisplayPath(string buildsDir, string file)
    {
        try
        {
            return Path.GetRelativePath(buildsDir, file);
        }
        catch (ArgumentException)
        {
            return file;
        }
    }
}
