using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Phase-3 B1 guards: worldzone leaves export at their AUTHORED vertex
///     positions (the pre-B1 depth-bias/stagger bake corrupted every blend/mask
///     leaf's geometry), draw order ships as metadata instead (DrawIndex /
///     PassIndex / OverlapGroup + the object-level Blender separation vector),
///     and the GLB path publishes it as mesh extras for the viewer's
///     renderOrder enforcement.
/// </summary>
public sealed class Ps2WorldzoneDrawOrderTests
{
    private const float CoordinateScale = 0.0254f;

    [Fact]
    public void PopulateLeaves_ExportsAuthoredVertices_AndAssignsPassIndexes()
    {
        // Two coplanar stacked blend passes over one opaque base. The opaque
        // leaf draws first (order key 0x0100 vs 0x0200); the two blend leaves
        // share a geometry key, so they form one overlap group with pass ranks
        // 0 and 1 in draw order.
        var opaqueLeaf = MakeQuadLeaf(0x1111_0001, 0x00, offsetX: 10f);
        var blendBase = MakeQuadLeaf(0x2222_0002, 0x44);
        var blendOverlay = MakeQuadLeaf(0x3333_0003, 0x44);
        var document = Populate([blendBase, blendOverlay, opaqueLeaf]);

        var metadata = document.Meshes
            .Select(static mesh => mesh.Primitives.Single().NativeMetadata
                .OfType<Ps2WorldzoneLeafRenderMetadata>().Single())
            .ToList();
        Assert.Equal(3, metadata.Count);

        var opaque = Assert.Single(metadata, static m => m.LeafIndex == 2);
        var basePass = Assert.Single(metadata, static m => m.LeafIndex == 0);
        var overlayPass = Assert.Single(metadata, static m => m.LeafIndex == 1);

        // Draw order: opaque first, then the two blend passes in leaf order.
        Assert.Equal(0, opaque.DrawIndex);
        Assert.Equal(1, basePass.DrawIndex);
        Assert.Equal(2, overlayPass.DrawIndex);

        // The stacked blend passes share an overlap group and ladder up pass
        // indices; the opaque quad sits alone in its own group at pass 0.
        Assert.Equal(basePass.OverlapGroup, overlayPass.OverlapGroup);
        Assert.NotEqual(opaque.OverlapGroup, basePass.OverlapGroup);
        Assert.Equal(0, basePass.PassIndex);
        Assert.Equal(1, overlayPass.PassIndex);
        Assert.Equal(0, opaque.PassIndex);

        // Blender separation vector: leaf normal (+Z) x depthBias x scale for
        // the blend passes (group checksum 0 => mode bias 0.005 only); zero for
        // the opaque base.
        var expectedOffset = 0.005f * CoordinateScale;
        Assert.Equal(0f, opaque.BlendOffsetZ, 6);
        Assert.Equal(expectedOffset, basePass.BlendOffsetZ, 6);
        Assert.Equal(expectedOffset, overlayPass.BlendOffsetZ, 6);
        Assert.Equal(0f, basePass.BlendOffsetX, 6);
        Assert.Equal(0f, basePass.BlendOffsetY, 6);
    }

    [Fact]
    public void PopulateLeaves_DoesNotDisplaceBlendVertices()
    {
        var blendBase = MakeQuadLeaf(0x2222_0002, 0x44);
        var blendOverlay = MakeQuadLeaf(0x3333_0003, 0x44);
        var document = Populate([blendBase, blendOverlay]);

        // World position = node translation + localized vertex; with identity
        // placement it must equal the authored position x coordinateScale
        // EXACTLY for every leaf — the pre-B1 bake shifted blend leaves along
        // their normal by depthBias (+ draw-order stagger).
        // The strip walk re-expands vertices per triangle, so compare as a
        // position SET: every emitted world position must coincide with an
        // authored position x coordinateScale. The pre-B1 bake shifted every
        // blend vertex off the authored plane (Z = 0) by depthBias x scale.
        Assert.Equal(2, document.Meshes.Count);
        var authored = MakeQuadPositions().Select(static p => p * CoordinateScale).ToArray();
        for (var meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            var node = document.Nodes.Single(n => n.MeshIndex == meshIndex);
            var primitive = document.Meshes[meshIndex].Primitives.Single();
            Assert.NotEmpty(primitive.Vertices);
            foreach (var vertex in primitive.Vertices)
            {
                var world = node.Transform.Translation + vertex.Position;
                var nearest = authored.Min(p => Vector3.Distance(p, world));
                Assert.True(nearest < 1e-5f,
                    $"Vertex at {world} is {nearest} from every authored position — geometry was displaced.");
                Assert.Equal(0f, world.Z, 6);
            }
        }
    }

