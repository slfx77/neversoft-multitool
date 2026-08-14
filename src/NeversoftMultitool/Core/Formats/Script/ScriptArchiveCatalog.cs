using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Script;

/// <summary>
///     One script entry discovered in an archive. The source retains its exact
///     filesystem/entry identity, while the full virtual display name prevents
///     equal basenames in different directories or nested archives from collapsing.
/// </summary>
internal sealed record ScriptAssetCandidate(
    AssetSource Source,
    string DisplayName,
    ScriptAssetKind Kind);

/// <summary>
///     Bounded breadth-first script browser over a root archive and its nested
///     archives. The catalog owns every archive filesystem it opens; callers must
///     keep it alive until all candidate bytes have been parsed, then dispose it.
/// </summary>
internal sealed class ScriptArchiveCatalog : IDisposable
{
    private static readonly HashSet<string> EnumerableArchiveExtensions =
    [
        ".wad", ".pre", ".prx", ".prd", ".prf", ".prg",
        ".pkr", ".ddx", ".bon", ".pak", ".apk", ".zip", ".cut", ".z64"
    ];

    private readonly List<IArchiveFileSystem> _ownedFileSystems;
    private bool _disposed;

    private ScriptArchiveCatalog(
        List<IArchiveFileSystem> ownedFileSystems,
        IReadOnlyList<ScriptAssetCandidate> candidates)
    {
        _ownedFileSystems = ownedFileSystems;
        Candidates = candidates;
    }

    /// <summary>
    ///     Final suffixes accepted by FileOpenPicker. Platform suffixes reach
    ///     compound archives such as scripts.pak.wpc; archive content is still
    ///     validated by <see cref="ArchiveFileSystem" /> after selection.
    /// </summary>
    public static IReadOnlyList<string> PickerExtensions { get; } =
        ArchiveTypeDetector.ArchiveExtensions
            .Where(EnumerableArchiveExtensions.Contains)
            .Concat([".ps2", ".wpc", ".ngc", ".xbx", ".xen", ".n64"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<ScriptAssetCandidate> Candidates { get; }

    public static ScriptArchiveCatalog Open(
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        cancellationToken.ThrowIfCancellationRequested();

        var root = ArchiveFileSystem.TryOpen(archivePath)
                   ?? throw new InvalidDataException(
                       $"'{Path.GetFileName(archivePath)}' is not a supported enumerable archive.");
        return Open(root, cancellationToken);
    }

    /// <summary>
    ///     Opens an already-created filesystem and assumes ownership of it. This
    ///     overload also provides a fully in-memory seam for focused tests.
    /// </summary>
    internal static ScriptArchiveCatalog Open(
        IArchiveFileSystem root,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var owned = new List<IArchiveFileSystem> { root };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = new List<ScriptAssetCandidate>();
            var pending = new Queue<IArchiveFileSystem>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fileSystem = pending.Dequeue();
                foreach (var entry in fileSystem.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var kind = ScriptAssetParser.ClassifyEntryName(entry.Name);
                    if (kind is { } scriptKind)
                    {
                        var source = new ScriptArchiveAssetSource(fileSystem, entry);
                        candidates.Add(new ScriptAssetCandidate(
                            source,
                            source.DisplayName,
                            scriptKind));
                    }

                    var nested = fileSystem.TryOpenNested(entry);
                    if (nested == null)
                        continue;

                    owned.Add(nested);
                    pending.Enqueue(nested);
                }
            }

            var ordered = candidates
                .OrderBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.DisplayName, StringComparer.Ordinal)
                .ToArray();
            return new ScriptArchiveCatalog(owned, ordered);
        }
        catch
        {
            DisposeFileSystems(owned);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        DisposeFileSystems(_ownedFileSystems);
    }

    private static void DisposeFileSystems(List<IArchiveFileSystem> fileSystems)
    {
        // Nested filesystems retain reload delegates into their parents. Release
        // children first and the root last so no live child observes a closed root.
        for (var index = fileSystems.Count - 1; index >= 0; index--)
            fileSystems[index].Dispose();
    }
}

/// <summary>
///     AssetSource adapter over a single archive-filesystem entry. This uses the
///     complete ArchiveFileSystem type set rather than ArchiveAssetBackend's
///     intentionally narrower legacy tab filter.
/// </summary>
internal sealed class ScriptArchiveAssetSource(
    IArchiveFileSystem fileSystem,
    ArchiveEntry entry) : AssetSource
{
    public override string DisplayName => $"{fileSystem.DisplayPath}::{entry.FullName}";

    public override string EntryName => EntryBasename(entry.Name);

    public override byte[] ReadBytes() => fileSystem.ReadEntry(entry);

    public override bool CompanionExists(string nameWithExtension) =>
        FindCompanionEntry(nameWithExtension) != null;

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        var companion = FindCompanionEntry(nameWithExtension);
        return companion == null ? null : fileSystem.ReadEntry(companion);
    }

    public override byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        foreach (var extension in extensions)
        {
            var bytes = TryReadCompanion(stem + extension);
            if (bytes != null)
                return bytes;
        }

        return null;
    }

    private ArchiveEntry? FindCompanionEntry(string nameWithExtension)
    {
        var candidates = fileSystem.Entries
            .Where(candidate => string.Equals(
                EntryBasename(candidate.Name),
                nameWithExtension,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (candidates.Length == 0)
            return null;

        var sourceDirectory = EntryDirectory(entry);
        return candidates.FirstOrDefault(candidate => string.Equals(
                   EntryDirectory(candidate),
                   sourceDirectory,
                   StringComparison.OrdinalIgnoreCase))
               ?? candidates[0];
    }

    private static string EntryBasename(string name)
    {
        var separator = name.LastIndexOfAny(['/', '\\']);
        return separator < 0 ? name : name[(separator + 1)..];
    }

    private static string EntryDirectory(ArchiveEntry value)
    {
        if (!string.IsNullOrEmpty(value.Directory))
            return value.Directory.Replace('\\', '/').Trim('/');

        var normalized = value.Name.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator].Trim('/');
    }
}
