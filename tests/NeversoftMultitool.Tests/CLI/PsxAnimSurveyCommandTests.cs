using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class PsxAnimSurveyCommandTests
{
    private const string CsvHeader =
        "build,relpath,version,mesh_revision,has_hierarchy,bones,meshes,file_size," +
        "post_mesh_size,layout,anim_revision,num_streams_declared,entries_recovered," +
        "runtime_revision,error";

    [Fact]
    public void Execute_EmptyDirectory_WritesHeaderOnlyCsvAndSucceeds()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "nested", "survey.csv");
        Directory.CreateDirectory(input);

        var result = PsxAnimSurveyCommand.Execute(
            input,
            output,
            verbose: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal([CsvHeader], File.ReadAllLines(output));
    }

    [Fact]
    public void Execute_OutputCanonicalAliasCannotOverwriteSurveyedPsx()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        Directory.CreateDirectory(input);
        var source = Path.Combine(input, "sample.psx");
        var original = "NOPE"u8.ToArray();
        File.WriteAllBytes(source, original);
        var outputAlias = Path.Combine(input, ".", Path.GetFileName(source));

        var result = PsxAnimSurveyCommand.Execute(
            input,
            outputAlias,
            verbose: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        Assert.Equal(original, File.ReadAllBytes(source));
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesWithoutCreatingOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "nested", "survey.csv");
        Directory.CreateDirectory(input);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => PsxAnimSurveyCommand.Execute(
            input,
            output,
            verbose: true,
            cancellation.Token));
        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        Assert.False(File.Exists(output));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-psx-anim-survey-{Guid.NewGuid():N}");
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