    [Fact]
    public void ToDictionary_SerializesDrawOrderFields()
    {
        var metadata = new Ps2WorldzoneLeafRenderMetadata(
            "0000AA00", 7, "world", "Base", 0x0200, false, false, 0x80808080, 0,
            DrawIndex: 42, PassIndex: 1, OverlapGroup: 3,
            BlendOffsetX: 0f, BlendOffsetY: 0f, BlendOffsetZ: 0.000127f);

        var dictionary = BlendPackageManifest.ToDictionary(metadata);

        Assert.Equal(42, dictionary["drawIndex"]);
        Assert.Equal(1, dictionary["passIndex"]);
        Assert.Equal(3, dictionary["overlapGroup"]);
        var offset = Assert.IsType<float[]>(dictionary["blendOffset"]);
        Assert.Equal([0f, 0f, 0.000127f], offset);
    }

    [Fact]
    public void BuildGlbBytes_PublishesDrawOrderExtrasPerLeafMesh()
    {
        var blendBase = MakeQuadLeaf(0x2222_0002, 0x44);
        var blendOverlay = MakeQuadLeaf(0x3333_0003, 0x44);
        var document = Populate([blendBase, blendOverlay]);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var json = ParseGlbJson(glbBytes);
        var meshes = json.RootElement.GetProperty("meshes");

        // Two leaf meshes, each carrying its own draw rank in extras — this is
        // what the three.js viewer maps onto Object3D.renderOrder. Distinct
        // extras also stop SharpGLTF from merging the identical-geometry passes.
        Assert.Equal(2, meshes.GetArrayLength());
        var drawIndexes = new List<int>();
        foreach (var mesh in meshes.EnumerateArray())
        {
            var extras = mesh.GetProperty("extras");
            drawIndexes.Add(extras.GetProperty("neversoftDrawIndex").GetInt32());
            Assert.True(extras.TryGetProperty("neversoftPassIndex", out _));
            Assert.True(extras.TryGetProperty("neversoftOverlapGroup", out _));
        }

        drawIndexes.Sort();
        Assert.Equal([0, 1], drawIndexes);
    }

    [Fact]
    public void BuildGlbBytes_PublishesAdditiveBlendClassInMaterialExtras()
    {
        // Additive GS blend (A=Cs B=0 C=As D=Cd → byte 0x48): the exporter
        // bakes luminance-to-alpha, and the material must DECLARE the class in
        // extras — PS2 material names never match the PSX __st suffix the
        // viewer's additive path keys on (B11 / triage follow-up 4).
        var additive = MakeQuadLeaf(0x4444_0004, 0x48);
        var plainBlend = MakeQuadLeaf(0x5555_0005, 0x44);
        var document = Populate([additive, plainBlend]);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var json = ParseGlbJson(glbBytes);
        var materials = json.RootElement.GetProperty("materials");

        var blendClasses = new List<string?>();
        foreach (var material in materials.EnumerateArray())
        {
            blendClasses.Add(
                material.TryGetProperty("extras", out var extras) &&
                extras.TryGetProperty("neversoftBlendClass", out var blendClass)
                    ? blendClass.GetString()
                    : null);
        }

        Assert.Contains("additive", blendClasses);
        Assert.Contains(null, blendClasses);
    }

    [Fact]
    public void PopulateLeaves_OpaqueOverlayPassesGetRankScaledSeparation()
    {
        // Coplanar OPAQUE stacks previously exported with ZERO separation —
        // renderOrder+LEQUAL cannot split different polygons sharing a plane,
        // so they z-fought in-app (E3/E5). The base layer stays authored; pass
        // ranks above it take the PSX opaque-overlay treatment (rank-scaled
        // node-transform offset, vertices untouched).
        var opaqueBase = MakeQuadLeaf(0x6666_0006, 0x00);
        var opaqueOverlay = MakeQuadLeaf(0x7777_0007, 0x00);
        var document = Populate([opaqueBase, opaqueOverlay]);

        var metadata = document.Meshes
            .Select(static mesh => mesh.Primitives.Single().NativeMetadata
                .OfType<Ps2WorldzoneLeafRenderMetadata>().Single())
            .ToList();
        var basePass = Assert.Single(metadata, static m => m.PassIndex == 0);
        var overlayPass = Assert.Single(metadata, static m => m.PassIndex == 1);
        Assert.Equal(0f, basePass.BlendOffsetZ, 6);
        Assert.Equal(0.005f * CoordinateScale, overlayPass.BlendOffsetZ, 6);
    }

