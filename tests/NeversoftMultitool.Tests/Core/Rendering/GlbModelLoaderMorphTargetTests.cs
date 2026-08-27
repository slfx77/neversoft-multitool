using System.Numerics;
using NeversoftMultitool.Core.Rendering;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Rendering;

using GltfVertex = VertexBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;
using MorphMesh = MeshBuilder<VertexPositionNormal, VertexColor1Texture1, VertexEmpty>;

/// <summary>
///     A source with no skeleton animates purely through morph weights (the GBA
///     skater blends whole posed vertex sets), so a loader that evaluates only
///     node TRS renders its bind pose at every frame — a silent wrong-output bug
///     rather than a crash. These pin the blend itself, both sampler
///     interpolation modes, and that the pixels actually move.
/// </summary>
public sealed class GlbModelLoaderMorphTargetTests
{
    private static readonly Vector3 BaseA = Vector3.Zero;
    private static readonly Vector3 BaseB = new(1f, 0f, 0f);
    private static readonly Vector3 BaseC = new(0f, 2f, 0f);

    // Target 0 lifts the apex; target 1 stretches the base corner sideways.
    // Each moves a DIFFERENT vertex along a different axis, so a blend cannot be
    // mistaken for either target alone, and neither is a rigid translation the
    // renderer's fit-to-bounds framing could cancel out.
    private static readonly Vector3 Target0Delta = new(0f, 4f, 0f);
    private static readonly Vector3 Target1Delta = new(8f, 0f, 0f);

    [Fact]
    public void Load_AnimatedMorphWeights_BlendsTargetDeltasAtSampledTime()
    {
        var model = BuildMorphingTriangle(linear: true);
        var animation = Assert.Single(model.LogicalAnimations);

        var atZero = Assert.Single(GlbModelLoader.Load(model, animation, 0f).Submeshes);
        var atHalf = Assert.Single(GlbModelLoader.Load(model, animation, 0.5f).Submeshes);
        var atOne = Assert.Single(GlbModelLoader.Load(model, animation, 1f).Submeshes);

        AssertPositions(atZero.Positions, Blend(1f, 0f));
        AssertPositions(atHalf.Positions, Blend(0.5f, 0.5f));
        AssertPositions(atOne.Positions, Blend(0f, 1f));
    }

    [Fact]
    public void Load_StepMorphSampler_HoldsThePreviousKeyframe()
    {
        var model = BuildMorphingTriangle(linear: false);
        var animation = Assert.Single(model.LogicalAnimations);

        var atHalf = Assert.Single(GlbModelLoader.Load(model, animation, 0.5f).Submeshes);
        var atOne = Assert.Single(GlbModelLoader.Load(model, animation, 1f).Submeshes);

        AssertPositions(atHalf.Positions, Blend(1f, 0f));
        AssertPositions(atOne.Positions, Blend(0f, 1f));
    }

    [Fact]
    public void Load_StaticMeshMorphWeights_ApplyWithoutAnimation()
    {
        var model = BuildMorphingTriangle(linear: true);
        model.LogicalMeshes[0].SetMorphWeights(SparseWeight8.Create(0f, 1f));

        var submesh = Assert.Single(GlbModelLoader.Load(model, null, 0f).Submeshes);

        AssertPositions(submesh.Positions, Blend(0f, 1f));
    }

    [Fact]
    public void Load_MorphTargetsWithAllWeightsZero_KeepsTheBasePose()
    {
        var model = BuildMorphingTriangle(linear: true);

        var submesh = Assert.Single(GlbModelLoader.Load(model, null, 0f).Submeshes);

        AssertPositions(submesh.Positions, Blend(0f, 0f));
    }

    [Fact]
    public void Load_MorphTargetNormalDeltas_ApplyToShadingNormals()
    {
        var model = BuildMorphingTriangle(linear: true, normalDelta: new Vector3(0f, 1f, -1f));
        var animation = Assert.Single(model.LogicalAnimations);

        var atZero = Assert.Single(GlbModelLoader.Load(model, animation, 0f).Submeshes);
        var atOne = Assert.Single(GlbModelLoader.Load(model, animation, 1f).Submeshes);

        // Target 0 carries the delta, target 1 does not: fully weighted onto
        // target 0 the normal tips off +Z, and onto target 1 it stays put.
        var morphed = Assert.IsType<float[]>(atZero.Normals);
        var untouched = Assert.IsType<float[]>(atOne.Normals);
        Assert.Equal(Vector3.Normalize(Vector3.UnitZ + new Vector3(0f, 1f, -1f)),
            new Vector3(morphed[0], morphed[1], morphed[2]));
        Assert.Equal(Vector3.UnitZ, new Vector3(untouched[0], untouched[1], untouched[2]));
    }

