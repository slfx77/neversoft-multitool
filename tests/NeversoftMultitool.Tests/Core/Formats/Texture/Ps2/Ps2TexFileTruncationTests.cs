using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public sealed class Ps2TexFileTruncationTests
{
    [Fact]
    public void Parse_DeclaredGroupWithoutHeader_Fails()
    {
        var data = CreateVersion3Header(groupCount: 1, totalTextureCount: 0);

        var result = Ps2TexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated group header at group 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_DeclaredTextureWithoutHeader_Fails()
    {
        var data = new byte[24];
        WriteUInt32(data, 0, 3); // version
        WriteUInt32(data, 4, 1); // group count
        WriteUInt32(data, 8, 1); // total texture count (informational)
        WriteUInt32(data, 12, 0x12345678); // group checksum
        WriteUInt32(data, 16, 0); // group flags
        WriteUInt32(data, 20, 1); // texture count

        var result = Ps2TexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Truncated texture header at group 0, texture 0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_ZeroDeclaredGroups_SucceedsWithNoTextures()
    {
        var data = CreateVersion3Header(groupCount: 0, totalTextureCount: 0);

        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    private static byte[] CreateVersion3Header(uint groupCount, uint totalTextureCount)
    {
        var data = new byte[12];
        WriteUInt32(data, 0, 3);
        WriteUInt32(data, 4, groupCount);
        WriteUInt32(data, 8, totalTextureCount);
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
