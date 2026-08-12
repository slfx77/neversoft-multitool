using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class QbKeyImportCommandTests
{
    [Fact]
    public void Command_ValidBracketedNamesFileSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[names].txt");
        File.WriteAllText(input, "synthetic_candidate\n");

        var result = QbKeyCommand.Create()
            .Parse(["import", input])
            .Invoke();

        Assert.Equal(0, result);
    }

    [Fact]
    public void Command_MissingBracketedNamesFileReturnsFailure()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing].txt");

        var result = QbKeyCommand.Create()
            .Parse(["import", missing])
            .Invoke();

        Assert.Equal(1, result);
    }

    [Fact]
    public void Command_MissingBracketedPsxDirectoryReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "names.txt");
        var missingPsxDirectory = Path.Combine(temp.Path, "[missing-psx]");
        File.WriteAllText(input, "synthetic_candidate\n");

        var result = QbKeyCommand.Create()
            .Parse(["import", input, "--psx-dir", missingPsxDirectory])
            .Invoke();

        Assert.Equal(1, result);
    }

    [Fact]
    public void Command_BracketedExportPathWritesMappingsAndSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "names.txt");
        var export = Path.Combine(temp.Path, "[out].txt");
        File.WriteAllText(input, "synthetic_candidate\n");

        var result = QbKeyCommand.Create()
            .Parse(["import", input, "--export", export])
            .Invoke();

        Assert.Equal(0, result);
        Assert.Contains("synthetic_candidate=0x", File.ReadAllText(export));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-qbkey-import-{Guid.NewGuid():N}");
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
