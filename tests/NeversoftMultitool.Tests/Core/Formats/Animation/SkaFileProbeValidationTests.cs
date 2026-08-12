using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaFileProbeValidationTests
{
    [Theory]
    [InlineData(SkaFile.FlagPlatform)]
    [InlineData(SkaFile.FlagUseCompressTable)]
    public void TryProbe_UnrepresentableBoneCount_ReturnsNull(uint flags)
    {
        var data = BuildHeader(flags, uint.MaxValue);

        Assert.True(SkaFile.IsSkaFile(data));
        Assert.Null(SkaFile.TryProbe(data));
    }

    [Theory]
    [InlineData(SkaFile.FlagPlatform)]
    [InlineData(SkaFile.FlagUseCompressTable)]
    public void TryProbe_RepresentableBoneCount_ReturnsMetadata(uint flags)
    {
        var data = BuildHeader(flags, 1);

        var result = SkaFile.TryProbe(data);

        Assert.NotNull(result);
        Assert.Equal(1, result.BoneCount);
    }

    private static byte[] BuildHeader(uint flags, uint boneCount)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), flags);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 1.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), boneCount);
        return data;
    }
}
