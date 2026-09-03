using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class PsmfAudioExtractorTests(TestPaths paths)
{
    private static readonly int[] AtracFrameSizes = [568, 752];

    private const string RemixBuild = "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";
    private const string Project8FinalBuild = "Tony Hawk's Project 8 (2006-10-14, PSP - Final)";
    private const string Project8Rev1Build = "Tony Hawk's Project 8 (2007-2-16, PSP - Rev1)";

    [Fact]
    public void Probe_ConcatenatesPesPayloadsAndConsumesWholeAtracFrames()
    {
        var data = PsmfTestBuilder.Create(frameCount: 3, frameSize: 568, splitPayload: true);

        var probe = PsmfAudioExtractor.Probe(data);

        Assert.NotNull(probe);
        Assert.True(probe.HasAudio);
        Assert.Equal(0, probe.PrivateStreamId);
        Assert.Equal(2, probe.PacketCount);
        Assert.Equal(3, probe.FrameCount);
        Assert.Equal(568, probe.FrameSize);
        Assert.Equal(44_100, probe.SampleRate);
        Assert.Equal(2, probe.Channels);
        Assert.Equal(3 * 2048 / 44_100.0, probe.DurationSeconds);
    }

    [Fact]
    public void Probe_MapsAtracLayoutIdToItsChannelCount()
    {
        var data = PsmfTestBuilder.Create(
            frameCount: 1,
            frameSize: 568,
            channelLayoutId: 5);

        var probe = PsmfAudioExtractor.Probe(data);

        Assert.NotNull(probe);
        Assert.Equal(6, probe.Channels);
    }

    [Fact]
    public void TryWriteOma_StripsEightBytePsmfFrameHeaders()
    {
        var data = PsmfTestBuilder.Create(frameCount: 2, frameSize: 752, splitPayload: true);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"nmt-psmf-audio-{Guid.NewGuid():N}.oma");

        try
        {
            var success = PsmfAudioExtractor.TryWriteOma(data, outputPath, out var probe, out var error);

            Assert.True(success, error);
            Assert.True(probe.HasAudio);
            var oma = File.ReadAllBytes(outputPath);
            Assert.Equal(96 + 2 * (752 - 8), oma.Length);
            Assert.True(oma.AsSpan(0, 4).SequenceEqual("EA3\0"u8));
            Assert.Equal(96, oma[5]);
            Assert.Equal(0xFF, oma[6]);
            Assert.Equal(0xFF, oma[7]);
            Assert.Equal(1, oma[32]);
            Assert.Equal(0x285C, BinaryPrimitives.ReadUInt16BigEndian(oma.AsSpan(34)));

            var expectedBodies = PsmfTestBuilder.GetFrameBodies(data);
            Assert.Equal(expectedBodies, oma.AsSpan(96).ToArray());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Probe_VideoOnlyPsmfReportsNoAudioWithoutInventingOma()
    {
        var data = PsmfTestBuilder.CreateVideoOnly();
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"nmt-psmf-silent-{Guid.NewGuid():N}.oma");

        try
        {
            var probe = PsmfAudioExtractor.Probe(data);
            var success = PsmfAudioExtractor.TryWriteOma(data, outputPath, out var writtenProbe, out var error);

            Assert.NotNull(probe);
            Assert.False(probe.HasAudio);
            Assert.True(success, error);
            Assert.False(writtenProbe.HasAudio);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void Probe_AllowsDeclaredMpegPsStuffingAndPaddingButRejectsUnframedGarbage()
    {
        var framed = PsmfTestBuilder.CreateVideoOnlyWithStuffingAndPadding();
        var garbage = PsmfTestBuilder.CreateVideoOnlyWithTrailingGarbage();

        var framedProbe = PsmfAudioExtractor.Probe(framed);

        Assert.NotNull(framedProbe);
        Assert.False(framedProbe.HasAudio);
        Assert.Null(PsmfAudioExtractor.Probe(garbage));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToWav_PathAndArchiveBytesPublishValidatedPcm(bool archiveBytes)
    {
        using var temp = new TempDirectory();
        var data = PsmfTestBuilder.Create(frameCount: 2, frameSize: 568, splitPayload: true);
        var inputPath = Path.Combine(temp.Path, "movie.pmf");
        File.WriteAllBytes(inputPath, data);
        string? stagedOma = null;

        AudioPcmTranscoder transcoder =
            (string omaPath, string wavPath, out string error) =>
            {
                stagedOma = omaPath;
                Assert.True(File.Exists(omaPath));
                Assert.True(File.ReadAllBytes(omaPath).AsSpan(0, 4).SequenceEqual("EA3\0"u8));
                WavWriter.WritePcm16(wavPath, 44_100, 2, [0, 0, 0, 0]);
                error = "";
                return true;
            };

        var result = archiveBytes
            ? PsmfAudioExtractor.ConvertToWav(data, "soundtrack", temp.Path, transcoder)
            : PsmfAudioExtractor.ConvertToWav(inputPath, "soundtrack", temp.Path, transcoder);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, result.SamplesWritten);
        Assert.True(XmaRiffAudio.IsPcm16WaveFile(
            Path.Combine(temp.Path, "soundtrack.wav"),
            44_100,
            2));
        Assert.NotNull(stagedOma);
        Assert.False(File.Exists(stagedOma));
    }

    [Fact]
    public void ConvertToWav_VideoOnlyPsmfSkipsWithoutCallingFfmpeg()
    {
        using var temp = new TempDirectory();
        var transcoderCalled = false;
        AudioPcmTranscoder transcoder =
            (string _, string _, out string error) =>
            {
                transcoderCalled = true;
                error = "unexpected";
                return false;
            };

        var result = PsmfAudioExtractor.ConvertToWav(
            PsmfTestBuilder.CreateVideoOnly(),
            "silent",
            temp.Path,
            transcoder);

        Assert.False(result.Success);
        Assert.True(result.Skipped);
        Assert.Contains("no ATRAC3+ audio", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(transcoderCalled);
        Assert.False(File.Exists(Path.Combine(temp.Path, "silent.wav")));
    }

    [Fact]
    public void Probe_PartialFinalAtracFrameFailsClosed()
    {
        var data = PsmfTestBuilder.Create(frameCount: 2, frameSize: 568, truncateAudioBytes: 1);
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            $"nmt-psmf-partial-{Guid.NewGuid():N}.oma");

        try
        {
            Assert.Null(PsmfAudioExtractor.Probe(data));
            Assert.False(PsmfAudioExtractor.TryWriteOma(data, outputPath, out _, out var error));
            Assert.Contains("partial frame", error, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public void ProbeVideo_ValidPsmfWithStrictAtracStreamIsSupported()
    {
        var path = FormatProbeTestHelper.CreateTempFile(
            ".pmf",
            PsmfTestBuilder.Create(frameCount: 1, frameSize: 568));

        try
        {
            var result = FormatProbe.ProbeVideo(path);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("PSMF Video (PSP)", result.FormatName);
            Assert.Null(result.UnsupportedReason);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ProbeAudio_DistinguishesAtracSoundtrackFromVideoOnlyPsmf()
    {
        var audioPath = FormatProbeTestHelper.CreateTempFile(
            ".pmf",
            PsmfTestBuilder.Create(frameCount: 3, frameSize: 568));
        var silentPath = FormatProbeTestHelper.CreateTempFile(
            ".pmf",
            PsmfTestBuilder.CreateVideoOnly());

        try
        {
            var audio = FormatProbe.ProbeAudio(audioPath);
            var silent = FormatProbe.ProbeAudio(silentPath);

            Assert.Equal(FormatProbe.FormatSupport.Supported, audio.Support);
            Assert.Equal("PSMF ATRAC3+ Audio", audio.FormatName);
            Assert.Contains("44100 Hz", audio.UnsupportedReason, StringComparison.Ordinal);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, silent.Support);
            Assert.Contains("no ATRAC3+ audio", silent.UnsupportedReason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(audioPath);
            File.Delete(silentPath);
        }
    }

    [Fact]
    public void AudioTabMetadata_PathAndArchivePredicatesExposeOnlyAuthoredAudio()
    {
        using var temp = new TempDirectory();
        var audio = PsmfTestBuilder.Create(frameCount: 2, frameSize: 568);
        var silent = PsmfTestBuilder.CreateVideoOnly();
        var audioPath = Path.Combine(temp.Path, "movie.PMF");
        var silentPath = Path.Combine(temp.Path, "icon.pmf");
        File.WriteAllBytes(audioPath, audio);
        File.WriteAllBytes(silentPath, silent);

        Assert.True(PsmfAudioExtractor.Probe(audioPath)?.HasAudio);
        Assert.False(PsmfAudioExtractor.Probe(silentPath)?.HasAudio);
        Assert.True(PsmfAudioExtractor.Probe(audio)?.HasAudio);
        Assert.False(PsmfAudioExtractor.Probe(silent)?.HasAudio);
        Assert.Equal(
            2 * 2048 / 44_100.0,
            AudioDurationProbe.Probe("PMF", audio));
    }

    [CorpusFact]
    public void Corpus_All334PmfFilesHaveExactlyConsumedAudioOrAreIntentionallySilent()
    {
        var files = new[] { RemixBuild, Project8FinalBuild, Project8Rev1Build }
            .SelectMany(build => paths.FindSampleFiles(build, "*.pmf"))
            .ToArray();
        Assert.Equal(334, files.Length);

        var probes = files.Select(path =>
        {
            var probe = PsmfAudioExtractor.Probe(path);
            Assert.NotNull(probe);
            return (Path: path, Probe: probe);
        }).ToArray();

        var audio = probes.Where(static item => item.Probe!.HasAudio).ToArray();
        var silent = probes.Where(static item => !item.Probe!.HasAudio).ToArray();
        Assert.Equal(333, audio.Length);
        Assert.Single(silent);
        Assert.Equal("ICON1.PMF", Path.GetFileName(silent[0].Path), ignoreCase: true);
        Assert.All(audio, static item =>
        {
            Assert.Equal(0, item.Probe!.PrivateStreamId);
            Assert.True(item.Probe.PacketCount > 0);
            Assert.True(item.Probe.FrameCount > 0);
            Assert.Equal(44_100, item.Probe.SampleRate);
            Assert.Equal(2, item.Probe.Channels);
            Assert.Contains(item.Probe.FrameSize, AtracFrameSizes);
        });
    }

    [CorpusFact]
    public void Corpus_EachAtracFrameLayoutProducesAacMp4AndPcmWav()
    {
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg is not installed");
        Assert.SkipWhen(SfdConverter.FindFfprobe() == null, "ffprobe is not installed");

        var representatives = new[] { RemixBuild, Project8FinalBuild, Project8Rev1Build }
            .SelectMany(build => paths.FindSampleFiles(build, "*.pmf"))
            .Select(path => (Path: path, Probe: PsmfAudioExtractor.Probe(path)))
            .Where(static item => item.Probe?.HasAudio == true)
            .GroupBy(static item => item.Probe!.FrameSize)
            .Select(static group => group.MinBy(item => new FileInfo(item.Path).Length))
            .OrderBy(static item => item.Probe!.FrameSize)
            .ToArray();
        Assert.Equal(AtracFrameSizes, representatives.Select(static item => item.Probe!.FrameSize));

        using var temp = new TempDirectory();
        foreach (var item in representatives)
        {
            var result = item.Probe!.FrameSize == 568
                ? SfdConverter.ConvertToMp4(
                    item.Path,
                    temp.Path,
                    previewQuality: true,
                    cancellationToken: TestContext.Current.CancellationToken)
                : SfdConverter.ConvertToMp4(
                    File.ReadAllBytes(item.Path),
                    $"layout_video_{item.Probe.FrameSize}",
                    temp.Path,
                    previewQuality: true,
                    cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, $"{item.Path}: {result.ErrorMessage}");

            var output = SfdConverter.Probe(result.OutputPath!);
            Assert.NotNull(output);
            Assert.Equal("h264", output.VideoCodec);
            Assert.Equal("aac", output.AudioCodec);
            Assert.Equal(44_100, output.AudioSampleRate);
            Assert.Equal(2, output.AudioChannels);

            var wavStem = $"layout_{item.Probe!.FrameSize}";
            var wavResult = item.Probe.FrameSize == 568
                ? PsmfAudioExtractor.ConvertToWav(item.Path, wavStem, temp.Path)
                : PsmfAudioExtractor.ConvertToWav(
                    File.ReadAllBytes(item.Path),
                    wavStem,
                    temp.Path);
            Assert.True(wavResult.Success, $"{item.Path}: {wavResult.ErrorMessage}");
            Assert.True(XmaRiffAudio.IsPcm16WaveFile(
                Path.Combine(temp.Path, wavStem + ".wav"),
                44_100,
                2));
        }
    }

    [CorpusFact]
    public void Corpus_PreviouslyFailing1325FrameTrackNowConvertsWithCompleteAudio()
    {
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg is not installed");
        Assert.SkipWhen(SfdConverter.FindFfprobe() == null, "ffprobe is not installed");
        var input = paths.FindSampleFiles(Project8Rev1Build, "m_bts_bb.pmf").Single();
        var sourceAudio = PsmfAudioExtractor.Probe(input);
        Assert.NotNull(sourceAudio);
        Assert.Equal(1325, sourceAudio.FrameCount);
        Assert.Equal(752, sourceAudio.FrameSize);

        using var temp = new TempDirectory();
        var result = SfdConverter.ConvertToMp4(
            input,
            temp.Path,
            previewQuality: true,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        var output = SfdConverter.Probe(result.OutputPath!);
        Assert.NotNull(output);
        Assert.Equal("aac", output.AudioCodec);
        Assert.Equal(2, output.AudioChannels);
        Assert.True(output.Duration.TotalSeconds > 60);

        var wavResult = PsmfAudioExtractor.ConvertToWav(
            input,
            "m_bts_bb_audio",
            temp.Path);
        Assert.True(wavResult.Success, wavResult.ErrorMessage);
        var wavData = File.ReadAllBytes(Path.Combine(temp.Path, "m_bts_bb_audio.wav"));
        Assert.True(RiffWaveReader.TryRead(wavData, out var wav));
        Assert.Equal(44_100, wav.SampleRate);
        Assert.Equal(2, wav.Channels);
        Assert.InRange(
            Math.Abs(wav.DataLength / (double)wav.AvgBytesPerSec - sourceAudio.DurationSeconds),
            0,
            1.0 / sourceAudio.SampleRate);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"nmt-psmf-convert-{Guid.NewGuid():N}");
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

internal static class PsmfTestBuilder
{
    private const int HeaderSize = 0x800;

    public static byte[] Create(
        int frameCount,
        int frameSize,
        bool splitPayload = false,
        int truncateAudioBytes = 0,
        int channelLayoutId = 2)
    {
        if (frameSize < 24 || (frameSize - 16) % 8 != 0)
            throw new ArgumentOutOfRangeException(nameof(frameSize));
        if (channelLayoutId is < 1 or > 7)
            throw new ArgumentOutOfRangeException(nameof(channelLayoutId));

        var codecParameters = checked((ushort)(
            0x2000 | channelLayoutId << 10 | ((frameSize - 16) / 8)));
        var elementary = new byte[checked(frameCount * frameSize - truncateAudioBytes)];
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var offset = frameIndex * frameSize;
            if (offset + 8 > elementary.Length)
                break;

            elementary[offset] = 0x0F;
            elementary[offset + 1] = 0xD0;
            BinaryPrimitives.WriteUInt16BigEndian(elementary.AsSpan(offset + 2), codecParameters);
            BinaryPrimitives.WriteUInt32BigEndian(elementary.AsSpan(offset + 4), (uint)frameIndex);
            for (var index = offset + 8; index < Math.Min(offset + frameSize, elementary.Length); index++)
                elementary[index] = (byte)(index * 37 + frameIndex);
        }

        using var programStream = new MemoryStream();
        WriteVideoPacket(programStream);
        if (splitPayload)
        {
            var split = Math.Min(173, elementary.Length);
            WritePrivatePacket(programStream, elementary.AsSpan(0, split));
            WritePrivatePacket(programStream, elementary.AsSpan(split));
        }
        else
        {
            WritePrivatePacket(programStream, elementary);
        }

        return Wrap(programStream.ToArray());
    }

    public static byte[] CreateVideoOnly()
    {
        using var programStream = new MemoryStream();
        WriteVideoPacket(programStream);
        return Wrap(programStream.ToArray());
    }

    public static byte[] CreateVideoOnlyWithStuffingAndPadding()
    {
        using var programStream = new MemoryStream();
        WriteVideoPacket(programStream, stuffingLength: 3);
        programStream.Write([0x00, 0x00, 0x01, 0xBE, 0x00, 0x03, 0xFF, 0xFF, 0xFF]);
        return Wrap(programStream.ToArray());
    }

    public static byte[] CreateVideoOnlyWithTrailingGarbage()
    {
        using var programStream = new MemoryStream();
        WriteVideoPacket(programStream);
        programStream.WriteByte(0xFF);
        return Wrap(programStream.ToArray());
    }

    public static byte[] GetFrameBodies(byte[] psmf)
    {
        var probe = PsmfAudioExtractor.Probe(psmf)!;
        var frameSize = probe.FrameSize;
        var elementary = ExtractSyntheticElementaryStream(psmf);
        using var bodies = new MemoryStream();
        for (var offset = 0; offset < elementary.Length; offset += frameSize)
            bodies.Write(elementary.AsSpan(offset + 8, frameSize - 8));
        return bodies.ToArray();
    }

    private static byte[] Wrap(byte[] programStream)
    {
        var data = new byte[HeaderSize + programStream.Length];
        "PSMF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(8), HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), (uint)programStream.Length);
        programStream.CopyTo(data, HeaderSize);
        return data;
    }

    private static void WriteVideoPacket(Stream stream, int stuffingLength = 0)
    {
        WritePackHeader(stream, stuffingLength);
        stream.Write([0x00, 0x00, 0x01, 0xE0, 0x00, 0x04, 0x81, 0x00, 0x00, 0x00]);
    }

    private static void WritePrivatePacket(Stream stream, ReadOnlySpan<byte> payload)
    {
        WritePackHeader(stream);
        stream.Write([0x00, 0x00, 0x01, 0xBD]);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)(12 + payload.Length)));
        stream.Write(length);
        stream.Write([0x81, 0x80, 0x05, 0x21, 0x00, 0x01, 0x00, 0x01]);
        stream.Write([0x00, 0x00, 0x00, 0x00]);
        stream.Write(payload);
    }

    private static void WritePackHeader(Stream stream, int stuffingLength = 0)
    {
        if (stuffingLength is < 0 or > 7)
            throw new ArgumentOutOfRangeException(nameof(stuffingLength));

        stream.Write([0x00, 0x00, 0x01, 0xBA]);
        stream.Write([0x44, 0x00, 0x05, 0x3D, 0x1D, 0x11, 0x01, 0x86, 0xA3, (byte)(0xF8 | stuffingLength)]);
        for (var index = 0; index < stuffingLength; index++)
            stream.WriteByte(0xFF);
    }

    private static byte[] ExtractSyntheticElementaryStream(byte[] psmf)
    {
        using var elementary = new MemoryStream();
        var offset = HeaderSize;
        while (offset <= psmf.Length - 6)
        {
            if (psmf[offset] != 0x00
                || psmf[offset + 1] != 0x00
                || psmf[offset + 2] != 0x01
                || psmf[offset + 3] != 0xBD)
            {
                offset++;
                continue;
            }

            var packetLength = BinaryPrimitives.ReadUInt16BigEndian(psmf.AsSpan(offset + 4));
            var payloadOffset = offset + 6 + 12;
            elementary.Write(psmf.AsSpan(payloadOffset, packetLength - 12));
            offset += 6 + packetLength;
        }

        return elementary.ToArray();
    }
}
