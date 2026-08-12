using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class XbxImgFileValidationTests
{
    [Fact]
    public void Parse_PaletteSizeLargerThanRemainingData_FailsWithoutTextures()
    {
        var data = CreateSinglePixelImg(uint.MaxValue);

        var result = XbxImgFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated palette", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_ZeroPaletteSize_DecodesBgraPixelAsRgba()
    {
        var data = CreateSinglePixelImg(0);
        data[32] = 0x10;
        data[33] = 0x20;
        data[34] = 0x30;
        data[35] = 0xFF;

        var result = XbxImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal(new byte[] { 0x30, 0x20, 0x10, 0xFF }, texture.Pixels);
    }

    private static byte[] CreateSinglePixelImg(uint clutSize)
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), clutSize);
        return data;
    }
}
