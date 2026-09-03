using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Tests.Core.BinaryIO;

public sealed class DxtDecoderTests
{
    [Theory]
    [InlineData(2, 85, 0, 170)]
    [InlineData(3, 170, 0, 85)]
    public void DecodeDxt3_Color0BelowColor1_UsesFourColorInterpolation(
        int selector,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue)
    {
        var block = new byte[16];
        Array.Fill(block, (byte)0xFF, 0, 8);
        WriteColorBlock(block.AsSpan(8), selector);

        var pixels = DxtDecoder.DecodeDxt3(block, 4, 4);

        AssertSolidColor(pixels, expectedRed, expectedGreen, expectedBlue, 255);
    }

    [Theory]
    [InlineData(2, 85, 0, 170)]
    [InlineData(3, 170, 0, 85)]
    public void DecodeDxt5_Color0BelowColor1_UsesFourColorInterpolation(
        int selector,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue)
    {
        var block = new byte[16];
        block[0] = 255;
        block[1] = 255;
        WriteColorBlock(block.AsSpan(8), selector);

        var pixels = DxtDecoder.DecodeDxt5(block, 4, 4);

        AssertSolidColor(pixels, expectedRed, expectedGreen, expectedBlue, 255);
    }

    [Fact]
    public void DecodeDxt1_Color0BelowColor1_Selector3RemainsTransparent()
    {
        var block = new byte[8];
        WriteColorBlock(block, selector: 3);

        var pixels = DxtDecoder.DecodeDxt1(block, 4, 4);

        AssertSolidColor(pixels, 0, 0, 0, 0);
    }

    [Fact]
    public void DecodeDxt1_Color0BelowColor1_Selector2UsesFloorAverage()
    {
        var block = new byte[8];
        WriteColorBlock(block, selector: 2);

        var pixels = DxtDecoder.DecodeDxt1(block, 4, 4);

        AssertSolidColor(pixels, 127, 0, 127, 255);
    }

    [Fact]
    public void DecodeDxt1_TruncatedBlock_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            DxtDecoder.DecodeDxt1(new byte[7], 4, 4));

        Assert.Equal("Truncated DXT1 block data.", error.Message);
    }

    [Fact]
    public void DecodeDxt3_TruncatedBlock_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            DxtDecoder.DecodeDxt3(new byte[15], 4, 4));

        Assert.Equal("Truncated DXT3 block data.", error.Message);
    }

    [Fact]
    public void DecodeDxt5_TruncatedBlock_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            DxtDecoder.DecodeDxt5(new byte[15], 4, 4));

        Assert.Equal("Truncated DXT5 block data.", error.Message);
    }

    [Fact]
    public void DecodeBc5_FlatNormal_ReconstructsPositiveZAndOpaqueAlpha()
    {
        var block = new byte[16];
        WriteBc4Block(block, 128, 128, selector: 0);
        WriteBc4Block(block.AsSpan(8), 128, 128, selector: 0);

        var pixels = DxtDecoder.DecodeBc5(block, 4, 4);

        AssertSolidColor(pixels, 128, 128, 255, 255);
    }

    [Fact]
    public void DecodeBc5_DecodesBothBc4PalettesBeforeReconstructingZ()
    {
        var block = new byte[16];
        // Descending endpoints use the six interpolated-value BC4 palette.
        WriteBc4Block(block, 255, 0, selector: 2);
        // Ascending endpoints use four interpolated values plus 0 and 255.
        WriteBc4Block(block.AsSpan(8), 64, 192, selector: 2);

        var pixels = DxtDecoder.DecodeBc5(block, 4, 4);

        AssertSolidColor(pixels, 219, 90, 208, 255);
    }

    [Fact]
    public void DecodeBc5_TruncatedBlock_Throws()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            DxtDecoder.DecodeBc5(new byte[15], 4, 4));

        Assert.Equal("Truncated BC5 block data.", error.Message);
    }

    [Theory]
    [InlineData(1, 0, 4, "width")]
    [InlineData(1, -1, 4, "width")]
    [InlineData(1, 4, 0, "height")]
    [InlineData(1, 4, -1, "height")]
    [InlineData(3, 0, 4, "width")]
    [InlineData(3, -1, 4, "width")]
    [InlineData(3, 4, 0, "height")]
    [InlineData(3, 4, -1, "height")]
    [InlineData(5, 0, 4, "width")]
    [InlineData(5, -1, 4, "width")]
    [InlineData(5, 4, 0, "height")]
    [InlineData(5, 4, -1, "height")]
    public void Decode_InvalidDimensions_ThrowArgumentOutOfRange(
        int dxtVersion,
        int width,
        int height,
        string parameterName)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            Decode(dxtVersion, [], width, height));

        Assert.Equal(parameterName, error.ParamName);
    }

    [Theory]
    [InlineData(1, "DXT1")]
    [InlineData(3, "DXT3")]
    [InlineData(5, "DXT5")]
    public void Decode_OutputLargerThanRuntimeArrayLimit_ThrowsInvalidData(
        int dxtVersion,
        string format)
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            Decode(dxtVersion, [], int.MaxValue, 1));

        Assert.Equal(
            $"{format} dimensions {int.MaxValue}x1 exceed the runtime array limit.",
            error.Message);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(3, 16)]
    [InlineData(5, 16)]
    public void Decode_OneByOneTextureWithOneBlock_ReturnsOneRgbaPixel(
        int dxtVersion,
        int blockSize)
    {
        var pixels = Decode(dxtVersion, new byte[blockSize], 1, 1);

        Assert.Equal(4, pixels.Length);
    }

    private static void WriteColorBlock(Span<byte> block, int selector)
    {
        // Blue sorts below red, exercising the endpoint order that triggers
        // BC1's special three-color palette but not BC2/BC3's color palette.
        BinaryPrimitives.WriteUInt16LittleEndian(block, 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(block[2..], 0xF800);

        uint selectors = 0;
        for (var pixel = 0; pixel < 16; pixel++)
            selectors |= (uint)selector << (pixel * 2);
        BinaryPrimitives.WriteUInt32LittleEndian(block[4..], selectors);
    }

    private static void WriteBc4Block(
        Span<byte> block,
        byte endpoint0,
        byte endpoint1,
        int selector)
    {
        block[0] = endpoint0;
        block[1] = endpoint1;

        ulong selectors = 0;
        for (var pixel = 0; pixel < 16; pixel++)
            selectors |= (ulong)selector << (pixel * 3);
        for (var i = 0; i < 6; i++)
            block[2 + i] = (byte)(selectors >> (i * 8));
    }

    private static void AssertSolidColor(
        byte[] pixels,
        byte expectedRed,
        byte expectedGreen,
        byte expectedBlue,
        byte expectedAlpha)
    {
        Assert.Equal(4 * 4 * 4, pixels.Length);
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            Assert.Equal(expectedRed, pixels[offset]);
            Assert.Equal(expectedGreen, pixels[offset + 1]);
            Assert.Equal(expectedBlue, pixels[offset + 2]);
            Assert.Equal(expectedAlpha, pixels[offset + 3]);
        }
    }

    private static byte[] Decode(
        int dxtVersion,
        byte[] data,
        int width,
        int height)
    {
        return dxtVersion switch
        {
            1 => DxtDecoder.DecodeDxt1(data, width, height),
            3 => DxtDecoder.DecodeDxt3(data, width, height),
            5 => DxtDecoder.DecodeDxt5(data, width, height),
            _ => throw new ArgumentOutOfRangeException(nameof(dxtVersion))
        };
    }
}
