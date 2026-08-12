using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public sealed class Ps2TextureParserTests
{
    [Fact]
    public void Parse_StandardVersion2Img_PreservesStandardDecode()
    {
        var data = BuildVersion2Img();

        var result = Ps2TextureParser.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(0xDEADC0DEu, texture.Checksum);
        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.NotNull(texture.Pixels);
        Assert.Equal([0x11, 0x22, 0x33, 0xFF], texture.Pixels);
    }

    [Fact]
    public void Parse_ThawVersion6SceneTex_FallsBackAndDecodes()
    {
        var data = BuildVersion6SceneTex();
        Assert.False(Ps2TexFile.Parse(data).Success);

        var result = Ps2TextureParser.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(0x12345678u, texture.Checksum);
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

    private static byte[] BuildVersion2Img()
    {
        var data = new byte[36];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 0xDEADC0DE);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(30), 1);
        data[32] = 0x11;
        data[33] = 0x22;
        data[34] = 0x33;
        data[35] = 0x80;
        return data;
    }

    private static byte[] BuildVersion6SceneTex()
    {
        var data = new byte[0x70];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 6);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x58);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C), 0x60);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x40), 0x12345678);

        const ulong tex0 = 0x2BC0UL
                           | (1UL << 14)
                           | (1UL << 26)
                           | (1UL << 30);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(0x50), tex0);

        byte[] storedBottomUp =
        [
            0, 0, 255, 128,
            255, 255, 255, 128,
            255, 0, 0, 128,
            0, 255, 0, 128
        ];
        storedBottomUp.CopyTo(data, 0x60);
        return data;
    }
}
