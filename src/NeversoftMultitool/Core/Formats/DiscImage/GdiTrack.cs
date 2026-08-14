namespace NeversoftMultitool.Core.Formats.DiscImage;

public sealed record GdiTrack(
    int Number,
    long StartLba,
    bool IsData,
    int SectorSize,
    string FilePath)
{
    /// <summary>Byte offset of the first physical sector inside <see cref="FilePath" />.</summary>
    public long FileByteOffset { get; init; }

    public long SectorCount
    {
        get
        {
            if (SectorSize <= 0 || FileByteOffset < 0 || !File.Exists(FilePath))
                return 0;

            var fileLength = new FileInfo(FilePath).Length;
            return FileByteOffset <= fileLength
                ? (fileLength - FileByteOffset) / SectorSize
                : 0;
        }
    }
}
