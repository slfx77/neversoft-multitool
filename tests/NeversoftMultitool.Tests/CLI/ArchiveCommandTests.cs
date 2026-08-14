using System.CommandLine;
using System.IO.Compression;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class ArchiveCommandTests
{
    [Fact]
    public async Task Command_PreCancelledPropagatesBeforeOutputCreationAndPreservesSource()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "valid.zip");
        var output = Path.Combine(temp.Path, "output");
        using (var zipStream = File.Create(input))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("payload.bin", CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            entryStream.Write([0x10, 0x20, 0x30, 0x40]);
        }

        var sourceBytes = File.ReadAllBytes(input);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        await cancellation.CancelAsync();

        var parseResult = ArchiveCommand.Create()
            .Parse([input, "-o", output]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            parseResult.InvokeAsync(
                new InvocationConfiguration
                {
                    EnableDefaultExceptionHandler = false
                },
                cancellation.Token));

        Assert.Equal(sourceBytes, File.ReadAllBytes(input));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Command_MissingBracketedInputReturnsFailureWithoutMarkupException()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[missing].wad");
        var output = Path.Combine(temp.Path, "output");

        var result = Invoke(input, output, verbose: true);

        Assert.Equal(1, result);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Command_BracketedArchiveErrorReturnsFailureWithoutMarkupException()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[bad].wad");
        var output = Path.Combine(temp.Path, "output");
        File.WriteAllBytes(input, []);

        var result = Invoke(input, output, verbose: true);

        Assert.Equal(1, result);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static int Invoke(string input, string output, bool verbose)
    {
        var arguments = new List<string> { input, "-o", output };
        if (verbose)
            arguments.Add("-v");

        return ArchiveCommand.Create()
            .Parse(arguments.ToArray())
            .Invoke(new InvocationConfiguration
            {
                EnableDefaultExceptionHandler = false
            });
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-archive-command-{Guid.NewGuid():N}");
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
