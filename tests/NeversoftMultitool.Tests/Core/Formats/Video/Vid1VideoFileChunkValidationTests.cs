using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoFileChunkValidationTests
{
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
}
