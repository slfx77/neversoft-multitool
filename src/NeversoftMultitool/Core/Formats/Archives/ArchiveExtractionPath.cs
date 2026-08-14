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

            if (OperatingSystem.IsWindows())
                ValidateWindowsComponents(normalizedPath, pathKind, relativePath);

            return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"{pathKind} '{relativePath}' has an invalid extraction path", ex);
        }
    }

    private static InvalidDataException OutsideRoot(string pathKind, string path)
        => new($"{pathKind} '{path}' resolves outside its extraction directory");

    private static void ValidateWindowsComponents(string normalizedPath, string pathKind, string relativePath)
    {
        var invalidFileNameChars = Path.GetInvalidFileNameChars();
        foreach (var component in normalizedPath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            // Containment was already checked against the canonical path. Keep
            // benign dot segments valid while checking the original spelling,
            // which Windows path canonicalization can otherwise normalize away.
            if (component is "." or "..")
                continue;

            if (component.IndexOfAny(invalidFileNameChars) >= 0 ||
                component.EndsWith(' ') ||
                component.EndsWith('.') ||
                IsWindowsDeviceName(component))
            {
                throw new InvalidDataException(
                    $"{pathKind} '{relativePath}' contains a Windows-unsafe path component '{component}'");
            }
        }
    }

    private static bool IsWindowsDeviceName(string component)
    {
        var dotIndex = component.IndexOf('.');
        var device = dotIndex >= 0 ? component[..dotIndex] : component;
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return device.Length == 4 &&
               (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               device[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }
}
