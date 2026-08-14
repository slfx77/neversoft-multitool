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

    [Theory]
    [InlineData(2, 0x7FC00000u)] // X = NaN
    [InlineData(6, 0x7F800000u)] // Y = +Infinity
    [InlineData(10, 0xFF800000u)] // Z = -Infinity
    public void ParsePlatform_NonFiniteHighResolutionQuaternionComponent_Throws(
        int componentOffset,
        uint componentBits)
    {
        var data = BuildHighResolutionTracks(qKeyCount: 1, tKeyCount: 0);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32 + componentOffset), componentBits);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Equal(
            "SKA platform: bone 0 high-resolution Q key 0 contains a non-finite component",
            exception.Message);
    }

    [Theory]
    [InlineData(2, 0x7FC00000u)] // X = NaN
    [InlineData(6, 0x7F800000u)] // Y = +Infinity
    [InlineData(10, 0xFF800000u)] // Z = -Infinity
    public void ParsePlatform_NonFiniteHighResolutionTranslationComponent_Throws(
        int componentOffset,
        uint componentBits)
    {
        var data = BuildHighResolutionTracks(qKeyCount: 0, tKeyCount: 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32 + componentOffset), componentBits);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Equal(
            "SKA platform: bone 0 high-resolution T key 0 contains a non-finite component",
            exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ParsePlatform_FiniteHighResolutionKey_DecodesTrack(bool isQuaternion)
    {
        var data = BuildHighResolutionTracks(
            qKeyCount: isQuaternion ? 1 : 0,
            tKeyCount: isQuaternion ? 0 : 1);

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), 60); // timestamp: 1 second
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(34), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(38), -0.5f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(42), 0.125f);

        var animation = SkaFile.Parse(data);

        var track = Assert.Single(animation.BoneTracks);
        if (isQuaternion)
        {
            var qKey = Assert.Single(track.RotationKeys);
            Assert.Empty(track.TranslationKeys);
            Assert.Equal(1f, qKey.Time);
            Assert.Equal(-0.25f, qKey.Rotation.X);
            Assert.Equal(0.5f, qKey.Rotation.Y);
            Assert.Equal(-0.125f, qKey.Rotation.Z);
            Assert.True(float.IsFinite(qKey.Rotation.W));
        }
        else
        {
            Assert.Empty(track.RotationKeys);
            var tKey = Assert.Single(track.TranslationKeys);
            Assert.Equal(1f, tKey.Time);
            Assert.Equal(new Vector3(0.25f, -0.5f, 0.125f), tKey.Translation);
        }
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

    private static byte[] BuildHighResolutionTracks(int qKeyCount, int tKeyCount)
    {
        const int keyDataOffset = 32;
        const int highResolutionKeySize = 14;
        var data = new byte[keyDataOffset + highResolutionKeySize * (qKeyCount + tKeyCount)];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1); // version
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(4, 4),
            SkaFile.FlagPlatform | SkaFile.FlagHiResFramePointers);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(8, 4), 2f); // duration
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 1); // numBones
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), (uint)qKeyCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20, 4), (uint)tKeyCount);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(28, 2), (short)qKeyCount);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(30, 2), (short)tKeyCount);

        return data;
    }
}
