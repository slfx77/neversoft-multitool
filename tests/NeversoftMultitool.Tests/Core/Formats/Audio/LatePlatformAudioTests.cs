using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class LatePlatformAudioTests(TestPaths paths)
{
    private const string Project8Ps3 = "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";
    private const string ProvingGroundPs3 = "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)";
    private const string Project8Xen = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string ProvingGroundXen = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";

    [Fact]
    public void Probe_StrictRawMp3ReportsAuthoredLayoutAndTimeline()
    {
        var data = BuildMp3(frameCount: 3, channelMode: 3);
        var probe = LatePlatformAudio.Probe("voice.wav.ps3", data);

        Assert.NotNull(probe);
        Assert.Equal(LatePlatformAudioKind.Ps3MpegLayer3, probe.Kind);
        Assert.Equal(44_100, probe.SampleRate);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(3, probe.FrameOrPacketCount);
        Assert.Equal(3456, probe.TimelineSampleCount);
        Assert.Equal(3456d / 44_100d, probe.DurationSeconds!.Value, precision: 12);
        Assert.Equal(probe.DurationSeconds, AudioDurationProbe.Probe("PS3 MP3", data));

        Assert.Null(LatePlatformAudio.Probe("voice.mp3", data));
        Assert.Null(LatePlatformAudio.Probe("voice.wav.ps3", [.. data, 0]));
        Assert.Null(LatePlatformAudio.Probe("voice.wav.ps3", data[..(data.Length / 3 * 2)]));

        var id3 = new byte[data.Length + 10];
        "ID3"u8.CopyTo(id3);
        data.CopyTo(id3.AsSpan(10));
        Assert.Null(LatePlatformAudio.Probe("voice.wav.ps3", id3));

        var changedLayout = (byte[])data.Clone();
        var secondFrame = GetMpegFrameLength();
        changedLayout[secondFrame + 3] &= 0x3f;
        Assert.Null(LatePlatformAudio.Probe("voice.wav.ps3", changedLayout));
    }

    [Fact]
    public void Probe_StrictXma1ValidatesRiffPacketsAndSeekTable()
    {
        var data = BuildXma(sampleRate: 48_000, channels: 2, packetCount: 2);
        var probe = LatePlatformAudio.Probe("effect.wav.xen", data);

        Assert.NotNull(probe);
        Assert.Equal(LatePlatformAudioKind.Xbox360Xma1, probe.Kind);
        Assert.Equal(48_000, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(2, probe.FrameOrPacketCount);
        Assert.Null(probe.DurationSeconds);
        Assert.Null(AudioDurationProbe.Probe("XMA1", data));

        static void Reject(byte[] bytes) =>
            Assert.Null(LatePlatformAudio.Probe("effect.wav.xen", bytes));

        var malformed = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(malformed.AsSpan(20), 1);
        Reject(malformed);

        malformed = (byte[])data.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(56), 2049);
        Reject(malformed);

        malformed = (byte[])data.Clone();
        malformed[60 + 3] = 1;
        Reject(malformed);

        malformed = (byte[])data.Clone();
        var seekOffset = 60 + 4096;
        BinaryPrimitives.WriteUInt32LittleEndian(malformed.AsSpan(seekOffset + 20), 513);
        Reject(malformed);

        Reject(data[..^1]);
        Assert.Null(LatePlatformAudio.Probe("effect.wav.ps3", data));
    }

    [Fact]
    public void Probe_StrictSingleSampleFsb3DistinguishesMp3AndImaAdpcm()
    {
        var mp3 = LatePlatformAudio.Probe("voice.wav.ps3", BuildPs3FsbMp3());
        Assert.NotNull(mp3);
        Assert.Equal(LatePlatformAudioKind.Ps3Fsb3, mp3.Kind);
        Assert.Equal("MPEG Layer III", mp3.CodecName);
        Assert.Equal(44_100, mp3.SampleRate);
        Assert.Equal(1, mp3.Channels);

        var imaData = BuildPs3FsbIma();
        var ima = LatePlatformAudio.Probe("effect.wav.ps3", imaData);
        Assert.NotNull(ima);
        Assert.Equal("IMA ADPCM", ima.CodecName);
        Assert.Equal(64d / 48_000d, ima.DurationSeconds!.Value, precision: 12);
        Assert.Equal(ima.DurationSeconds, AudioDurationProbe.Probe("PS3 FSB3", imaData));

        imaData[24 + 80 + 2] = 89; // IMA step-table index is 0..88.
        Assert.Null(LatePlatformAudio.Probe("effect.wav.ps3", imaData));

        var mp3Data = BuildPs3FsbMp3();
        mp3Data[24 + 80] = 1; // The measured extended header tail is all zero.
        Assert.Null(LatePlatformAudio.Probe("voice.wav.ps3", mp3Data));
    }

    [Fact]
    public void Routing_IsContentGatedAcrossProbeCliAndGui()
    {
        using var temp = new TempDirectory();
        var mp3 = Path.Combine(temp.Path, "voice.wav.PS3");
        var xma = Path.Combine(temp.Path, "effect.WAV.XEN");
        var foreignPs3 = Path.Combine(temp.Path, "foreign.wav.ps3");
        var foreignXen = Path.Combine(temp.Path, "foreign.wav.xen");
        File.WriteAllBytes(mp3, BuildMp3());
        File.WriteAllBytes(xma, BuildXma());
        File.WriteAllBytes(foreignPs3, "RIFF foreign"u8.ToArray());
        File.WriteAllBytes(foreignXen, "FSB3 foreign"u8.ToArray());

        Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(mp3).Support);
        Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(xma).Support);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, FormatProbe.ProbeAudio(foreignPs3).Support);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, FormatProbe.ProbeAudio(foreignXen).Support);
        Assert.Equal(
            [mp3, xma],
            AudioCommand.SelectNamedCandidatePaths([mp3, xma, foreignPs3, foreignXen]));

        Assert.NotNull(LatePlatformAudio.Probe("archive/voice.wav.ps3", BuildMp3()));
        Assert.Null(LatePlatformAudio.Probe(
            "archive/voice.wav.ps3", "foreign"u8.ToArray()));
    }

    [Fact]
    public void ConvertToWav_StagesBytesAndPreservesDestinationOnFailure()
    {
        using var temp = new TempDirectory();
        var mp3 = BuildMp3();
        string? stagedInput = null;

        bool Succeed(string inputPath, string outputPath, out string error)
        {
            stagedInput = inputPath;
            Assert.EndsWith(".mp3", inputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(mp3, File.ReadAllBytes(inputPath));
            File.WriteAllBytes(outputPath, BuildWave(44_100, 1));
            error = "";
            return true;
        }

        var result = LatePlatformAudio.ConvertToWav(
            mp3, "voice.wav.ps3", "voice", temp.Path, Succeed);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(stagedInput);
        Assert.False(File.Exists(stagedInput));

        var destination = Path.Combine(temp.Path, "effect.wav");
        var original = BuildWave(48_000, 2);
        File.WriteAllBytes(destination, original);
        bool Fail(string inputPath, string outputPath, out string error)
        {
            File.WriteAllBytes(outputPath, "partial"u8.ToArray());
            error = "decode failed";
            return false;
        }

        result = LatePlatformAudio.ConvertToWav(
            BuildXma(), "effect.wav.xen", "effect", temp.Path, Fail);
        Assert.False(result.Success);
        Assert.Equal("decode failed", result.ErrorMessage);
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(2, Directory.GetFiles(temp.Path).Length);

        bool DecodeFsb(string inputPath, string outputPath, out string error)
        {
            Assert.EndsWith(".fsb", inputPath, StringComparison.OrdinalIgnoreCase);
            File.WriteAllBytes(outputPath, BuildWave(48_000, 1));
            error = "";
            return true;
        }

        result = LatePlatformAudio.ConvertToWav(
            BuildPs3FsbIma(), "effect.wav.ps3", "bank", temp.Path, DecodeFsb);
        Assert.True(result.Success, result.ErrorMessage);
    }

    [Fact]
    public void OutputStemPlanner_RemovesTheCompoundPlatformSuffixAndStillDisambiguates()
    {
        var stems = AudioOutputStemPlanner.Plan(
        [
            new AudioOutputStemInput("voice.wav.ps3", "a/voice.wav.ps3"),
            new AudioOutputStemInput("voice.wav.xen", "b/voice.wav.xen")
        ]);

        Assert.Equal(2, stems.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(stems, static stem => Assert.StartsWith("voice_", stem));
        Assert.DoesNotContain(stems, static stem => stem.Contains(".wav", StringComparison.OrdinalIgnoreCase));
    }

    [CorpusFact]
    public void Corpus_All6534CompoundFilesAreExactlyClassified()
    {
        var ps3 = new[] { Project8Ps3, ProvingGroundPs3 }
            .SelectMany(build => paths.FindSampleFiles(build, "*.wav.ps3"))
            .ToArray();
        var xen = new[] { Project8Xen, ProvingGroundXen }
            .SelectMany(build => paths.FindSampleFiles(build, "*.wav.xen"))
            .ToArray();
        Assert.Equal(3759, ps3.Length);
        Assert.Equal(2775, xen.Length);
        Assert.Equal(65_231_394L, ps3.Concat(xen).Sum(static path => new FileInfo(path).Length));

        var probes = ps3.Concat(xen).Select(path =>
        {
            var probe = LatePlatformAudio.Probe(path);
            Assert.True(probe != null, path);
            return probe;
        }).ToArray();
        Assert.Equal(6534, probes.Length);
        Assert.Equal(3530, probes.Count(static p => p.Kind == LatePlatformAudioKind.Ps3MpegLayer3));
        Assert.Equal(229, probes.Count(static p => p.Kind == LatePlatformAudioKind.Ps3Fsb3));
        Assert.Equal(2775, probes.Count(static p => p.Kind == LatePlatformAudioKind.Xbox360Xma1));
        var mp3 = probes.Where(static p => p.Kind == LatePlatformAudioKind.Ps3MpegLayer3).ToArray();
        Assert.Equal(117_786, mp3.Sum(static p => p.FrameOrPacketCount));
        Assert.Equal(121_280_832L, mp3.Sum(static p => p.TimelineSampleCount!.Value));
        Assert.Equal(3070, mp3.Count(static p => p.Channels == 1));
        Assert.Equal(460, mp3.Count(static p => p.Channels == 2));
        AssertRateCounts(mp3, (48_000, 2121), (32_000, 558), (44_100, 388),
            (24_000, 368), (22_050, 95));

        var fsb = probes.Where(static p => p.Kind == LatePlatformAudioKind.Ps3Fsb3).ToArray();
        Assert.Equal(160, fsb.Count(static p => p.CodecName == "MPEG Layer III"));
        Assert.Equal(69, fsb.Count(static p => p.CodecName == "IMA ADPCM"));
        Assert.Equal(213, fsb.Count(static p => p.Channels == 1));
        Assert.Equal(16, fsb.Count(static p => p.Channels == 2));
        AssertRateCounts(fsb, (48_000, 178), (44_100, 34), (24_000, 14), (22_050, 3));

        var xma = probes.Where(static p => p.Kind == LatePlatformAudioKind.Xbox360Xma1).ToArray();
        Assert.Equal(14_807, xma.Sum(static p => p.FrameOrPacketCount));
        Assert.Equal(2415, xma.Count(static p => p.Channels == 1));
        Assert.Equal(360, xma.Count(static p => p.Channels == 2));
        Assert.Equal(2618, xma.Count(static p => p.LoopCount == 0));
        Assert.Equal(157, xma.Count(static p => p.LoopCount == 255));
        AssertRateCounts(xma, (48_000, 2207), (44_100, 566), (22_050, 2));
    }

    [CorpusFact]
    public void Corpus_EachLateSingleStreamLayoutDecodesWithFfmpeg()
    {
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg is not installed");
        var files = new[] { Project8Ps3, ProvingGroundPs3 }
            .SelectMany(build => paths.FindSampleFiles(build, "*.wav.ps3"))
            .Concat(new[] { Project8Xen, ProvingGroundXen }
                .SelectMany(build => paths.FindSampleFiles(build, "*.wav.xen")))
            .Select(path => (Path: path, Probe: LatePlatformAudio.Probe(path)))
            .Where(static item => item.Probe != null)
            .GroupBy(static item => (
                item.Probe!.Kind,
                item.Probe.CodecName,
                item.Probe.SampleRate,
                item.Probe.Channels,
                item.Probe.LoopCount))
            .Select(static group => group.MinBy(item => new FileInfo(item.Path).Length))
            .ToArray();
        Assert.Equal(27, files.Length);

        using var temp = new TempDirectory();
        for (var index = 0; index < files.Length; index++)
        {
            var result = LatePlatformAudio.ConvertToWav(
                files[index].Path, $"layout-{index}", temp.Path);
            Assert.True(result.Success, result.ErrorMessage);
        }
    }

    private static void AssertRateCounts(
        IEnumerable<LatePlatformAudioProbeResult> probes,
        params (int Rate, int Count)[] expected)
    {
        var actual = probes.GroupBy(static p => p.SampleRate!.Value)
            .ToDictionary(static group => group.Key, static group => group.Count());
        Assert.Equal(expected.Length, actual.Count);
        foreach (var (rate, count) in expected)
            Assert.Equal(count, actual[rate]);
    }

    private static byte[] BuildMp3(int frameCount = 3, int channelMode = 3)
    {
        const uint baseHeader = 0xfffb9000; // MPEG-1 Layer III, 128 kbps, 44.1 kHz
        var header = baseHeader | ((uint)channelMode << 6);
        var frameLength = 144 * 128_000 / 44_100;
        var data = new byte[frameCount * frameLength];
        for (var frame = 0; frame < frameCount; frame++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(frame * frameLength), header);
        return data;
    }

    private static int GetMpegFrameLength()
    {
        return 144 * 128_000 / 44_100;
    }

    private static byte[] BuildXma(int sampleRate = 48_000, int channels = 2, int packetCount = 2)
    {
        var encodedSize = packetCount * 2048;
        var seekSize = (packetCount + 2) * sizeof(uint);
        var data = new byte[60 + encodedSize + 8 + seekSize];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 32);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 0x0165);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(24), 0x10d6);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), 1);
        data[31] = 2;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 12_000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(36), (uint)sampleRate);
        data[49] = (byte)channels;
        "data"u8.CopyTo(data.AsSpan(52));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), (uint)encodedSize);

        var seekOffset = 60 + encodedSize;
        "seek"u8.CopyTo(data.AsSpan(seekOffset));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(seekOffset + 4), (uint)seekSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(seekOffset + 8), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(seekOffset + 12), (uint)packetCount);
        for (var packet = 0; packet < packetCount; packet++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                data.AsSpan(seekOffset + 16 + packet * 4), (uint)(packet * 512));
        }
        return data;
    }

    private static byte[] BuildPs3FsbMp3()
    {
        var payload = BuildMp3();
        var encodedSize = (payload.Length + 15) & ~15;
        var data = BuildPs3FsbHeader(
            headerSize: 88,
            decodedSamples: 3456,
            encodedSize,
            sampleRate: 44_100,
            channels: 1,
            mode: 0x00000220);
        payload.CopyTo(data.AsSpan(24 + 88));
        return data;
    }

    private static byte[] BuildPs3FsbIma()
    {
        const int encodedSize = 36;
        return BuildPs3FsbHeader(
            headerSize: 80,
            decodedSamples: 64,
            encodedSize,
            sampleRate: 48_000,
            channels: 1,
            mode: 0x00400020);
    }

    private static byte[] BuildPs3FsbHeader(
        int headerSize,
        int decodedSamples,
        int encodedSize,
        int sampleRate,
        ushort channels,
        uint mode)
    {
        var data = new byte[24 + headerSize + encodedSize];
        "FSB3"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)headerSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)encodedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 0x00030001);

        var header = data.AsSpan(24, headerSize);
        BinaryPrimitives.WriteUInt16LittleEndian(header, 80);
        "sample.wav"u8.CopyTo(header[2..]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[32..], (uint)decodedSamples);
        BinaryPrimitives.WriteUInt32LittleEndian(header[36..], (uint)encodedSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..], (uint)(decodedSamples - 1));
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..], mode);
        BinaryPrimitives.WriteInt32LittleEndian(header[52..], sampleRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header[56..], 255);
        BinaryPrimitives.WriteInt16LittleEndian(header[58..], 128);
        BinaryPrimitives.WriteUInt16LittleEndian(header[60..], 255);
        BinaryPrimitives.WriteUInt16LittleEndian(header[62..], channels);
        BinaryPrimitives.WriteSingleLittleEndian(header[64..], 1f);
        BinaryPrimitives.WriteSingleLittleEndian(header[68..], 10_000f);
        return data;
    }

    private static byte[] BuildWave(int sampleRate, int channels)
    {
        const int frames = 32;
        var dataBytes = frames * channels * sizeof(short);
        var data = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        "WAVEfmt "u8.CopyTo(data.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(32), (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), (uint)dataBytes);
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-late-audio-{Guid.NewGuid():N}");
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
