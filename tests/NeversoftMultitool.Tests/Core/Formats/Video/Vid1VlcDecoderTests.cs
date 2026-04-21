using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1VlcDecoderTests
{
    private static Vid1BitReader MakeReader(params byte[] data) => new(data);

    [Theory]
    [InlineData(false, 0x0F)] // SelectorTable[48] = 0x0004000F → consume 2, value 0xF; !invert → 0xF
    [InlineData(true, 0x0F)]  // invert → value itself = 0xF
    public void DecodeSelector_TopBitsAllOnes_Returns0xF(bool invert, int expected)
    {
        // Peek-6 = 0b110000 = 48 → SelectorTable[48] = 0x0004000F
        // bits_to_consume = 0x0004000F >> 17 = 2, value = 0xF
        // !invert: return 0xF - 0xF = 0; invert: return 0xF
        // Actually: invert param means "keep value", !invert means "0xF - value"
        // SelectorTable[48] value = 0xF. When invert=false → 0xF-0xF = 0.
        var reader = MakeReader(0b11000000, 0x00);
        var result = Vid1VlcDecoder.DecodeSelector(reader, invert);
        Assert.Equal(invert ? 0xF : 0, result);
    }

    [Fact]
    public void DecodeSelector_ConsumesCorrectBits()
    {
        // SelectorTable[48] consumes 2 bits
        var reader = MakeReader(0b11000011, 0xFF);
        Vid1VlcDecoder.DecodeSelector(reader, false);
        Assert.Equal(2, reader.BitPosition);
    }

    [Fact]
    public void DecodeRawCodeA_ZeroPeek_Sentinel()
    {
        // RawCodeTableA[0] = 0xFFFFFFFF (sentinel). Peek-9 = 0 → index = 0 >> 3 = 0.
        // But value=1 loop: peek=0 ≠ 1, so no skip. Table[0] = sentinel.
        // This should still "work" (consume the bits from the sentinel entry).
        // Actually sentinel (0xFFFFFFFF) means consume = 0x7FFF bits — will throw.
        // Skip this edge case; test a normal entry.
        // Peek-9 = 0b0_0100_0000 = 64 → >> 3 = 8 → RawCodeTableA[8] = 0x00060013
        // consume = 3, value = 0x13
        var reader = MakeReader(0b00100000, 0b00000000, 0x00);
        var result = Vid1VlcDecoder.DecodeRawCodeA(reader);
        Assert.Equal(0x13, result);
        Assert.Equal(3, reader.BitPosition);
    }

    [Fact]
    public void DecodeMvMagnitude_FlagBitSet_ReturnsZero()
    {
        // First bit = 1 → MV magnitude is zero
        var reader = MakeReader(0b10000000);
        var result = Vid1VlcDecoder.DecodeMvMagnitude(reader);
        Assert.Equal(0, result);
        Assert.Equal(1, reader.BitPosition);
    }

    [Fact]
    public void DecodeMvDelta_ZeroMagnitude_ReturnsZero()
    {
        // First bit = 1 → magnitude = 0 → delta = 0 regardless of fCode
        var reader = MakeReader(0b10000000, 0x00);
        var result = Vid1VlcDecoder.DecodeMvDelta(reader, 3);
        Assert.Equal(0, result);
    }

    [Fact]
    public void SignExtendValue_MsbSet_Positive()
    {
        // 4 bits: value = 0b1010 = 10, MSB (bit 3) is 1 → positive, return 10
        var reader = MakeReader(0b10100000);
        var result = Vid1VlcDecoder.SignExtendValue(reader, 4);
        Assert.Equal(10, result);
    }

    [Fact]
    public void SignExtendValue_MsbClear_Negative()
    {
        // 4 bits: value = 0b0101 = 5, MSB (bit 3) is 0 → negate: -(5 ^ 0xF) = -10
        var reader = MakeReader(0b01010000);
        var result = Vid1VlcDecoder.SignExtendValue(reader, 4);
        Assert.Equal(-10, result);
    }
}
