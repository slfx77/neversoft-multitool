using Microsoft.Win32.SafeHandles;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Archive filesystem over the Vicarious Visions DS GOB container. Unlike every
///     other container here, one entry is not one byte range: a logical file is a
///     CHAIN of chunks scattered through the <c>.gob</c>, each independently stored
///     or deflated, so neither <see cref="FileArchiveFileSystem" />'s single-range
///     read nor <see cref="ArchiveEntryDecoder" />'s single-codec decode applies.
///     <see cref="ArchiveEntry.Offset" /> indexes the index's file table instead.
///
///     The data lives either on disk (a <c>.gob</c> beside its <c>.gfc</c>, read
///     through <see cref="RandomAccess" /> with nothing buffered) or in memory (the
///     pair opened in place inside a <c>.nds</c>, where the parent hands over the
///     already-read bytes — 21-60 MB depending on the cart).
/// </summary>
public sealed class GobArchiveFileSystem : ArchiveFileSystemBase
{
    private readonly SafeFileHandle? _handle;
    private readonly GobIndex _index;
    private readonly GobDataReader _read;

    private GobArchiveFileSystem(
        string displayPath,
        string containerPath,
        int nestingDepth,
        IArchiveFileSystem? parent,
        GobIndex index,
        GobDataReader read,
        SafeFileHandle? handle)
        : base(displayPath, containerPath, ArchiveAssetType.Gob,
            nestingDepth, GobArchive.BuildFileList(index, read), parent)
    {
        _index = index;
        _read = read;
        _handle = handle;
    }

    /// <summary>Opens a <c>.gob</c> on disk together with its companion <c>.gfc</c>.</summary>
    internal static GobArchiveFileSystem Open(string gobPath)
    {
        var handle = File.OpenHandle(
            gobPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        try
        {
            // Validate the index against the length of the handle we actually
            // opened rather than re-stating the path.
            var length = RandomAccess.GetLength(handle);
            var index = GobArchive.ReadIndex(File.ReadAllBytes(GobArchive.GetIndexPath(gobPath)), length);
            return new GobArchiveFileSystem(
                Path.GetFileName(gobPath), gobPath, 0, null, index,
                GobArchive.CreateReader(handle, length), handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Opens a pair already read out of a parent archive (a <c>.nds</c> cart).</summary>
    internal static GobArchiveFileSystem Open(
        byte[] gobData, byte[] indexData, string displayPath, string containerPath,
        int nestingDepth, IArchiveFileSystem? parent)
    {
        var index = GobArchive.ReadIndex(indexData, gobData.LongLength);
        return new GobArchiveFileSystem(
            displayPath, containerPath, nestingDepth, parent, index,
            GobArchive.CreateReader(gobData), null);
    }

    public override byte[] ReadEntry(ArchiveEntry entry)
    {
        if (entry.Offset < 0 || entry.Offset >= _index.Files.Count)
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: entry index {entry.Offset} is outside the GOB file table.");

        return GobArchive.ReadFile(_index, (int)entry.Offset, _read);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _handle?.Dispose();
        base.Dispose(disposing);
    }
}
