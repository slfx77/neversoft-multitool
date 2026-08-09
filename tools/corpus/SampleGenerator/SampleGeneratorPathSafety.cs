using System.Text;
using static SampleGenerator.SampleGeneratorConfig;

namespace SampleGenerator;

/// <summary>
/// Canonical path handling shared by every extractor and output writer.
/// </summary>
internal static class SampleGeneratorPathSafety
{
    /// <summary>
    /// Resolves a relative path below <paramref name="destinationRoot" /> and rejects
    /// rooted paths, the root itself, and any normalized path that escapes the root.
    /// </summary>
    internal static string ResolveDestinationPath(string destinationRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot))
            throw new ArgumentException("A destination root is required.", nameof(destinationRoot));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new InvalidDataException("An output relative path is required.");
        if (Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Refusing rooted output path: {relativePath}");

        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        string candidate;
        try
        {
            candidate = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidDataException($"Invalid output path: {relativePath}", ex);
        }

        if (!IsStrictDescendant(candidate, normalizedRoot))
        {
            throw new InvalidDataException(
                $"Refusing output path outside destination root: {relativePath}");
        }

        RejectReparseTraversal(normalizedRoot, candidate);
        return candidate;
    }

    /// <summary>
    /// Proves that a caller-supplied output directory is below one of the two
    /// configured writable roots before any directory or file is created.
    /// </summary>
    internal static string ResolveConfiguredOutputDirectory(string outputDirectory)
    {
        var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        var configuredRoot = new[] { ResearchBuilds, SampleBuilds }
            .FirstOrDefault(root => IsStrictDescendant(candidate, root));
        if (configuredRoot == null)
        {
            throw new InvalidOperationException(
                $"Refusing output directory outside configured output roots: {candidate}");
        }

        RejectReparseTraversal(configuredRoot, candidate);
        return candidate;
    }

    /// <summary>
    /// Rejects an existing reparse point anywhere in the candidate's ancestor
    /// chain, including at or above the trusted root. Nonexistent suffixes are safe
    /// to stop at because a deeper component cannot exist until its parent does.
    /// </summary>
    internal static void RejectReparseTraversal(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        if (!PathsEqual(normalizedCandidate, normalizedRoot) &&
            !IsStrictDescendant(normalizedCandidate, normalizedRoot))
        {
            throw new InvalidOperationException(
                $"Cannot validate a path outside its destination root: {normalizedCandidate}");
        }

        var filesystemRoot = Path.GetPathRoot(normalizedCandidate)
            ?? throw new InvalidOperationException($"Output path has no filesystem root: {normalizedCandidate}");
        var relativePath = normalizedCandidate[filesystemRoot.Length..];
        var current = filesystemRoot;
        RejectIfReparsePoint(current);
        foreach (var segment in relativePath.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                RejectIfReparsePoint(current);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                break;
            }
        }
    }

    internal static bool IsStrictDescendant(string candidate, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;

        var normalizedCandidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return !normalizedCandidate.Equals(normalizedRoot, comparison) &&
               normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, comparison);
    }

    private static void RejectIfReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Refusing path through a reparse point: {path}");
    }

    private static bool PathsEqual(string left, string right)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return left.Equals(right, comparison);
    }

    /// <summary>
    /// Converts an on-disc filename to one host path segment. Slash and control
    /// characters are replaced, while traversal aliases are discarded entirely.
    /// </summary>
    internal static string SanitizeDiscPathSegment(string name)
    {
        if (string.IsNullOrEmpty(name))
            return string.Empty;

        var sanitized = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is '\\' or '/' || char.IsControl(c))
                sanitized.Append('_');
            else
                sanitized.Append(c);
        }

        var result = sanitized.ToString();
        return result is "." or ".." ? string.Empty : result;
    }
}
