using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaPlatformParserCountTests
{
    [Fact]
    public void ParsePlatform_TTrackCannotExceedDeclaredKeyTotal()
    {
        var data = BuildSingleTranslationTrack(declaredTKeys: 0);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Contains("T key counts total 1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("declared total 0", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParsePlatform_ExactDeclaredTKeyTotalDecodesTrack()
    {
        var animation = SkaFile.Parse(BuildSingleTranslationTrack(declaredTKeys: 1));

        var track = Assert.Single(animation.BoneTracks);
        Assert.Empty(track.RotationKeys);
        var key = Assert.Single(track.TranslationKeys);
        Assert.Equal(0f, key.Time);
        Assert.Equal(Vector3.Zero, key.Translation);
    }

    private static byte[] BuildSingleTranslationTrack(uint declaredTKeys)
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1); // version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1u << 28); // PLATFORM
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 1); // numBones
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), declaredTKeys);
        data[29] = 1; // bone 0 T key count; Q count at byte 28 remains zero
        // The standard 8-byte T record at byte 32 is all zero.
        return data;
    }
}
