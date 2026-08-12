using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.CLI;

public sealed class LegacyMeshWrapperCommandTests
{
    [Fact]
    public void PsxMesh_MissingBracketedPathFailsWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing].psx");
        var output = Path.Combine(temp.Path, "output");

        Assert.Equal(1, ExecutePsxMesh(input, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void PsxMesh_EmptyDirectoryPreservesNoWorkSuccessButPreCancelledPropagates()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Equal(0, ExecutePsxMesh(input, output));
        Assert.False(Directory.Exists(output));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExecutePsxMesh(
            input,
            output,
            cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void RwDff_MissingBracketedPathFailsWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing].skn");
        var output = Path.Combine(temp.Path, "output");

        Assert.Equal(1, ExecuteRwDff(input, output));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void RwDff_EmptyDirectoryPreservesNoWorkSuccessButPreCancelledPropagates()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Equal(0, ExecuteRwDff(input, output));
        Assert.False(Directory.Exists(output));

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ExecuteRwDff(
            input,
            output,
            cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private static int ExecutePsxMesh(
        string input,
        string output,
        CancellationToken? cancellationToken = null)
    {
        return PsxMeshCommand.Execute(
            input,
            output,
            verbose: true,
            format: MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private static int ExecuteRwDff(
        string input,
        string output,
        CancellationToken? cancellationToken = null)
    {
        return RwDffCommand.Execute(
            input,
            output,
            texPath: null,
            verbose: true,
            format: MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-legacy-mesh-wrapper-{Guid.NewGuid():N}");
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
