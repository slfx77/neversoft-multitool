using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.CLI;

public sealed class StrCommandTests
{
    [Fact]
    public void SelectCandidatePaths_KeepsEveryStrExtensionCaseAndRemovesOnlyExactDuplicates()
    {
        var lowerCasePath = Path.Combine("input", "clip.str");
        var upperCasePath = Path.Combine("input", "CLIP.STR");
        var mixedCasePath = Path.Combine("input", "mixed.Str");
        var unrelatedPath = Path.Combine("input", "not-video.txt");

        var result = StrCommand.SelectCandidatePaths(
            [lowerCasePath, lowerCasePath, upperCasePath, mixedCasePath, unrelatedPath]);

        Assert.Equal([lowerCasePath, upperCasePath, mixedCasePath], result);
    }

    [Fact]
    public void FindDuplicateOutputStems_UsesExactStemIdentity()
    {
        var lowerCasePath = Path.Combine("input", "clip.str");
        var sameStemPath = Path.Combine("input", "clip.STR");
        var upperCasePath = Path.Combine("input", "CLIP.STR");

        Assert.Equal(
            ["clip"],
            StrCommand.FindDuplicateOutputStems([lowerCasePath, sameStemPath]));
        Assert.Empty(StrCommand.FindDuplicateOutputStems([lowerCasePath, upperCasePath]));
    }

    [Fact]
    public void Execute_MissingAndEmptyInputsPreserveExitContractsWithoutConverter()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var rejected = Path.Combine(temp.Path, "rejected");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        var rejectedOutput = Path.Combine(temp.Path, "rejected-output");
        Directory.CreateDirectory(empty);
        Directory.CreateDirectory(rejected);
        File.WriteAllBytes(Path.Combine(rejected, "[short].str"), new byte[15]);
        var afsHeader = new byte[16];
        "AFS\0"u8.CopyTo(afsHeader);
        File.WriteAllBytes(Path.Combine(rejected, "[archive].STR"), afsHeader);
        var calls = 0;

        Assert.Equal(1, Execute(missing, missingOutput, Convert));
        Assert.Equal(0, Execute(empty, emptyOutput, Convert));
        Assert.Equal(0, Execute(rejected, rejectedOutput, Convert));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(rejectedOutput));

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
        File.WriteAllBytes(Path.Combine(input, "[bad].str"), new byte[16]);
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
    public void Execute_ConverterCancellationIsReassertedAfterFailedResult()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[cancelled].str"), new byte[16]);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() => StrCommand.Execute(
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
        return StrCommand.Execute(
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
                System.IO.Path.GetTempPath(), $"nmt-str-command-{Guid.NewGuid():N}");
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
