using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class GsDumpCommandTests
{
    [Fact]
    public void Command_MissingBracketedInputFailsWithoutOutput()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var output = Path.Combine(temp.Path, "output");

        var result = GsDumpCommand.Create()
            .Parse([missing, "--output", output])
            .Invoke();

        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Command_ExistingEmptyDirectorySucceedsWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        var result = GsDumpCommand.Create()
            .Parse([input, "--output", output])
            .Invoke();

        Assert.Equal(0, result);
        Assert.False(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(input));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].gs");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            Execute(input, output, cancellation.Token));
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(string input, string output, CancellationToken cancellationToken)
    {
        return GsDumpCommand.Execute(
            input,
            output,
            pngPath: null,
            texPath: null,
            maxDumps: null,
            verbose: false,
            jsonOnly: true,
            probeX: null,
            probeY: null,
            probeOut: null,
            probeFbp: null,
            maxVsync: null,
            saveRtDir: null,
            saveRtStart: null,
            saveRtCount: null,
            saveRtFbp: null,
            saveRtOnStateTransition: false,
            dumpVramRegionSpecs: null,
            dumpFbpBuffers: false,
            dumpVertices: false,
            cancellationToken: cancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-gsdump-{Guid.NewGuid():N}");
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
