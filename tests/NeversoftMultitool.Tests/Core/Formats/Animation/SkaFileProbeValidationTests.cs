using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class SkaFileProbeValidationTests
{
    private const uint FlagThps3RpHAnim = 1u << 31;

    private static readonly uint[] InvalidDurationBits =
    [
        0xBF800000u, // -1
        0x7FC00000u, // NaN
        0x7F800000u, // +infinity
        0xFF800000u // -infinity
    ];

    [Theory]
    [InlineData(SkaFile.FlagPlatform)]
    [InlineData(SkaFile.FlagUseCompressTable)]
    public void TryProbe_UnrepresentableBoneCount_ReturnsNull(uint flags)
    {
        var data = BuildHeader(flags, uint.MaxValue);

        Assert.True(SkaFile.IsSkaFile(data));
        Assert.Null(SkaFile.TryProbe(data));
    }

    [Theory]
    [InlineData(SkaFile.FlagPlatform)]
    [InlineData(SkaFile.FlagUseCompressTable)]
    public void TryProbe_RepresentableBoneCount_ReturnsMetadata(uint flags)
    {
        var data = BuildHeader(flags, 1);

        var result = SkaFile.TryProbe(data);

        Assert.NotNull(result);
        Assert.Equal(1, result.BoneCount);
    }

    [Theory]
    [InlineData(SkaFile.FlagPlatform)]
    [InlineData(SkaFile.FlagUseCompressTable)]
    [InlineData(FlagThps3RpHAnim)]
    public void TryProbe_OrdinaryInvalidDuration_ReturnsNull(uint flags)
    {
        foreach (var durationBits in InvalidDurationBits)
        {
            var data = BuildHeader(flags, 1, durationBits);

            Assert.True(SkaFile.IsSkaFile(data));
            Assert.Null(SkaFile.TryProbe(data));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TryProbe_ThawInvalidDuration_ReturnsNull(bool bigEndian)
    {
        foreach (var durationBits in InvalidDurationBits)
        {
            var data = BuildThawHeader(bigEndian, durationBits);

            Assert.True(SkaFile.IsSkaFile(data));
            Assert.Null(SkaFile.TryProbe(data));
        }
    }

    [Theory]
    [InlineData(SkaFile.FlagPlatform, 0u)]
    [InlineData(SkaFile.FlagPlatform, 0x80000000u)]
    [InlineData(SkaFile.FlagUseCompressTable, 0u)]
    [InlineData(SkaFile.FlagUseCompressTable, 0x80000000u)]
    [InlineData(FlagThps3RpHAnim, 0u)]
    [InlineData(FlagThps3RpHAnim, 0x80000000u)]
    public void TryProbe_OrdinarySignedZeroDuration_ReturnsMetadata(uint flags, uint durationBits)
    {
        var data = BuildHeader(flags, 1, durationBits);

        Assert.True(SkaFile.IsSkaFile(data));
        var result = Assert.IsType<SkaProbeResult>(SkaFile.TryProbe(data));
        Assert.Equal(durationBits, BitConverter.SingleToUInt32Bits(result.Duration));
        int? expectedBoneCount = flags == FlagThps3RpHAnim ? null : 1;
        Assert.Equal(expectedBoneCount, result.BoneCount);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(false, 0x80000000u)]
    [InlineData(true, 0u)]
    [InlineData(true, 0x80000000u)]
    public void TryProbe_ThawSignedZeroDuration_ReturnsMetadata(bool bigEndian, uint durationBits)
    {
        var data = BuildThawHeader(bigEndian, durationBits);

        Assert.True(SkaFile.IsSkaFile(data));
        var result = Assert.IsType<SkaProbeResult>(SkaFile.TryProbe(data));
        Assert.Equal(durationBits, BitConverter.SingleToUInt32Bits(result.Duration));
        Assert.Equal(1, result.BoneCount);
    }

    private static byte[] BuildHeader(
        uint flags, uint boneCount, uint durationBits = 0x3FC00000u)
    {
        var data = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), flags);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), durationBits);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), boneCount);
        return data;
    }

    private static byte[] BuildThawHeader(bool bigEndian, uint durationBits)
    {
        var data = new byte[0x30];
        WriteUInt32(data, 0, SkaThawParser.ThawVersion, bigEndian);
        WriteUInt32(data, 4, SkaFile.FlagPlatform, bigEndian);
        WriteUInt32(data, 8, durationBits, bigEndian);
        data[0x0D] = 1;
        data.AsSpan(0x14, 20).Fill(0xFF);
        return data;
    }

    private static void WriteUInt32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
