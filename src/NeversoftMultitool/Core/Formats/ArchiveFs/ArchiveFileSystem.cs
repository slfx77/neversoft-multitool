using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Factory for <see cref="IArchiveFileSystem" /> instances. Folds the
///     per-format detection and entry-listing dispatch that used to be
///     duplicated across the unpacker, CLI, probes, and tab backends.
/// </summary>
public static class ArchiveFileSystem
{
    /// <summary>
    ///     Nesting cap: WAD → PRE → PAK is the deepest real corpus shape; 4 is a
    ///     backstop against pathological self-referential containers.
    /// </summary>
    public const int MaxNestingDepth = 4;

    /// <summary>
    ///     Opens an on-disk archive, or returns null when the file is not a
    ///     supported/parseable archive (raw-data paks, WADs without .HED, …).
    /// </summary>
    public static IArchiveFileSystem? TryOpen(string path)
    {
        if (!File.Exists(path))
            return null;

        var type = ArchiveTypeDetector.DetectAssetType(path);
        if (type == null)
            return null;

        List<ArchiveEntry> entries;
        try
        {
            entries = type switch
            {
                ArchiveAssetType.Wad => WadArchive.GetFileList(path),
                ArchiveAssetType.Pre => PreArchive.GetFileList(path),
                ArchiveAssetType.CompressedPre => CompressedPreArchive.GetFileList(path),
                ArchiveAssetType.Pkr => PkrArchive.GetFileList(path),
                ArchiveAssetType.Pak => PakArchive.GetFileList(path),
                ArchiveAssetType.Ddx => DdxArchive.GetFileList(path),
                ArchiveAssetType.Bon => BonArchive.GetFileList(path),
                ArchiveAssetType.Zip => QZipArchive.GetFileList(path),
                ArchiveAssetType.Cut => CutArchive.GetFileList(path),
                _ => throw new InvalidOperationException()
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }

        var companionPath = type == ArchiveAssetType.Pak ? PakArchive.GetPabPath(path) : null;
        return new FileArchiveFileSystem(path, type.Value, entries, companionPath);
    }

    /// <summary>
    ///     Opens archive bytes with no disk container — synthetic buffers in
    ///     tests, or ad-hoc in-memory archives. The buffer is held strongly.
    /// </summary>
    public static IArchiveFileSystem? TryOpen(byte[] data, string entryName, string displayPath)
    {
        return TryOpenNested(data, entryName, displayPath, displayPath, nestingDepth: 0, parent: null,
            () => data, companionData: null, reloadCompanion: null);
    }

    /// <summary>
    ///     Nested/in-memory open used by <see cref="ArchiveFileSystemBase.TryOpenNested" />.
    /// </summary>
    internal static IArchiveFileSystem? TryOpenNested(
        byte[] data, string entryName, string displayPath, string containerPath, int nestingDepth,
        IArchiveFileSystem? parent, Func<byte[]> reload, byte[]? companionData, Func<byte[]>? reloadCompanion)
    {
        var type = ArchiveTypeDetector.DetectNestedAssetType(entryName, data);
        if (type == null)
            return null;

        List<ArchiveEntry> entries;
        try
        {
            entries = type switch
            {
                ArchiveAssetType.Pre => PreArchive.GetFileList(data),
                ArchiveAssetType.CompressedPre => CompressedPreArchive.GetFileList(data),
                ArchiveAssetType.Pkr => PkrArchive.GetFileList(data),
                ArchiveAssetType.Pak => PakArchive.GetFileList(data, hasPab: companionData != null),
                _ => throw new InvalidOperationException()
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            return null;
        }

        if (entries.Count == 0)
            return null;

        return new BufferArchiveFileSystem(
            data, displayPath, containerPath, type.Value, nestingDepth, entries, parent,
            reload, companionData, reloadCompanion);
    }
}
