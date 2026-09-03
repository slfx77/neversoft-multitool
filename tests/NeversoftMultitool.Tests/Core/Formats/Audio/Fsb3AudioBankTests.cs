using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Fsb3AudioBankTests(TestPaths paths)
{
    private const string Project8Ps3Build = "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";
    private const string Project8XenBuild = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string ProvingGroundPs3Build = "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)";
    private const string ProvingGroundXenBuild = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";

    [Fact]
    public void Probe_ConsumesMixedBankAndReportsNamedStreams()
    {
        var data = BuildMixedBank();

        var bank = Fsb3AudioBank.Probe(data);

        Assert.NotNull(bank);
        Assert.Equal(0x00030001U, bank.Version);
        Assert.Equal(4, bank.HeaderPaddingBytes);
        Assert.Equal(196, bank.SampleHeaderSize);
        Assert.Equal(220, bank.DataOffset);
        Assert.Equal(4112, bank.DataSize);
        Assert.Collection(bank.Samples,
            mp3 =>
            {
                Assert.Equal(0, mp3.Index);
                Assert.Equal("theme.mp3", mp3.Name);
                Assert.Equal(Fsb3AudioCodec.MpegLayer3, mp3.Codec);
                Assert.Equal(44_100, mp3.SampleRate);
                Assert.Equal(2, mp3.Channels);
                Assert.Equal(16, mp3.CompressedSize);
                Assert.Equal(220, mp3.DataOffset);
                Assert.Equal(44_100d / 44_100, mp3.DurationSeconds, precision: 12);
            },
            xma =>
            {
                Assert.Equal(1, xma.Index);
                Assert.Equal("voice.xma", xma.Name);
                Assert.Equal(Fsb3AudioCodec.Xma1, xma.Codec);
                Assert.Equal(48_000, xma.SampleRate);
                Assert.Equal(1, xma.Channels);
                Assert.Equal(4096, xma.CompressedSize);
                Assert.Equal(236, xma.DataOffset);
                Assert.Equal(112, xma.HeaderSize);
            });
    }

    [Fact]
    public void CompoundNames_AreRoutedThroughCliAndSharedProbe()
    {
        using var temp = new TempDirectory();
        var bankPath = Path.Combine(temp.Path, "sounds.FSB.XEN");
        File.WriteAllBytes(bankPath, BuildMixedBank());

        Assert.True(Fsb3AudioBank.HasSupportedFileName(bankPath));
        Assert.Equal([bankPath], AudioCommand.SelectNamedCandidatePaths([bankPath]));

        var result = FormatProbe.ProbeAudio(bankPath);
        Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
        Assert.Equal("FSB3 MP3/XMA1 Sound Bank", result.FormatName);
    }

    [Fact]
    public void SharedProbe_RejectsACompoundNamedNearMiss()
    {
        using var temp = new TempDirectory();
        var bankPath = Path.Combine(temp.Path, "sounds.fsb.ps3");
        File.WriteAllBytes(bankPath, BuildMixedBank()[..^1]);

        var result = FormatProbe.ProbeAudio(bankPath);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal("FSB3 Sound Bank", result.FormatName);
        Assert.Contains("exact", result.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Probe_RejectsBrokenIndependentIdentities(int mutation)
    {
        var data = BuildMixedBank();
        switch (mutation)
        {
            case 1:
                Array.Resize(ref data, data.Length + 1); // Main length declaration no longer reaches EOF.
                break;
            case 2:
                data[216] = 1; // Non-zero header-alignment byte.
                break;
            case 3:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12, 4), 4096); // Sum of streams differs.
                break;
            case 4:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(204, 4), 3); // XMA packet count differs.
                break;
            case 5:
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72, 4), 0x00000040); // No codec.
                break;
            case 6:
                data[236] = 0; // Raw XMA packet signature.
                break;
        }

        Assert.Null(Fsb3AudioBank.Probe(data));
    }

    [Fact]
    public void CreatePlayableStream_CopiesMp3AndWrapsXma1Exactly()
    {
        var bankData = BuildMixedBank();

        var mp3 = Fsb3AudioBank.CreatePlayableStream(bankData, 0);
        var xma = Fsb3AudioBank.CreatePlayableStream(bankData, 1);

        Assert.Equal(bankData.AsSpan(220, 16).ToArray(), mp3);
        Assert.Equal(4096 + Fsb3AudioBank.XmaWaveHeaderSize, xma.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(xma, 0, 4));
        Assert.Equal((uint)(xma.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(4, 4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(xma, 8, 8));
        Assert.Equal(52U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(16, 4)));
        Assert.Equal(0x0166, BinaryPrimitives.ReadUInt16LittleEndian(xma.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(xma.AsSpan(22, 2)));
        Assert.Equal(48_000U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(24, 4)));
        Assert.Equal(96_000U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(28, 4)));
        Assert.Equal(48_000U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(44, 4)));
        Assert.Equal(0x8000U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(48, 4)));
        Assert.Equal(48_000U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(56, 4)));
        Assert.Equal(4, xma[69]);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(xma.AsSpan(70, 2)));
        Assert.Equal("data", Encoding.ASCII.GetString(xma, 72, 4));
        Assert.Equal(4096U, BinaryPrimitives.ReadUInt32LittleEndian(xma.AsSpan(76, 4)));
        Assert.Equal(bankData.AsSpan(236, 4096).ToArray(), xma.AsSpan(80).ToArray());
        Assert.Empty(Fsb3AudioBank.CreatePlayableStream(bankData, 2));
    }

    [Fact]
    public void ExtractEncoded_UsesNamesAndIndicesWithoutCollisions()
    {
        using var temp = new TempDirectory();
        var data = BuildMixedBank();

        var result = Fsb3AudioBank.ExtractEncoded(data, "bank", temp.Path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.SamplesWritten);
        var mp3Path = Path.Combine(temp.Path, "bank", "00000_theme.mp3");
        var xmaPath = Path.Combine(temp.Path, "bank", "00001_voice.xma");
        Assert.Equal(data.AsSpan(220, 16).ToArray(), File.ReadAllBytes(mp3Path));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(xmaPath), 0, 4));
    }

    [Fact]
    public void ExtractEncoded_InvalidInputPreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var bankDirectory = Path.Combine(temp.Path, "bank");
        Directory.CreateDirectory(bankDirectory);
        var existingPath = Path.Combine(bankDirectory, "00000_theme.mp3");
        var sentinel = "do not replace"u8.ToArray();
        File.WriteAllBytes(existingPath, sentinel);
        var invalid = BuildMixedBank()[..^1];

        var result = Fsb3AudioBank.ExtractEncoded(invalid, "bank", temp.Path);

        Assert.True(result.Skipped);
        Assert.Equal(sentinel, File.ReadAllBytes(existingPath));
        Assert.Single(Directory.GetFiles(bankDirectory));
    }

    [Fact]
    public void ConvertToWav_StagesEveryTargetAndAcceptsBothPlayableContainers()
    {
        using var temp = new TempDirectory();
        var seenContainers = new List<string>();

        var result = Fsb3AudioBank.ConvertToWav(
            BuildMixedBank(),
            "bank",
            temp.Path,
            Transcode);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.SamplesWritten);
        Assert.Equal(["MP3", "XMA"], seenContainers);
        foreach (var path in Directory.GetFiles(Path.Combine(temp.Path, "bank"), "*.wav"))
            Assert.True(RiffWaveReader.TryRead(File.ReadAllBytes(path), out _));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(temp.Path, "bank"), "*.wav").Length);
        return;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            var bytes = File.ReadAllBytes(inputPath);
            var isXma = bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8);
            seenContainers.Add(isXma ? "XMA" : "MP3");
            WavWriter.WritePcm16(
                outputPath,
                isXma ? 48_000 : 44_100,
                isXma ? 1 : 2,
                [0, 1, -1, 2]);
            error = "";
            return true;
        }
    }

    [Fact]
    public void ConvertToWav_DecoderFailurePreservesThatSamplesExistingTarget()
    {
        using var temp = new TempDirectory();
        var bankDirectory = Path.Combine(temp.Path, "bank");
        Directory.CreateDirectory(bankDirectory);
        var existingPath = Path.Combine(bankDirectory, "00001_voice.wav");
        var sentinel = "existing wave"u8.ToArray();
        File.WriteAllBytes(existingPath, sentinel);
        var calls = 0;

        var result = Fsb3AudioBank.ConvertToWav(
            BuildMixedBank(),
            "bank",
            temp.Path,
            Transcode);

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal("synthetic decoder failure", result.ErrorMessage);
        Assert.Equal(sentinel, File.ReadAllBytes(existingPath));
        Assert.DoesNotContain(Directory.GetFiles(bankDirectory),
            static path => Path.GetFileName(path)[0] == '.');
        return;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            calls++;
            if (calls == 2)
            {
                File.WriteAllBytes(outputPath, "partial"u8.ToArray());
                error = "synthetic decoder failure";
                return false;
            }

            WavWriter.WritePcm16(outputPath, 44_100, 2, [0, 1, -1, 2]);
            error = "";
            return true;
        }
    }

    [CorpusFact]
    public void Probe_AllTwelveBanks_ConsumesExactCorpusAndCodecPopulation()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = FindCorpusBanks();
        Assert.Equal(12, files.Length);

        long totalBytes = 0;
        var totalSamples = 0;
        var mp3Samples = 0;
        var xmaSamples = 0;
        var monoSamples = 0;
        var stereoSamples = 0;
        var duplicateNames = 0;
        var sampleRates = new Dictionary<int, int>();
        var modes = new Dictionary<uint, int>();
        var padding = new Dictionary<int, int>();
        foreach (var file in files)
        {
            var bank = Fsb3AudioBank.Probe(file);
            Assert.NotNull(bank);
            var length = new FileInfo(file).Length;
            Assert.Equal(length, bank.DataOffset + bank.DataSize);
            Assert.Equal(bank.DataSize, bank.Samples.Sum(static sample => (long)sample.CompressedSize));
            totalBytes += length;
            totalSamples += bank.Samples.Count;
            duplicateNames += bank.Samples.Count - bank.Samples
                .Select(static sample => sample.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            Increment(padding, bank.HeaderPaddingBytes);

            var expectedOffset = bank.DataOffset;
            foreach (var sample in bank.Samples)
            {
                Assert.Equal(expectedOffset, sample.DataOffset);
                Assert.Equal(sample.DecodedSampleCount - 1, sample.LoopEnd);
                Assert.Equal(0, sample.LoopStart);
                Assert.NotEmpty(sample.Name);
                expectedOffset += sample.CompressedSize;
                if (sample.Codec == Fsb3AudioCodec.MpegLayer3)
                {
                    mp3Samples++;
                    Assert.Equal(Fsb3AudioBank.SampleHeaderSize, sample.HeaderSize);
                }
                else
                {
                    xmaSamples++;
                    Assert.Equal(0, sample.CompressedSize % Fsb3AudioBank.XmaPacketSize);
                    Assert.Equal(
                        104 + sample.CompressedSize / Fsb3AudioBank.XmaPacketSize * sizeof(uint),
                        sample.HeaderSize);
                }

                if (sample.Channels == 1) monoSamples++;
                else stereoSamples++;
                Increment(sampleRates, sample.SampleRate);
                Increment(modes, sample.Mode);
            }
        }

        Assert.Equal(1_782_745_082, totalBytes);
        Assert.Equal(22_454, totalSamples);
        Assert.Equal(5_418, mp3Samples);
        Assert.Equal(17_036, xmaSamples);
        Assert.Equal(21_754, monoSamples);
        Assert.Equal(700, stereoSamples);
        Assert.Equal(679, duplicateNames);
        Assert.Equal(new Dictionary<int, int> { [44_100] = 557, [48_000] = 21_897 }, sampleRates);
        Assert.Equal(new Dictionary<uint, int>
        {
            [0x00000220] = 5_068,
            [0x00000240] = 350,
            [0x01100020] = 16_686,
            [0x01100040] = 350
        }, modes);
        Assert.Equal(new Dictionary<int, int> { [0] = 4, [4] = 4, [8] = 3, [12] = 1 }, padding);
    }

    [CorpusFact]
    public void ConvertSingleToWav_RealXma1Stream_DecodesThroughFfmpeg()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not available");
        var file = paths.FindSampleFile(ProvingGroundXenBuild, "vo_English.fsb.xen");
        Assert.NotNull(file);
        var bank = Fsb3AudioBank.Probe(file);
        Assert.NotNull(bank);
        var sample = bank.Samples
            .Where(static item => item.Codec == Fsb3AudioCodec.Xma1)
            .MinBy(static item => item.CompressedSize);
        Assert.NotNull(sample);
        using var temp = new TempDirectory();

        var output = Fsb3AudioBank.ConvertSingleToWav(
            file, sample.Index, temp.Path);

        Assert.NotNull(output);
        var wave = File.ReadAllBytes(output);
        Assert.True(RiffWaveReader.TryRead(wave, out var parsed));
        Assert.Equal(sample.SampleRate, parsed.SampleRate);
        Assert.Equal(sample.Channels, parsed.Channels);
        Assert.True(parsed.DataLength > 0);
    }

    private string[] FindCorpusBanks()
    {
        return new[]
            {
                Project8Ps3Build,
                Project8XenBuild,
                ProvingGroundPs3Build,
                ProvingGroundXenBuild
            }
            .SelectMany(build => paths.FindSampleFiles(build, "*"))
            .Where(static path => path.EndsWith(".fsb.ps3", StringComparison.OrdinalIgnoreCase)
                                  || path.EndsWith(".fsb.xen", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void Increment<TKey>(Dictionary<TKey, int> values, TKey key) where TKey : notnull
    {
        values.TryGetValue(key, out var count);
        values[key] = count + 1;
    }

    private static byte[] BuildMixedBank()
    {
        var mp3Payload = new byte[16];
        mp3Payload[0] = 0xFF;
        mp3Payload[1] = 0xFB;
        mp3Payload[2] = 0x90;
        mp3Payload[3] = 0x64;
        var xmaPayload = new byte[4096];
        xmaPayload[0] = 0x08;
        xmaPayload[Fsb3AudioBank.XmaPacketSize] = 0x08;

        const int mp3HeaderSize = Fsb3AudioBank.SampleHeaderSize;
        const int xmaHeaderSize = 112;
        const int padding = 4;
        const int headerSectionSize = mp3HeaderSize + xmaHeaderSize + padding;
        var data = new byte[Fsb3AudioBank.MainHeaderSize + headerSectionSize
                            + mp3Payload.Length + xmaPayload.Length];
        "FSB3"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8, 4), headerSectionSize);
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(12, 4), (uint)(mp3Payload.Length + xmaPayload.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16, 4), 0x00030001);

        WriteSampleHeader(
            data.AsSpan(Fsb3AudioBank.MainHeaderSize, mp3HeaderSize),
            "theme.mp3",
            44_100,
            mp3Payload.Length,
            44_100,
            2,
            0x00000240);
        WriteSampleHeader(
            data.AsSpan(Fsb3AudioBank.MainHeaderSize + mp3HeaderSize, xmaHeaderSize),
            "voice.xma",
            48_000,
            xmaPayload.Length,
            48_000,
            1,
            0x01100020);

        var xmaHeader = data.AsSpan(Fsb3AudioBank.MainHeaderSize + mp3HeaderSize, xmaHeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[80..84], 0x20);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[84..88], 1234);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[88..92], 0x23);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[92..96], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[96..100], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[100..104], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[104..108], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(xmaHeader[108..112], 32);

        var dataOffset = Fsb3AudioBank.MainHeaderSize + headerSectionSize;
        mp3Payload.CopyTo(data.AsSpan(dataOffset));
        xmaPayload.CopyTo(data.AsSpan(dataOffset + mp3Payload.Length));
        return data;
    }

    private static void WriteSampleHeader(
        Span<byte> header,
        string name,
        int decodedSamples,
        int compressedSize,
        int sampleRate,
        ushort channels,
        uint mode)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(header[..2], (ushort)header.Length);
        Encoding.Latin1.GetBytes(name).CopyTo(header[2..32]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..36], (uint)decodedSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..40], (uint)compressedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], (uint)(decodedSamples - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], mode);
        BinaryPrimitives.WriteInt32LittleEndian(header[52..56], sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[56..58], 255);
        BinaryPrimitives.WriteUInt16LittleEndian(header[60..62], 255);
        BinaryPrimitives.WriteUInt16LittleEndian(header[62..64], channels);
        BinaryPrimitives.WriteSingleLittleEndian(header[64..68], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header[68..72], 10_000f);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-fsb3-{Guid.NewGuid():N}");
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
