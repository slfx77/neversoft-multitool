using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaThawParserQTrackValidationTests
{
    private const int QDataOffset = 0x34;

    [Theory]
    [InlineData(1, 0x0000, "key header")]
    [InlineData(2, 0x0000, "key payload")]
    [InlineData(2, 0x4000, "key payload")]
    [InlineData(4, 0x7800, "key payload")]
    public void ParseThaw_TruncatedCompressedQRecord_ThrowsAtTrackBoundary(
        int qSize, ushort header, string expectedContext)
    {
        var data = BuildCompressedQTrack(qSize, header);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Contains(expectedContext, exception.Message, StringComparison.Ordinal);
        Assert.Contains("size-table entry", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseThaw_ZeroSizedCompressedQTrack_ReturnsEmptyTrack()
    {
        var animation = SkaFile.Parse(BuildCompressedQTrack(0));

        var track = Assert.Single(animation.BoneTracks);
        Assert.Empty(track.RotationKeys);
        Assert.Empty(track.TranslationKeys);
    }

    private static byte[] BuildCompressedQTrack(int qSize, ushort header = 0)
    {
        var data = new byte[QDataOffset + qSize];
        BinaryPrimitives.WriteUInt32LittleEndian(data, SkaThawParser.ThawVersion);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), SkaFile.FlagUseCompressTable);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8), 1f);
        data[0x0D] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x0E), qSize > 0 ? (ushort)1 : (ushort)0);
        data.AsSpan(0x14, 20).Fill(0xFF);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x28), (uint)qSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x30), (ushort)qSize);

        if (qSize > 0)
            data[QDataOffset] = (byte)header;
        if (qSize > 1)
            data[QDataOffset + 1] = (byte)(header >> 8);

        return data;
    }
}
