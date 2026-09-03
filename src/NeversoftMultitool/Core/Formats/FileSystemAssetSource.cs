namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     <see cref="AssetSource" /> backed by a real file on disk. Companion lookups
///     walk the filesystem via <see cref="CompanionSearch" />.
/// </summary>
public sealed class FileSystemAssetSource : AssetSource
{
    private readonly string _directory;

    public FileSystemAssetSource(string filePath)
    {
        DisplayName = filePath;
        _directory = Path.GetDirectoryName(Path.GetFullPath(filePath))
                     ?? Directory.GetCurrentDirectory();
    }

    public override string DisplayName { get; }

    public override string EntryName => Path.GetFileName(DisplayName);
    public override string? FileSystemPath => DisplayName;

    public override byte[] ReadBytes()
    {
        return File.ReadAllBytes(DisplayName);
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return ResolveCompanionPath(nameWithExtension) != null;
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        var path = ResolveCompanionPath(nameWithExtension);
        return path != null ? File.ReadAllBytes(path) : null;
    }

    public override byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        var path = ResolveCompanionPath(stem, extensions, subdirs);
        return path != null ? File.ReadAllBytes(path) : null;
    }

    public override string? TryResolveCompanionPath(string nameWithExtension)
    {
        return ResolveCompanionPath(nameWithExtension);
    }

    public override string? TryResolveCompanionPath(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        return ResolveCompanionPath(stem, extensions, subdirs);
    }

    private string? ResolveCompanionPath(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs)
    {
        var validExtensions = extensions
            .Where(extension => IsCompanionBasename(stem + extension))
            .ToArray();
        if (validExtensions.Length == 0)
            return null;

        foreach (var ext in validExtensions)
        {
            var path = ResolveCompanionPath(stem + ext);
            if (path != null) return path;
        }

        if (subdirs == null || subdirs.Count == 0)
            return null;

        var validSubdirs = subdirs
            .Where(IsCompanionBasename)
            .ToArray();

        return CompanionSearch.FindCompanion(
            _directory,
            stem,
            validExtensions,
            validSubdirs);
    }

    private string? ResolveCompanionPath(string nameWithExtension)
    {
        if (!IsCompanionBasename(nameWithExtension))
            return null;

        var path = Path.Combine(_directory, nameWithExtension);
        if (File.Exists(path))
            return path;
        if (OperatingSystem.IsWindows())
            return null;

        // Disc/archive extraction commonly preserves all-uppercase console
        // names. Companion identity is basename-based and archive lookup is
        // already case-insensitive, so provide the same behavior on
        // case-sensitive filesystems. Multiple case-folded matches are
        // ambiguous and deliberately fail closed.
        if (!Directory.Exists(_directory))
            return null;

        string? match = null;
        try
        {
            foreach (var candidate in Directory.EnumerateFiles(_directory))
            {
                if (!Path.GetFileName(candidate).Equals(
                        nameWithExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (match != null)
                    return null;
                match = candidate;
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return match;
    }

    private static bool IsCompanionBasename(string name)
    {
        return !string.IsNullOrEmpty(name)
               && name is not "." and not ".."
               && !Path.IsPathRooted(name)
               && !name.Contains('/')
               && !name.Contains('\\');
    }
}
