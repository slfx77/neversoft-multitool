using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.CLI;

public sealed class SfdCommandTests
{
    [Fact]
    public void Execute_MissingAndEmptyInputsPreserveExitContractsWithoutConverter()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);
        var calls = 0;

        Assert.Equal(1, Execute(missing, missingOutput, Convert));
        Assert.Equal(0, Execute(empty, emptyOutput, Convert));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));

        SfdConvertResult Convert(
            string _file,
            string _output,
            CancellationToken _cancellationToken)
        {
            calls++;
            throw new InvalidOperationException("Converter must not run");
        }
    }

    [Fact]
    public void Execute_BracketedConversionFailureReturnsFailureWithoutMarkupException()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[bad].sfd"), []);
        var calls = 0;

        var result = Execute(input, output, (_, _, _) =>
        {
            calls++;
            return new SfdConvertResult { ErrorMessage = "[broken]" };
        });

        Assert.Equal(1, result);
        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void Execute_DuplicateOutputStemsFailBeforeConversionOrOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[same].sfd"), []);
        File.WriteAllBytes(Path.Combine(input, "[same].bik"), []);
        var calls = 0;

        var result = Execute(input, output, (_, _, _) =>
        {
            calls++;
            throw new InvalidOperationException("Converter must not run");
        });

        Assert.Equal(1, result);
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_ConverterCancellationIsReassertedAfterFailedResult()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[cancelled].sfd"), []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() => SfdCommand.Execute(
            input,
            output,
            verbose: true,
            convertOverride: (_, _, _) =>
            {
                calls++;
                cancellation.Cancel();
                return new SfdConvertResult { ErrorMessage = "Cancelled" };
            },
            cancellationToken: cancellation.Token));

        Assert.Equal(1, calls);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    private static int Execute(
        string input,
        string output,
        Func<string, string, CancellationToken, SfdConvertResult> converter)
    {
        return SfdCommand.Execute(
            input,
            output,
            verbose: true,
            convertOverride: converter,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-sfd-command-{Guid.NewGuid():N}");
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
