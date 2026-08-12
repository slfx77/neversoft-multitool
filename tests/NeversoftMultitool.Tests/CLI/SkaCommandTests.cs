using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class SkaCommandTests
{
    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].ska");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        Assert.Throws<OperationCanceledException>(() => Execute(
            input,
            output,
            verbose: true,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_BracketedInvalidFile_ReturnsFailureWithoutMarkupException()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "bad[clip].ska");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(
            input,
            output,
            verbose: true,
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(
        string input,
        string output,
        bool verbose,
        CancellationToken cancellationToken)
    {
        return SkaCommand.Execute(
            input,
            output,
            verbose,
            skePath: null,
            skinPath: null,
            texPath: null,
            sknPath: null,
            animationSkePath: null,
            cancellationToken: cancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-ska-command-{Guid.NewGuid():N}");
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
