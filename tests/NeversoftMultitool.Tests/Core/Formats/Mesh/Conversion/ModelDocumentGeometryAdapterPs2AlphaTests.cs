using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ModelDocumentGeometryAdapterPs2AlphaTests
{
    private const uint MaterialChecksum = 0x12345678;
    private const ulong FixedSourceAlpha77 = 0x64UL | (77UL << 32);
    private const ulong AlphaTestGequalOne = 0x1UL | (5UL << 1) | (1UL << 4);

    [Fact]
    public void PopulatePs2Geom_FixedSourceAlphaBlend_UsesConstantMaterialAlphaWithoutVertexAlpha()
    {
        var document = new ModelDocument { Name = "fixed_geom", SourceKind = ModelSourceKind.Ps2Geom };
        var scene = new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x01020304,
                    Vertices = TriangleStrip(64),
                    DmaAlpha1 = FixedSourceAlpha77
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, null, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.Equal(77f / 128f, material.BaseColor.W, 4);

        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.All(primitive.Vertices, vertex => Assert.Equal(1f, vertex.Color.W));
    }

    [Fact]
    public void PopulatePs2Geom_SourceAlphaBlend_PreservesVertexAlpha()
    {
        var document = new ModelDocument { Name = "source_alpha_geom", SourceKind = ModelSourceKind.Ps2Geom };
        var scene = new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x01020304,
                    Vertices = TriangleStrip(64),
                    DmaAlpha1 = 0x44
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, null, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);

        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.All(primitive.Vertices, vertex => Assert.Equal(0.5f, vertex.Color.W, 4));
    }

    [Fact]
    public void PopulatePs2Geom_SourceAlphaBlendWithOpaqueSource_UsesOpaqueMaterial()
    {
        var document = new ModelDocument { Name = "opaque_source_alpha_geom", SourceKind = ModelSourceKind.Ps2Geom };
        var scene = new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x01020304,
                    TextureChecksum = MaterialChecksum,
                    Vertices = TriangleStrip(128),
                    DmaAlpha1 = 0x44
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, OpaqueTextureProvider, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Opaque, material.AlphaMode);
    }

    [Fact]
    public void PopulatePs2Geom_SourceAlphaBlendWithOpaqueSourceAndAlphaTest_UsesMaskMaterial()
    {
        var document = new ModelDocument { Name = "masked_source_alpha_geom", SourceKind = ModelSourceKind.Ps2Geom };
        var scene = new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x01020304,
                    TextureChecksum = MaterialChecksum,
                    Vertices = TriangleStrip(128),
                    DmaAlpha1 = 0x44,
                    DmaTest1 = AlphaTestGequalOne
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, OpaqueTextureProvider, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        // AREF/128: cutoffs live in the exported-PNG alpha domain (×255/128).
        Assert.Equal(1f / 128f, material.AlphaCutoff, 4);
    }

    [Fact]
    public void PopulatePs2Geom_SourceAlphaBlendWithTranslucentVertexAlpha_StaysBlend()
    {
        var document = new ModelDocument
            { Name = "translucent_source_alpha_geom", SourceKind = ModelSourceKind.Ps2Geom };
        var scene = new Ps2GeomScene
        {
            Leaves =
            [
                new Ps2GeomLeaf
                {
                    Checksum = 0x01020304,
                    TextureChecksum = MaterialChecksum,
                    Vertices = TriangleStrip(64),
                    DmaAlpha1 = 0x44
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Geom(document, scene, OpaqueTextureProvider, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
    }

    [Fact]
    public void PopulatePs2Scene_FixedSourceAlphaBlend_UsesConstantMaterialAlphaWithoutVertexAlpha()
    {
        var document = new ModelDocument { Name = "fixed_scene", SourceKind = ModelSourceKind.Ps2Scene };
        document.Materials.Add(new RenderMaterial { Name = "fixed_material" });
        var scene = new ParsedPs2Scene
        {
            MaterialVersion = 6,
            MeshVersion = 6,
            VertexVersion = 1,
            Materials =
            [
                new Ps2Material
                {
                    Checksum = MaterialChecksum,
                    Flags = (uint)Ps2MaterialFlags.Transparent,
                    RegAlpha = FixedSourceAlpha77
                }
            ],
            MeshGroups =
            [
                new Ps2MeshGroup
                {
                    Checksum = 0x11111111,
                    Meshes =
                    [
                        new Ps2Mesh
                        {
                            Checksum = 0x22222222,
                            MaterialChecksum = MaterialChecksum,
                            Vertices = TriangleStrip(64)
                        }
                    ]
                }
            ]
        };

        Ps2SceneGeometryWriter.PopulatePs2Scene(document, scene, null);

        var material = Assert.Single(document.Materials);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.Equal(77f / 128f, material.BaseColor.W, 4);

        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.All(primitive.Vertices, vertex => Assert.Equal(1f, vertex.Color.W));
    }

    private static Ps2Vertex[] TriangleStrip(byte alpha)
    {
        return
        [
            MakeVertex(0f, 0f, alpha),
            MakeVertex(1f, 0f, alpha),
            MakeVertex(0f, 1f, alpha),
            MakeVertex(1f, 1f, alpha)
        ];
    }

    private static Ps2Vertex MakeVertex(float x, float y, byte alpha)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0f),
            Vector3.UnitZ,
            128,
            128,
            128,
            alpha,
            x,
            y,
            true,
            true,
            true,
            false);
    }

    private static byte[]? OpaqueTextureProvider(uint checksum)
    {
        return checksum == MaterialChecksum ? CreatePngBytes(new Rgba32(64, 96, 128, 255)) : null;
    }

    private static byte[] CreatePngBytes(Rgba32 color)
    {
        using var image = new Image<Rgba32>(1, 1, color);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}