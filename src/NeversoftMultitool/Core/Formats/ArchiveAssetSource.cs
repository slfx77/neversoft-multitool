using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     <see cref="AssetSource" /> backed by a single entry inside an archive.
///     Shares an <see cref="ArchiveAssetBackend" /> with every other entry from
///     the same archive, so the archive bytes are read once and entry lookups
///     are a cheap dictionary hit. Companion resolution is a flat basename
///     match over the backend's entry index — directory prefixes inside a PAK
///     are ignored for companion purposes. <see cref="FileSystemPath" /> is
///     always null; callers that truly need a path must degrade to byte-based
///     APIs.
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

    public override string DisplayName => $"{Path.GetFileName(Backend.ArchivePath)}::{Entry.Name}";
    public override string EntryName => Entry.Name;

    public override byte[] ReadBytes()
    {
        return Backend.ReadEntryBytes(Entry);
    }

    public override bool CompanionExists(string nameWithExtension)
    {
        return Backend.FindEntry(nameWithExtension) != null;
    }

    public override byte[]? TryReadCompanion(string nameWithExtension)
    {
        var entry = Backend.FindEntry(nameWithExtension);
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
}
