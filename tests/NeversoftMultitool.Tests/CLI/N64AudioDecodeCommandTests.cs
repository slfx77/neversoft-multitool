using System.Buffers.Binary;
using System.Security.Cryptography;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64AudioDecodeCommandTests(TestPaths paths)
{
    [Fact]
    public void Command_RequiredOptionsAndValidationFailures_DoNotCreateOutput()
    {
        using var temp = new TempDirectory();
        var (pointerData, waveData) = N64SoundToolsBankTests.BuildPair(pointerTail: 0, waveTail: 0);
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        var wavePath = Path.Combine(temp.Path, "waves.wbk");
        File.WriteAllBytes(pointerPath, pointerData);
        File.WriteAllBytes(wavePath, waveData);

        string[][] missingRequiredCases =
        [
            [pointerPath, "--wave", wavePath, "--sample-rate", "32000", "-o", Path.Combine(temp.Path, "missing-index.wav")],
            [pointerPath, "--wave", wavePath, "--index", "0", "-o", Path.Combine(temp.Path, "missing-rate.wav")],
            [pointerPath, "--wave", wavePath, "--index", "0", "--sample-rate", "32000"]
        ];
        foreach (var arguments in missingRequiredCases)
            Assert.NotEqual(0, N64AudioDecodeCommand.Create().Parse(arguments).Invoke());
        Assert.Empty(Directory.GetFiles(temp.Path, "*.wav", SearchOption.AllDirectories));

        foreach (var sampleRate in new[] { int.MinValue, -1, 0, 192_001, int.MaxValue })
        {
            var output = Path.Combine(temp.Path, $"rate-{sampleRate}.wav");
            Assert.Equal(1, N64AudioDecodeCommand.Execute(
                pointerPath, wavePath, 0, sampleRate, output, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(output));
        }

        foreach (var index in new[] { int.MinValue, -1, 3, int.MaxValue })
        {
            var output = Path.Combine(temp.Path, $"index-{index}.wav");
            Assert.Equal(1, N64AudioDecodeCommand.Execute(
                pointerPath, wavePath, index, 32_000, output, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(output));
        }

        var noWaveOutput = Path.Combine(temp.Path, "missing-wave.wav");
        Assert.Equal(1, N64AudioDecodeCommand.Execute(
            pointerPath, null, 0, 32_000, noWaveOutput, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(noWaveOutput));

        var malformedWave = waveData.ToArray();
        malformedWave[0x19] = 0xD0; // late frame scale 13: pair is valid, decode is not
        var malformedWavePath = Path.Combine(temp.Path, "malformed-header.wbk");
        var malformedOutput = Path.Combine(temp.Path, "malformed-header.wav");
        File.WriteAllBytes(malformedWavePath, malformedWave);
        byte[] sentinel = [0x51, 0x52, 0x53, 0x54];
        File.WriteAllBytes(malformedOutput, sentinel);
        Assert.Equal(1,
            N64AudioDecodeCommand.Execute(
                pointerPath, malformedWavePath, 0, 32_000, malformedOutput,
                TestContext.Current.CancellationToken));
        Assert.Equal(sentinel, File.ReadAllBytes(malformedOutput));

        var mismatchedWave = waveData.ToArray();
        mismatchedWave[0x22] = 1; // canonical inter-wave padding must remain zero
        var mismatchedWavePath = Path.Combine(temp.Path, "mismatched.wbk");
        var mismatchedOutput = Path.Combine(temp.Path, "mismatched.wav");
        File.WriteAllBytes(mismatchedWavePath, mismatchedWave);
        Assert.Equal(1,
            N64AudioDecodeCommand.Execute(
                pointerPath, mismatchedWavePath, 0, 32_000, mismatchedOutput,
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(mismatchedOutput));

        Assert.Equal(1, N64AudioDecodeCommand.Execute(
            pointerPath, wavePath, 0, 32_000, "\0", TestContext.Current.CancellationToken));
        Assert.Throws<InvalidDataException>(() =>
            N64AudioDecodeCommand.ValidateWavBounds(
                uint.MaxValue - uint.MaxValue % N64AdpcmDecoder.FrameSize,
                192_000));
    }

    [Fact]
    public void Command_CanonicalOutputPathsCannotOverwritePointerOrWaveSources()
    {
        using var temp = new TempDirectory();
        var (pointerData, waveData) = N64SoundToolsBankTests.BuildPair(pointerTail: 6, waveTail: 1);
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        var wavePath = Path.Combine(temp.Path, "waves.wbk");
        File.WriteAllBytes(pointerPath, pointerData);
        File.WriteAllBytes(wavePath, waveData);

        var pointerAlias = Path.Combine(temp.Path, ".", "bank.ptr.n64");
        var waveAlias = Path.Combine(temp.Path, "nested", "..", "waves.wbk");
        Assert.Equal(1,
            N64AudioDecodeCommand.Execute(
                pointerPath, wavePath, 0, 32_000, pointerAlias,
                TestContext.Current.CancellationToken));
        Assert.Equal(1,
            N64AudioDecodeCommand.Execute(
                pointerPath, wavePath, 0, 32_000, waveAlias,
                TestContext.Current.CancellationToken));

        Assert.Equal(pointerData, File.ReadAllBytes(pointerPath));
        Assert.Equal(waveData, File.ReadAllBytes(wavePath));
        Assert.Equal(2, Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Execute_PreCancelled_PropagatesBeforeInputOrOutputAccess()
    {
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "[cancelled].ptr.n64");
        var output = Path.Combine(temp.Path, "nested", "cancelled.wav");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => N64AudioDecodeCommand.Execute(
            input,
            wavePath: null,
            waveIndex: 0,
            sampleRate: 32_000,
            outputPath: output,
            cancellationToken: cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(output)));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void Command_ExplicitPair_WritesExactMonoPcm16WavAtCallerRate()
    {
        using var temp = new TempDirectory();
        var (pointerData, waveData) = N64SoundToolsBankTests.BuildPair(pointerTail: 6, waveTail: 1);
        var pointerPath = Path.Combine(temp.Path, "bank.ptr.n64");
        var wavePath = Path.Combine(temp.Path, "waves.wbk");
        var outputPath = Path.Combine(temp.Path, "nested", "wave.wav");
        File.WriteAllBytes(pointerPath, pointerData);
        File.WriteAllBytes(wavePath, waveData);

        var exitCode = N64AudioDecodeCommand.Create()
            .Parse([
                pointerPath, "--wave", wavePath, "--index", "0",
                "--sample-rate", "32000", "-o", outputPath
            ])
            .Invoke();

        Assert.Equal(0, exitCode);
        var wav = File.ReadAllBytes(outputPath);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(36 + 64, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal("WAVE"u8.ToArray(), wav[8..12]);
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20))); // PCM
        Assert.Equal((short)1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22))); // mono
        Assert.Equal(32_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(64_000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(32)));
        Assert.Equal((short)16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal("data"u8.ToArray(), wav[36..40]);
        Assert.Equal(64, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(44 + 64, wav.Length);

        var bank = N64SoundToolsBank.Parse(pointerData, waveData);
        var wave = bank.PointerBank.Waves[0];
        var expected = N64AdpcmDecoder.Decode(
            waveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength),
            wave.Book);
        Assert.Equal(N64AdpcmDecoderTests.HashPcmLittleEndian(expected),
            Convert.ToHexString(SHA256.HashData(wav.AsSpan(44))));
    }

    [Fact]
    public void Command_Thps3RomAndExplicitPair_AreByteIdenticalRuntimeGolden()
    {
        const string build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
        const string romName = "Tony Hawk's Pro Skater 3 (USA).z64";
        var romPath = paths.FindSampleFile(build, romName);
        Assert.SkipWhen(romPath == null, "THPS3 N64 ROM sample not available");
        var sources = N64AudioInspectCommand.ResolveSources(romPath!, wavePath: null);

        using var temp = new TempDirectory();
        var pointerPath = Path.Combine(temp.Path, sources.PointerSource);
        var wavePath = Path.Combine(temp.Path, sources.WaveSource);
        var romOutput = Path.Combine(temp.Path, "rom.wav");
        var pairOutput = Path.Combine(temp.Path, "pair.wav");
        File.WriteAllBytes(pointerPath, sources.PointerData);
        File.WriteAllBytes(wavePath, sources.WaveData);

        Assert.Equal(0, N64AudioDecodeCommand.Execute(
            romPath!, null, 221, 32_000, romOutput, TestContext.Current.CancellationToken));
        Assert.Equal(0, N64AudioDecodeCommand.Execute(
            pointerPath, wavePath, 221, 32_000, pairOutput, TestContext.Current.CancellationToken));

        var romWav = File.ReadAllBytes(romOutput);
        Assert.Equal(romWav, File.ReadAllBytes(pairOutput));
        Assert.Equal(640, BinaryPrimitives.ReadInt32LittleEndian(romWav.AsSpan(40)));
        Assert.Equal(
            "FB5D1A6718250BF978FC3C63B354199EE065437D8A46B0EB22C6D4F031D23E16",
            Convert.ToHexString(SHA256.HashData(romWav.AsSpan(44))));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-audio-decode-" + Guid.NewGuid().ToString("N"));
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
