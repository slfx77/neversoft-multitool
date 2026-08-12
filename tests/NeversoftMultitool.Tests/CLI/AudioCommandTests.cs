using System.Buffers.Binary;
using NeversoftMultitool.CLI;

namespace NeversoftMultitool.Tests.CLI;

public sealed class AudioCommandTests
{
    private static readonly byte[] WavSignature = "RIFF"u8.ToArray();

    [Fact]
    public void Execute_MixedBracketedBatch_PreservesSuccessAndReturnsFailure()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[good].vag"), new byte[16]);
        File.WriteAllBytes(Path.Combine(input, "[bad].vag"), new byte[4]);

        var result = AudioCommand.Execute(
            input, output, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken);

        Assert.Equal(1, result);
        var wavPath = Path.Combine(output, "[good].wav");
        Assert.Equal(wavPath, Assert.Single(Directory.EnumerateFiles(output, "*.wav")));
        Assert.Equal(WavSignature, File.ReadAllBytes(wavPath)[..WavSignature.Length]);
        Assert.False(File.Exists(Path.Combine(output, "[bad].wav")));

        var validOnlyInput = Path.Combine(temp.Path, "valid-only-input");
        var validOnlyOutput = Path.Combine(temp.Path, "valid-only-output");
        Directory.CreateDirectory(validOnlyInput);
        File.WriteAllBytes(Path.Combine(validOnlyInput, "[good].vag"), new byte[16]);
        Assert.Equal(0, AudioCommand.Execute(
            validOnlyInput,
            validOnlyOutput,
            verbose: true,
            sampleRate: 32_000,
            TestContext.Current.CancellationToken));
        var validOnlyWav = File.ReadAllBytes(Path.Combine(validOnlyOutput, "[good].wav"));
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(validOnlyWav.AsSpan(0x18, 4)));
    }

    [Fact]
    public void Execute_MissingDirectoryFailsAndEmptyDirectorySucceeds()
    {
        using var temp = new TempDirectory();
        var missing = Path.Combine(temp.Path, "[missing]");
        var empty = Path.Combine(temp.Path, "empty");
        var missingOutput = Path.Combine(temp.Path, "missing-output");
        var emptyOutput = Path.Combine(temp.Path, "empty-output");
        Directory.CreateDirectory(empty);

        Assert.Equal(1, AudioCommand.Execute(
            missing, missingOutput, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken));
        Assert.Equal(0, AudioCommand.Execute(
            empty, emptyOutput, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(missingOutput));
        Assert.False(Directory.Exists(emptyOutput));
    }

    [Fact]
    public void Execute_PreCancelled_DoesNotWriteWav()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "[cancelled].vag"), new byte[16]);

        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        Assert.Throws<OperationCanceledException>(() => AudioCommand.Execute(
            input, output, verbose: true, sampleRate: 0, cancellationSource.Token));
        Assert.Empty(Directory.EnumerateFiles(output, "*.wav"));
    }

    [Fact]
    public void Execute_ExtensionlessAndVagWithSameStem_WriteDistinctDeterministicOutputs()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "same"), new byte[16]);
        File.WriteAllBytes(Path.Combine(input, "same.vag"), new byte[16]);

        var result = AudioCommand.Execute(
            input, output, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        var expected = new[]
        {
            Path.Combine(output, "same_0967115f.wav"),
            Path.Combine(output, "same_f15be53e.wav")
        };
        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            Directory.EnumerateFiles(output, "*.wav").Order(StringComparer.Ordinal));
        Assert.All(expected, path =>
            Assert.Equal(WavSignature, File.ReadAllBytes(path)[..WavSignature.Length]));
        Assert.False(File.Exists(Path.Combine(output, "same.wav")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-audio-{Guid.NewGuid():N}");
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
