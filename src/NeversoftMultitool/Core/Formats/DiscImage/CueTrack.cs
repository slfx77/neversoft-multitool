namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record CueTrack(
    int Number,
    string Type,
    string FilePath,
    long FileLength,
    int SectorSize,
    long Index01Frames)
{
    public bool IsAudio => Type.Equals("AUDIO", StringComparison.OrdinalIgnoreCase);
    public long SectorCount => FileLength / SectorSize;
}
