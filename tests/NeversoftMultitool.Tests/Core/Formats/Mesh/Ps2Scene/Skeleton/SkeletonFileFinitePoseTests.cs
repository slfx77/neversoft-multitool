using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skeleton;

public sealed class SkeletonFileFinitePoseTests
{
    [Theory]
    [InlineData(28, "NaN")]
    [InlineData(44, "Infinity")]
    public void Parse_ThugSkeletonWithNonFinitePose_Throws(int offset, string value)
    {
        var data = BuildOneBoneThugSkeleton();
        BinaryPrimitives.WriteSingleLittleEndian(
            data.AsSpan(offset, 4),
            value == "NaN" ? float.NaN : float.PositiveInfinity);

        var error = Assert.Throws<InvalidDataException>(() => SkeletonFile.Parse(data));

        Assert.Contains("non-finite", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ThugSkeletonWithIdentityPose_ReturnsFiniteBone()
    {
        var skeleton = SkeletonFile.Parse(BuildOneBoneThugSkeleton());

        var bone = Assert.Single(skeleton.Bones);
        Assert.Equal(Quaternion.Identity, bone.LocalRotation);
        Assert.Equal(Vector3.Zero, bone.LocalTranslation);
        Assert.Equal(Matrix4x4.Identity, bone.InverseBindMatrix);
    }

    private static byte[] BuildOneBoneThugSkeleton()
    {
        // 16-byte standalone header + three u32 name tables + one 32-byte neutral pose.
        var data = new byte[60];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 0x222756D5);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0x12345678);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(40, 4), 1f);
        return data;
    }
}
