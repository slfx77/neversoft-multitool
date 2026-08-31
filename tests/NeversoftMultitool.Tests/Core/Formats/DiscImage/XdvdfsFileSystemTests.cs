using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class XdvdfsFileSystemTests
{
    private const int SectorSize = 2048;

    [Fact]
    public void ReadFileList_TreePointerPastDeclaredTable_DoesNotParseSectorPadding()
    {
        var image = new byte[34 * SectorSize];
        var descriptor = image.AsSpan(32 * SectorSize, SectorSize);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 33);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], 15);

        var root = image.AsSpan(33 * SectorSize, SectorSize);
        BinaryPrimitives.WriteUInt16LittleEndian(root, 4); // byte offset 16, past the 15-byte table
        BinaryPrimitives.WriteUInt32LittleEndian(root[4..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(root[8..], 1);
        root[12] = 0;
        root[13] = 1;
        root[14] = (byte)'A';

        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var entry = Assert.Single(XdvdfsFileSystem.ReadFileList(source, 0));
        Assert.Equal("A", entry.Name);
        Assert.Equal(1L, entry.ExtentLba);
        Assert.Equal(1L, entry.Size);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void ReadFileList_NonemptyExtentPastSource_DoesNotPublishEntry()
    {
        var image = CreateSingleFileImage(35, 35, 1);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        Assert.Empty(XdvdfsFileSystem.ReadFileList(source, 0));
    }

    [Fact]
    public void ReadFileList_NonemptyExtentEndingAtSourceEof_IsAccepted()
    {
        var image = CreateSingleFileImage(35, 34, SectorSize);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var entry = Assert.Single(XdvdfsFileSystem.ReadFileList(source, 0));
        Assert.Equal("A", entry.Name);
        Assert.Equal(34L, entry.ExtentLba);
        Assert.Equal((long)SectorSize, entry.Size);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void ReadFileList_NonzeroBaseExtentEndingAtSourceEof_IsAccepted()
    {
        const int baseSector = 2;
        var image = CreateSingleFileImage(37, 34, SectorSize, baseSector);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var entry = Assert.Single(XdvdfsFileSystem.ReadFileList(source, baseSector));
        Assert.Equal("A", entry.Name);
        Assert.Equal(36L, entry.ExtentLba);
        Assert.Equal((long)SectorSize, entry.Size);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void ReadFileList_NonzeroBaseExtentPastSource_DoesNotPublishEntry()
    {
        const int baseSector = 2;
        var image = CreateSingleFileImage(37, 35, 1, baseSector);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        Assert.Empty(XdvdfsFileSystem.ReadFileList(source, baseSector));
    }

    [Fact]
    public void ReadFileList_ZeroSizeExtentPastSource_IsAccepted()
    {
        var image = CreateSingleFileImage(35, 35, 0);
        using var source = new IsoSectorSource(new MemoryStream(image, false));

        var entry = Assert.Single(XdvdfsFileSystem.ReadFileList(source, 0));
        Assert.Equal("A", entry.Name);
        Assert.Equal(35L, entry.ExtentLba);
        Assert.Equal(0L, entry.Size);
        Assert.False(entry.IsDirectory);
    }

    [Fact]
    public void TryFindBase_Xgd2RedumpGamePartition_IsProbed()
    {
        // Full X360 XGD2 dumps front-load a video partition; the game
        // partition's XDVDFS descriptor lands at 0xFD90000 + 32 sectors
        // (measured on the Proving Ground X360 redump).
        const long baseSector = 0xFD90000 / SectorSize;
        using var source = new SparseMagicSectorSource(baseSector + 32);

        Assert.True(XdvdfsFileSystem.TryFindBase(source, out var found));
        Assert.Equal(baseSector, found);
    }

    /// <summary>Serves the XDVDFS magic at one LBA and zeros everywhere else.</summary>
    private sealed class SparseMagicSectorSource(long magicLba) : IDiscSectorSource
    {
        public long SectorCount => magicLba + 2;
        public bool HasRawSectors => false;

        public byte ReadSector(long lba, Span<byte> buffer)
        {
            buffer.Clear();
            if (lba == magicLba)
                "MICROSOFT*XBOX*MEDIA"u8.CopyTo(buffer);
            return 0;
        }

        public byte ReadSectorTail(long lba, Span<byte> buffer) =>
            throw new NotSupportedException();

        public void Dispose()
        {
        }
    }

    private static byte[] CreateSingleFileImage(
        int sectorCount, uint startSector, uint fileSize, int baseSector = 0)
    {
        var image = new byte[sectorCount * SectorSize];
        var descriptor = image.AsSpan((baseSector + 32) * SectorSize, SectorSize);
        "MICROSOFT*XBOX*MEDIA"u8.CopyTo(descriptor);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 33);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], 15);

        var root = image.AsSpan((baseSector + 33) * SectorSize, SectorSize);
        BinaryPrimitives.WriteUInt32LittleEndian(root[4..], startSector);
        BinaryPrimitives.WriteUInt32LittleEndian(root[8..], fileSize);
        root[12] = 0;
        root[13] = 1;
        root[14] = (byte)'A';

        return image;
    }
}
