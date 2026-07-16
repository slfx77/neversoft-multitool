using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     A game archive treated as a read-only filesystem: lazily-indexed entries,
///     thread-safe per-entry reads, and in-place opening of nested archives.
///     Obtain instances via <see cref="ArchiveFileSystem.TryOpen(string)" />.
/// </summary>
public interface IArchiveFileSystem : IDisposable
{
    /// <summary>"SKATE3.WAD" for a root archive; "SKATE3.WAD::pre/Ap.pre" for nested.</summary>
    string DisplayPath { get; }

    /// <summary>Real disk path of the outermost container file.</summary>
    string ContainerPath { get; }

    ArchiveAssetType Type { get; }

    /// <summary>0 for a root archive; children add 1 (capped at <see cref="ArchiveFileSystem.MaxNestingDepth" />).</summary>
    int NestingDepth { get; }

    /// <summary>The archive this one is nested inside, or null for a root archive.</summary>
    IArchiveFileSystem? Parent { get; }

    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    ///     Decompressed bytes of one entry. Safe for concurrent callers. Throws
    ///     <see cref="InvalidDataException" /> for entries whose data is not
    ///     present in the container (THUG WAD NO_WAD references, missing PAK
    ///     companions, out-of-range tables).
    /// </summary>
    byte[] ReadEntry(ArchiveEntry entry);

    /// <summary>Full relative-path lookup; case-insensitive; '\' and '/' both accepted.</summary>
    ArchiveEntry? FindByPath(string relativePath);

    /// <summary>Legacy flat basename match, first-wins (preserves the historical companion behavior).</summary>
    ArchiveEntry? FindByName(string basename);

    /// <summary>All entries sharing a basename — lets locators disambiguate by directory.</summary>
    IReadOnlyList<ArchiveEntry> FindAllByName(string basename);

    /// <summary>
    ///     Opens a nested archive entry (.pre/.prx/.prd/.prf/.prg/.pkr/.pak/.apk)
    ///     as a child filesystem. Null for non-archives, raw-data paks, parse
    ///     failures, or when the nesting depth cap is reached. The child holds a
    ///     reference to this parent; dispose the root last.
    /// </summary>
    IArchiveFileSystem? TryOpenNested(ArchiveEntry entry);
}