    [Fact]
    public void RenderScene_MorphedFrames_ProduceDifferentPixels()
    {
        var model = BuildMorphingTriangle(linear: true);
        var animation = Assert.Single(model.LogicalAnimations);

        using var first = RenderFixedCanvas(GlbModelLoader.Load(model, animation, 0f));
        using var last = RenderFixedCanvas(GlbModelLoader.Load(model, animation, 1f));

        var differing = 0;
        for (var y = 0; y < first.Height; y++)
        {
            for (var x = 0; x < first.Width; x++)
            {
                if (first[x, y] != last[x, y]) differing++;
            }
        }

        Assert.True(differing > 0,
            "Morph-animated frames rendered identically; the loader is showing the bind pose.");
    }

    private static SixLabors.ImageSharp.Image<Rgba32> RenderFixedCanvas(RenderScene scene)
    {
        // A fixed canvas with a shared reference extent keeps both frames on the
        // same projection, so a pixel difference means the geometry moved rather
        // than the auto-framing re-fitting around it.
        return GlbRenderer.RenderScene(scene, longEdge: 64,
            fixedWidth: 32, fixedHeight: 32, referenceWidth: 16f, referenceHeight: 16f);
    }

    private static Vector3[] Blend(float weight0, float weight1)
    {
        return
        [
            BaseA,
            BaseB + Target1Delta * weight1,
            BaseC + Target0Delta * weight0
        ];
    }

    private static ModelRoot BuildMorphingTriangle(bool linear, Vector3 normalDelta = default)
    {
        var mesh = new MorphMesh("morphing_triangle");
        var primitive = mesh.UsePrimitive(new MaterialBuilder("material"));
        primitive.AddTriangle(MakeVertex(BaseA), MakeVertex(BaseB), MakeVertex(BaseC));

        AddMorphTarget(mesh, 0, BaseC, Target0Delta, normalDelta);
        AddMorphTarget(mesh, 1, BaseB, Target1Delta, Vector3.Zero);

        var scene = new SceneBuilder();
        scene.AddRigidMesh(mesh, new NodeBuilder("morphing_node"));
        var model = scene.ToGltf2();

        var node = Assert.Single(model.LogicalNodes, candidate => candidate.Mesh != null);
        var animation = model.CreateAnimation("morph");
        animation.CreateMorphChannel(node, new Dictionary<float, SparseWeight8>
        {
            [0f] = SparseWeight8.Create(1f, 0f),
            [1f] = SparseWeight8.Create(0f, 1f)
        }, morphCount: 2, linear);

        return model;
    }

    /// <summary>
    ///     Only <paramref name="movedVertex" /> takes the position delta; every
    ///     vertex takes the normal delta, so the normal channel is exercised
    ///     independently of whether that vertex moves.
    /// </summary>
    private static void AddMorphTarget(MorphMesh mesh, int index,
        Vector3 movedVertex, Vector3 positionDelta, Vector3 normalDelta)
    {
        var target = mesh.UseMorphTarget(index);
        foreach (var basePosition in new[] { BaseA, BaseB, BaseC })
        {
            var delta = basePosition == movedVertex ? positionDelta : Vector3.Zero;
            target.SetVertexDelta(basePosition,
                new VertexGeometryDelta(delta, normalDelta, Vector3.Zero));
        }
    }

    private static GltfVertex MakeVertex(Vector3 position)
    {
        return new GltfVertex(
            new VertexPositionNormal(position, Vector3.UnitZ),
            new VertexColor1Texture1(Vector4.One, Vector2.Zero));
    }

    private static void AssertPositions(float[] actual, Vector3[] expected)
    {
        Assert.Equal(expected.Length * 3, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].X, actual[i * 3], 4);
            Assert.Equal(expected[i].Y, actual[i * 3 + 1], 4);
            Assert.Equal(expected[i].Z, actual[i * 3 + 2], 4);
        }
    }
}
