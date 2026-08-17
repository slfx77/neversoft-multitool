using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     The TRG SetSkyColor backdrop is the engine's framebuffer clear colour
///     (Db_UpdateSky: Draw.isbg=1), applied whether or not the region owns a
///     sky dome — so the export must carry it at DOCUMENT/scene scope, not
///     only on sky-dome mesh extras. The motivating case is skny's two-player
///     region: AUTOEXEC2's bank (SkNY_O2) has no background object, but every
///     RESTART node issues SetSkyColor (0,9,25) — the night-blue clear is
///     that region's entire sky.
/// </summary>
public sealed class PsxSkyBackdropTests(TestPaths paths)
{
    [Fact]
    public void SceneExtras_CarryTheBackdropColour()
    {
        var document = new ModelDocument { Name = "backdrop", SourceKind = ModelSourceKind.Psx };
        document.NativeMetadata.Add(new PsxSkyBackdropMetadata(2329));
        AddTriangle(document);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);

        using var json = ParseGlbJson(glbBytes);
        var scene = json.RootElement.GetProperty("scenes")[0];
        Assert.True(scene.TryGetProperty("extras", out var extras));
        Assert.Equal(2329u, extras.GetProperty("neversoftSkyBackdrop").GetUInt32());
    }

    [Fact]
    public void SceneExtras_AbsentWithoutABackdrop()
    {
        var document = new ModelDocument { Name = "plain", SourceKind = ModelSourceKind.Psx };
        AddTriangle(document);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);

        using var json = ParseGlbJson(glbBytes);
        var scene = json.RootElement.GetProperty("scenes")[0];
        Assert.False(
            scene.TryGetProperty("extras", out var extras)
            && extras.TryGetProperty("neversoftSkyBackdrop", out _));
    }

    [Fact]
    public void ToDictionary_SerializesTheBackdropKind()
    {
        var dictionary = BlendPackageManifest.ToDictionary(new PsxSkyBackdropMetadata(2329));

        Assert.Equal("psx_sky_backdrop", dictionary["kind"]);
        Assert.Equal(2329u, dictionary["skyColor"]);
    }

    [CorpusFact]
    public void Skny2_DomelessTwoPlayerRegion_StillCarriesTheNightBlueBackdrop()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)", "skny_2.psx");
        Assert.SkipWhen(path == null, "skny_2.psx fixture is not available");

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path!),
            FileName = "skny_2.psx",
            OutputStem = "skny_2",
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = true
        });

        // The 2P bank genuinely has no skybox (SkNY_O2: barrier + rail), so
        // no sky mesh may exist — but the backdrop must survive at document
        // scope. 2329 = RGB (0, 9, 25), skny's authored night blue, issued by
        // every RESTART node in skny_t.trg.
        Assert.DoesNotContain(document.Nodes, static n =>
            n.Name.StartsWith("sky__", StringComparison.Ordinal));
        var backdrop = Assert.Single(
            document.NativeMetadata.OfType<PsxSkyBackdropMetadata>());
        Assert.Equal(2329u, backdrop.SkyColor);

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        using var json = ParseGlbJson(glbBytes);
        var scene = json.RootElement.GetProperty("scenes")[0];
        Assert.Equal(
            2329u,
            scene.GetProperty("extras").GetProperty("neversoftSkyBackdrop").GetUInt32());
    }

    private static void AddTriangle(ModelDocument document)
    {
        var mesh = new ModelMesh { Name = "tri" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "tri",
            Vertices =
            [
                new ModelVertex(
                    System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitZ,
                    System.Numerics.Vector4.One, System.Numerics.Vector2.Zero),
                new ModelVertex(
                    System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitZ,
                    System.Numerics.Vector4.One, System.Numerics.Vector2.UnitX),
                new ModelVertex(
                    System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ,
                    System.Numerics.Vector4.One, System.Numerics.Vector2.UnitY)
            ],
            Indices = [0, 1, 2]
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "tri",
            MeshIndex = 0,
            Transform = System.Numerics.Matrix4x4.Identity
        });
    }

    private static JsonDocument ParseGlbJson(byte[] glbBytes)
    {
        var jsonLength = BitConverter.ToInt32(glbBytes, 12);
        return JsonDocument.Parse(
            System.Text.Encoding.UTF8.GetString(glbBytes, 20, jsonLength).TrimEnd('\0'));
    }
}
