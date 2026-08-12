using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Scene;

public sealed class Ps2SceneFileValidationTests
{
    [Fact]
    public void Parse_NegativeVertexCount_Throws()
    {
        var data = BuildMinimalScene(-1);

        var exception = Assert.Throws<InvalidDataException>(() => Ps2SceneFile.Parse(data));

        Assert.Equal("Mesh 0x12345678 has invalid vertex count -1.", exception.Message);
    }

    [Fact]
    public void Parse_ZeroVertexCount_ReturnsEmptyMesh()
    {
        var scene = Ps2SceneFile.Parse(BuildMinimalScene(0));

        var group = Assert.Single(scene.MeshGroups);
        var mesh = Assert.Single(group.Meshes);
        Assert.Empty(mesh.Vertices);
    }

    private static byte[] BuildMinimalScene(int vertexCount)
    {
        var data = new byte[120];
        BinaryPrimitives.WriteInt32LittleEndian(data, 3); // material version
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 4); // mesh version
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 1); // vertex version
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 0); // materials
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), 1); // mesh groups
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20), 1); // total meshes
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 0xAABBCCDD); // group checksum
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), 1); // group meshes
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 0x12345678); // mesh checksum
        // LODs, hierarchy, children, sphere, material, flags, and bounds remain zero.
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(116), vertexCount);
        return data;
    }
}
