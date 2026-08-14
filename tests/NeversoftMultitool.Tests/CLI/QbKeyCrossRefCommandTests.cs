using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class QbKeyCrossRefCommandTests
{
    [Fact]
    public void Execute_PreCancelled_PropagatesBeforeExport()
    {
        using var temp = new TempDirectory();
        var ddmDirectory = Path.Combine(temp.Path, "ddm");
        var psxDirectory = Path.Combine(temp.Path, "psx");
        var outputDirectory = Path.Combine(temp.Path, "output");
        var exportPath = Path.Combine(outputDirectory, "names.txt");
        Directory.CreateDirectory(ddmDirectory);
        Directory.CreateDirectory(psxDirectory);

        Assert.Throws<OperationCanceledException>(() => QbKeyCrossRefCommand.Execute(
            ddmDirectory,
            psxDirectory,
            exportPath,
            verbose: true,
            showUnmatched: true,
            scanArchives: null,
            scanPsh: null,
            cancellationToken: new CancellationToken(canceled: true)));
        Assert.False(File.Exists(exportPath));
        Assert.False(Directory.Exists(outputDirectory));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-qbkey-crossref-{Guid.NewGuid():N}");
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
