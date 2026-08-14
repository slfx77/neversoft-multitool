using System.Buffers.Binary;
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

    [Fact]
    public void ReadFileList_DirectoryRecordShorterThanFixedPrefix_ThrowsInvalidData()
    {
        var image = CreateImageWithRootDirectory(2048);
        image[18 * IsoSectorSource.UserDataSize] = 1;
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var exception = Assert.Throws<InvalidDataException>(
            () => Iso9660FileSystem.ReadFileList(source));

        Assert.Contains("length 1", exception.Message);
        Assert.Contains("at least 34 bytes", exception.Message);
    }

    [Fact]
    public void ReadFileList_MinimumDirectoryRecord_IsAccepted()
    {
        var image = CreateImageWithRootDirectory(34);
        WriteDirectoryRecord(
            image.AsSpan(18 * IsoSectorSource.UserDataSize),
            recordLength: 34,
            name: (byte)'A');
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var entry = Assert.Single(Iso9660FileSystem.ReadFileList(source));

        Assert.Equal("A", entry.Name);
        Assert.Equal(0L, entry.Size);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void ReadFileList_RecordPastLogicalDirectoryExtent_ThrowsInvalidData()
    {
        var image = CreateImageWithRootDirectory(34);
        WriteDirectoryRecord(
            image.AsSpan(18 * IsoSectorSource.UserDataSize),
            recordLength: 35,
            name: (byte)'A');
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var exception = Assert.Throws<InvalidDataException>(
            () => Iso9660FileSystem.ReadFileList(source));

        Assert.Contains("ends at 35", exception.Message);
        Assert.Contains("extent size 34", exception.Message);
    }

    private static byte[] CreateImageWithRootDirectory(uint rootSize)
    {
        var image = new byte[SectorCount * IsoSectorSource.UserDataSize];
        WriteVolumeDescriptor(image, 16, 1);
        WriteVolumeDescriptor(image, 17, 255);

        var root = image.AsSpan(16 * IsoSectorSource.UserDataSize + 156, 34);
        root[0] = 34;
        BinaryPrimitives.WriteUInt32LittleEndian(root[2..], 18);
        BinaryPrimitives.WriteUInt32BigEndian(root[6..], 18);
        BinaryPrimitives.WriteUInt32LittleEndian(root[10..], rootSize);
        BinaryPrimitives.WriteUInt32BigEndian(root[14..], rootSize);
        root[25] = 0x02;
        BinaryPrimitives.WriteUInt16LittleEndian(root[28..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(root[30..], 1);
        root[32] = 1;
        root[33] = 0;
        return image;
    }

    private static void WriteDirectoryRecord(Span<byte> sector, byte recordLength, byte name)
    {
        sector[0] = recordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[2..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(sector[6..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[10..], 0);
        BinaryPrimitives.WriteUInt32BigEndian(sector[14..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[28..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(sector[30..], 1);
        sector[32] = 1;
        sector[33] = name;
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
