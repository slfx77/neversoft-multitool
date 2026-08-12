using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaThps3ParserValidationTests
{
    [Theory]
    [InlineData(0x80000001u, 0u)]
    [InlineData(1u, uint.MaxValue)]
    public void ParseThps3_UnrepresentableKeyCount_Throws(uint qKeys, uint tKeys)
    {
        var data = BuildHeader(qKeys, tKeys);

        var exception = Assert.Throws<InvalidDataException>(
            () => SkaThps3Parser.ParseThps3(data, 1, 0x80000000, 1f));

        Assert.Equal($"THPS3 SKA key counts are too large: Q={qKeys}, T={tKeys}.", exception.Message);
    }

    [Fact]
    public void ParseThps3_EmptyRepresentableTables_ReturnImplicitRootTrack()
    {
        var animation = SkaThps3Parser.ParseThps3(BuildHeader(1, 0), 1, 0x80000000, 1f);

        var root = Assert.Single(animation.BoneTracks);
        Assert.Empty(root.RotationKeys);
        Assert.Empty(root.TranslationKeys);
    }

    [Fact]
    public void ParseThps3_RepresentableQCountPastFile_ThrowsBeforeAllocation()
    {
        var data = BuildHeader(int.MaxValue, 0);

        var exception = Assert.Throws<InvalidDataException>(
            () => SkaThps3Parser.ParseThps3(data, 1, 0x80000000, 1f));

        Assert.Contains("Q table needs", exception.Message);
    }

    [Fact]
    public void ParseThps3_RepresentableHugeTCount_IsCappedToPhysicalRecords()
    {
        var animation = SkaThps3Parser.ParseThps3(
            BuildHeader(1, int.MaxValue), 1, 0x80000000, 1f);

        var root = Assert.Single(animation.BoneTracks);
        Assert.Empty(root.TranslationKeys);
    }

    private static byte[] BuildHeader(uint qKeys, uint tKeys)
    {
        var data = new byte[44];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0x80000000);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 1f);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), qKeys);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), tKeys);
        return data;
    }
}