    [Fact]
    public void BuildGlbBytes_SubtractiveBakesDeclareTheirClassAndShipUnlit()
    {
        // Subtractive GS blend (A=2,B=0,C=0,D=1 → byte 0x42): the portable
        // bake is black RGB + luminance alpha, a pure darkening layer. The
        // in-app opacity-gain pass keys on the "subtractive" blend-class
        // extra, and the darkening only reads correctly UNLIT — which every
        // PS2 worldzone material already is (RenderMaterial.Unlit defaults
        // true → KHR_materials_unlit). This pins BOTH load-bearing facts.
        var subtractive = MakeQuadLeaf(0x8888_0008, 0x42);
        var plainBlend = MakeQuadLeaf(0x9999_0009, 0x44);
        var document = Populate([subtractive, plainBlend]);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var json = ParseGlbJson(glbBytes);

        var subtractiveCount = 0;
        foreach (var material in json.RootElement.GetProperty("materials").EnumerateArray())
        {
            Assert.True(
                material.TryGetProperty("extensions", out var extensions) &&
                extensions.TryGetProperty("KHR_materials_unlit", out _),
                "worldzone materials ship unlit — the subtractive darkening depends on it");
            if (material.TryGetProperty("extras", out var extras) &&
                extras.TryGetProperty("neversoftBlendClass", out var blendClass) &&
                blendClass.GetString() == "subtractive")
            {
                subtractiveCount++;
            }
        }

        Assert.Equal(1, subtractiveCount);
    }

    private static ModelDocument Populate(List<Ps2GeomLeaf> leaves)
    {
        var document = new ModelDocument
        {
            Name = "worldzone_test",
            SourceKind = ModelSourceKind.Ps2Worldzone
        };
        Ps2WorldzoneGeometryWriter.PopulatePs2WorldzoneLeaves(
            document,
            new Ps2GeomScene { Leaves = leaves },
            "0000AA00",
            [(Vector3.Zero, Quaternion.Identity)],
            static _ => null,
            [],
            null,
            null,
            null,
            CoordinateScale,
            "world");
        return document;
    }

    private static Vector3[] MakeQuadPositions(float offsetX = 0f)
    {
        return
        [
            new Vector3(offsetX, 0f, 0f),
            new Vector3(offsetX + 100f, 0f, 0f),
            new Vector3(offsetX, 100f, 0f),
            new Vector3(offsetX + 100f, 100f, 0f)
        ];
    }

    private static Ps2GeomLeaf MakeQuadLeaf(uint textureChecksum, ulong alpha1, float offsetX = 0f)
    {
        var positions = MakeQuadPositions(offsetX);
        return new Ps2GeomLeaf
        {
            TextureChecksum = textureChecksum,
            BoundingSphere = Vector4.Zero,
            Vertices =
            [
                MakeVertex(positions[0], 0f, 0f),
                MakeVertex(positions[1], 1f, 0f),
                MakeVertex(positions[2], 0f, 1f),
                MakeVertex(positions[3], 1f, 1f)
            ],
            DmaAlpha1 = alpha1
        };
    }

    private static Ps2Vertex MakeVertex(Vector3 position, float u, float v)
    {
        return new Ps2Vertex(
            position,
            Vector3.UnitZ,
            128, 128, 128, 128,
            u, v,
            true, true, true, false,
            0, 0, 0,
            0f, 0f, 0f,
            false);
    }

    private static JsonDocument ParseGlbJson(byte[] glbBytes)
    {
        var chunkLength = BitConverter.ToInt32(glbBytes, 12);
        return JsonDocument.Parse(
            System.Text.Encoding.UTF8.GetString(glbBytes, 20, chunkLength).TrimEnd('\0'));
    }
}
