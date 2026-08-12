using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimTraceCommandTests
{
    [Fact]
    public void Execute_MissingAndInvalidBracketedInputs_ReturnFailure()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing].psx");
        var invalid = Path.Combine(temp.Path, "[bad].psx");
        File.WriteAllBytes(invalid, "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(missing));
        Assert.Equal(1, Execute(invalid));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutSideEffects()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].psx");
        var original = "NOPE"u8.ToArray();
        File.WriteAllBytes(input, original);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => Execute(input, cancellation.Token));
        Assert.Equal(original, File.ReadAllBytes(input));
        Assert.Equal(input, Assert.Single(Directory.EnumerateFiles(temp.Path)));
    }

    private static int Execute(string input, CancellationToken? cancellationToken = null)
    {
        return PsxAnimTraceCommand.Execute(
            input,
            animSourcePath: null,
            animIndex: 0,
            frame: 0,
            fps: 30f,
            bonesSpec: null,
            glbPath: null,
            glbAnimIndex: 0,
            transDivisorScale: 1f,
            rotComposeText: "yxz",
            rotScale: 1f,
            flatSkeleton: false,
            flatBonesSpec: null,
            vertexBounds: false,
            cancellationToken: cancellationToken ?? TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-trace-{Guid.NewGuid():N}");
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
