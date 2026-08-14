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

    [Theory]
    [InlineData(true, 4, 0x7FC00000u)]
    [InlineData(true, 8, 0x7F800000u)]
    [InlineData(true, 12, 0xFF800000u)]
    [InlineData(true, 16, 0x7FC00000u)]
    [InlineData(true, 20, 0x7F800000u)]
    [InlineData(false, 0, 0x7FC00000u)]
    [InlineData(false, 4, 0x7F800000u)]
    [InlineData(false, 8, 0xFF800000u)]
    [InlineData(false, 12, 0x7FC00000u)]
    public void Parse_Thps3NonFiniteRecordComponent_Throws(
        bool rotation, int componentOffset, uint bits)
    {
        var data = rotation ? BuildSingleRotationKey() : BuildSingleTranslationKey();
        var recordOffset = rotation ? 40 : 44;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(recordOffset + componentOffset), bits);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Equal(
            $"THPS3 SKA {(rotation ? "Q" : "T")} key 0 contains a non-finite component.",
            exception.Message);
    }

    [Fact]
    public void Parse_Thps3FiniteRotationRecord_IsAccepted()
    {
        var animation = SkaFile.Parse(BuildSingleRotationKey());

        var root = Assert.Single(animation.BoneTracks);
        Assert.Empty(root.RotationKeys);
        Assert.Empty(root.TranslationKeys);
    }

    [Fact]
    public void Parse_Thps3FiniteTranslationRecord_IsAccepted()
    {
        var animation = SkaFile.Parse(BuildSingleTranslationKey());

        var root = Assert.Single(animation.BoneTracks);
        var key = Assert.Single(root.TranslationKeys);
        Assert.Equal(1f, key.Translation.X);
        Assert.Equal(2f, key.Translation.Y);
        Assert.Equal(3f, key.Translation.Z);
        Assert.Equal(0.5f, key.Time);
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

    private static byte[] BuildSingleRotationKey()
    {
        var data = BuildHeader(2, 0);
        Array.Resize(ref data, 68);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), -1);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(44), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(48), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(52), 0f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(56), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(60), 0.5f);
        return data;
    }

    private static byte[] BuildSingleTranslationKey()
    {
        var data = BuildHeader(1, 1);
        Array.Resize(ref data, 64);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(44), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(48), 2f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(52), 3f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(56), 0.5f);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), -1);
        return data;
    }
}
