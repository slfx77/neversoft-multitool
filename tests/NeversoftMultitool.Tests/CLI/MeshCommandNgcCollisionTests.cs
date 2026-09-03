using NeversoftMultitool.CLI;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshCommandNgcCollisionTests(TestPaths paths)
{
    private const string BuildName =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    [CorpusFact]
    public void MeshCommand_ExportsStandaloneNgcCollisionAndOptInSceneOverlay()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var collision = FindCanonical("anl_pigeon.col.ngc");
        Assert.SkipWhen(collision is null, "Canonical anl_pigeon.col.ngc not found");
        var scene = Path.Combine(Path.GetDirectoryName(collision)!, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(!File.Exists(scene), "Canonical anl_pigeon.skin.ngc not found");

        using var temp = new TempDirectory();
        var collisionOutput = Path.Combine(temp.Path, "collision");
        var plainOutput = Path.Combine(temp.Path, "plain");
        var overlayOutput = Path.Combine(temp.Path, "overlay");

        Assert.Equal(0, MeshCommand.Create()
            .Parse([collision!, "--output", collisionOutput])
            .Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([scene, "--output", plainOutput])
            .Invoke());
        Assert.Equal(0, MeshCommand.Create()
            .Parse([scene, "--output", overlayOutput, "--collision-overlay"])
            .Invoke());

        var collisionGlb = ReadOnlyGlb(collisionOutput);
        var plainGlb = ReadOnlyGlb(plainOutput);
        var overlayGlb = ReadOnlyGlb(overlayOutput);
        Assert.Equal(45, TriangleCount(collisionGlb));
        Assert.Contains(collisionGlb.LogicalMaterials,
            static material => material.Name == "collision");
        Assert.DoesNotContain(plainGlb.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.Contains(overlayGlb.LogicalMaterials,
            static material => material.Name == "collision_overlay");
        Assert.Equal(TriangleCount(plainGlb) + 45, TriangleCount(overlayGlb));
    }

    private string? FindCanonical(string fileName)
    {
        if (paths.SampleBuildsDir == null)
            return null;
        var root = Path.Combine(paths.SampleBuildsDir, BuildName);
        return paths.FindSampleFiles(BuildName, fileName)
            .FirstOrDefault(file =>
            {
                var directory = Path.GetDirectoryName(Path.GetRelativePath(root, file));
                return string.IsNullOrEmpty(directory)
                       || !directory.Split(
                               [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                               StringSplitOptions.RemoveEmptyEntries)
                           .Any(static part =>
                               part.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
            });
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
                "nmt-ngc-col-cli-" + Guid.NewGuid().ToString("N"));
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
