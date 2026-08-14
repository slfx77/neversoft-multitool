using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     <see cref="AssetSource" /> backed by a single entry inside an archive.
///     Shares an <see cref="ArchiveAssetBackend" /> with every other entry from
///     the same archive, so the archive bytes are read once and entry lookups
///     are a cheap dictionary hit. Companion resolution uses a unique basename
///     match in the selected entry's directory, then falls back only when that
///     basename is unique across the archive; ambiguous matches are left
///     unresolved rather than chosen by archive order.
///     <see cref="AssetSource.FileSystemPath" /> is always null; callers that
///     truly need a path must degrade to byte-based APIs.
/// </summary>
public sealed class ArchiveAssetSource : AssetSource
{
    public ArchiveAssetSource(ArchiveAssetBackend backend, ArchiveEntry entry)
    {
        Backend = backend;
        Entry = entry;
    }

    /// <summary>The archive this source reads from; exposed so consumers that need</summary>
    /// <summary>to enumerate all entries (e.g. skeleton scoring) can.</summary>
    public ArchiveAssetBackend Backend { get; }

    /// <summary>The specific entry this source represents within the archive.</summary>
    public ArchiveEntry Entry { get; }

    public override string DisplayName => $"{Backend.DisplayPath}::{Entry.FullName}";
    public override string EntryName => Entry.Name;

    public override byte[] ReadBytes()
    {
        return Backend.ReadEntryBytes(Entry);
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return FindCompanionEntry(nameWithExtension) != null;
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        var entry = FindCompanionEntry(nameWithExtension);
        return entry == null ? null : Backend.ReadEntryBytes(entry);
    }

    public override byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        // Subdirs are ignored — archive entries are treated as a flat namespace.
        foreach (var ext in extensions)
        {
            var bytes = TryReadCompanion(stem + ext);
            if (bytes != null) return bytes;
        }

        return null;
    }

    private ArchiveEntry? FindCompanionEntry(string nameWithExtension)
    {
        var candidates = Backend.FindAllByName(nameWithExtension);
        if (candidates.Count == 0)
            return null;

        // Archives such as THAW DATAP.WAD contain same-named male/female CAS
        // textures. The game resolves those relative to the model directory;
        // a flat first-wins lookup silently binds the other model's texture.
        var selectedDirectory = GetEntryDirectory(Entry);
        ArchiveEntry? sameDirectory = null;
        foreach (var candidate in candidates)
        {
            if (!string.Equals(
                    GetEntryDirectory(candidate),
                    selectedDirectory,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (sameDirectory != null)
                return null;
            sameDirectory = candidate;
        }

        if (sameDirectory != null)
            return sameDirectory;

        // Preserve the legacy flat fallback only when archive-wide ownership
        // is unambiguous. Multiple remote matches have no safe first choice.
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static string GetEntryDirectory(ArchiveEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Directory))
            return entry.Directory.Replace('\\', '/').Trim('/');

        var normalized = entry.Name.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator].Trim('/');
    }
}
