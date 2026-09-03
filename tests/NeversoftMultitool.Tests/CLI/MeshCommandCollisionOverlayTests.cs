using NeversoftMultitool.CLI;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshCommandCollisionOverlayTests(TestPaths paths)
{
    private const string Thps2PsxBuild =
        "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string SpiderManDcBuild =
        "Spider-Man (2001-2-14, DC - Prototype)";
    private const string Thps3Ps2Build =
        "Tony Hawk's Pro Skater 3 (2001-10-22, PS2 - Final)";
    private const string Thps4Ps2Build =
        "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";
    private const string Thps2xBuild =
        "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)";

    [Fact]
    public void CollisionOverlayOption_IsAcceptedAsAFlag()
    {
        var result = MeshCommand.Create().Parse(["missing.geom.ps2", "--collision-overlay"]);

        Assert.Empty(result.Errors);
    }

    [CorpusFact]
    public void CollisionOverlayOption_IsOptInAndRoutesToTheExportDocument()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = paths.FindSampleFiles(Thps4Ps2Build, "Veh_ElephantTruck_Gate.geom.ps2")
            .FirstOrDefault(path => File.Exists(
                path[..^".geom.ps2".Length] + ".col.ps2"));
        Assert.SkipWhen(scenePath == null, "Exact-stem THPS4 PS2 scene/COL pair not found");

        using var temp = new TempDirectory();
        var plainDirectory = Path.Combine(temp.Path, "plain");
        var overlayDirectory = Path.Combine(temp.Path, "overlay");

        Assert.Equal(0, MeshCommand.Create()
            .Parse([scenePath!, "--output", plainDirectory])
            .Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([scenePath!, "--output", overlayDirectory, "--collision-overlay"])
            .Invoke());

        var plain = ReadOnlyGlb(plainDirectory);
        var overlay = ReadOnlyGlb(overlayDirectory);
        Assert.DoesNotContain(plain.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.Contains(overlay.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.True(TriangleCount(overlay) > TriangleCount(plain));
    }

    [CorpusFact]
    public void CollisionOverlayOption_ExportsAuthoredThps2xDdmPsxPair()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = paths.FindSampleFile(Thps2xBuild, "skware.DDM");
        Assert.SkipWhen(scenePath == null, "THPS2X skware authored level family not found");

        using var temp = new TempDirectory();
        Assert.Equal(0, MeshCommand.Create()
            .Parse([scenePath!, "--output", temp.Path, "--collision-overlay"])
            .Invoke());

        var overlay = ReadOnlyGlb(temp.Path);
        Assert.Contains(overlay.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.True(TriangleCount(overlay) > 0);
    }

    [CorpusFact]
    public void CollisionOverlayOption_ExportsInlineThps2PsxLevel()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = paths.FindSampleFile(Thps2PsxBuild, "skware.psx");
        Assert.SkipWhen(scenePath == null, "THPS2 PSX skware level not found");

        AssertCollisionOverlayAddsTriangles(scenePath!);
    }

    [CorpusFact]
    public void CollisionOverlayOption_ExportsInlineSpiderManDreamcastLevel()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = paths.FindSampleFile(SpiderManDcBuild, "l1a2a_g.psx");
        Assert.SkipWhen(scenePath == null, "Spider-Man Dreamcast l1a2a_g level not found");

        AssertCollisionOverlayAddsTriangles(scenePath!);
    }

    [CorpusFact]
    public void CollisionOverlayOption_ExportsInlineThps3Ps2Bsp()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var scenePath = paths.FindSampleFile(Thps3Ps2Build, "Burn.bsp");
        Assert.SkipWhen(scenePath == null, "THPS3 PS2 Burn BSP not found");

        AssertCollisionOverlayAddsTriangles(scenePath!);
    }

    private static void AssertCollisionOverlayAddsTriangles(string scenePath)
    {
        using var temp = new TempDirectory();
        var plainDirectory = Path.Combine(temp.Path, "plain");
        var overlayDirectory = Path.Combine(temp.Path, "overlay");

        Assert.Equal(0, MeshCommand.Create()
            .Parse([scenePath, "--output", plainDirectory])
            .Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([scenePath, "--output", overlayDirectory, "--collision-overlay"])
            .Invoke());

        var plain = ReadOnlyGlb(plainDirectory);
        var overlay = ReadOnlyGlb(overlayDirectory);
        Assert.DoesNotContain(plain.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.Contains(overlay.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.True(TriangleCount(overlay) > TriangleCount(plain));
    }

    private static int TriangleCount(ModelRoot model) =>
        model.LogicalMeshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.GetTriangleIndices().Count());

    private static ModelRoot ReadOnlyGlb(string outputDirectory)
    {
        var path = Assert.Single(Directory.GetFiles(
            outputDirectory, "*.glb", SearchOption.AllDirectories));
        using var stream = File.OpenRead(path);
        return ModelRoot.ReadGLB(stream);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-col-overlay-cli-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
