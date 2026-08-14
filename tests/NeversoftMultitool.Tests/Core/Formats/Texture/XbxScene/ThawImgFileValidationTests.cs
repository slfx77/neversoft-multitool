using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class ThawImgFileValidationTests
{
    [Fact]
    public void Parse_OverflowingPaletteCount_FailsAsTruncatedPalette()
    {
        var data = BuildBgra32Img(1, 1, 0, 0, []);
        data[21] = 8;
        data[23] = 32;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), uint.MaxValue);

        var result = ThawImgFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated palette data", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_OverflowingCompressedMipSize_FailsAsTruncatedMip()
    {
        var data = BuildBgra32Img(1, 1, 0, 0, []);
        data[22] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), uint.MaxValue);

        var result = ThawImgFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated mip data at mip 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_Bgra32MipWithOnlyOneOfFourPixels_Fails()
    {
        var data = BuildBgra32Img(2, 2, 4, 1,
        [
            0, 0, 255, 255
        ]);

        var result = ThawImgFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Failed to decode THAW IMG mip 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_Bgra32MipWithExactPixelData_Succeeds()
    {
        var data = BuildBgra32Img(2, 2, 8, 2,
        [
            // Stored bottom-up: blue/white, then red/green.
            255, 0, 0, 255,
            255, 255, 255, 255,
            0, 0, 255, 255,
            0, 255, 0, 255
        ]);

        var result = ThawImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(2, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.NotNull(texture.Pixels);
        Assert.Equal(
        [
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 255, 255
        ], texture.Pixels);
    }

    private static byte[] BuildBgra32Img(
        ushort width,
        ushort height,
        ushort bytesPerLine,
        ushort numLines,
        byte[] pixels)
    {
        var data = new byte[28 + pixels.Length];
        BitConverter.GetBytes(0xABADD00Du).CopyTo(data, 0);
        data[4] = 2;
        BitConverter.GetBytes((ushort)0x14).CopyTo(data, 6);
        BitConverter.GetBytes(width).CopyTo(data, 12);
        BitConverter.GetBytes(height).CopyTo(data, 14);
        BitConverter.GetBytes(width).CopyTo(data, 16);
        BitConverter.GetBytes(height).CopyTo(data, 18);
        data[20] = 1;
        data[21] = 32;
        BitConverter.GetBytes(bytesPerLine).CopyTo(data, 24);
        BitConverter.GetBytes(numLines).CopyTo(data, 26);
        pixels.CopyTo(data, 28);
        return data;
    }
}
