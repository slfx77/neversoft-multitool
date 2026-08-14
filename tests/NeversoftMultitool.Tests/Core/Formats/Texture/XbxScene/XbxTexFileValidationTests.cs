using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class XbxTexFileValidationTests
{
    private static readonly byte[] TestPalette =
    [
        0, 0, 0, 255,
        10, 10, 10, 255,
        20, 20, 20, 255,
        30, 30, 30, 255
    ];

    [Theory]
    [InlineData(2u, 2u, 32u, false, 15)]
    [InlineData(2u, 2u, 16u, false, 7)]
    [InlineData(2u, 2u, 8u, true, 3)]
    [InlineData(3u, 1u, 4u, true, 1)]
    public void Parse_UncompressedMipShorterThanLogicalPixels_Fails(
        uint width,
        uint height,
        uint texelDepth,
        bool paletted,
        int payloadBytes)
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(
            width,
            height,
            texelDepth,
            paletted ? TestPalette : null,
            new byte[payloadBytes]));

        Assert.False(result.Success);
        Assert.Equal("Failed to decode Xbox TEX texture 0 mip 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_CompleteBgra32Mip_DecodesExactRgba()
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(
            1,
            1,
            32,
            null,
            [0x10, 0x20, 0x30, 0xFF]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0xFF }, Assert.Single(result.Textures).Pixels);
    }

    [Fact]
    public void Parse_CompleteArgb1555Mip_DecodesExactRgba()
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(
            2,
            1,
            16,
            null,
            [0x00, 0xFC, 0xE0, 0x03]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
        [
            255, 0, 0, 255,
            0, 255, 0, 0
        ], Assert.Single(result.Textures).Pixels);
    }

    [Fact]
    public void Parse_OddWidthIndexed4Mip_UsesCeilingByteCountAndLowNibbleFirst()
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(
            3,
            1,
            4,
            TestPalette,
            [0x21, 0x03]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(
        [
            10, 10, 10, 255,
            20, 20, 20, 255,
            30, 30, 30, 255
        ], Assert.Single(result.Textures).Pixels);
    }

    [Fact]
    public void Parse_Bgra32MipWithTrailingPadding_RemainsAccepted()
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(
            1,
            1,
            32,
            null,
            [0, 0, 255, 255, 0xAA, 0xBB, 0xCC, 0xDD]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Assert.Single(result.Textures).Pixels);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(4097u, 1u)]
    public void Parse_InvalidDimensions_FailBeforeMipDecode(uint width, uint height)
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(width, height, 32, null, []));

        Assert.False(result.Success);
        Assert.Equal($"Texture 0 has invalid dimensions {width}x{height}", result.ErrorMessage);
    }

    [Fact]
    public void Parse_UnknownUncompressedTuple_RemainsARecognizedNullPixelEntry()
    {
        var result = XbxTexFile.Parse(BuildSingleTexture(1, 1, 24, null, []));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(Assert.Single(result.Textures).Pixels);
    }

    [Fact]
    public void Parse_OverflowingPaletteSize_FailsAsTruncatedPalette()
    {
        var data = BuildSingleTexture(1, 1, 8, null, [], rawPaletteSize: uint.MaxValue);

        var result = XbxTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated palette at texture 0", result.ErrorMessage);
    }

    [Fact]
    public void Parse_OverflowingMipSize_FailsAsTruncatedMip()
    {
        var data = BuildSingleTexture(1, 1, 32, null, [], rawMipSize: uint.MaxValue);

        var result = XbxTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated mip data at texture 0, mip 0", result.ErrorMessage);
    }

    private static byte[] BuildSingleTexture(
        uint width,
        uint height,
        uint texelDepth,
        byte[]? palette,
        byte[] pixels,
        uint? rawPaletteSize = null,
        uint? rawMipSize = null)
    {
        var paletteBytes = palette?.Length ?? 0;
        var data = new byte[8 + 32 + paletteBytes + 4 + pixels.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);

        var entry = data.AsSpan(8, 32);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], width);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], height);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], texelDepth);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], palette == null ? 0u : 32u);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(
            entry[28..],
            rawPaletteSize ?? (uint)paletteBytes);

        var offset = 40;
        if (palette != null)
        {
            palette.CopyTo(data, offset);
            offset += palette.Length;
        }

        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(offset),
            rawMipSize ?? (uint)pixels.Length);
        pixels.CopyTo(data, offset + 4);
        return data;
    }
}
