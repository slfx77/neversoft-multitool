using System.Buffers.Binary;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class MdecDecoderQuantizationScaleTests
{
    private static readonly ushort[] PayloadWords =
    [
        0x0001, 0x0464, 0x8000, 0x4119, 0x2000, 0x1046, 0x4800,
        0x0411, 0x9200, 0x0104, 0x6480, 0x0041, 0x1920
    ];

    [Fact]
    public void DecodeFrame_ZeroQuantizationScale_UsesLinearUnquantizedMode()
    {
        var unquantized = Decode(0);
        var quantized = Decode(1);

        Assert.Equal(16 * 16 * 3, unquantized.Length);
        Assert.Contains(unquantized, static value => value != 128);
        Assert.False(unquantized.SequenceEqual(quantized));
        Assert.Equal(
            "bb793222d3696257f9176c7ab14d28f278b2bf531968e33777f332a293a5ab68",
            Convert.ToHexStringLower(SHA256.HashData(unquantized)));
    }

    [Theory]
    [InlineData(0, 64)]
    [InlineData(1, 65)]
    [InlineData(63, ushort.MaxValue)]
    public void DecodeFrame_HeaderQuantizationScale_UsesOnlyLowSixBits(
        ushort effectiveScale,
        ushort storedScale)
    {
        Assert.Equal(Decode(effectiveScale), Decode(storedScale));
    }

    private static byte[] Decode(ushort scale)
    {
        return MdecDecoder.DecodeFrame(CreateFrame(scale), 16, 16);
    }

    private static byte[] CreateFrame(ushort scale)
    {
        var frame = new byte[8 + PayloadWords.Length * sizeof(ushort)];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, 9); // Expanded MDEC data: 18 halfwords.
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2), 0x3800);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), scale);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), 2);

        for (var i = 0; i < PayloadWords.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(8 + i * sizeof(ushort)), PayloadWords[i]);

        return frame;
    }
}
