using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Shared entry-index and nested-open plumbing for the archive filesystems.
///     Indexes build lazily (scans list thousands of entries but look up few)
///     and are case-insensitive with '\'-vs-'/' normalized on both sides.
/// </summary>
#pragma warning disable S3881 // Dispose stays virtual to preserve the public 1.x subclassing contract.
public abstract class ArchiveFileSystemBase : IArchiveFileSystem
{
    private readonly Lazy<Dictionary<string, List<ArchiveEntry>>> _allByName;
    private readonly Lazy<Dictionary<string, ArchiveEntry>> _byName;
    private readonly Lazy<Dictionary<string, ArchiveEntry>> _byPath;
    private int _disposeState;

    protected ArchiveFileSystemBase(
        string displayPath, string containerPath, ArchiveAssetType type, int nestingDepth,
        IReadOnlyList<ArchiveEntry> entries, IArchiveFileSystem? parent)
    {
        DisplayPath = displayPath;
        ContainerPath = containerPath;
        Type = type;
        NestingDepth = nestingDepth;
        Entries = entries;
        Parent = parent;
        _byPath = new Lazy<Dictionary<string, ArchiveEntry>>(BuildPathIndex);
        _byName = new Lazy<Dictionary<string, ArchiveEntry>>(BuildNameIndex);
        _allByName = new Lazy<Dictionary<string, List<ArchiveEntry>>>(BuildAllByNameIndex);
    }

    public string DisplayPath { get; }
    public string ContainerPath { get; }
    public ArchiveAssetType Type { get; }
    public int NestingDepth { get; }
    public IArchiveFileSystem? Parent { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    public abstract byte[] ReadEntry(ArchiveEntry entry);

    protected static bool IsRangeWithin(long position, int size, long sourceLength)
    {
        return position >= 0
               && size >= 0
               && position <= sourceLength
               && size <= sourceLength - position;
    }

    public ArchiveEntry? FindByPath(string relativePath)
    {
        return _byPath.Value.TryGetValue(NormalizePath(relativePath), out var entry) ? entry : null;
    }

    public ArchiveEntry? FindByName(string basename)
    {
        return _byName.Value.TryGetValue(basename, out var entry) ? entry : null;
    }

    public IReadOnlyList<ArchiveEntry> FindAllByName(string basename)
    {
        return _allByName.Value.TryGetValue(basename, out var list) ? list : [];
    }

    public IArchiveFileSystem? TryOpenNested(ArchiveEntry entry)
    {
        if (NestingDepth >= ArchiveFileSystem.MaxNestingDepth)
            return null;

        var ext = ArchiveTypeDetector.GetArchiveExtension(entry.Name);
        if (ext is not (".pre" or ".prx" or ".prd" or ".prf" or ".prg" or ".pkr" or ".pak" or ".apk"))
            return null;

        byte[] data;
        try
        {
            data = ReadEntry(entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }

        // Nested PAKs may ship their companion data as a sibling entry in the
        // SAME parent (THAW DATAP.WAD holds anims.pak.ps2 + anims.pab.ps2).
        byte[]? companion = null;
        Func<byte[]>? reloadCompanion = null;
        if (ext is ".pak" or ".apk")
        {
            var pabName = Path.GetFileName(PakArchive.GetPabPath(entry.Name));
            var pabEntry = FindByPath(string.IsNullOrEmpty(entry.Directory) ? pabName : $"{entry.Directory}/{pabName}");
            if (pabEntry is { Size: > 32 })
            {
                try
                {
                    companion = ReadEntry(pabEntry);
                    reloadCompanion = () => ReadEntry(pabEntry);
                }
                catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
                {
                    companion = null;
                }
            }
        }

        return ArchiveFileSystem.TryOpenNested(
            data, entry.Name, $"{DisplayPath}::{entry.FullName}", ContainerPath, NestingDepth + 1,
            this, () => ReadEntry(entry), companion, reloadCompanion);
    }

    public virtual void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }

    private Dictionary<string, ArchiveEntry> BuildPathIndex()
    {
        var index = new Dictionary<string, ArchiveEntry>(Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
            index.TryAdd(NormalizePath(entry.FullName), entry);
        return index;
    }

    private Dictionary<string, ArchiveEntry> BuildNameIndex()
    {
        var index = new Dictionary<string, ArchiveEntry>(Entries.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            // If two entries share a basename (possible with directory prefixes),
            // the first one wins; consumers can use FindAllByName to disambiguate.
            index.TryAdd(entry.Name, entry);
        }

        return index;
    }

    private Dictionary<string, List<ArchiveEntry>> BuildAllByNameIndex()
    {
        var index = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Entries)
        {
            if (!index.TryGetValue(entry.Name, out var list))
            {
                list = [];
                index[entry.Name] = list;
            }

            list.Add(entry);
        }

        return index;
    }
}
#pragma warning restore S3881
