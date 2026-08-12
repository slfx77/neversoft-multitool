using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class Iso9660FileSystemTests
{
    private const int SectorCount = 19;

    [Fact]
    public void HasVolumeDescriptor_BootRecordBeforePrimary_ReturnsTrue()
    {
        var image = new byte[SectorCount * IsoSectorSource.UserDataSize];
        WriteVolumeDescriptor(image, 16, 0);
        WriteVolumeDescriptor(image, 17, 1);
        WriteVolumeDescriptor(image, 18, 255);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        Assert.True(Iso9660FileSystem.HasVolumeDescriptor(source));
    }

    [Fact]
    public void HasVolumeDescriptor_TerminatorBeforePrimary_ReturnsFalse()
    {
        var image = new byte[SectorCount * IsoSectorSource.UserDataSize];
        WriteVolumeDescriptor(image, 16, 0);
        WriteVolumeDescriptor(image, 17, 255);
        WriteVolumeDescriptor(image, 18, 1);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        Assert.False(Iso9660FileSystem.HasVolumeDescriptor(source));
    }

    private static void WriteVolumeDescriptor(Span<byte> image, int lba, byte type)
    {
        var descriptor = image.Slice(
            lba * IsoSectorSource.UserDataSize,
            IsoSectorSource.UserDataSize);
        descriptor[0] = type;
        "CD001"u8.CopyTo(descriptor[1..]);
        descriptor[6] = 1;
    }
}
