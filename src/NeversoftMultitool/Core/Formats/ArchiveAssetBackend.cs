using System.IO.Compression;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     Shared access to an archive's entry table plus per-entry byte reads.
///     One instance per archive path; created once by a tab's scanner and
///     referenced by every <see cref="ArchiveAssetSource" /> it produces, so the
///     archive file is memory-mapped once and entries are decompressed on
///     demand.
/// </summary>
public sealed class ArchiveAssetBackend
{
    private readonly byte[] _archiveBytes;
    private readonly Dictionary<string, ArchiveEntry> _entriesByBasename;

    public ArchiveAssetBackend(string archivePath, ArchiveAssetType type, IReadOnlyList<ArchiveEntry> entries)
    {
        ArchivePath = archivePath;
        Type = type;
        Entries = entries;
        _archiveBytes = File.ReadAllBytes(archivePath);
        _entriesByBasename = BuildBasenameIndex(entries);
    }

    public string ArchivePath { get; }
    public ArchiveAssetType Type { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    ///     Probes <paramref name="path" /> and returns a backend if it's a supported
    ///     archive, or null otherwise. PAK archives that look like THAW worldzones
    ///     are NOT returned here — they go through the dedicated worldzone
    ///     pipeline instead of per-entry enumeration.
    /// </summary>
    public static ArchiveAssetBackend? TryOpen(string path)
    {
        if (!File.Exists(path)) return null;

        var type = DetectType(path);
        if (type == null) return null;

        var entries = type switch
        {
            ArchiveAssetType.Wad => WadArchive.GetFileList(path),
            ArchiveAssetType.Pre => PreArchive.GetFileList(path),
            ArchiveAssetType.CompressedPre => CompressedPreArchive.GetFileList(path),
            ArchiveAssetType.Pkr => PkrArchive.GetFileList(path),
            ArchiveAssetType.Pak => PakArchive.GetFileList(path),
            _ => throw new InvalidOperationException()
        };

        return new ArchiveAssetBackend(path, type.Value, entries);
    }

    /// <summary>
    ///     Decompressed/raw bytes of one entry from the archive.
    /// </summary>
    public byte[] ReadEntryBytes(ArchiveEntry entry)
    {
        var offset = (int)entry.Offset;

        if (entry.IsCompressed && Type == ArchiveAssetType.CompressedPre)
        {
            var compressedSize = (int)entry.CompressedSize;
            var compressed = new byte[compressedSize];
            Array.Copy(_archiveBytes, offset, compressed, 0, compressedSize);
            return LzssDecoder.Decode(compressed, (int)entry.Size);
        }

        if (entry.IsCompressed && Type == ArchiveAssetType.Pkr)
        {
            var compressedSize = (int)entry.CompressedSize;
            using var input = new MemoryStream(_archiveBytes, offset, compressedSize);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            var output = new byte[entry.Size];
            zlib.ReadExactly(output);
            return output;
        }

        var size = (int)entry.Size;
        var raw = new byte[size];
        Array.Copy(_archiveBytes, offset, raw, 0, size);
        return raw;
    }

    /// <summary>
    ///     Locate an entry by basename (case-insensitive). Directory prefixes are
    ///     ignored — companion lookups treat the archive as a flat namespace.
    /// </summary>
    public ArchiveEntry? FindEntry(string basename)
    {
        return _entriesByBasename.TryGetValue(basename, out var entry) ? entry : null;
    }

    private static Dictionary<string, ArchiveEntry> BuildBasenameIndex(IReadOnlyList<ArchiveEntry> entries)
    {
        var index = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            // If two entries share a basename (rare but possible with PAK directory prefixes),
            // the first one wins; consumers can still iterate Entries directly if needed.
            index.TryAdd(entry.Name, entry);
        }

        return index;
    }

    private static ArchiveAssetType? DetectType(string path)
    {
        var lower = path.ToLowerInvariant();

        if (lower.EndsWith(".pak") || lower.EndsWith(".pak.ps2"))
            return PakArchive.IsPakArchive(path) ? ArchiveAssetType.Pak : null;

        if (lower.EndsWith(".pkr"))
            return ArchiveAssetType.Pkr;

        if (lower.EndsWith(".prx"))
            return ArchiveAssetType.CompressedPre;

        if (lower.EndsWith(".pre"))
            return CompressedPreArchive.IsCompressedPre(path)
                ? ArchiveAssetType.CompressedPre
                : ArchiveAssetType.Pre;

        if (lower.EndsWith(".wad"))
        {
            // WAD needs a sibling .HED — without it, the listing is unreadable.
            return File.Exists(WadArchive.GetHedPath(path)) ? ArchiveAssetType.Wad : null;
        }

        return null;
    }
}
