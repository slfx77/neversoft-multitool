using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Replay;

public sealed class ThpgPositionUnwrapperBoundaryTests
{
    [Theory]
    [InlineData(0x29)]
    [InlineData(0x30)]
    [InlineData(0x33)]
    public void UsesQ412Positions_RequiredSignatureCrossesOwnedEnd_ReturnsFalse(int scanEnd)
    {
        var data = CreateCandidate(short.MaxValue);

        var result = ThpgPositionUnwrapper.UsesQ412Positions(data, 0x20, scanEnd);

        Assert.False(result);
    }

    [Fact]
    public void UsesQ412Positions_RequiredSignatureEndsAtOwnedEnd_DetectsHighRange()
    {
        var data = CreateCandidate(short.MaxValue);

        var result = ThpgPositionUnwrapper.UsesQ412Positions(data, 0x20, 0x34);

        Assert.True(result);
    }

    [Fact]
    public void UsesQ412Positions_LowRangeWithinOwnedSignature_RemainsFalse()
    {
        var data = CreateCandidate(16);

        var result = ThpgPositionUnwrapper.UsesQ412Positions(data, 0x20, 0x34);

        Assert.False(result);
    }

    private static byte[] CreateCandidate(short largestPositionComponent)
    {
        var data = new byte[0x34];
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(0x1C), 1f);

        data[0x20] = 3;
        data[0x21] = 1;
        data[0x23] = 1;
        data[0x26] = 1;
        data[0x27] = 0x69;
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(0x2C), largestPositionComponent);
        data[0x32] = 1;
        data[0x33] = 0x6A;

        return data;
    }
}
