namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Plans archive extraction paths that remain lexically contained beneath a caller-owned root.
/// </summary>
internal static class ArchiveExtractionPath
{
    public static string GetContainedPath(string root, string relativePath, string pathKind)
    {
        // Following pre-existing filesystem links beneath the output directory is a separate policy.
        try
        {
            var normalizedPath = relativePath
                .Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(normalizedPath))
                throw OutsideRoot(pathKind, relativePath);

            var canonicalRoot = Path.GetFullPath(root);
            var candidate = Path.GetFullPath(Path.Combine(canonicalRoot, normalizedPath));
            var relativeCandidate = Path.GetRelativePath(canonicalRoot, candidate);
            var escapesRoot = relativeCandidate is "." or ".." ||
                              Path.IsPathRooted(relativeCandidate) ||
                              relativeCandidate.StartsWith($"..{Path.DirectorySeparatorChar}",
                                  StringComparison.Ordinal) ||
                              relativeCandidate.StartsWith($"..{Path.AltDirectorySeparatorChar}",
                                  StringComparison.Ordinal);
            if (escapesRoot)
                throw OutsideRoot(pathKind, relativePath);

            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{pathKind} '{relativePath}' has an invalid extraction path", ex);
        }
    }

    private static InvalidDataException OutsideRoot(string pathKind, string path)
        => new($"{pathKind} '{path}' resolves outside its extraction directory");
}
