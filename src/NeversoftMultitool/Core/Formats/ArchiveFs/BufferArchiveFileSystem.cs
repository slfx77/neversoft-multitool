using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     In-memory archive filesystem for nested archives (a PRE inside a WAD).
///     The stored (still-compressed) container bytes are held through a weak
///     reference so long-lived tab lists don't pin hundreds of megabytes: under
///     memory pressure the buffer is collected and transparently re-read from
///     the parent on the next entry access.
/// </summary>
public sealed class BufferArchiveFileSystem : ArchiveFileSystemBase
{
    private readonly Lock _bufferLock = new();

    // The reload closures capture the parent filesystem, which both revives a
    // collected buffer and keeps the root's file handle reachable.
    private readonly Func<byte[]> _reload;
    private readonly Func<byte[]>? _reloadCompanion;
    private WeakReference<byte[]> _buffer;
    private WeakReference<byte[]>? _companion;

    internal BufferArchiveFileSystem(
        byte[] data, string displayPath, string containerPath, ArchiveAssetType type, int nestingDepth,
        IReadOnlyList<ArchiveEntry> entries, IArchiveFileSystem? parent,
        Func<byte[]> reload, byte[]? companionData, Func<byte[]>? reloadCompanion)
        : base(displayPath, containerPath, type, nestingDepth, entries, parent)
    {
        _reload = reload;
        _reloadCompanion = reloadCompanion;
        _buffer = new WeakReference<byte[]>(data);
        _companion = companionData != null ? new WeakReference<byte[]>(companionData) : null;
    }

    public override byte[] ReadEntry(ArchiveEntry entry)
    {
        if (entry.Size > int.MaxValue)
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: entry size {entry.Size} exceeds the 2 GB per-entry limit.");

        var data = GetBuffer();
        var storedSize = checked((int)ArchiveEntryDecoder.GetStoredSize(Type, entry));

        var source = data;
        var position = entry.Offset;
        if (Type == ArchiveAssetType.Pak)
        {
            var companion = GetCompanion();
            if (entry.InCompanion)
            {
                source = companion
                         ?? throw new InvalidDataException(
                             $"{DisplayPath}::{entry.FullName}: companion-resident entry but no data file found.");
            }
            else if (!IsRangeWithin(position, storedSize, data.LongLength) && companion != null)
            {
                source = companion;
                position -= data.Length;
            }
        }

        if (!IsRangeWithin(position, storedSize, source.LongLength))
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: data range starting at {position} with length {storedSize} " +
                $"is outside the container (length {source.Length}).");

        var stored = new byte[storedSize];
        Array.Copy(source, position, stored, 0, storedSize);
        return ArchiveEntryDecoder.Decode(Type, entry, stored);
    }

    private byte[] GetBuffer()
    {
        lock (_bufferLock)
        {
            if (_buffer.TryGetTarget(out var data))
                return data;

            data = _reload();
            _buffer = new WeakReference<byte[]>(data);
            return data;
        }
    }

    private byte[]? GetCompanion()
    {
        if (_companion == null)
            return null;

        lock (_bufferLock)
        {
            if (_companion.TryGetTarget(out var data))
                return data;

            if (_reloadCompanion == null)
                return null;

            data = _reloadCompanion();
            _companion = new WeakReference<byte[]>(data);
            return data;
        }
    }
}
