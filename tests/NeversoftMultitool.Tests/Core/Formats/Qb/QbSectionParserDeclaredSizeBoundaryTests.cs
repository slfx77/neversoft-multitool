using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.Core.Formats.Qb;

public class QbSectionParserDeclaredSizeBoundaryTests
{
    private static readonly byte[] SectionedQbSignature =
    [
        0x1C, 0x08, 0x02, 0x04, 0x10, 0x04, 0x08, 0x0C, 0x0C, 0x08,
        0x02, 0x04, 0x14, 0x02, 0x04, 0x0C, 0x10, 0x10, 0x0C, 0x00
    ];

    [Fact]
    public void SectionString_CannotBorrowTerminatorFromBackingBytesPastDeclaredSize()
    {
        var data = BuildStringSection(52, 48);

        var exception = Assert.Throws<InvalidDataException>(() => QbSectionParser.ParseToTokens(data));

        Assert.Equal(
            "Sectioned-QB access at 0x30 for 0x1 bytes exceeds parse limit 0x30",
            exception.Message);
    }

    [Fact]
    public void SectionString_EndingAtDeclaredSize_Parses()
    {
        var data = BuildStringSection(52, 52);

        var tokens = QbSectionParser.ParseToTokens(data);

        Assert.Equal("A", Assert.Single(tokens, static token => token.Type == QbTokenType.String).StringValue);
        Assert.Equal(52, tokens[^1].Offset);
    }

    [Fact]
    public void SectionString_TerminatorSearch_StopsAtDeclaredSize()
    {
        var data = BuildStringSection(52, 49);

        var exception = Assert.Throws<InvalidDataException>(() => QbSectionParser.ParseToTokens(data));

        Assert.Equal("Unterminated string at 0x30 before parse limit 0x31", exception.Message);
    }

    [Fact]
    public void BackingBytesPastDeclaredSize_AreIgnored()
    {
        var data = BuildStringSection(56, 52);
        data.AsSpan(52).Fill(0xA5);

        var tokens = QbSectionParser.ParseToTokens(data);

        Assert.Equal("A", Assert.Single(tokens, static token => token.Type == QbTokenType.String).StringValue);
        Assert.Equal(52, tokens[^1].Offset);
    }

    [Fact]
    public void SectionAlignment_CannotEndPastDeclaredSize()
    {
        var data = BuildStringSection(52, 50);

        var exception = Assert.Throws<InvalidDataException>(() => QbSectionParser.ParseToTokens(data));

        Assert.Equal("Section at 0x1C ends at 0x34, beyond parse limit 0x32", exception.Message);
    }

    [Fact]
    public void StructLink_CannotVisitBackingBytesPastDeclaredSizeThenReturnInBounds()
    {
        var data = CreateOldSectionedQb(108, 88);
        WriteUInt32(data, 28, 0x000A0400); // SectionStruct
        WriteUInt32(data, 32, 0x11111111); // section key
        WriteUInt32(data, 40, 48); // struct payload
        WriteUInt32(data, 48, 0x00010000); // StructHeader
        WriteUInt32(data, 52, 56); // first item

        WriteStructInteger(data, 56, 0x22222222, 1, 92);
        WriteStructInteger(data, 92, 0x33333333, 2, 72); // outside, then back in bounds
        WriteStructInteger(data, 72, 0x44444444, 3, 0); // final end is declared size 88

        var exception = Assert.Throws<InvalidDataException>(() => QbSectionParser.ParseToTokens(data));

        Assert.Equal(
            "Sectioned-QB pointer target 0x5C is outside parse limit 0x58",
            exception.Message);
    }

    private static byte[] BuildStringSection(int backingSize, uint declaredSize)
    {
        var data = CreateOldSectionedQb(backingSize, declaredSize);
        WriteUInt32(data, 28, 0x00030400); // SectionString
        WriteUInt32(data, 32, 0x11111111); // section key
        WriteUInt32(data, 40, 48); // string payload
        data[48] = (byte)'A';
        data[49] = 0;
        return data;
    }

    private static byte[] CreateOldSectionedQb(int backingSize, uint declaredSize)
    {
        var data = new byte[backingSize];
        WriteUInt32(data, 4, declaredSize);
        SectionedQbSignature.CopyTo(data, 8);
        return data;
    }

    private static void WriteStructInteger(byte[] data, int offset, uint key, uint value, uint next)
    {
        WriteUInt32(data, offset, 0x00000300); // StructItemInteger
        WriteUInt32(data, offset + 4, key);
        WriteUInt32(data, offset + 8, value);
        WriteUInt32(data, offset + 12, next);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
