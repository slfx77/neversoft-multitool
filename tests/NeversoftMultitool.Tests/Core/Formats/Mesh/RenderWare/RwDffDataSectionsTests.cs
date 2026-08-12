using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.RenderWare;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.RenderWare;

public sealed class RwDffDataSectionsTests
{
    [Fact]
    public void ParseSkinPlg_DeclaredPayloadPastEnd_ReturnsNull()
    {
        var data = BuildSkinHeader(1, 1, 8);

        Assert.Null(RwDffDataSections.ParseSkinPlg(data, 0, 104));
    }

    [Fact]
    public void ParseSkinPlg_OverflowingVertexCount_ReturnsNull()
    {
        var data = BuildSkinHeader(1, int.MaxValue, 64);

        Assert.Null(RwDffDataSections.ParseSkinPlg(data, 0, data.Length));
    }

    [Fact]
    public void ParseSkinPlg_CompleteSingleVertexAndBone_Parses()
    {
        var data = BuildSkinHeader(1, 1, 104);
        data[8] = 7;
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(12), 1f);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 42);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32), 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(36), 5);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(40), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(60), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(80), 1f);
        BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(100), 1f);

        var skin = RwDffDataSections.ParseSkinPlg(data, 0, data.Length);

        Assert.NotNull(skin);
        Assert.Equal(1, skin.NumBones);
        Assert.Equal(1, skin.NumVertices);
        Assert.Equal([7, 0, 0, 0], skin.BoneIndices);
        Assert.Equal([1f, 0f, 0f, 0f], skin.BoneWeights);
        var bone = Assert.Single(skin.Bones);
        Assert.Equal(42, bone.Id);
        Assert.Equal(3, bone.Index);
        Assert.Equal(5, bone.Flags);
        Assert.Equal(1f, bone.InverseBindMatrix.M11);
        Assert.Equal(1f, bone.InverseBindMatrix.M22);
        Assert.Equal(1f, bone.InverseBindMatrix.M33);
        Assert.Equal(1f, bone.InverseBindMatrix.M44);
    }

    private static byte[] BuildSkinHeader(int numBones, int numVertices, int length)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(data, numBones);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), numVertices);
        return data;
    }
}
