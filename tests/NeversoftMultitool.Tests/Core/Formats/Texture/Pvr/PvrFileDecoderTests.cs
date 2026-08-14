using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Pvr;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Pvr;

public sealed class PvrFileDecoderTests
{
    [Fact]
    public void DecodeToRgba_PvrtSizeBelowMetadata_ReturnsNull()
    {
        var data = new byte[8];
        "PVRT"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 7);

        Assert.Null(PvrFileDecoder.DecodeToRgba(data));
    }

    [Fact]
    public void DecodeToRgba_PvrtChunkPastEnd_ReturnsNull()
    {
        var data = new byte[16];
        "PVRT"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        data[8] = 1; // RGB565
        data[9] = 9; // rectangle
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 2);

        Assert.Null(PvrFileDecoder.DecodeToRgba(data));
    }

    [Fact]
    public void DecodeToRgba_ValidDirectPvrtRectangle_DecodesPixels()
    {
        var data = new byte[24];
        "PVRT"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 16);
        data[8] = 1; // RGB565
        data[9] = 9; // rectangle
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(14), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16), 0xF800);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18), 0x07E0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 0xFFFF);

        var result = PvrFileDecoder.DecodeToRgba(data);

        Assert.True(result.HasValue);
        var decoded = result.GetValueOrDefault();
        Assert.Equal(2, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Equal(
            new byte[]
            {
                255, 0, 0, 255,
                0, 255, 0, 255,
                0, 0, 255, 255,
                255, 255, 255, 255
            },
            decoded.Rgba);
    }
}
