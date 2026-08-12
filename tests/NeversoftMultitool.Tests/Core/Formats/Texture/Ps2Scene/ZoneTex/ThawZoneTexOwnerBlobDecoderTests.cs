using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

public sealed class ThawZoneTexOwnerBlobDecoderTests
{
    [Fact]
    public void DecodeRecord_OneByOnePsmt4_ReadsItsPackedIndexByte()
    {
        var data = new byte[33];
        data[0] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), 0x001F);
        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            Checksum: 0x12345678,
            Tex0: ((ulong)Ps2TexPixelDecoder.PSMT4 << 20) |
                  ((ulong)Ps2TexPixelDecoder.PSMCT16 << 51),
            DataSize: 1,
            DataOffset: 1,
            PaletteBytes: 32,
            UploadOffset: 0);

        var texture = ThawZoneTexOwnerBlobDecoder.DecodeRecord(data, entry, 0, 0, 0, 0);

        Assert.NotNull(texture);
        Assert.Equal(1, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal([248, 0, 0, 255], texture.Pixels);
    }

    [Fact]
    public void DecodeRecord_TwoByOnePsmt4_PreservesPackedNibbleOrder()
    {
        var data = new byte[33];
        data[0] = 0x21;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(3), 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(5), 0x03E0);
        var entry = new ThawZoneTexFile.ZoneTexHeaderEntry(
            Checksum: 0x12345678,
            Tex0: ((ulong)Ps2TexPixelDecoder.PSMT4 << 20) |
                  (1UL << 26) |
                  ((ulong)Ps2TexPixelDecoder.PSMCT16 << 51),
            DataSize: 1,
            DataOffset: 1,
            PaletteBytes: 32,
            UploadOffset: 0);

        var texture = ThawZoneTexOwnerBlobDecoder.DecodeRecord(data, entry, 0, 0, 0, 0);

        Assert.NotNull(texture);
        Assert.Equal(2, texture.Width);
        Assert.Equal(1, texture.Height);
        Assert.Equal([248, 0, 0, 255, 0, 248, 0, 255], texture.Pixels);
    }
}
