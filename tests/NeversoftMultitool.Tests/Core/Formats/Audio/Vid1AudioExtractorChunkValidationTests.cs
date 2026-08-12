using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Vid1AudioExtractorChunkValidationTests
{
    [Fact]
    public void TryParseVid1_UnrepresentableRootChunkSize_ReturnsFalse()
    {
        var data = new byte[12];
        "VID1"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 0x80000000u);

        var success = Vid1AudioExtractor.TryParseVid1(data, out var tracks, out var error);

        Assert.False(success);
        Assert.Empty(tracks);
        Assert.Equal("VID1 chunk size is invalid", error);
    }

    [Fact]
    public void TryParseVid1_RepresentableChunkWhoseEndOverflows_ReturnsFalse()
    {
        var data = new byte[16];
        "VID1"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 8u);
        "HEAD"u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), int.MaxValue);

        var success = Vid1AudioExtractor.TryParseVid1(data, out var tracks, out var error);

        Assert.False(success);
        Assert.Empty(tracks);
        Assert.Equal("VID1 chunk extends beyond the file", error);
    }
}
