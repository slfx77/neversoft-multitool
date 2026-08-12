using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ModelDocumentGeometryAdapterTests
{
    [Fact]
    public void AddTriangle_NonFinitePosition_IsNotEmitted()
    {
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var vertices = new List<ModelVertex>();
            var indices = new List<int>();

            ModelDocumentGeometryAdapter.AddTriangle(
                vertices,
                indices,
                Vertex(new Vector3(value, 0f, 0f)),
                Vertex(Vector3.UnitX),
                Vertex(Vector3.UnitY));

            Assert.Empty(vertices);
            Assert.Empty(indices);
        }
    }

    [Fact]
    public void AddSkinnedTriangle_NonFinitePosition_IsNotEmitted()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        var influences = new List<ModelBoneInfluences>();
        var influence = ModelBoneInfluences.Single(0);

        ModelDocumentGeometryAdapter.AddSkinnedTriangle(
            vertices,
            indices,
            influences,
            Vertex(Vector3.Zero), influence,
            Vertex(new Vector3(0f, float.NaN, 0f)), influence,
            Vertex(Vector3.UnitY), influence);

        Assert.Empty(vertices);
        Assert.Empty(indices);
        Assert.Empty(influences);
    }

    [Fact]
    public void AddTriangle_FiniteNondegeneratePositions_IsEmitted()
    {
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();

        ModelDocumentGeometryAdapter.AddTriangle(
            vertices,
            indices,
            Vertex(Vector3.Zero),
            Vertex(Vector3.UnitX),
            Vertex(Vector3.UnitY));

        Assert.Equal(3, vertices.Count);
        Assert.Equal([0, 1, 2], indices);
    }

    private static ModelVertex Vertex(Vector3 position) =>
        new(position, Vector3.UnitZ, Vector4.One, Vector2.Zero);
}
