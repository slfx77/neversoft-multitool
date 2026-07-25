namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record GdiTrack(
    int Number,
    long StartLba,
    bool IsData,
    int SectorSize,
    string FilePath)
{
    public long SectorCount => File.Exists(FilePath) ? new FileInfo(FilePath).Length / SectorSize : 0;
}
