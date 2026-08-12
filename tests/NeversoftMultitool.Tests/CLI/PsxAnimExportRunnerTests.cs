using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimExportRunnerTests
{
    [Fact]
    public void Run_MissingBracketedInput_ReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing].psx");
        var output = Path.Combine(temp.Path, "output", "[animated].glb");

        Assert.Equal(1, Run(input, output));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    [Fact]
    public void Run_PreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var output = Path.Combine(temp.Path, "output", "[animated].glb");
        var original = "NOPE"u8.ToArray();
        File.WriteAllBytes(input, original);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Run(input, output, cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
    }

    private static int Run(
        string input,
        string output,
        CancellationToken? cancellationToken = null)
    {
        return PsxAnimExportRunner.Run(
            input,
            output,
            animSourcePath: null,
            animIndex: -1,
            animName: null,
            opts: new PsxAnimationOptions(),
            format: MeshOutputFormat.Glb,
            blenderHelper: null,
            flatSkeleton: false,
            flatBoneFilter: null,
            verbose: false,
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-export-{Guid.NewGuid():N}");
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
