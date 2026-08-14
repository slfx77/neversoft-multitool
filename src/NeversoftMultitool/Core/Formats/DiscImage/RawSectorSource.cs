namespace NeversoftMultitool.Core.Formats.DiscImage;

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
        var orderedRegions = regions.OrderBy(r => r.StartLba).ToList();
        foreach (var region in orderedRegions)
        {
            if (region.PhysicalSectorSize is not (IsoSectorSource.UserDataSize or Mode2TailSize or RawSectorSize))
            {
                throw new InvalidDataException(
                    $"Unsupported physical sector size {region.PhysicalSectorSize}; expected 2048, 2336, or 2352 bytes.");
            }

            if (region.FileByteOffset < 0)
            {
                throw new InvalidDataException(
                    $"Track file byte offset {region.FileByteOffset} cannot be negative.");
            }

            if (region.SectorCountValue < 0)
            {
                throw new InvalidDataException(
                    $"Track sector count {region.SectorCountValue} cannot be negative.");
            }

            if (region.StartLba < 0)
            {
                throw new InvalidDataException(
                    $"Track start LBA {region.StartLba} cannot be negative.");
            }

            if (region.StartLba > long.MaxValue - region.SectorCountValue)
            {
                throw new InvalidDataException(
                    $"Track LBA range {region.StartLba} + {region.SectorCountValue} overflows Int64.");
            }

            // Division avoids overflowing sectorCount * sectorSize. Exact EOF
            // is valid; any advertised sector beyond it can never be read.
            var fileLength = new FileInfo(region.FilePath).Length;
            if (region.FileByteOffset > fileLength ||
                region.SectorCountValue > (fileLength - region.FileByteOffset) / region.PhysicalSectorSize)
            {
                throw new InvalidDataException(
                    $"Track region at byte {region.FileByteOffset} with {region.SectorCountValue} " +
                    $"sectors of {region.PhysicalSectorSize} bytes exceeds file length {fileLength}.");
            }
        }

        if (orderedRegions.Count == 0)
            throw new InvalidDataException("Disc image has no track regions.");

        // Only retain streams after every region has passed the structural
        // preflight, so a malformed later region cannot leak earlier handles.
        var openedTracks = new List<(DiscTrackRegion Region, FileStream Stream)>(orderedRegions.Count);
        try
        {
            foreach (var region in orderedRegions)
            {
                var stream = new FileStream(region.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                openedTracks.Add((region, stream));
            }
        }
        catch
        {
            foreach (var (_, stream) in openedTracks)
                stream.Dispose();
            throw;
        }

        _tracks.AddRange(openedTracks);

        SectorCount = _tracks.Max(t => t.Region.EndLba);
        Tracks = _tracks.Select(t => t.Region).ToList();
    }

    public IReadOnlyList<DiscTrackRegion> Tracks { get; }

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

    public void Dispose()
    {
        foreach (var (_, stream) in _tracks)
            stream.Dispose();
        _tracks.Clear();
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
}
