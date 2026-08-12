using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.N64;

public sealed class N64TexFileCi4PredicateTests
{
    private const int PayloadOffset = 0x3F;
    private const int PayloadSize = 8;
    private const int PaletteSize = 32;

    [Fact]
    public void IsDictionaryRecord_Ci4PayloadWithoutPalette_IsRejected()
    {
        var data = CreateCi4Record(includePalette: false);

        Assert.Equal(71, data.Length);
        Assert.False(N64TexFile.IsDictionaryRecord(data));
        Assert.False(N64TexFile.IsN64Texture(data));
    }

    [Fact]
    public void IsDictionaryRecord_Ci4PayloadWithCompletePalette_IsAcceptedAndDecodes()
    {
        var data = CreateCi4Record(includePalette: true);

        Assert.Equal(103, data.Length);
        Assert.True(N64TexFile.IsDictionaryRecord(data));
        Assert.True(N64TexFile.IsN64Texture(data));

        var texture = N64TexFile.Decode(data);

        Assert.Equal((1, 1, "CI4"), (texture.Width, texture.Height, texture.Format));
        Assert.Equal([255, 255, 255, 255], texture.Rgba);
    }

    private static byte[] CreateCi4Record(bool includePalette)
    {
        var data = new byte[PayloadOffset + PayloadSize + (includePalette ? PaletteSize : 0)];
        data[0] = (byte)'c';
        data[1] = (byte)'i';
        data[2] = (byte)'4';

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x20), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x22), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x26), 0x0204);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x2A), PayloadSize);

        if (includePalette)
        {
            // Pixel index zero selects opaque white; the remaining entries
            // stay zero because this 1x1 control fixture never references them.
            BinaryPrimitives.WriteUInt16BigEndian(
                data.AsSpan(PayloadOffset + PayloadSize),
                0xFFFF);
        }

        return data;
    }
}
