using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class BlendPackageWriterIndexValidationTests
{
    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    public void Write_IncompleteTriangleIndices_RejectsBeforeArchiveCreation(int indexCount)
    {
        var document = new ModelDocument { Name = "incomplete" };
        var mesh = new ModelMesh { Name = "mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "partial",
            Vertices = Enumerable.Range(0, indexCount)
                .Select(static index => new ModelVertex(
                    new Vector3(index, 0, 0),
                    Vector3.UnitZ,
                    Vector4.One,
                    Vector2.Zero))
                .ToArray(),
            Indices = Enumerable.Range(0, indexCount).ToArray()
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "node",
            MeshIndex = 0
        });
        using var package = new MemoryStream();

        var exception = Assert.Throws<InvalidDataException>(() =>
            BlendPackageWriter.Write(document, package, "incomplete.blend"));

        Assert.Equal(
            $"Mesh primitive 'partial' has {indexCount} indices; " +
            "triangle indices must contain complete triples.",
            exception.Message);
        Assert.Equal(0, package.Length);
    }
}
