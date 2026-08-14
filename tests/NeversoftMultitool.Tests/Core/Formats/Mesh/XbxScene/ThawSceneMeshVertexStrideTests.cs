using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class ThawSceneMeshVertexStrideTests
{
    [Fact]
    public void ReadSMesh_NegativeFaceCount_ThrowsInvalidData()
    {
        var data = new byte[224];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(40), -1);
        using var reader = new BinaryReader(new MemoryStream(data, writable: false));

        var exception = Assert.Throws<InvalidDataException>(
            () => ThawSceneMeshSupport.ReadSMesh(reader, 0, 0, 0, []));

        Assert.Equal("THAW mesh face count -1 is invalid", exception.Message);
    }

    [Fact]
    public void ReadSMesh_VertexStrideShorterThanDecodedFields_SkipsVertices()
    {
        using var reader = CreateMeshReader(vertexStride: 19);

        var mesh = ThawSceneMeshSupport.ReadSMesh(reader, 0, 0, 0, []);

        Assert.Empty(mesh.Vertices);
    }

    [Fact]
    public void ReadSMesh_ExactVertexStride_DecodesVertex()
    {
        using var reader = CreateMeshReader(vertexStride: 20);

        var mesh = ThawSceneMeshSupport.ReadSMesh(reader, 0, 0, 0, []);

        Assert.Single(mesh.Vertices);
    }

    private static BinaryReader CreateMeshReader(byte vertexStride)
    {
        const int headerSize = 224;
        const int decodedVertexSize = 20;
        var data = new byte[headerSize + decodedVertexSize];
        data[24] = vertexStride;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(124), headerSize);
        return new BinaryReader(new MemoryStream(data, writable: false));
    }
}
