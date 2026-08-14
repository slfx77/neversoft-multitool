using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.CLI;

public sealed class ColCommandTests
{
    [Fact]
    public void Execute_ExplicitMalformedColReturnsFailureWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[bad].col");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, "NOPE"u8.ToArray());

        var result = Execute(input, output);

        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_MissingFailsButUnsupportedOnlyDirectoryRemainsNoWorkSuccess()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var directory = Path.Combine(temp.Path, "input");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var directoryOutput = Path.Combine(temp.Path, "directory-output");
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "[bad].col"), "NOPE"u8.ToArray());

        Assert.Equal(1, Execute(missing, missingOutput));
        Assert.Equal(0, Execute(directory, directoryOutput));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(directoryOutput));
    }

    [Fact]
    public void Execute_EmptyDirectoryPreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Throws<OperationCanceledException>(() => ColCommand.Execute(
            input,
            output,
            verbose: true,
            format: MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(string input, string output)
    {
        return ColCommand.Execute(
            input,
            output,
            verbose: true,
            format: MeshOutputFormat.Glb,
            blenderHelperPath: null,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-col-command-{Guid.NewGuid():N}");
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
