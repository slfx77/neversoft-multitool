using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.RenderWare;

public sealed class RwTxdFileTests
{
    [Fact]
    public void Parse_DeclaredTextureWithoutChildHeader_FailsExplicitly()
    {
        var result = RwTxdFile.Parse(CreateDictionary(textureCount: 1));

        Assert.False(result.Success);
        Assert.Equal(
            "RW TexDict is truncated before declared texture 1 of 1.",
            result.ErrorMessage);
    }

    [Fact]
    public void Parse_EmptyDictionary_Succeeds()
    {
        var result = RwTxdFile.Parse(CreateDictionary(textureCount: 0));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    private static byte[] CreateDictionary(ushort textureCount)
    {
        var data = new byte[28];

        BinaryPrimitives.WriteUInt32LittleEndian(data, 0x0016); // Texture Dictionary
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), 0x0310);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x0001); // Struct
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 0x0310);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), textureCount);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), 0); // deviceId

        return data;
    }
}
