using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Rendering;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class GltfModelExporterTests
{
    [Fact]
    public void BuildGlbBytes_StoresVertexColorsAsNormalizedUnsignedShort()
    {
        var document = CreateTriangleDocument();

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var json = ReadGlbJson(glbBytes);
        var root = json.RootElement;
        var colorAccessorIndex = root
            .GetProperty("meshes")[0]
            .GetProperty("primitives")[0]
            .GetProperty("attributes")
            .GetProperty("COLOR_0")
            .GetInt32();
        var attributes = root
            .GetProperty("meshes")[0]
            .GetProperty("primitives")[0]
            .GetProperty("attributes");
        var colorAccessor = root.GetProperty("accessors")[colorAccessorIndex];

        Assert.Equal(5123, colorAccessor.GetProperty("componentType").GetInt32());
        Assert.True(colorAccessor.GetProperty("normalized").GetBoolean());
        Assert.Equal("VEC4", colorAccessor.GetProperty("type").GetString());
        Assert.False(attributes.TryGetProperty(PsxOverbrightVertexColor1Texture1.AttributeName, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildGlbBytes_TransportsOutOfRangeColorsInCustomFloatAccessor(bool skinned)
    {
        var document = CreateTriangleDocument(overbright: true, skinned);

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);

        using var stream = new MemoryStream(glbBytes, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var portableColors = primitive.GetVertexAccessor("COLOR_0").AsVector4Array();
        var psxColors = primitive
            .GetVertexAccessor(PsxOverbrightVertexColor1Texture1.AttributeName)
            .AsVector4Array();

        Assert.All(portableColors, static color =>
        {
            Assert.InRange(color.X, 0f, 1f);
            Assert.InRange(color.Y, 0f, 1f);
            Assert.InRange(color.Z, 0f, 1f);
            Assert.InRange(color.W, 0f, 1f);
        });
        Assert.Contains(psxColors, static color => color.X > 1f);

        var renderScene = GlbModelLoader.Load(model, animation: null, time: 0f);
        var renderColors = Assert.Single(renderScene.Submeshes).VertexColors;
        Assert.NotNull(renderColors);
        Assert.Contains(renderColors, static component => component > 1f);

        using var json = ReadGlbJson(glbBytes);
        var root = json.RootElement;
        var attributes = root
            .GetProperty("meshes")[0]
            .GetProperty("primitives")[0]
            .GetProperty("attributes");
        var customAccessorIndex = attributes
            .GetProperty(PsxOverbrightVertexColor1Texture1.AttributeName)
            .GetInt32();
        var customAccessor = root.GetProperty("accessors")[customAccessorIndex];
        Assert.Equal(5126, customAccessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC4", customAccessor.GetProperty("type").GetString());
        Assert.False(customAccessor.TryGetProperty("normalized", out var normalized) &&
                     normalized.GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildGlbBytes_PreservesPsxTextureWibbleInCustomVertexAttributes(bool skinned)
    {
        var document = CreateTriangleDocument(overbright: true, skinned);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        primitive.Vertices[0] = primitive.Vertices[0] with
        {
            TextureWibble = new ModelTextureWibble(
                UVelocity: 4096,
                VVelocity: -2048,
                Frequency: 595,
                UAmplitude: 7,
                UPhase: 3,
                VAmplitude: 11,
                VPhase: 9,
                TextureWidth: 64,
                TextureHeight: 128)
        };

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        var exported = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var attributes = exported.VertexAccessors;

        Assert.Contains(PsxAnimatedVertexColor1Texture1.ColorAttributeName, attributes.Keys);
        Assert.Contains(PsxAnimatedVertexColor1Texture1.MotionAttributeName, attributes.Keys);
        Assert.Contains(PsxAnimatedVertexColor1Texture1.WaveAttributeName, attributes.Keys);
        Assert.Contains(PsxAnimatedVertexColor1Texture1.SizeAttributeName, attributes.Keys);

        var motion = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.MotionAttributeName)
            .AsVector4Array();
        var wave = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.WaveAttributeName)
            .AsVector4Array();
        var size = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.SizeAttributeName)
            .AsVector2Array();
        Assert.Contains(motion, static value => value == new Vector4(4096, -2048, 595, 1));
        Assert.Contains(wave, static value => value == new Vector4(7, 3, 11, 9));
        Assert.Contains(size, static value => value == new Vector2(64, 128));
    }

    private static ModelDocument CreateTriangleDocument(bool overbright = false, bool skinned = false)
    {
        var document = new ModelDocument { Name = "high_precision_vertex_color" };
        var mesh = new ModelMesh { Name = "triangle" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "triangle",
            Vertices =
            [
                new ModelVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    new Vector4(overbright ? 1.5f : 0.004f, 0.008f, 0.012f, 1f),
                    Vector2.Zero),
                new ModelVertex(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    new Vector4(0.016f, 0.020f, 0.024f, 1f),
                    Vector2.UnitX),
                new ModelVertex(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    new Vector4(0.028f, 0.032f, 0.036f, 1f),
                    Vector2.UnitY)
            ],
            Indices = [0, 1, 2],
            Skin = skinned
                ? new ModelSkinBinding
                {
                    SkeletonIndex = 0,
                    Influences =
                    [
                        ModelBoneInfluences.Single(0),
                        ModelBoneInfluences.Single(0),
                        ModelBoneInfluences.Single(0)
                    ]
                }
                : null
        });
        document.Meshes.Add(mesh);
        if (skinned)
        {
            var skeleton = new ModelSkeleton { Name = "triangle_skeleton" };
            skeleton.Bones.Add(new ModelBone { Name = "root" });
            document.Skeletons.Add(skeleton);
        }
        document.Nodes.Add(new ModelNode
        {
            Name = "triangle",
            MeshIndex = 0
        });
        return document;
    }

    private static JsonDocument ReadGlbJson(byte[] glbBytes)
    {
        using var stream = new MemoryStream(glbBytes, writable: false);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(checked((uint)glbBytes.Length), reader.ReadUInt32());

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        return JsonDocument.Parse(reader.ReadBytes(checked((int)jsonLength)));
    }
}
