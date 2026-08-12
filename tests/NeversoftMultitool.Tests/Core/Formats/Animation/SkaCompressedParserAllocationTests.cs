using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaCompressedParserAllocationTests
{
    [Fact]
    public void ParseCompressed_TTrackCannotBorrowBytesBeyondDeclaredAllocation()
    {
        var data = BuildSingleTranslationTrack(tAllocationSize: 0);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Contains("T size table totals 7 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("allocation 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCompressed_ExactDeclaredTAllocationDecodesTrack()
    {
        var animation = SkaFile.Parse(BuildSingleTranslationTrack(tAllocationSize: 7));

        var track = Assert.Single(animation.BoneTracks);
        Assert.Empty(track.RotationKeys);
        var key = Assert.Single(track.TranslationKeys);
        Assert.Equal(0f, key.Time);
        Assert.Equal(Vector3.Zero, key.Translation);
    }

    private static byte[] BuildSingleTranslationTrack(uint tAllocationSize)
    {
        var data = new byte[47];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1); // version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1u << 23); // USECOMPRESSTABLE
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 1); // numBones
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), 1); // numTKeys
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), tAllocationSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 7); // bone 0 T byte size

        // Short timestamp 0 followed by three direct s16 zero components.
        data[40] = 0x40;
        return data;
    }
}
