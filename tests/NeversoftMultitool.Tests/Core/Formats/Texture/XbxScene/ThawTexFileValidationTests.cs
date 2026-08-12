using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.XbxScene;

public sealed class ThawTexFileValidationTests
{
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

    private static byte[] CreateSingleTextureDictionary(bool hasMip)
    {
        var data = new byte[hasMip ? 40 : 32];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0xABADD00D);
        data[4] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 1);

        var entry = data.AsSpan(8, 24);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0xABADD00D);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[12..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[14..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[16..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(entry[18..], 1);
        entry[20] = hasMip ? (byte)1 : (byte)0;
        entry[21] = 32;

        if (hasMip)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 4);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 1);
            data[36] = 0x00;
            data[37] = 0x00;
            data[38] = 0xFF;
            data[39] = 0xFF;
        }

        return data;
    }
}
