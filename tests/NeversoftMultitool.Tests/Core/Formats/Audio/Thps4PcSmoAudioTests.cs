using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Thps4PcSmoAudioTests(TestPaths paths)
{
    private const string PcBuild = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    [Fact]
    public void Probe_RequiresTheMeasuredSmoAudioOnlyProfile()
    {
        var probe = Thps4PcSmoAudio.Probe(BuildSmo());

        Assert.NotNull(probe);
        Assert.Equal(48_000, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(4U, probe.FrameCount);
        Assert.Equal(4d / 15d, probe.DurationSeconds, precision: 12);
        Assert.Equal(probe.DurationSeconds, AudioDurationProbe.Probe("SMO", BuildSmo()));

        var mono = BuildSmo();
        BinaryPrimitives.WriteUInt16LittleEndian(mono.AsSpan(50), 0x5000);
        Assert.Null(Thps4PcSmoAudio.Probe(mono));

        var movie = BuildSmo();
        BinaryPrimitives.WriteUInt32LittleEndian(movie.AsSpan(20), 640);
        Assert.Null(Thps4PcSmoAudio.Probe(movie));

        var truncated = BuildSmo()[..^1];
        Assert.Null(Thps4PcSmoAudio.Probe(truncated));
    }

    [Fact]
    public void Routing_ClaimsOnlyStrictSmoNamesAndNotGenericBinkMovies()
    {
        using var temp = new TempDirectory();
        var valid = Path.Combine(temp.Path, "soundtrack.SMO");
        var foreign = Path.Combine(temp.Path, "movie.smo");
        var renamed = Path.Combine(temp.Path, "soundtrack.bik");
        File.WriteAllBytes(valid, BuildSmo());
        File.WriteAllBytes(foreign, "BIKf movie"u8.ToArray());
        File.WriteAllBytes(renamed, BuildSmo());

        Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(valid).Support);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, FormatProbe.ProbeAudio(foreign).Support);
        Assert.Equal([valid], AudioCommand.SelectNamedCandidatePaths([valid, foreign, renamed]));
    }

    [Fact]
    public void ConvertToWav_StagesArchiveBytesAndPublishesValidatedPcmAtomically()
    {
        using var temp = new TempDirectory();
        var data = BuildSmo();
        string? stagedInput = null;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            stagedInput = inputPath;
            Assert.EndsWith(".smo", inputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(data, File.ReadAllBytes(inputPath));
            File.WriteAllBytes(outputPath, BuildWave(48_000, 2));
            error = "";
            return true;
        }

        var result = Thps4PcSmoAudio.ConvertToWav(data, "music", temp.Path, Transcode);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(File.Exists(Path.Combine(temp.Path, "music.wav")));
        Assert.NotNull(stagedInput);
        Assert.False(File.Exists(stagedInput));
    }

    [CorpusFact]
    public void Corpus_All47SmoFilesMatchTheExactProfile()
    {
        var files = paths.FindSampleFiles(PcBuild, "*.smo").ToArray();
        Assert.Equal(47, files.Length);
        Assert.Equal(163_764_932L, files.Sum(static path => new FileInfo(path).Length));

        var probes = files.Select(path =>
        {
            var probe = Thps4PcSmoAudio.Probe(path);
            Assert.NotNull(probe);
            Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(path).Support);
            return probe;
        }).ToArray();

        Assert.Equal(133_215L, probes.Sum(static probe => (long)probe.FrameCount));
        Assert.Equal(45, probes.Count(static probe => probe.SampleRate == 48_000));
        Assert.Equal(2, probes.Count(static probe => probe.SampleRate == 44_100));
        Assert.All(probes, static probe => Assert.Equal(2, probe.Channels));
        Assert.Equal(45, probes.Count(static probe => probe.MaximumDecodedAudioSize == 145_920));
        Assert.Equal(2, probes.Count(static probe => probe.MaximumDecodedAudioSize == 138_240));
    }

    [CorpusFact]
    public void Corpus_EachAuthoredSmoLayoutDecodesWithFfmpeg()
    {
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg is not installed");
        var representatives = paths.FindSampleFiles(PcBuild, "*.smo")
            .Select(path => (Path: path, Probe: Thps4PcSmoAudio.Probe(path)))
            .Where(static item => item.Probe != null)
            .GroupBy(static item => (item.Probe!.SampleRate, item.Probe.Channels))
            .Select(static group => group.MinBy(item => new FileInfo(item.Path).Length))
            .ToArray();
        Assert.Equal(2, representatives.Length);

        using var temp = new TempDirectory();
        for (var index = 0; index < representatives.Length; index++)
        {
            var item = representatives[index];
            var result = Thps4PcSmoAudio.ConvertToWav(
                item.Path, $"layout-{index}", temp.Path);
            Assert.True(result.Success, result.ErrorMessage);
        }
    }

    private static byte[] BuildSmo()
    {
        const int length = 128;
        const uint frameCount = 4;
        var data = new byte[length];
        "BIKi"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 145_920);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), 0x7000);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(68), 112);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), length);
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
                System.IO.Path.GetTempPath(), $"nmt-thps4-smo-{Guid.NewGuid():N}");
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
