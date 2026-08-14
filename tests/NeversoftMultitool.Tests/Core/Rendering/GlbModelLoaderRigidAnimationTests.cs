using System.Numerics;
using NeversoftMultitool.Core.Rendering;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Rendering;

using GltfVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

public sealed class GlbModelLoaderRigidAnimationTests
{
    [Fact]
    public void Load_RigidTranslationAnimation_AppliesRequestedTime()
    {
        var model = BuildAnimatedTriangle();
        var animation = Assert.Single(model.LogicalAnimations);

        var bindPose = Assert.Single(GlbModelLoader.Load(model, null, 0f).Submeshes);
        var atZero = Assert.Single(GlbModelLoader.Load(model, animation, 0f).Submeshes);
        var atOne = Assert.Single(GlbModelLoader.Load(model, animation, 1f).Submeshes);

        AssertPositions(bindPose.Positions,
            Vector3.Zero,
            Vector3.UnitX,
            new Vector3(0f, 2f, 0f));
        AssertPositions(atZero.Positions,
            Vector3.Zero,
            Vector3.UnitX,
            new Vector3(0f, 2f, 0f));
        AssertPositions(atOne.Positions,
            new Vector3(10f, 0f, 0f),
            new Vector3(11f, 0f, 0f),
            new Vector3(10f, 2f, 0f));
    }

    [Fact]
    public void Load_AnimatedParentWithStaticShearChild_PreservesMatrixTransform()
    {
        var mesh = BuildTriangleMesh();
        var parent = new NodeBuilder("moving_parent");
        var translation = parent.UseTranslation("move");
        translation.SetPoint(0f, Vector3.Zero, true);
        translation.SetPoint(1f, new Vector3(10f, 0f, 0f), true);

        var shear = Matrix4x4.Identity;
        shear.M21 = 0.5f;
        var child = parent.CreateNode("sheared_triangle");
        child.LocalMatrix = shear;

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, child);
        var model = scene.ToGltf2();
        var animation = Assert.Single(model.LogicalAnimations);

        var atOne = Assert.Single(GlbModelLoader.Load(model, animation, 1f).Submeshes);

        AssertPositions(atOne.Positions,
            new Vector3(10f, 0f, 0f),
            new Vector3(11f, 0f, 0f),
            new Vector3(11f, 2f, 0f));
    }

    private static ModelRoot BuildAnimatedTriangle()
    {
        var mesh = BuildTriangleMesh();

        var node = new NodeBuilder("moving_triangle");
        var translation = node.UseTranslation("move");
        translation.SetPoint(0f, Vector3.Zero, true);
        translation.SetPoint(1f, new Vector3(10f, 0f, 0f), true);

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, node);
        return scene.ToGltf2();
    }

    private static MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>
        BuildTriangleMesh()
    {
        var mesh = new MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>("triangle");
        var primitive = mesh.UsePrimitive(new MaterialBuilder("material"));
        primitive.AddTriangle(
            MakeVertex(Vector3.Zero),
            MakeVertex(Vector3.UnitX),
            MakeVertex(new Vector3(0f, 2f, 0f)));
        return mesh;
    }

    private static GltfVertex MakeVertex(Vector3 position)
    {
        return new GltfVertex(
            new VertexPositionNormal(position, Vector3.UnitZ),
            new VertexColor1Texture1(Vector4.One, Vector2.Zero));
    }

    private static void AssertPositions(float[] actual, params Vector3[] expected)
    {
        Assert.Equal(expected.Length * 3, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual[i * 3]);
            Assert.Equal(expected[i].Y, actual[i * 3 + 1]);
            Assert.Equal(expected[i].Z, actual[i * 3 + 2]);
        }
    }
}
