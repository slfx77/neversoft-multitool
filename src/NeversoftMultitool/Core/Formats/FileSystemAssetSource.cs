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
        _directory = Path.GetDirectoryName(filePath) ?? "";
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
        return File.Exists(Path.Combine(_directory, nameWithExtension));
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        var path = Path.Combine(_directory, nameWithExtension);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
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
        var path = Path.Combine(_directory, nameWithExtension);
        return File.Exists(path) ? path : null;
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
        if (subdirs == null || subdirs.Count == 0)
        {
            foreach (var ext in extensions)
            {
                var path = Path.Combine(_directory, stem + ext);
                if (File.Exists(path)) return path;
            }

            return null;
        }

        return CompanionSearch.FindCompanion(
            _directory,
            stem,
            extensions is string[] arr ? arr : [.. extensions],
            subdirs is string[] subArr ? subArr : [.. subdirs]);
    }
}
