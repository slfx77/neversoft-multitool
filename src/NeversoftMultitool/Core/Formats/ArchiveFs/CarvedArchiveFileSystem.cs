using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Archive filesystem over containers whose entries are TRANSFORMATION
///     PRODUCTS rather than byte ranges of the container — N64 .z64 ROMs,
///     where each asset is the result of ERZ decompression, 16KB-stream
///     reassembly, and typed carving. The full asset set is materialized at
///     open (a carved ROM is ~20-40 MB decoded) and held for the filesystem's
///     lifetime; <see cref="ArchiveEntry.Offset" /> indexes the asset list.
/// </summary>
public sealed class CarvedArchiveFileSystem : ArchiveFileSystemBase
{
    private readonly IReadOnlyList<byte[]> _assetData;

    internal CarvedArchiveFileSystem(
        string displayPath,
        string containerPath,
        ArchiveAssetType type,
        IReadOnlyList<ArchiveEntry> entries,
        IReadOnlyList<byte[]> assetData)
        : base(displayPath, containerPath, type, 0, entries, null)
    {
        _assetData = assetData;
    }

    public override byte[] ReadEntry(ArchiveEntry entry)
    {
        if (entry.Offset < 0 || entry.Offset >= _assetData.Count)
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: entry index {entry.Offset} is outside the carved asset list.");

        // Callers may mutate returned buffers (the disk-backed filesystems
        // return fresh arrays per read) — hand out a copy.
        return (byte[])_assetData[(int)entry.Offset].Clone();
    }
}
