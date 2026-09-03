using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Tests.Core.Formats.Audio;

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
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void Execute_EmptyDirectoryPreCancelled_PropagatesWithoutOutput()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "empty");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);

        Assert.Throws<OperationCanceledException>(() =>
            AudioCommand.Execute(
                input,
                output,
                verbose: true,
                sampleRate: 0,
                new CancellationToken(canceled: true)));
        Assert.False(Directory.Exists(output));
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

    [Fact]
    public void Execute_ExtensionlessWiiDsp_UsesItsAuthoredSampleRate()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        File.WriteAllBytes(Path.Combine(input, "fallwater"), BuildWiiDsp());

        var result = AudioCommand.Execute(
            input, output, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        var wav = File.ReadAllBytes(Path.Combine(output, "fallwater.wav"));
        Assert.Equal(WavSignature, wav[..WavSignature.Length]);
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(0x18, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(0x16, 2)));
        Assert.Equal(28, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(0x28, 4)));
    }

    [Fact]
    public void SelectNamedCandidatePaths_IncludesStandardAudioCaseInsensitively()
    {
        var wave = Path.Combine("input", "music.WAV");
        var wma = Path.Combine("input", "voice.WmA");
        var unrelated = Path.Combine("input", "notes.txt");

        Assert.Equal([wave, wma], AudioCommand.SelectNamedCandidatePaths([wave, wma, unrelated]));
    }

    [Fact]
    public void SelectNamedCandidatePaths_ContentGatesPmfToActualAudioStreams()
    {
        using var temp = new TempDirectory();
        var audio = Path.Combine(temp.Path, "movie.PMF");
        var silent = Path.Combine(temp.Path, "icon.pmf");
        var malformed = Path.Combine(temp.Path, "broken.pmf");
        File.WriteAllBytes(audio, PsmfTestBuilder.Create(frameCount: 2, frameSize: 568));
        File.WriteAllBytes(silent, PsmfTestBuilder.CreateVideoOnly());
        File.WriteAllBytes(malformed, "PSMF"u8.ToArray());

        Assert.Equal(
            [audio],
            AudioCommand.SelectNamedCandidatePaths([audio, silent, malformed]));
    }

    [Fact]
    public void Execute_WaveInput_PassesThroughLosslessly()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "input");
        var output = Path.Combine(temp.Path, "output");
        Directory.CreateDirectory(input);
        var wave = BuildWave();
        File.WriteAllBytes(Path.Combine(input, "music.wav"), wave);

        var result = AudioCommand.Execute(
            input, output, verbose: true, sampleRate: 0, TestContext.Current.CancellationToken);

        Assert.Equal(0, result);
        Assert.Equal(wave, File.ReadAllBytes(Path.Combine(output, "music.wav")));
    }

    [Fact]
    public void Create_HelpNamesWaveWindowsMediaAndPmfInputs()
    {
        var command = AudioCommand.Create();
        var input = Assert.Single(command.Arguments);

        Assert.Contains("WAV", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WMA", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PMF", command.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".wav", input.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".wma", input.Description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".pmf", input.Description, StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] BuildWave()
    {
        const int sampleRate = 8_000;
        const int dataBytes = sampleRate * sizeof(short);
        var data = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), sampleRate * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), dataBytes);
        return data;
    }

    private static byte[] BuildWiiDsp()
    {
        var data = new byte[0x68];
        BinaryPrimitives.WriteUInt32BigEndian(data, 14);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), 32_000);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x3E), 0x0B);
        data[0x60] = 0x0B;
        return data;
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
