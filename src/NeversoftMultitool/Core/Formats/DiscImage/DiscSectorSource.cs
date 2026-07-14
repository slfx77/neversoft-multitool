namespace NeversoftMultitool.Core.Formats.DiscImage;

/// <summary>
///     Uniform 2048-byte-logical-sector view over a disc image, regardless of
///     the container's physical sector size (2048 plain ISO, 2352 raw CD with
///     sync/header, 2336 Mode2-without-sync). Raw-capable sources additionally
///     expose the Mode2 sector tail (subheader + data) so Form2 XA/STR streams
///     can be extracted losslessly.
/// </summary>
public interface IDiscSectorSource : IDisposable
{
    /// <summary>Total addressable logical sectors (highest mapped LBA + 1).</summary>
    long SectorCount { get; }

    /// <summary>True when 2352-byte raw sectors (with subheaders) are available.</summary>
    bool HasRawSectors { get; }

    /// <summary>
    ///     Reads the 2048-byte user data of the given LBA. Returns the Mode2
    ///     XA submode byte (bit 5 = Form2), or 0 for Mode1/plain sectors.
    /// </summary>
    byte ReadSector(long lba, Span<byte> buffer);

    /// <summary>
    ///     Reads the sector's Mode2 tail — subheader + data + EDC, i.e. raw
    ///     bytes 16..2352 (2336 bytes) — and returns the submode byte
    ///     (bit 5 set = Form2). Only valid when <see cref="HasRawSectors" />.
    /// </summary>
    byte ReadSectorTail(long lba, Span<byte> buffer);
}

/// <summary>Plain 2048-bytes-per-sector image (.iso).</summary>
public sealed class IsoSectorSource(Stream stream) : IDiscSectorSource
{
    public const int UserDataSize = 2048;

    public long SectorCount => stream.Length / UserDataSize;

    public bool HasRawSectors => false;

    public byte ReadSector(long lba, Span<byte> buffer)
    {
        stream.Position = lba * UserDataSize;
        stream.ReadExactly(buffer[..UserDataSize]);
        return 0;
    }

    public byte ReadSectorTail(long lba, Span<byte> buffer)
    {
        throw new NotSupportedException("Plain ISO images carry no raw sector data.");
    }

    public void Dispose()
    {
        stream.Dispose();
    }
}

/// <summary>
///     One physical track region inside a raw image set: a byte range of a
///     file mapped to an absolute LBA range.
/// </summary>
public sealed record DiscTrackRegion(
    long StartLba,
    long SectorCountValue,
    string FilePath,
    long FileByteOffset,
    int PhysicalSectorSize,
    bool IsAudio)
{
    public long EndLba => StartLba + SectorCountValue;
}

/// <summary>
///     Raw-sector source over one or more track files (bin+cue, img+ccd, gdi).
///     Physical sectors are 2352 (sync + header + data), 2336 (Mode2 without
///     sync), or 2048; LBAs map through the track table, so Dreamcast GD-ROM
///     high-density tracks at LBA 45000 address naturally.
/// </summary>
public sealed class RawSectorSource : IDiscSectorSource
{
    private const int RawSectorSize = 2352;
    private const int Mode2TailSize = 2336;

    private readonly byte[] _rawBuffer = new byte[RawSectorSize];
    private readonly List<(DiscTrackRegion Region, FileStream Stream)> _tracks = [];

    public RawSectorSource(IEnumerable<DiscTrackRegion> regions)
    {
        foreach (var region in regions.OrderBy(r => r.StartLba))
        {
            var stream = new FileStream(region.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _tracks.Add((region, stream));
        }

        if (_tracks.Count == 0)
            throw new InvalidDataException("Disc image has no track regions.");

        SectorCount = _tracks.Max(t => t.Region.EndLba);
        Tracks = _tracks.Select(t => t.Region).ToList();
    }

    public long SectorCount { get; }

    public bool HasRawSectors => true;

    public byte ReadSector(long lba, Span<byte> buffer)
    {
        var target = buffer[..IsoSectorSource.UserDataSize];
        var (region, stream) = FindTrack(lba);
        ReadPhysical(region, stream, lba);

        switch (region.PhysicalSectorSize)
        {
            case 2048:
                _rawBuffer.AsSpan(0, 2048).CopyTo(target);
                return 0;

            case Mode2TailSize:
                // Subheader(8) + data — Mode2 without sync/header.
                _rawBuffer.AsSpan(8, 2048).CopyTo(target);
                return _rawBuffer[2];

            default:
                var mode = _rawBuffer[15];
                // Mode1 user data at 16; Mode2 (XA) user data after the
                // 8-byte subheader at 24. Form2 sectors still expose their
                // first 2048 payload bytes here; XA-aware callers use
                // ReadSectorTail instead.
                if (mode == 2)
                {
                    _rawBuffer.AsSpan(24, 2048).CopyTo(target);
                    return _rawBuffer[18];
                }

                _rawBuffer.AsSpan(16, 2048).CopyTo(target);
                return 0;
        }
    }

    public byte ReadSectorTail(long lba, Span<byte> buffer)
    {
        var target = buffer[..Mode2TailSize];
        var (region, stream) = FindTrack(lba);
        ReadPhysical(region, stream, lba);

        switch (region.PhysicalSectorSize)
        {
            case 2048:
                // No subheader exists; synthesize an empty one.
                target.Clear();
                _rawBuffer.AsSpan(0, 2048).CopyTo(target[8..]);
                return 0;

            case Mode2TailSize:
                _rawBuffer.AsSpan(0, Mode2TailSize).CopyTo(target);
                return _rawBuffer[2];

            default:
                _rawBuffer.AsSpan(16, Mode2TailSize).CopyTo(target);
                return _rawBuffer[15] == 2 ? _rawBuffer[18] : (byte)0;
        }
    }

    /// <summary>Raw 2352 read for CD-DA extraction (audio tracks).</summary>
    public int ReadRawSector(long lba, Span<byte> buffer)
    {
        var (region, stream) = FindTrack(lba);
        ReadPhysical(region, stream, lba);
        var size = region.PhysicalSectorSize;
        _rawBuffer.AsSpan(0, size).CopyTo(buffer[..size]);
        return size;
    }

    public IReadOnlyList<DiscTrackRegion> Tracks { get; }

    private (DiscTrackRegion Region, FileStream Stream) FindTrack(long lba)
    {
        foreach (var track in _tracks)
        {
            if (lba >= track.Region.StartLba && lba < track.Region.EndLba)
                return track;
        }

        throw new InvalidDataException($"LBA {lba} is outside every track region.");
    }

    private void ReadPhysical(DiscTrackRegion region, FileStream stream, long lba)
    {
        var offset = region.FileByteOffset + (lba - region.StartLba) * region.PhysicalSectorSize;
        stream.Position = offset;
        stream.ReadExactly(_rawBuffer.AsSpan(0, region.PhysicalSectorSize));
    }

    public void Dispose()
    {
        foreach (var (_, stream) in _tracks)
            stream.Dispose();
        _tracks.Clear();
    }
}
