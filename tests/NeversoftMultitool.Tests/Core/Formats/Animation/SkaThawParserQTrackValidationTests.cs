using System.Buffers.Binary;
using System.Numerics;
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

    [Theory]
    [InlineData(false, 4, 0x7FC00000u)] // LE X = NaN
    [InlineData(false, 8, 0x7F800000u)] // LE Y = +Infinity
    [InlineData(true, 4, 0x7FC00000u)] // BE X = NaN
    [InlineData(true, 12, 0xFF800000u)] // BE Z = -Infinity
    public void ParseThawHiRes_NonFiniteQuaternionComponent_Throws(
        bool bigEndian,
        int componentOffset,
        uint componentBits)
    {
        var data = BuildHighResolutionTrack(bigEndian, isQuaternion: true);
        WriteUInt32(data, 0x2C + componentOffset, componentBits, bigEndian);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Equal(
            "THAW SKA hi-res: bone 0 Q key 0 contains a non-finite component",
            exception.Message);
    }

    [Theory]
    [InlineData(false, 4, 0x7FC00000u)] // LE X = NaN
    [InlineData(false, 12, 0xFF800000u)] // LE Z = -Infinity
    [InlineData(true, 4, 0x7FC00000u)] // BE X = NaN
    [InlineData(true, 8, 0x7F800000u)] // BE Y = +Infinity
    public void ParseThawHiRes_NonFiniteTranslationComponent_Throws(
        bool bigEndian,
        int componentOffset,
        uint componentBits)
    {
        var data = BuildHighResolutionTrack(bigEndian, isQuaternion: false);
        WriteUInt32(data, 0x2C + componentOffset, componentBits, bigEndian);

        var exception = Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));

        Assert.Equal(
            "THAW SKA hi-res: bone 0 T key 0 contains a non-finite component",
            exception.Message);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ParseThawHiRes_FiniteKey_DecodesTrack(bool bigEndian, bool isQuaternion)
    {
        var data = BuildHighResolutionTrack(bigEndian, isQuaternion);
        WriteSingle(data, 0x30, 0.25f, bigEndian);
        WriteSingle(data, 0x34, -0.5f, bigEndian);
        WriteSingle(data, 0x38, 0.125f, bigEndian);

        var animation = SkaFile.Parse(data);

        var track = Assert.Single(animation.BoneTracks);
        if (isQuaternion)
        {
            var key = Assert.Single(track.RotationKeys);
            Assert.Empty(track.TranslationKeys);
            Assert.Equal(1f, key.Time);
            Assert.Equal(-0.25f, key.Rotation.X);
            Assert.Equal(0.5f, key.Rotation.Y);
            Assert.Equal(-0.125f, key.Rotation.Z);
            Assert.True(float.IsFinite(key.Rotation.W));
        }
        else
        {
            Assert.Empty(track.RotationKeys);
            var key = Assert.Single(track.TranslationKeys);
            Assert.Equal(1f, key.Time);
            Assert.Equal(new Vector3(0.25f, -0.5f, 0.125f), key.Translation);
        }
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

    private static byte[] BuildHighResolutionTrack(bool bigEndian, bool isQuaternion)
    {
        const int keyOffset = 0x2C;
        const int highResolutionKeySize = 16;
        var data = new byte[keyOffset + highResolutionKeySize];

        WriteUInt32(data, 0, SkaThawParser.ThawVersion, bigEndian);
        WriteUInt32(data, 4, SkaFile.FlagPlatform, bigEndian);
        WriteSingle(data, 8, 1f, bigEndian);
        data[0x0D] = 1; // numBones
        WriteUInt16(data, 0x0E, isQuaternion ? (ushort)1 : (ushort)0, bigEndian);
        WriteUInt16(data, 0x10, isQuaternion ? (ushort)0 : (ushort)1, bigEndian);
        data.AsSpan(0x14, 20).Fill(0xFF);
        data[0x28] = isQuaternion ? (byte)1 : (byte)0;
        data[0x29] = isQuaternion ? (byte)0 : (byte)1;
        WriteUInt16(data, keyOffset, 60, bigEndian); // timestamp: 1 second

        return data;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteSingle(byte[] data, int offset, float value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);
    }
}
