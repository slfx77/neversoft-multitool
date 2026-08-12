using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool.Tests.CLI;

public sealed class QbCommandTests
{
    private static readonly byte[] MinimalQb = [(byte)QbTokenType.EndOfFile];

    [Fact]
    public void Execute_MissingBracketedPathsFailAndExistingEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missingFile = Path.Combine(temp.Path, "[missing].qb");
        var missingDirectory = Path.Combine(temp.Path, "[missing-directory]");
        var emptyDirectory = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(emptyDirectory);

        Assert.Equal(1, QbCommand.Execute(
            missingFile, output, verbose: true, CancellationToken.None));
        Assert.Equal(1, QbCommand.Execute(
            missingDirectory, output, verbose: true, CancellationToken.None));
        Assert.Equal(0, QbCommand.Execute(
            emptyDirectory, output, verbose: true, CancellationToken.None));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_MixedBracketedDirectory_PreservesSuccessAndRejectsMalformedQb()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[good].qb"), MinimalQb);
        File.WriteAllBytes(Path.Combine(input, "[bad].qb"), "NOPE"u8.ToArray());

        var result = QbCommand.Execute(
            input, output, verbose: true, CancellationToken.None);

        Assert.Equal(1, result);
        var goodOutput = Path.Combine(output, "[good].q");
        Assert.Equal(goodOutput, Assert.Single(Directory.EnumerateFiles(output, "*.q")));
        Assert.Empty(File.ReadAllText(goodOutput));
        Assert.False(File.Exists(Path.Combine(output, "[bad].q")));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutWritingOutputFile()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancel].qb");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, MinimalQb);

        Assert.Throws<OperationCanceledException>(() => QbCommand.Execute(
            input, output, verbose: true, new CancellationToken(canceled: true)));
        Assert.False(File.Exists(Path.Combine(output, "[cancel].q")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-qb-{Guid.NewGuid():N}");
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
