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
        Assert.False(attributes.TryGetProperty(PsxOverbrightVertexColor1Texture1.FlagsAttributeName, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildGlbBytes_TransportsOutOfRangeColorsInCustomFloatAccessor(bool skinned)
    {
        var document = CreateTriangleDocument(true, skinned);

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);

        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var primitive = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var portableColors = primitive.GetVertexAccessor("COLOR_0").AsVector4Array();
        var psxColors = primitive
            .GetVertexAccessor(PsxOverbrightVertexColor1Texture1.AttributeName)
            .AsVector4Array();
        var psxFlags = primitive
            .GetVertexAccessor(PsxOverbrightVertexColor1Texture1.FlagsAttributeName)
            .AsVector3Array();

        Assert.All(portableColors, static color =>
        {
            Assert.InRange(color.X, 0f, 1f);
            Assert.InRange(color.Y, 0f, 1f);
            Assert.InRange(color.Z, 0f, 1f);
            Assert.InRange(color.W, 0f, 1f);
        });
        Assert.Contains(psxColors, static color => color.X > 1f);
        Assert.All(psxFlags, static flags => Assert.Equal(Vector3.Zero, flags));

        var renderScene = GlbModelLoader.Load(model, null, 0f);
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
        var flagsAccessorIndex = attributes
            .GetProperty(PsxOverbrightVertexColor1Texture1.FlagsAttributeName)
            .GetInt32();
        var customAccessor = root.GetProperty("accessors")[customAccessorIndex];
        var flagsAccessor = root.GetProperty("accessors")[flagsAccessorIndex];
        Assert.Equal(5126, customAccessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC4", customAccessor.GetProperty("type").GetString());
        Assert.False(customAccessor.TryGetProperty("normalized", out var normalized) &&
                     normalized.GetBoolean());
        Assert.Equal(5126, flagsAccessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC3", flagsAccessor.GetProperty("type").GetString());
        Assert.False(flagsAccessor.TryGetProperty("normalized", out normalized) &&
                     normalized.GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildGlbBytes_TransportsRawPsxPacketColorAlongsideLinearFallback(bool skinned)
    {
        var document = CreateTriangleDocument(skinned: skinned);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        var portableLinear = new Vector4(0.19f, 0.31f, 0.47f, 0.75f);
        var packetColor = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 0.75f);
        var packetFlags = new Vector3(1f, 1f, 1f);
        for (var i = 0; i < primitive.Vertices.Length; i++)
        {
            primitive.Vertices[i] = primitive.Vertices[i] with
            {
                PsxPacketColor = packetColor,
                PsxPrimitiveFlags = packetFlags
            };
        }

        primitive.Vertices[0] = primitive.Vertices[0] with { Color = portableLinear };

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var exported = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var portable = exported.GetVertexAccessor("COLOR_0").AsVector4Array()[0];
        var packet = exported
            .GetVertexAccessor(PsxOverbrightVertexColor1Texture1.AttributeName)
            .AsVector4Array()[0];
        var flags = exported
            .GetVertexAccessor(PsxOverbrightVertexColor1Texture1.FlagsAttributeName)
            .AsVector3Array();

        AssertVectorNear(portableLinear, portable, 2e-5f);
        AssertVectorNear(packetColor, packet, 1e-6f);
        Assert.All(flags, value => Assert.Equal(packetFlags, value));

        var renderScene = GlbModelLoader.Load(model, null, 0f);
        var renderColors = Assert.Single(renderScene.Submeshes).VertexColors;
        Assert.NotNull(renderColors);
        AssertVectorNear(
            portableLinear,
            new Vector4(renderColors[0], renderColors[1], renderColors[2], renderColors[3]),
            2e-5f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildGlbBytes_PreservesPsxTextureWibbleInCustomVertexAttributes(bool skinned)
    {
        var document = CreateTriangleDocument(true, skinned);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        var packetColor = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f);
        var packetFlags = new Vector3(1f, 1f, 1f);
        for (var i = 0; i < primitive.Vertices.Length; i++)
        {
            primitive.Vertices[i] = primitive.Vertices[i] with
            {
                PsxPacketColor = packetColor,
                PsxPrimitiveFlags = packetFlags
            };
        }

        primitive.Vertices[0] = primitive.Vertices[0] with
        {
            TextureWibble = new ModelTextureWibble(
                4096,
                -2048,
                595,
                7,
                3,
                11,
                9,
                64,
                128)
        };

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var exported = Assert.Single(Assert.Single(model.LogicalMeshes).Primitives);
        var attributes = exported.VertexAccessors;

        Assert.Contains(PsxAnimatedVertexColor1Texture1.ColorAttributeName, attributes.Keys);
        Assert.Contains(PsxAnimatedVertexColor1Texture1.FlagsAttributeName, attributes.Keys);
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
        var packetColors = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.ColorAttributeName)
            .AsVector4Array();
        var flags = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.FlagsAttributeName)
            .AsVector3Array();
        Assert.Contains(motion, static value => value == new Vector4(4096, -2048, 595, 1));
        Assert.Contains(wave, static value => value == new Vector4(7, 3, 11, 9));
        Assert.Contains(size, static value => value == new Vector2(64, 128));
        AssertVectorNear(packetColor, packetColors[0], 1e-6f);
        Assert.All(flags, value => Assert.Equal(packetFlags, value));
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
        using var stream = new MemoryStream(glbBytes, false);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(checked((uint)glbBytes.Length), reader.ReadUInt32());

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        return JsonDocument.Parse(reader.ReadBytes(checked((int)jsonLength)));
    }

    private static void AssertVectorNear(Vector4 expected, Vector4 actual, float tolerance)
    {
        Assert.InRange(Vector4.Distance(expected, actual), 0f, tolerance);
    }
}