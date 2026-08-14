using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skeleton;

public sealed class ThawSkeletonFileValidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_OverlappingMetadataArrays_Throws(bool bigEndian)
    {
        var data = BuildSkeleton([0x80, 0x81, 0x82, 0x83, 0x84, 0x88], bigEndian);

        Assert.False(ThawSkeletonFile.IsThawSkeleton(data));
        Assert.Throws<InvalidDataException>(() => ThawSkeletonFile.Parse(data));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_NonOverlappingMetadataArrays_ReturnsOneBone(bool bigEndian)
    {
        var data = BuildSkeleton([0x80, 0x84, 0x88, 0x8C, 0x90, 0x94], bigEndian);

        Assert.True(ThawSkeletonFile.IsThawSkeleton(data));
        var skeleton = ThawSkeletonFile.Parse(data);
        Assert.Single(skeleton.Bones);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_TrailingByteAfterFinalBlock_Throws(bool bigEndian)
    {
        byte[] data = [.. BuildSkeleton([0x80, 0x84, 0x88, 0x8C, 0x90, 0x94], bigEndian), 0];

        Assert.False(ThawSkeletonFile.IsThawSkeleton(data));
        Assert.Throws<InvalidDataException>(() => ThawSkeletonFile.Parse(data));
    }

    private static byte[] BuildSkeleton(int[] offsets, bool bigEndian)
    {
        var data = new byte[offsets[5] + 128];
        WriteU16(data, 0, 1, bigEndian);
        WriteU16(data, 2, 0x30, bigEndian);
        WriteU32(data, 4, 1, bigEndian);
        for (var i = 0; i < offsets.Length; i++)
            WriteU32(data, 0x10 + i * 4, (uint)offsets[i], bigEndian);

        WriteU32(data, 0x28, 0x40, bigEndian);
        WriteU32(data, 0x2C, 0x30, bigEndian);

        // One identity inverse-bind matrix at matrixOffset.
        for (var i = 0; i < 4; i++)
            WriteU32(data, 0x40 + (i * 5) * 4, BitConverter.SingleToUInt32Bits(1f), bigEndian);

        return data;
    }

    private static void WriteU16(byte[] data, int offset, ushort value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), value);
    }

    private static void WriteU32(byte[] data, int offset, uint value, bool bigEndian)
    {
        if (bigEndian)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);
        else
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), value);
    }
}
