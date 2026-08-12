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
            .AsVector4Array();

        Assert.All(portableColors, static color =>
        {
            Assert.InRange(color.X, 0f, 1f);
            Assert.InRange(color.Y, 0f, 1f);
            Assert.InRange(color.Z, 0f, 1f);
            Assert.InRange(color.W, 0f, 1f);
        });
        Assert.Contains(psxColors, static color => color.X > 1f);
        Assert.All(psxFlags, static flags => Assert.Equal(Vector4.Zero, flags));

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
        Assert.Equal(5123, flagsAccessor.GetProperty("componentType").GetInt32());
        Assert.Equal("VEC4", flagsAccessor.GetProperty("type").GetString());
        Assert.True(flagsAccessor.GetProperty("normalized").GetBoolean());
    }

    [Fact]
    public void BuildGlbBytes_ComposesDrawOrderSeparation_ButBlendManifestStaysUntouched()
    {
        // The GLB is the one output whose consumers get NO object-level
        // BlendOffset application, and renderOrder alone cannot resolve
        // DIFFERENT polygons sharing a plane — so the exporter composes the
        // separation into the node transform (2026-08-03). The .blend package
        // must NOT change: it serializes the raw ModelDocument transform and
        // import_package.py adds blendOffset itself at object level —
        // composing upstream would double-apply and break the importer's
        // re-zero-to-authored contract.
        var document = CreateTriangleDocument();
        document.Meshes[0].Primitives[0].NativeMetadata.Add(
            new MeshDrawOrderMetadata(1, 1, 0, 0f, 0.25f, 0f));

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var meshNode = Assert.Single(
            model.LogicalNodes, static node => node.Mesh != null);
        Assert.Equal(0.25f, meshNode.LocalMatrix.Translation.Y, 5);

        using var payload = new MemoryStream();
        BlendPackageWriter.Write(document, payload, "triangle.blend");
        payload.Position = 0;
        using var archive = new System.IO.Compression.ZipArchive(
            payload, System.IO.Compression.ZipArchiveMode.Read);
        using var manifestStream = archive.GetEntry("manifest.json")!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var node = Assert.Single(manifest.RootElement.GetProperty("Nodes").EnumerateArray());
        var transform = node.GetProperty("Transform").EnumerateArray()
            .Select(static value => value.GetSingle()).ToArray();
        // Row-major identity: the raw ModelDocument transform, offset-free.
        Assert.Equal(1f, transform[0], 6);
        Assert.Equal(0f, transform[13], 6); // translation Y
        var primitiveMetadata = manifest.RootElement.GetProperty("Meshes")[0]
            .GetProperty("Primitives")[0].GetProperty("NativeMetadata")[0];
        var blendOffset = primitiveMetadata.GetProperty("blendOffset").EnumerateArray()
            .Select(static value => value.GetSingle()).ToArray();
        Assert.Equal([0f, 0.25f, 0f], blendOffset);
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
            .AsVector4Array();

        AssertVectorNear(portableLinear, portable, 2e-5f);
        AssertVectorNear(packetColor, packet, 1e-6f);
        Assert.All(flags, value => Assert.Equal(new Vector4(packetFlags, 0f), value));

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
    public void BuildGlbBytes_PreservesPsxTextureWibbleInStandardVertexAttributes(bool skinned)
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
            .AsVector2Array();
        var wave = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.WaveAttributeName)
            .AsVector2Array();
        var size = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.SizeAttributeName)
            .AsVector2Array();
        var packetColors = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.ColorAttributeName)
            .AsVector4Array();
        var flags = exported
            .GetVertexAccessor(PsxAnimatedVertexColor1Texture1.FlagsAttributeName)
            .AsVector4Array();
        Assert.Contains(motion, static value => value == new Vector2(4096, 2049));
        Assert.Contains(wave, static value => value == new Vector2(595, 1 - 0x73B9));
        Assert.Contains(size, static value => value == new Vector2(64, -127));
        Assert.Contains(size, static value => value == new Vector2(0, 1));
        Assert.Equal(
            new ModelTextureWibble(4096, -2048, 595, 7, 3, 11, 9, 64, 128),
            PsxGltfVertexCarriers.DecodeTextureWibble(motion[0], wave[0], size[0]));
        Assert.Null(PsxGltfVertexCarriers.DecodeTextureWibble(motion[1], wave[1], size[1]));
        AssertVectorNear(packetColor, packetColors[0], 1e-6f);
        Assert.All(flags, value => Assert.Equal(new Vector4(packetFlags, 0f), value));

        Assert.All(attributes.Keys, static name =>
        {
            if (name.StartsWith('_'))
                Assert.Equal(PsxAnimatedVertexColor1Texture1.ColorAttributeName, name);
        });

        using var json = ReadGlbJson(glbBytes);
        Assert.Equal(
            1,
            json.RootElement.GetProperty("meshes")[0].GetProperty("extras")
                .GetProperty("neversoftPsxVertexCarriers").GetInt32());
    }

    [Fact]
    public void PsxCarriers_RoundTripEveryPulseByteAndAllWibbleNibbles()
    {
        for (var channel = 0; channel <= byte.MaxValue; channel++)
        {
            var encoded = PsxGltfVertexCarriers.EncodeFlagsAndPulse(
                new Vector3(1f, 0f, 1f), channel);
            Assert.Equal(channel, PsxGltfVertexCarriers.DecodeOneBasedPulseChannel(encoded.W));
        }

        for (var uAmplitude = 0; uAmplitude < 16; uAmplitude++)
        {
            for (var uPhase = 0; uPhase < 16; uPhase++)
            {
                for (var vAmplitude = 0; vAmplitude < 16; vAmplitude++)
                {
                    for (var vPhase = 0; vPhase < 16; vPhase++)
                    {
                        var packed = PsxGltfVertexCarriers.PackWibbleNibbles(
                            (byte)uAmplitude, (byte)uPhase, (byte)vAmplitude, (byte)vPhase);
                        Assert.Equal(
                            ((byte)uAmplitude, (byte)uPhase, (byte)vAmplitude, (byte)vPhase),
                            PsxGltfVertexCarriers.UnpackWibbleNibbles(packed));
                    }
                }
            }
        }
    }

    [Fact]
    public void GlbModelLoader_NonUniformNodeScale_UsesInverseTransposeForNormals()
    {
        var document = CreateTriangleDocument();
        var sourceNormal = Vector3.Normalize(new Vector3(1f, 1f, 0f));
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        primitive.Vertices[0] = primitive.Vertices[0] with
        {
            Position = Vector3.Zero,
            Normal = sourceNormal
        };
        primitive.Vertices[1] = primitive.Vertices[1] with
        {
            Position = new Vector3(-1f, 1f, 0f),
            Normal = sourceNormal
        };
        primitive.Vertices[2] = primitive.Vertices[2] with
        {
            Position = Vector3.UnitZ,
            Normal = sourceNormal
        };
        document.Nodes[0] = new ModelNode
        {
            Name = "triangle",
            MeshIndex = 0,
            Transform = Matrix4x4.CreateScale(2f, 1f, 1f)
        };

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var stream = new MemoryStream(glbBytes, false);
        var model = ModelRoot.ReadGLB(stream);
        var submesh = Assert.Single(GlbModelLoader.Load(model, null, 0f).Submeshes);
        var normals = Assert.IsType<float[]>(submesh.Normals);
        var expected = Vector3.Normalize(new Vector3(0.5f, 1f, 0f));
        for (var vertex = 0; vertex < 3; vertex++)
        {
            var offset = vertex * 3;
            var actual = new Vector3(normals[offset], normals[offset + 1], normals[offset + 2]);
            Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-5f);
        }
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
