using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public class LzssDecoderTests
{
    [Fact]
    public void Decode_AllLiterals_ReturnsOriginalBytes()
    {
        // Flag byte 0xFF = all 8 items are literal bytes
        byte[] compressed = [0xFF, 0x48, 0x65, 0x6C, 0x6C, 0x6F, 0x21, 0x21, 0x21];
        var result = LzssDecoder.Decode(compressed, 8);
        Assert.Equal("Hello!!!"u8.ToArray(), result);
    }

    [Fact]
    public void Decode_BackReferenceToSpaceBuffer_ProducesSpaces()
    {
        // Flag 0xFE: bit 0=0 (back-ref), bits 1-7=1 (7 literals)
        // Back-ref: offset=0, raw_j=0 → length=(0+2)=2, copies 3 bytes from ring buffer
        // Ring buffer is initialized with spaces, so offset 0 → 3 spaces
        // Then 7 literal bytes follow
        byte[] compressed = [0xFE, 0x00, 0x00, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47];
        var result = LzssDecoder.Decode(compressed, 10);
        Assert.Equal("   ABCDEFG"u8.ToArray(), result);
    }

    [Fact]
    public void Decode_EmptyInput_ReturnsZeroLengthArray()
    {
        var result = LzssDecoder.Decode([], 0);
        Assert.Empty(result);
    }

    [Fact]
    public void Decode_PartialFlagByte_DecodesAvailableItems()
    {
        // Flag byte 0xFF but only request 3 bytes of output
        byte[] compressed = [0xFF, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48];
        var result = LzssDecoder.Decode(compressed, 3);
        Assert.Equal(3, result.Length);
        Assert.Equal("ABC"u8.ToArray(), result);
    }

    [Fact]
    public void Decode_MultipleFlags_HandlesConsecutiveFlagBytes()
    {
        // Two flag bytes of 0xFF = 16 literal bytes total
        byte[] compressed =
        [
            0xFF, 0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, // "01234567"
            0xFF, 0x38, 0x39, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46 // "89ABCDEF"
        ];
        var result = LzssDecoder.Decode(compressed, 16);
        Assert.Equal("0123456789ABCDEF"u8.ToArray(), result);
    }
}