using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Core.Formats.Video;

namespace NeversoftMultitool.Tests.CLI;

public sealed class VidCommandTests
{
    [Fact]
    public void SelectCandidatePaths_FiltersExtensionAndRemovesOnlyExactDuplicates()
    {
        var lowerCasePath = Path.Combine("input", "clip.vid");
        var upperCasePath = Path.Combine("input", "CLIP.VID");
        var mixedCasePath = Path.Combine("input", "mixed.ViD");
        var unrelatedPath = Path.Combine("input", "clip.txt");

        var result = VidCommand.SelectCandidatePaths(
            [lowerCasePath, lowerCasePath, upperCasePath, mixedCasePath, unrelatedPath]);

        Assert.Equal([lowerCasePath, upperCasePath, mixedCasePath], result);
    }

    [Fact]
    public void FindDuplicateOutputStems_UsesExactStemIdentity()
    {
        var lowerCasePath = Path.Combine("input", "clip.vid");
        var sameStemPath = Path.Combine("input", "clip.VID");
        var upperCasePath = Path.Combine("input", "CLIP.VID");

        Assert.Equal(
            ["clip"],
            VidCommand.FindDuplicateOutputStems([lowerCasePath, sameStemPath]));
        Assert.Empty(VidCommand.FindDuplicateOutputStems([lowerCasePath, upperCasePath]));
    }

    [Fact]
    public void Execute_MissingAndNoWorkInputsPreserveExitContractsWithoutConverter()
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
        File.WriteAllBytes(Path.Combine(rejected, "[invalid].VID"), "NOPE"u8.ToArray());
        var calls = 0;

        Assert.Equal(1, Execute(missing, missingOutput, writeFrames: false, converter: Convert));
        Assert.Equal(0, Execute(empty, emptyOutput, writeFrames: false, converter: Convert));
        Assert.Equal(0, Execute(rejected, rejectedOutput, writeFrames: false, converter: Convert));
        Assert.Equal(0, calls);
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
        Assert.False(Directory.Exists(rejectedOutput));

        SfdConvertResult Convert(
            string _file,
            string _output,
            bool _writeFrames,
            CancellationToken _cancellationToken)
        {
            calls++;
            throw new InvalidOperationException("Converter must not run");
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Execute_BracketedConversionResultPreservesModeAndExitContract(
        bool writeFrames,
        bool success)
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(
            Path.Combine(input, "[sample].vid"),
            Vid1VideoTestBuilder.CreateVideoVid1());
        var calls = 0;

        var result = Execute(input, output, writeFrames, (_, _, requestedFrames, _) =>
        {
            calls++;
            Assert.Equal(writeFrames, requestedFrames);
            return new SfdConvertResult
            {
                Success = success,
                ErrorMessage = success ? null : "[broken]"
            };
        });

        Assert.Equal(success ? 0 : 1, result);
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
        File.WriteAllBytes(
            Path.Combine(input, "[cancelled].vid"),
            Vid1VideoTestBuilder.CreateVideoVid1());
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var calls = 0;

        Assert.Throws<OperationCanceledException>(() => VidCommand.Execute(
            input,
            output,
            verbose: true,
            writeFrames: true,
            convertOverride: (_, _, _, _) =>
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

    [Fact]
    public void Execute_ProbeCancellationIsReassertedBeforeOutputOrConversion()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[cancelled].vid"), []);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var probeCalls = 0;
        var convertCalls = 0;

        Assert.Throws<OperationCanceledException>(() => VidCommand.Execute(
            input,
            output,
            verbose: true,
            writeFrames: false,
            probeOverride: _ =>
            {
                probeCalls++;
                cancellation.Cancel();
                return null;
            },
            convertOverride: (_, _, _, _) =>
            {
                convertCalls++;
                throw new InvalidOperationException("Converter must not run");
            },
            cancellationToken: cancellation.Token));

        Assert.Equal(1, probeCalls);
        Assert.Equal(0, convertCalls);
        Assert.False(Directory.Exists(output));
    }

    private static int Execute(
        string input,
        string output,
        bool writeFrames,
        Func<string, string, bool, CancellationToken, SfdConvertResult> converter)
    {
        return VidCommand.Execute(
            input,
            output,
            verbose: true,
            writeFrames: writeFrames,
            convertOverride: converter,
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-vid-command-{Guid.NewGuid():N}");
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
