using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoFileChunkValidationTests
{
    [Fact]
    public void TryParse_NonzeroPartialFramChildHeader_ReturnsFalse()
    {
        var data = ExtendFrameByOneByte(0xA5);

        var success = Vid1VideoFile.TryParse(data, null, out var file, out var error);

        Assert.False(success);
        Assert.Null(file);
        Assert.Equal("VID1 FRAM child header is truncated", error);
    }

    [Fact]
    public void TryParse_ZeroPartialFramChildPadding_RemainsValid()
    {
        var data = ExtendFrameByOneByte(0);

        var success = Vid1VideoFile.TryParse(data, null, out var file, out var error);

        Assert.True(success, error);
        Assert.NotNull(file);
        Assert.Single(file.Frames);
    }

    [Fact]
    public void TryParse_UnrepresentableRootChunkSize_ReturnsFalse()
    {
        var data = new byte[12];
        "VID1"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x80000000u);

        var success = Vid1VideoFile.TryParse(data, null, out var file, out var error);

        Assert.False(success);
        Assert.Null(file);
        Assert.Equal("VID1 chunk size is invalid", error);
    }

    private static byte[] ExtendFrameByOneByte(byte trailingByte)
    {
        const int frameOffset = 84;
        const uint extendedFrameSize = 65;
        var data = Vid1VideoTestBuilder.CreateVideoVid1();
        Assert.Equal(148, data.Length);
        Array.Resize(ref data, data.Length + 1);
        data[^1] = trailingByte;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(frameOffset + 4), extendedFrameSize);
        return data;
    }
}
