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
}
