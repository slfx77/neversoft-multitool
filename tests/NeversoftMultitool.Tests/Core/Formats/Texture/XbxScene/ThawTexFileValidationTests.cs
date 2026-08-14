using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.XbxScene;

public sealed class ThawTexFileValidationTests
{
    private static readonly byte[] TestPalette =
    [
        0, 0, 0, 255,
        10, 10, 10, 255,
        20, 20, 20, 255,
        30, 30, 30, 255
    ];

    [Fact]
    public void Parse_TextureWithNoMipLevels_FailsExplicitly()
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(hasMip: false));

        Assert.False(result.Success);
        Assert.Equal("Texture 0 has no mip levels", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_CompleteOneByOneBgraTexture_DecodesExactRgba()
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(hasMip: true));

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal(new byte[] { 0xFF, 0x00, 0x00, 0xFF }, texture.Pixels);
    }

    [Theory]
    [InlineData(2, 2, 32, false, 4)]
    [InlineData(2, 2, 8, true, 3)]
    [InlineData(3, 1, 4, true, 1)]
    public void Parse_UncompressedMipShorterThanLogicalPixels_Fails(
        int width,
        int height,
        byte texelDepth,
        bool paletted,
        int payloadBytes)
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(
            hasMip: true,
            width: (ushort)width,
            height: (ushort)height,
            texelDepth: texelDepth,
            palette: paletted ? TestPalette : null,
            pixels: new byte[payloadBytes]));

        Assert.False(result.Success);
        Assert.Equal("Failed to decode THAW TEX texture 0 mip 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_OddWidthIndexed4Mip_UsesCeilingByteCountAndLowNibbleFirst()
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(
            hasMip: true,
            width: 3,
            height: 1,
            texelDepth: 4,
            palette: TestPalette,
            pixels: [0x21, 0x03]));

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(
        [
            10, 10, 10, 255,
            20, 20, 20, 255,
            30, 30, 30, 255
        ], texture.Pixels);
    }

    [Fact]
    public void Parse_Bgra32MipWithTrailingPadding_RemainsAccepted()
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(
            hasMip: true,
            pixels: [0, 0, 255, 255, 0xAA, 0xBB, 0xCC, 0xDD]));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, Assert.Single(result.Textures).Pixels);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(4097, 1)]
    public void Parse_InvalidDimensions_FailBeforeMipDecode(int width, int height)
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(
            hasMip: true,
            width: (ushort)width,
            height: (ushort)height,
            pixels: []));

        Assert.False(result.Success);
        Assert.Equal($"Texture 0 has invalid dimensions {width}x{height}", result.ErrorMessage);
    }

    [Fact]
    public void Parse_UnknownUncompressedTuple_RemainsARecognizedNullPixelEntry()
    {
        var result = ThawTexFile.Parse(CreateSingleTextureDictionary(
            hasMip: true,
            texelDepth: 24,
            pixels: []));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Null(Assert.Single(result.Textures).Pixels);
    }

    [Fact]
    public void Parse_OverflowingPaletteCount_FailsAsTruncatedPalette()
    {
        var data = CreateSingleTextureDictionary(
            hasMip: true,
            texelDepth: 8,
            palette: TestPalette,
            pixels: [0]);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), uint.MaxValue);

        var result = ThawTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated palette data at texture 0", result.ErrorMessage);
    }

    [Fact]
    public void Parse_OverflowingCompressedMipSize_FailsAsTruncatedMip()
    {
        var data = CreateSingleTextureDictionary(
            hasMip: true,
            compression: 1,
            pixels: []);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), uint.MaxValue);

        var result = ThawTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated mip data at texture 0, mip 0", result.ErrorMessage);
    }

    private static byte[] CreateSingleTextureDictionary(
        bool hasMip,
        ushort width = 1,
        ushort height = 1,
        byte texelDepth = 32,
        byte[]? palette = null,
        byte[]? pixels = null,
        byte compression = 0)
    {
        pixels ??= hasMip ? [0x00, 0x00, 0xFF, 0xFF] : [];
        var paletteBytes = palette?.Length ?? 0;
        var data = new byte[32 + (palette == null ? 0 : 4 + paletteBytes)
            + (hasMip ? 4 + pixels.Length : 0)];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xABADD00D);
        data[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);

        var entry = data.AsSpan(8, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0xABADD00D);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], width);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[14..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], width);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[18..], height);
        entry[20] = hasMip ? (byte)1 : (byte)0;
        entry[21] = texelDepth;
        entry[22] = compression;
        entry[23] = palette == null ? (byte)0 : (byte)32;

        var offset = 32;
        if (palette != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)(palette.Length / 4));
            offset += 4;
            palette.CopyTo(data, offset);
            offset += palette.Length;
        }

        if (hasMip)
        {
            if (compression == 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), (ushort)pixels.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset + 2), 1);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), (uint)pixels.Length);
            }

            pixels.CopyTo(data, offset + 4);
        }

        return data;
    }
}
