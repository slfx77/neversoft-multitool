using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.XbxScene;

public sealed class XbxSceneVertexBlockValidationTests
{
    [Fact]
    public void Parse_NegativeLinkCount_ThrowsInvalidData()
    {
        var data = BuildMinimalScene(linkCount: -1);

        Assert.True(XbxSceneFile.IsXbxScene(data));
        var exception = Assert.Throws<InvalidDataException>(() => XbxSceneFile.Parse(data));

        Assert.Contains("link count -1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ZeroLinkCount_ReturnsEmptyScene()
    {
        var scene = XbxSceneFile.Parse(BuildMinimalScene(linkCount: 0));

        Assert.Empty(scene.Materials);
        Assert.Empty(scene.Sectors);
        Assert.Empty(scene.Links);
    }

    [Fact]
    public void Parse_DeclaredVertexStrideSpanCannotExceedVertexBlock()
    {
        var data = BuildSingleVertexScene(blockBytes: 19);

        var exception = Assert.Throws<InvalidDataException>(() => XbxSceneFile.Parse(data));

        Assert.Contains("requires 20 bytes", exception.Message, StringComparison.Ordinal);
        Assert.Contains("19-byte block", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ExactVertexBlockDecodesVertex()
    {
        var scene = XbxSceneFile.Parse(BuildSingleVertexScene(blockBytes: 20));

        var sector = Assert.Single(scene.Sectors);
        var mesh = Assert.Single(sector.Meshes);
        var vertex = Assert.Single(mesh.Vertices);
        Assert.Equal(Vector3.Zero, vertex.Position);
        Assert.Empty(scene.Links);
    }

    private static byte[] BuildMinimalScene(int linkCount)
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1); // material version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1); // mesh version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 1); // vertex version
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), linkCount);
        return data;
    }

    private static byte[] BuildSingleVertexScene(int blockBytes)
    {
        var data = new byte[201];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), 1); // material version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 1); // mesh version
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), 1); // vertex version
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 1); // sector count
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(32, 4), 1); // mesh count
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(124, 4), 1); // LOD count

        data[148] = 20; // vertex stride: position plus one UV set
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(149, 2), 1); // vertex count
        data[151] = 1; // vertex-buffer count
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(153, 4), blockBytes);
        // Vertex bytes, the 20-byte LOD trailer, and the zero link count remain zero.
        return data;
    }
}
