using NeversoftMultitool.CLI;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.CLI;

public sealed class MeshCommandXbxSkinTests(TestPaths paths)
{
    private const string Thug2XboxBuild =
        "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    [CorpusFact]
    public void MeshCommand_ExplicitSkeleton_WiresXbxSkinWithoutChangingDefault()
    {
        var skinPath = paths.FindSampleFile(Thug2XboxBuild, "Anl_Pigeon.skin.xbx");
        var skeletonPath = paths.FindSampleFile(Thug2XboxBuild, "anl_pigeon.ske.xbx");
        Assert.SkipWhen(skinPath == null || skeletonPath == null,
            "THUG2 Xbox pigeon skin/skeleton fixtures are unavailable");

        using var temp = new TempDirectory();
        var rigidOutput = Path.Combine(temp.Path, "rigid");
        var rigidExitCode = MeshCommand.Create()
            .Parse([skinPath!, "--output", rigidOutput])
            .Invoke();
        Assert.Equal(0, rigidExitCode);

        var skinnedOutput = Path.Combine(temp.Path, "skinned");
        var skinnedExitCode = MeshCommand.Create()
            .Parse([skinPath!, "--output", skinnedOutput, "--ske", skeletonPath!])
            .Invoke();
        Assert.Equal(0, skinnedExitCode);

        var rigid = ReadOnlyGlb(rigidOutput);
        Assert.Empty(rigid.LogicalSkins);

        var skinned = ReadOnlyGlb(skinnedOutput);
        Assert.Equal(4, Assert.Single(skinned.LogicalSkins).JointsCount);
        Assert.Equal(45, skinned.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive => primitive.GetTriangleIndices().Count()));
        Assert.Equal(46, skinned.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive => primitive.GetVertexAccessor("POSITION").Count));
    }

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
                "nmt-xbx-cli-" + Guid.NewGuid().ToString("N"));
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
