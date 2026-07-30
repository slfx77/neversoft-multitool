using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Guards that the UV-wibble-animated rigid export path publishes mesh
///     extras. <c>HasTextureWibble</c> is a WHOLE-MESH test — one animated
///     vertex reroutes the entire mesh to
///     <c>AddPsxAnimatedRigidMesh</c> — and that path shipped without an
///     <c>ApplyMeshExtras</c> call, so any sprite / sky / draw-order mesh that
///     also happened to animate silently lost its render metadata (found
///     2026-07-29: DC SKNY.PSX exported 206 of 213 billboards, l6a2_g's
///     13-layer coplanar overlay stack carried no draw order at all, and
///     l8a2_g's two semi-transparent sky domes lost the tag that keeps them in
///     the depth-cleared camera-locked sky pass). The whole suite stayed green
///     because nothing asserted exporter extras — only primitive metadata,
///     which was always attached. These tests fail if the call is removed.
/// </summary>
public sealed class PsxAnimatedMeshExtrasTests
{
    [Fact]
    public void BuildGlbBytes_WibbleAnimatedBillboardMesh_KeepsAxialBillboardExtras()
    {
        var document = CreateDocument(
            ("sprite_tree", new PsxAxialBillboardMetadata(0f, 0f, 0f, 0f, 1f, 0f)));

        using var extras = ExportMeshExtras(document);
        Assert.True(extras.RootElement.GetProperty("neversoftAxialBillboard").GetBoolean());
        Assert.Equal(3, extras.RootElement.GetProperty("neversoftBillboardAxis").GetArrayLength());
        Assert.Equal(3, extras.RootElement.GetProperty("neversoftBillboardAnchor").GetArrayLength());
    }

    [Fact]
    public void BuildGlbBytes_WibbleAnimatedSkyMesh_KeepsSkyExtras()
    {
        var document = CreateDocument(("sky__dome", new PsxSkyRenderMetadata(0x204060u)));

        using var extras = ExportMeshExtras(document);
        Assert.True(extras.RootElement.GetProperty("neversoftSky").GetBoolean());
        Assert.Equal(0x204060u, extras.RootElement.GetProperty("neversoftSkyColor").GetUInt32());
    }

    [Fact]
    public void BuildGlbBytes_WibbleAnimatedOverlayMesh_KeepsDrawOrderExtras()
    {
        var document = CreateDocument(
            ("water__overlay00", new MeshDrawOrderMetadata(1, 1, 7)));

        using var extras = ExportMeshExtras(document);
        Assert.Equal(1, extras.RootElement.GetProperty("neversoftDrawIndex").GetInt32());
        Assert.Equal(1, extras.RootElement.GetProperty("neversoftPassIndex").GetInt32());
        Assert.Equal(7, extras.RootElement.GetProperty("neversoftOverlapGroup").GetInt32());
    }

    [Fact]
    public void BuildGlbBytes_WibbleAnimatedMesh_TakesTheAnimatedPath()
    {
        // Pins the premise of the tests above: these fixtures really do route
        // through AddPsxAnimatedRigidMesh (the UV-wibble vertex encoding), so a
        // regression there cannot pass by falling back to the plain writer.
        var document = CreateDocument(
            ("sprite_tree", new PsxAxialBillboardMetadata(0f, 0f, 0f, 0f, 1f, 0f)));

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        using var json = ParseGlbJson(glbBytes!);
        var attributes = json.RootElement
            .GetProperty("meshes")[0]
            .GetProperty("primitives")[0]
            .GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("_PSX_UV_WIBBLE_0", out _));
    }

    /// <summary>
    ///     Exports the document and returns the single mesh's extras object,
    ///     re-parsed so it outlives the GLB's own JsonDocument. Asserting
    ///     exactly one extras block also catches the metadata landing on the
    ///     wrong mesh.
    /// </summary>
    private static JsonDocument ExportMeshExtras(ModelDocument document)
    {
        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var json = ParseGlbJson(glbBytes!);

        var extras = json.RootElement.GetProperty("meshes").EnumerateArray()
            .Where(static mesh => mesh.TryGetProperty("extras", out _))
            .Select(static mesh => mesh.GetProperty("extras").GetRawText())
            .ToList();

        return JsonDocument.Parse(Assert.Single(extras));
    }

    private static ModelDocument CreateDocument(
        params (string Name, NativeRenderMetadata Metadata)[] meshes)
    {
        var document = new ModelDocument { Name = "psx_animated_extras", SourceKind = ModelSourceKind.Psx };
        foreach (var (name, metadata) in meshes)
        {
            var primitive = new ModelPrimitive
            {
                Name = name,
                Vertices = CreateWibbledQuad(),
                Indices = [0, 1, 2, 1, 3, 2]
            };
            primitive.NativeMetadata.Add(metadata);

            var mesh = new ModelMesh { Name = name };
            mesh.Primitives.Add(primitive);
            document.Meshes.Add(mesh);
            document.Nodes.Add(new ModelNode { Name = name, MeshIndex = document.Meshes.Count - 1 });
        }

        return document;
    }

    private static ModelVertex[] CreateWibbledQuad()
    {
        // One scrolling UV vertex is enough: the wibble test is whole-mesh.
        var wibble = new ModelTextureWibble(4, 0, 8, 2, 0, 0, 0, 64, 64);
        return
        [
            MakeVertex(new Vector3(0f, 0f, 0f), 0f, 0f, wibble),
            MakeVertex(new Vector3(1f, 0f, 0f), 1f, 0f, null),
            MakeVertex(new Vector3(0f, 1f, 0f), 0f, 1f, null),
            MakeVertex(new Vector3(1f, 1f, 0f), 1f, 1f, null)
        ];
    }

    private static ModelVertex MakeVertex(Vector3 position, float u, float v, ModelTextureWibble? wibble)
    {
        return new ModelVertex(position, Vector3.UnitZ, Vector4.One, new Vector2(u, v))
        {
            TextureWibble = wibble
        };
    }

    private static JsonDocument ParseGlbJson(byte[] glbBytes)
    {
        var chunkLength = BitConverter.ToInt32(glbBytes, 12);
        return JsonDocument.Parse(
            System.Text.Encoding.UTF8.GetString(glbBytes, 20, chunkLength).TrimEnd('\0'));
    }
}
