using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class GcmFileSystemTests
{
    private const int ImageSize = 0x440;
    private const int FstOffset = 0x430;

    [Fact]
    public void ReadFileList_UnsignedFstEndOverflow_ThrowsInvalidDataException()
    {
        var image = CreateRootOnlyGcm();
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x424, 4), 0xFFFFFFF0);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x428, 4), 0x20);
        using var stream = new MemoryStream(image, false);

        Assert.True(GcmFileSystem.IsGcm(stream));

        var error = Assert.Throws<InvalidDataException>(() => GcmFileSystem.ReadFileList(stream));

        Assert.Equal("GCM FST offset/size invalid.", error.Message);
    }

    [Fact]
    public void ReadFileList_RootOnlyFst_ReturnsEmptyList()
    {
        using var stream = new MemoryStream(CreateRootOnlyGcm(), false);

        Assert.True(GcmFileSystem.IsGcm(stream));

        var entries = GcmFileSystem.ReadFileList(stream);

        Assert.Empty(entries);
    }

    private static byte[] CreateRootOnlyGcm()
    {
        var image = new byte[ImageSize];
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x1C, 4), 0xC2339F3D);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x424, 4), FstOffset);
        BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(0x428, 4), 13);

        var fst = image.AsSpan(FstOffset, 13);
        BinaryPrimitives.WriteUInt32BigEndian(fst, 0x01000000);
        BinaryPrimitives.WriteUInt32BigEndian(fst[8..], 1);
        return image;
    }
}
