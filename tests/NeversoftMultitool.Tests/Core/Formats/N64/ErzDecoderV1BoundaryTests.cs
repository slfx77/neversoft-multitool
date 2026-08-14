using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

public sealed class ErzDecoderV1BoundaryTests
{
    private static readonly byte[] OneLiteralPayload =
        [0x88, 0x08, 0x00, 0x02, 0x00, 0x02, 0x41, 0x00];

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void DecodeV1_InvalidCompressedSize_Throws(int declaredCompressedSize)
    {
        var block = CreateV1Block(1, declaredCompressedSize, [0x88]);

        var exception = Assert.Throws<InvalidDataException>(() => ErzDecoder.Decode(block));

        Assert.Equal(
            $"Implausible ERZ v1 compressed size {declaredCompressedSize} for a 19-byte block",
            exception.Message);
    }

    [Fact]
    public void DecodeV1_UndersizedPayload_CannotBorrowLiteralFromPhysicalSuffix()
    {
        var block = CreateV1Block(1, 6, OneLiteralPayload);

        var exception = Assert.Throws<InvalidDataException>(() => ErzDecoder.Decode(block));

        Assert.Equal("ERZ v1 literal exceeds its declared compressed payload", exception.Message);
    }

    [Fact]
    public void DecodeV1_ExactPayload_DecodesLiteralWithZeroFilledLookahead()
    {
        var block = CreateV1Block(1, 7, OneLiteralPayload.AsSpan(0, 7));

        Assert.Equal([0x41], ErzDecoder.Decode(block));
    }

    [Fact]
    public void DecodeV1_PhysicalSuffixAfterExactPayload_IsIgnored()
    {
        byte[] physicalPayload = [.. OneLiteralPayload, 0xA5, 0x5A];
        var block = CreateV1Block(1, 7, physicalPayload);

        Assert.Equal([0x41], ErzDecoder.Decode(block));
    }

    [Fact]
    public void DecodeV1_ZeroFilledLookahead_CannotFabricateMatchOnlyBlock()
    {
        byte[] payload = [0x88, 0x88, 0x10, 0x21, 0x02, 0x00, 0x02, 0x00, 0x41];
        var block = CreateV1Block(131_071, payload.Length, payload);

        var exception = Assert.Throws<InvalidDataException>(() => ErzDecoder.Decode(block));

        Assert.Equal("ERZ v1 bitstream exceeds its declared compressed payload", exception.Message);
    }

    private static byte[] CreateV1Block(
        int decompressedSize,
        int declaredCompressedSize,
        ReadOnlySpan<byte> physicalPayload)
    {
        var block = new byte[ErzDecoder.HeaderSize + physicalPayload.Length];
        block[0] = (byte)'E';
        block[1] = (byte)'R';
        block[2] = (byte)'Z';
        block[3] = 1;
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(4), decompressedSize);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(8), declaredCompressedSize);
        physicalPayload.CopyTo(block.AsSpan(ErzDecoder.HeaderSize));
        return block;
    }
}
