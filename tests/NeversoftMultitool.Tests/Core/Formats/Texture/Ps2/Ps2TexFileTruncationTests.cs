using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

public sealed class Ps2TexFileTruncationTests
{
    [Fact]
    public void Parse_ShiftWrappedTextureWidth_Fails()
    {
        var data = CreateVersion3Texture(tw: 32, th: 0, mxl: 0, includePixel: true);

        var result = Ps2TexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Invalid dimensions TW=32 TH=0", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_MaximumTextureWidth_RemainsValid()
    {
        var data = CreateVersion3Texture(tw: 11, th: 0, mxl: 0x80000000, includePixel: false);

        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(2048, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Null(texture.Pixels);
    }

    [Fact]
    public void Parse_DeclaredGroupWithoutHeader_Fails()
    {
        var data = CreateTexHeader(version: 3, groupCount: 1, totalTextureCount: 0);

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

    [Theory]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(5u)]
    public void Parse_ZeroDeclaredGroupsWithPositiveTotal_Fails(uint version)
    {
        var data = CreateTexHeader(version, groupCount: 0, totalTextureCount: 1);

        var result = Ps2TexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("Invalid total texture count 1 for zero groups", result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_ZeroDeclaredGroups_SucceedsWithNoTextures()
    {
        var data = CreateTexHeader(version: 3, groupCount: 0, totalTextureCount: 0);

        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    private static byte[] CreateTexHeader(uint version, uint groupCount, uint totalTextureCount)
    {
        var data = new byte[12];
        WriteUInt32(data, 0, version);
        WriteUInt32(data, 4, groupCount);
        WriteUInt32(data, 8, totalTextureCount);
        return data;
    }

    private static byte[] CreateVersion3Texture(uint tw, uint th, uint mxl, bool includePixel)
    {
        var data = new byte[includePixel ? 52 : 48];
        WriteUInt32(data, 0, 3); // version
        WriteUInt32(data, 4, 1); // group count
        WriteUInt32(data, 8, 1); // total texture count
        WriteUInt32(data, 12, 0x12345678); // group checksum
        WriteUInt32(data, 20, 1); // texture count
        WriteUInt32(data, 24, 0x89ABCDEF); // texture checksum
        WriteUInt32(data, 28, tw);
        WriteUInt32(data, 32, th);
        WriteUInt32(data, 44, mxl);
        // PSMCT32, CPSM, and the optional one-pixel payload remain zero.
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
