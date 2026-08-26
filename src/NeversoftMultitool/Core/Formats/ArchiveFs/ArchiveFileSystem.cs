using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Nds;

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

        if (type == ArchiveAssetType.N64)
            return TryOpenN64Rom(path);

        if (type == ArchiveAssetType.Gba)
            return TryOpenGbaRom(path);

        // GOB entries are chunk CHAINS, not byte ranges — its own filesystem owns
        // both the .gob handle and the companion .gfc index.
        if (type == ArchiveAssetType.Gob)
            return TryOpenGob(path);

        // X360 compression-wrapped paks have no on-disk byte ranges — entry offsets
        // address the decompressed image, so they browse through materialized
        // buffers (the same backend nested paks use) instead of the disk-backed
        // range filesystem below.
        if (type == ArchiveAssetType.Pak)
        {
            try
            {
                if (PakArchive.TryLoadCompressedForBrowsing(path, out var pakData, out var pabData))
                {
                    var name = Path.GetFileName(path);
                    return TryOpenNested(
                        pakData, name, path, path, 0, null,
                        () => PakArchive.TryLoadCompressedForBrowsing(path, out var reloaded, out _)
                            ? reloaded
                            : File.ReadAllBytes(path),
                        pabData,
                        pabData == null
                            ? null
                            : () => PakArchive.TryLoadCompressedForBrowsing(path, out _, out var reloadedPab)
                                ? reloadedPab ?? []
                                : []);
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException
                                       or ArgumentException or OverflowException)
            {
                return null;
            }
        }

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
                // NDS Nitro FS entries are plain contiguous byte ranges, so the
                // disk-backed backend serves them like any other range archive.
                ArchiveAssetType.Nds => NdsRomArchive.GetFileList(path),
                // SDAT members are plain byte ranges of the FILE block.
                ArchiveAssetType.Sdat => SdatArchive.GetFileList(path),
                _ => throw new InvalidOperationException()
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException
                                   or ArgumentException or OverflowException)
        {
            return null;
        }

        if (type == ArchiveAssetType.Pre && entries.Count == 0)
            return null;

        var companionPath = type == ArchiveAssetType.Pak ? PakArchive.GetPabPath(path) : null;
        return new FileArchiveFileSystem(path, type.Value, entries, companionPath);
    }

    /// <summary>
    ///     Opens archive bytes with no disk container — synthetic buffers in
    ///     tests, or ad-hoc in-memory archives. The buffer is held strongly.
    /// </summary>
    public static IArchiveFileSystem? TryOpen(byte[] data, string entryName, string displayPath)
    {
        return TryOpenNested(data, entryName, displayPath, displayPath, 0, null,
            () => data, null, null);
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

        // A nested GOB is the .gob blob plus the sibling .gfc the parent supplied as
        // its companion; with no index there is nothing to enumerate.
        if (type == ArchiveAssetType.Gob)
        {
            if (companionData == null)
                return null;
            try
            {
                return GobArchiveFileSystem.Open(
                    data, companionData, displayPath, containerPath, nestingDepth, parent);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException or OverflowException)
            {
                return null;
            }
        }

        List<ArchiveEntry> entries;
        try
        {
            entries = type switch
            {
                ArchiveAssetType.Pre => PreArchive.GetFileList(data),
                ArchiveAssetType.CompressedPre => CompressedPreArchive.GetFileList(data),
                ArchiveAssetType.Pkr => PkrArchive.GetFileList(data),
                ArchiveAssetType.Pak => PakArchive.GetFileList(data, companionData != null, entryName),
                ArchiveAssetType.Sdat => SdatArchive.BuildFileList(data),
                _ => throw new InvalidOperationException()
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException
                                   or ArgumentException or OverflowException)
        {
            return null;
        }

        if (entries.Count == 0)
            return null;

        return new BufferArchiveFileSystem(
            data, displayPath, containerPath, type.Value, nestingDepth, entries, parent,
            reload, companionData, reloadCompanion);
    }

    /// <summary>
    ///     .gob open: parse the companion .gfc index and keep a handle on the data
    ///     blob. Nothing is buffered — chunks are pulled per read.
    /// </summary>
    private static GobArchiveFileSystem? TryOpenGob(string path)
    {
        try
        {
            return GobArchiveFileSystem.Open(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException
                                   or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    ///     .z64 open: carve the ROM's asset tree in memory (entries are
    ///     decompress+reassemble+carve products, not byte ranges — see
    ///     <see cref="CarvedArchiveFileSystem" />). ROMs without a master
    ///     directory (nothing in the corpus) return null; the extraction
    ///     paths keep their flat-scan fallback.
    /// </summary>
    private static CarvedArchiveFileSystem? TryOpenN64Rom(string path)
    {
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        if (!N64AssetCarver.TryCarve(rom, out var assets))
            return null;

        var entries = new List<ArchiveEntry>(assets.Count);
        var data = new List<byte[]>(assets.Count);
        foreach (var asset in assets)
        {
            var slash = asset.Path.LastIndexOf('/');
            entries.Add(new ArchiveEntry
            {
                Directory = slash > 0 ? asset.Path[..slash] : "",
                Name = slash > 0 ? asset.Path[(slash + 1)..] : asset.Path,
                Size = asset.Data.Length,
                Offset = data.Count
            });
            data.Add(asset.Data);
        }

        return new CarvedArchiveFileSystem(
            Path.GetFileName(path), path, ArchiveAssetType.N64, entries, data);
    }

    /// <summary>
    ///     .gba open: carve the Vicarious Visions level tree in memory (per-level
    ///     table records plus the ROM companion the records dereference into).
    /// </summary>
    private static CarvedArchiveFileSystem? TryOpenGbaRom(string path)
    {
        byte[] rom;
        try
        {
            rom = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }

        var assets = Gba.GbaLevelCarver.Carve(rom);
        if (assets.Count == 0)
            return null;

        var entries = new List<ArchiveEntry>(assets.Count);
        var data = new List<byte[]>(assets.Count);
        foreach (var (assetPath, assetData) in assets)
        {
            var slash = assetPath.LastIndexOf('/');
            entries.Add(new ArchiveEntry
            {
                Directory = slash > 0 ? assetPath[..slash] : "",
                Name = slash > 0 ? assetPath[(slash + 1)..] : assetPath,
                Size = assetData.Length,
                Offset = data.Count
            });
            data.Add(assetData);
        }

        return new CarvedArchiveFileSystem(
            Path.GetFileName(path), path, ArchiveAssetType.Gba, entries, data);
    }
}
