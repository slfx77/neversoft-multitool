namespace NeversoftMultitool.Core.Formats.DiscImage;

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
