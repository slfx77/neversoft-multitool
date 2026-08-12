using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skeleton;

public sealed class Ps2SkeletonFileValidationTests
{
    [Theory]
    [InlineData(24, float.NaN)]
    [InlineData(40, float.PositiveInfinity)]
    public void Parse_NonFiniteNeutralPose_IsRejected(int offset, float value)
    {
        var data = CreateOneBoneSkeleton();
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset), value);

        var exception = Assert.Throws<InvalidDataException>(() => Ps2SkeletonFile.Parse(data));

        Assert.Equal("Bone 0 neutral pose contains non-finite values", exception.Message);
    }

    [Fact]
    public void Parse_FiniteIdentityPose_ReturnsFiniteBone()
    {
        var skeleton = Ps2SkeletonFile.Parse(CreateOneBoneSkeleton());

        var bone = Assert.Single(skeleton.Bones);
        Assert.Equal(Quaternion.Identity, bone.LocalRotation);
        Assert.Equal(Vector3.Zero, bone.LocalTranslation);
        Assert.Equal(Matrix4x4.Identity, bone.InverseBindMatrix);
    }

    private static byte[] CreateOneBoneSkeleton()
    {
        var data = new byte[56];
        BinaryPrimitives.WriteInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 0x12345678);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(36), 1f);
        return data;
    }
}
