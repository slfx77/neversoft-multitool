using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     Shared access to an archive's entry table plus per-entry byte reads.
///     One instance per archive (or nested archive); created once by a tab's
///     scanner and referenced by every <see cref="ArchiveAssetSource" /> it
///     produces. A thin compatibility shim over <see cref="IArchiveFileSystem" />
///     — reads are handle-based and thread-safe, with no whole-file buffering,
///     so archives beyond 2 GB open fine.
/// </summary>
public sealed class ArchiveAssetBackend
{
    // The historical tab-enumerable set. DDX/BON/ZIP/CUT open via
    // ArchiveFileSystem directly when a consumer opts in.
    private static readonly ArchiveAssetType[] LegacyEnumerableTypes =
    [
        ArchiveAssetType.Wad, ArchiveAssetType.Pre, ArchiveAssetType.CompressedPre,
        ArchiveAssetType.Pkr, ArchiveAssetType.Pak, ArchiveAssetType.N64
    ];

    private ArchiveAssetBackend(IArchiveFileSystem fileSystem)
    {
        FileSystem = fileSystem;
    }

    /// <summary>The underlying filesystem, for consumers that need nested opens or path lookups.</summary>
    public IArchiveFileSystem FileSystem { get; }

    /// <summary>Real disk path of the outermost container file.</summary>
    public string ArchivePath => FileSystem.ContainerPath;

    /// <summary>"SKATE3.WAD" for roots, "SKATE3.WAD::pre/Ap.pre" for nested archives.</summary>
    public string DisplayPath => FileSystem.DisplayPath;

    public ArchiveAssetType Type => FileSystem.Type;
    public IReadOnlyList<ArchiveEntry> Entries => FileSystem.Entries;

    /// <summary>
    ///     Probes <paramref name="path" /> and returns a backend if it's a supported
    ///     archive, or null otherwise. PAK archives that look like THAW worldzones
    ///     are filtered by the callers' scan gates — they go through the dedicated
    ///     worldzone pipeline instead of per-entry enumeration.
    /// </summary>
    public static ArchiveAssetBackend? TryOpen(string path)
    {
        var fs = ArchiveFileSystem.TryOpen(path);
        if (fs == null)
            return null;

        if (!LegacyEnumerableTypes.Contains(fs.Type))
        {
            fs.Dispose();
            return null;
        }

        return new ArchiveAssetBackend(fs);
    }

    /// <summary>
    ///     Decompressed/raw bytes of one entry from the archive. Thread-safe.
    /// </summary>
    public byte[] ReadEntryBytes(ArchiveEntry entry)
    {
        return FileSystem.ReadEntry(entry);
    }

    /// <summary>
    ///     Locate an entry by basename (case-insensitive). Directory prefixes are
    ///     ignored — companion lookups treat the archive as a flat namespace.
    /// </summary>
    public ArchiveEntry? FindEntry(string basename)
    {
        var matches = FindAllByName(basename);
        return matches.Count > 0 ? matches[0] : null;
    }

    /// <summary>Full relative-path lookup; case-insensitive; '\' and '/' both accepted.</summary>
    public ArchiveEntry? FindByPath(string relativePath)
    {
        return FileSystem.FindByPath(relativePath);
    }

    /// <summary>All entries sharing a basename — lets locators disambiguate by directory.</summary>
    public IReadOnlyList<ArchiveEntry> FindAllByName(string basename)
    {
        // Plaintext HED/WAD tables may store the full relative path in Name
        // rather than splitting it into Directory + Name. Preserve archive
        // order and honor this API's basename contract even when a table mixes
        // root names with path-bearing names.
        return Entries
            .Where(entry => string.Equals(
                EntryBasename(entry.Name), basename, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        static string EntryBasename(string value)
        {
            var separator = value.LastIndexOfAny(['/', '\\']);
            return separator >= 0 ? value[(separator + 1)..] : value;
        }
    }

    /// <summary>
    ///     Opens a nested archive entry (.pre/.prx/.pkr/.pak inside a WAD/PRE) as a
    ///     child backend, or null when the entry is not a parseable nested archive.
    /// </summary>
    public ArchiveAssetBackend? TryOpenNested(ArchiveEntry entry)
    {
        var child = FileSystem.TryOpenNested(entry);
        return child == null ? null : new ArchiveAssetBackend(child);
    }
}
