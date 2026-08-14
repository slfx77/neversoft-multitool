using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

public sealed class ThawZoneTexOwnerBlobDecoderTests
{
    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 1u, 2)]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 2u, 1)]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 3u, 4)]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 4u, 3)]
    public void DecodeRecord_IncompleteDirectColorBaseSurface_ReturnsNull(
        uint psm,
        uint dataSize,
        int fileSize)
    {
        var data = new byte[fileSize];
        var entry = CreateDirectColorEntry(psm, dataSize);

        var texture = ThawZoneTexOwnerBlobDecoder.DecodeRecord(data, entry, 0, 0, 0, 0);

        Assert.Null(texture);
    }

    [Theory]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 2u, 2)]
    [InlineData(Ps2TexPixelDecoder.PSMCT16, 3u, 3)]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 4u, 4)]
    [InlineData(Ps2TexPixelDecoder.PSMCT32, 5u, 5)]
    public void DecodeRecord_CompleteDirectColorBaseSurface_Decodes(
        uint psm,
        uint dataSize,
        int fileSize)
    {
        var data = new byte[fileSize];
        byte[] expectedPixels;
        if (psm == Ps2TexPixelDecoder.PSMCT16)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data, 0x001F);
            expectedPixels = [248, 0, 0, 255];
        }
        else
        {
            data[0] = 0x11;
            data[1] = 0x22;
            data[2] = 0x33;
            data[3] = 0x40;
            expectedPixels = [0x11, 0x22, 0x33, 0x80];
        }

        var entry = CreateDirectColorEntry(psm, dataSize);

        var texture = ThawZoneTexOwnerBlobDecoder.DecodeRecord(data, entry, 0, 0, 0, 0);

        Assert.NotNull(texture);
        Assert.Equal(expectedPixels, texture.Pixels);
    }

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

    private static ThawZoneTexFile.ZoneTexHeaderEntry CreateDirectColorEntry(uint psm, uint dataSize)
    {
        return new ThawZoneTexFile.ZoneTexHeaderEntry(
            Checksum: 0x12345678,
            Tex0: (ulong)psm << 20,
            DataSize: dataSize,
            DataOffset: 0,
            PaletteBytes: 0,
            UploadOffset: 0);
    }
}
