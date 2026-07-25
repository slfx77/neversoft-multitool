namespace NeversoftMultitool.Core.Formats.DiscImage;

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
