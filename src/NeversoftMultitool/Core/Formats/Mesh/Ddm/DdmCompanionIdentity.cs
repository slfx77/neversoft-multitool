namespace NeversoftMultitool.Core.Formats.Mesh.Ddm;

/// <summary>
///     Identifies a DDM and its placed-PSX companion by directory and stem.
///     Basenames alone are not identities when a recursive scan contains
///     same-named levels in different folders.
/// </summary>
internal static class DdmCompanionIdentity
{
    public static HashSet<string> FindCompanionPsxPaths(
        IReadOnlyList<string> ddmPaths,
        IReadOnlyList<string> psxPaths,
        Func<string, bool>? fileExists = null)
    {
        fileExists ??= File.Exists;
        var normalizedPsx = psxPaths
            .Select(static path => (Path: path, Normalized: Normalize(path)))
            .ToArray();
        var companions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ddmPath in ddmPaths)
        {
            var directory = Path.GetDirectoryName(ddmPath) ?? string.Empty;
            var stem = Path.GetFileNameWithoutExtension(ddmPath);
            var expectedPath = Path.Combine(directory, stem + ".psx");
            if (!fileExists(expectedPath))
                continue;

            var expected = Normalize(expectedPath);
            var exact = normalizedPsx.FirstOrDefault(candidate =>
                string.Equals(candidate.Normalized, expected, StringComparison.Ordinal));
            if (exact.Path != null)
            {
                companions.Add(exact.Path);
                continue;
            }

            // A case-insensitive filesystem can resolve expectedPath even when
            // Directory.EnumerateFiles returned the entry's preserved casing.
            // Only coalesce a unique match: case-sensitive stores can contain
            // two distinct files whose names differ solely by case.
            var folded = normalizedPsx.Where(candidate =>
                string.Equals(candidate.Normalized, expected, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (folded.Length == 1)
                companions.Add(folded[0].Path);
        }

        return companions;
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}
