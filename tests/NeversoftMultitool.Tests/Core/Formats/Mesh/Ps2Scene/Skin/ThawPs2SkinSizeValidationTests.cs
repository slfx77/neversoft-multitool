using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skin;

public sealed class ThawPs2SkinSizeValidationTests
{
    [Fact]
    public void IsThawPs2Skin_OverflowingDeclaredDataSize_IsRejected()
    {
        var data = BuildMinimalHeader(uint.MaxValue);

        Assert.False(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    [Fact]
    public void IsThawPs2Skin_ContainedDeclaredDataSize_IsAccepted()
    {
        var data = BuildMinimalHeader(88);

        Assert.True(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(104, true)]
    public void IsThawPs2Skin_HeaderPrefix_RequiresDeclaredFileToContainTables(
        long fileSize,
        bool expected)
    {
        var data = BuildMinimalHeader(16)[..32];

        Assert.Equal(expected, ThawPs2SkinFile.IsThawPs2Skin(data, fileSize));
    }

    private static byte[] BuildMinimalHeader(uint dataSize)
    {
        var data = new byte[104];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), dataSize);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0x1C), 1f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x20), 0x12345678);
        return data;
    }
}
