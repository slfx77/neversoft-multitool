using Microsoft.Win32.SafeHandles;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.ArchiveFs;

/// <summary>
///     Disk-backed archive filesystem. Reads go through
///     <see cref="RandomAccess" /> on a single shared handle — positionless and
///     thread-safe, with no whole-file buffering, so multi-gigabyte WADs open
///     without the 2 GB <c>File.ReadAllBytes</c> cap the old backend had.
/// </summary>
public sealed class FileArchiveFileSystem : ArchiveFileSystemBase
{
    private readonly SafeFileHandle? _companionHandle;
    private readonly long _companionLength;
    private readonly SafeFileHandle _handle;
    private readonly long _length;

    internal FileArchiveFileSystem(
        string path, ArchiveAssetType type, IReadOnlyList<ArchiveEntry> entries, string? companionPath)
        : base(Path.GetFileName(path), path, type, 0, entries, null)
    {
        _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
        _length = RandomAccess.GetLength(_handle);

        // GC ships 32-byte 0xAB/0xCD stub .mpk.ngc files next to self-contained
        // archives — those don't count as real companion data.
        if (companionPath != null && File.Exists(companionPath) && new FileInfo(companionPath).Length > 32)
        {
            _companionHandle = File.OpenHandle(
                companionPath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);
            _companionLength = RandomAccess.GetLength(_companionHandle);
        }
    }

    public override byte[] ReadEntry(ArchiveEntry entry)
    {
        if (entry.Size > int.MaxValue)
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: entry size {entry.Size} exceeds the 2 GB per-entry limit.");

        // THUG-era WAD NO_WAD flag: the entry references an external loose file,
        // not data inside this WAD.
        if (Type == ArchiveAssetType.Wad && entry.IsCompressed && !string.IsNullOrEmpty(entry.Directory))
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: NO_WAD entry (data ships as a loose file, not in the WAD).");

        var storedSize = checked((int)ArchiveEntryDecoder.GetStoredSize(Type, entry));

        // PAK: companion-resident entries carry raw .pab/.mpk offsets; LE entries
        // whose resolved position runs past the pak continue into the .pab at
        // (resolved − pak length) — the same rules PakArchive.ExtractFiles applies.
        var handle = _handle;
        var position = entry.Offset;
        var sourceLength = _length;
        if (Type == ArchiveAssetType.Pak)
        {
            if (entry.InCompanion)
            {
                handle = _companionHandle
                         ?? throw new InvalidDataException(
                             $"{DisplayPath}::{entry.FullName}: companion-resident entry but no data file found.");
                sourceLength = _companionLength;
            }
            else if (position + storedSize > _length && _companionHandle != null)
            {
                handle = _companionHandle;
                position -= _length;
                sourceLength = _companionLength;
            }
        }

        if (position < 0 || position + storedSize > sourceLength)
            throw new InvalidDataException(
                $"{DisplayPath}::{entry.FullName}: data range [{position}, {position + storedSize}) " +
                $"is outside the container (length {sourceLength}).");

        var stored = new byte[storedSize];
        var read = 0;
        while (read < storedSize)
        {
            var n = RandomAccess.Read(handle, stored.AsSpan(read), position + read);
            if (n == 0)
                throw new EndOfStreamException($"{DisplayPath}::{entry.FullName}: unexpected end of container.");
            read += n;
        }

        return ArchiveEntryDecoder.Decode(Type, entry, stored);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _handle.Dispose();
            _companionHandle?.Dispose();
        }

        base.Dispose(disposing);
    }
}
