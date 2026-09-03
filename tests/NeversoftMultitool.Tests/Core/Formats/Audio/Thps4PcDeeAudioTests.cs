using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class Thps4PcDeeAudioTests(TestPaths paths)
{
    private const string PcBuild = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";
    private const string Ps2Build = "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";

    [Fact]
    public void Probe_ExactCarrierReportsAuthoredAudioAndTimeline()
    {
        var data = BuildDee(sampleRate: 22_050, audioFlags: 0x5000);

        var probe = Thps4PcDeeAudio.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(22_050, probe.SampleRate);
        Assert.Equal(1, probe.Channels);
        Assert.Equal(4U, probe.FrameCount);
        Assert.Equal(4d / 15d, probe.DurationSeconds, precision: 12);
        Assert.Equal(16U, probe.LargestFrameSize);
        Assert.Equal(4096U, probe.MaximumDecodedAudioSize);
        Assert.Equal(probe.DurationSeconds, AudioDurationProbe.Probe("DEE", data));

        var stereo = Thps4PcDeeAudio.Probe(BuildDee(sampleRate: 44_100, audioFlags: 0x7000));
        Assert.NotNull(stereo);
        Assert.Equal(2, stereo.Channels);
    }

    [Fact]
    public void Probe_FailsClosedOnEveryStructuralIdentity()
    {
        static void Reject(byte[] data) => Assert.Null(Thps4PcDeeAudio.Probe(data));

        Reject("BIKi"u8.ToArray());

        var data = BuildDee();
        data[3] = (byte)'f';
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)data.Length - 9);
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 3);
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 640);
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), 48_000);
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), 0x1000);
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 76); // first frame must be a key frame
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), 124); // sentinel must be exact EOF
        Reject(data);

        data = BuildDee();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 15); // indexed maximum is 16
        Reject(data);
    }

    [Fact]
    public void Routing_ContentGatesDeeForProbeAndCli()
    {
        using var temp = new TempDirectory();
        var valid = Path.Combine(temp.Path, "Snd12345678.DEE");
        var foreign = Path.Combine(temp.Path, "foreign.dee");
        File.WriteAllBytes(valid, BuildDee());
        File.WriteAllBytes(foreign, "foreign metadata"u8.ToArray());

        var supported = FormatProbe.ProbeAudio(valid);
        var unsupported = FormatProbe.ProbeAudio(foreign);
        Assert.Equal(FormatProbe.FormatSupport.Supported, supported.Support);
        Assert.Equal("THPS4 PC Bink-DCT Sound", supported.FormatName);
        Assert.Contains("44100", supported.UnsupportedReason ?? "", StringComparison.Ordinal);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, unsupported.Support);
        Assert.Contains("BIKi DEE", unsupported.UnsupportedReason ?? "", StringComparison.Ordinal);

        Assert.Equal([valid], AudioCommand.SelectNamedCandidatePaths([valid, foreign]));
    }

    [Fact]
    public void ConvertToWav_StagesBytesAndPublishesOnlyAValidatedPcmWave()
    {
        using var temp = new TempDirectory();
        var data = BuildDee(sampleRate: 22_050, audioFlags: 0x7000);
        string? stagedInput = null;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            stagedInput = inputPath;
            Assert.EndsWith(".dee", inputPath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(data, File.ReadAllBytes(inputPath));
            File.WriteAllBytes(outputPath, BuildWave(sampleRate: 22_050, channels: 2));
            error = "";
            return true;
        }

        var result = Thps4PcDeeAudio.ConvertToWav(
            data, "voice", temp.Path, Transcode);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(File.Exists(Path.Combine(temp.Path, "voice.wav")));
        Assert.NotNull(stagedInput);
        Assert.False(File.Exists(stagedInput));
    }

    [Fact]
    public void ConvertToWav_FailurePreservesDestinationAndCleansPartialStage()
    {
        using var temp = new TempDirectory();
        var destination = Path.Combine(temp.Path, "voice.wav");
        var original = BuildWave();
        File.WriteAllBytes(destination, original);

        bool FailAfterPartialWrite(string inputPath, string outputPath, out string error)
        {
            File.WriteAllBytes(outputPath, "partial"u8.ToArray());
            error = "decode failed";
            return false;
        }

        var result = Thps4PcDeeAudio.ConvertToWav(
            BuildDee(), "voice", temp.Path, FailAfterPartialWrite);

        Assert.False(result.Success);
        Assert.Equal("decode failed", result.ErrorMessage);
        Assert.Equal(original, File.ReadAllBytes(destination));
        Assert.Equal(destination, Assert.Single(Directory.GetFiles(temp.Path)));
    }

    [Fact]
    public void ConvertToWav_RejectsMalformedCarrierBeforeCallingFfmpeg()
    {
        using var temp = new TempDirectory();
        var called = false;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            called = true;
            error = "should not run";
            return false;
        }

        var result = Thps4PcDeeAudio.ConvertToWav(
            "foreign"u8.ToArray(), "foreign", temp.Path, Transcode);

        Assert.True(result.Skipped);
        Assert.False(called);
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    [CorpusFact]
    public void Corpus_All3612FilesConsumeExactlyAndMatchPs2BasenameChecksums()
    {
        var files = paths.FindSampleFiles(PcBuild, "*.dee")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Assert.Equal(3612, files.Length);
        Assert.Equal(57_784_660L, files.Sum(static file => new FileInfo(file).Length));

        var probes = files.Select(file =>
        {
            var probe = Thps4PcDeeAudio.Probe(file);
            Assert.NotNull(probe);
            Assert.Equal(FormatProbe.FormatSupport.Supported, FormatProbe.ProbeAudio(file).Support);

            var baseName = Path.GetFileNameWithoutExtension(file);
            Assert.Equal(11, baseName.Length);
            Assert.StartsWith("Snd", baseName, StringComparison.OrdinalIgnoreCase);
            Assert.True(uint.TryParse(
                baseName.AsSpan(3),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out _));
            Assert.Equal(
                $"Snd{baseName[3]}",
                new DirectoryInfo(Path.GetDirectoryName(file)!).Name,
                ignoreCase: true);
            return probe;
        }).ToArray();

        Assert.Equal(116_881L, probes.Sum(static probe => (long)probe.FrameCount));
        Assert.Equal(3609, probes.Count(static probe => probe.SampleRate == 44_100));
        Assert.Equal(2, probes.Count(static probe => probe.SampleRate == 22_050));
        Assert.Single(probes, static probe => probe.SampleRate == 11_025);
        Assert.Equal(3611, probes.Count(static probe => probe.Channels == 1));
        Assert.Single(probes, static probe => probe.Channels == 2);

        // The PS2 build provides the unhashed source names. Every PC filename
        // is exactly QbKey(lowercase PS2 basename), so DEE needs no external
        // index; the Snd0..SndF directory is only a first-nibble shard.
        var ps2SoundHashes = paths.FindSampleFiles(Ps2Build, "*")
            .Where(static file =>
            {
                var normalized = file.Replace('\\', '/');
                return string.IsNullOrEmpty(Path.GetExtension(file))
                           && normalized.Contains(
                               "/STREAMS/STREAMS/Streams/",
                               StringComparison.OrdinalIgnoreCase)
                       || Path.GetExtension(file).Equals(".vag", StringComparison.OrdinalIgnoreCase);
            })
            .Select(static file => QbKey.HashLower(Path.GetFileNameWithoutExtension(file)))
            .ToHashSet();
        Assert.Equal(4105, ps2SoundHashes.Count);

        Assert.All(files, file =>
        {
            var hash = uint.Parse(
                Path.GetFileNameWithoutExtension(file).AsSpan(3),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture);
            Assert.Contains(hash, ps2SoundHashes);
        });
    }

    /// <summary>
    ///     One concat decode per authored rate/channel layout keeps this an
    ///     exhaustive codec smoke test without paying for 3,612 process starts.
    /// </summary>
    [CorpusFact]
    public void Corpus_All3612FilesDecodeTheirBinkDctTrackWithFfmpeg()
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        Assert.SkipWhen(ffmpeg == null, "ffmpeg is not installed");

        var groups = paths.FindSampleFiles(PcBuild, "*.dee")
            .Select(file => (File: file, Probe: Thps4PcDeeAudio.Probe(file)))
            .ToArray();
        Assert.Equal(3612, groups.Length);
        Assert.DoesNotContain(groups, static item => item.Probe == null);

        foreach (var group in groups.GroupBy(static item =>
                     (item.Probe!.SampleRate, item.Probe.Channels)))
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = ffmpeg!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in new[]
                     {
                         "-v", "error", "-xerror", "-f", "concat", "-safe", "0",
                         "-protocol_whitelist", "file,pipe", "-i", "pipe:0",
                         "-map", "0:a:0", "-vn", "-f", "null", "-"
                     })
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            foreach (var item in group.OrderBy(static item => item.File, StringComparer.OrdinalIgnoreCase))
            {
                var escaped = item.File.Replace('\\', '/').Replace("'", "'\\''");
                process.StandardInput.WriteLine($"file '{escaped}'");
            }

            process.StandardInput.Close();
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail($"ffmpeg timed out for {group.Key}");
            }

            _ = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            Assert.True(
                process.ExitCode == 0,
                $"ffmpeg rejected {group.Count()} DEE files at {group.Key}: {stderr}");
        }
    }

    private static byte[] BuildDee(int sampleRate = 44_100, ushort audioFlags = 0x5000)
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
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), checked((ushort)sampleRate));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), audioFlags);

        // Table end is 76. The low bit on the first offset marks its key frame.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(68), 112);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), length);
        return data;
    }

    private static byte[] BuildWave(int sampleRate = 44_100, int channels = 1)
    {
        const int frames = 32;
        var dataBytes = checked(frames * channels * sizeof(short));
        var data = new byte[44 + dataBytes];
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)(data.Length - 8));
        "WAVE"u8.CopyTo(data.AsSpan(8));
        "fmt "u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), checked((ushort)channels));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), checked((uint)sampleRate));
        BinaryPrimitives.WriteUInt32LittleEndian(
            data.AsSpan(28), checked((uint)(sampleRate * channels * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.AsSpan(32), checked((ushort)(channels * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(34), 16);
        "data"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), checked((uint)dataBytes));
        return data;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-thps4-dee-{Guid.NewGuid():N}");
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
