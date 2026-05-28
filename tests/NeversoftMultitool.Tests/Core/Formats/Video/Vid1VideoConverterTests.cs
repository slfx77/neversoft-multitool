using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoConverterTests(TestPaths paths)
{
    private string ThawGcVidDir =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Tony Hawk's American Wasteland (2005-8-22, GC - Final)",
            "movies",
            "vid");

    private string LongFormSample => Path.Combine(ThawGcVidDir, "intro.vid");
    private string AtviSample => Path.Combine(ThawGcVidDir, "atvi.vid");

    [Fact]
    public void Probe_SyntheticVid1_ReturnsExpectedMetadata()
    {
        var tempFile = FormatProbeTestHelper.CreateTempFile(
            ".vid",
            Vid1VideoTestBuilder.CreateVideoVid1(
                width: 320,
                height: 240,
                frameRateNumerator: 30,
                frameRateDenominator: 1,
                frames:
                [
                    new Vid1SyntheticVideoFrameSpec(
                        0x2107,
                        PreambleClass: 0,
                        Quantizer: 7,
                        CurrentFrameStateWord: 0x11223344,
                        HasSpecialCallerGate: true)
                ]));

        try
        {
            var probe = Vid1VideoConverter.Probe(tempFile);

            Assert.NotNull(probe);
            Assert.InRange(probe!.Duration.TotalSeconds, 0.03, 0.04);
            Assert.Equal(320, probe.Width);
            Assert.Equal(240, probe.Height);
            Assert.Equal(1, probe.FrameCount);
            Assert.Equal(30.0, probe.FrameRate, 5);
            Assert.Equal(Vid1VideoVariant.Unknown, probe.Variant);
            Assert.False(probe.HasAudio);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Probe_RepresentativeSamples_ReturnExpectedMetadata()
    {
        Assert.SkipWhen(!File.Exists(LongFormSample), "Representative THAW GameCube long-form VID sample not found");
        Assert.SkipWhen(!File.Exists(AtviSample), "Representative THAW GameCube ATVI VID sample not found");

        var introProbe = Vid1VideoConverter.Probe(LongFormSample);
        var atviProbe = Vid1VideoConverter.Probe(AtviSample);

        Assert.NotNull(introProbe);
        Assert.NotNull(atviProbe);

        Assert.Equal(512, introProbe!.Width);
        Assert.Equal(384, introProbe.Height);
        Assert.Equal(1292, introProbe.FrameCount);
        Assert.Equal(Vid1VideoVariant.ThawLongForm, introProbe.Variant);
        Assert.True(introProbe.HasAudio);
        Assert.Equal(48000, introProbe.AudioSampleRate);
        Assert.Equal(2, introProbe.AudioChannels);

        Assert.Equal(512, atviProbe!.Width);
        Assert.Equal(384, atviProbe.Height);
        Assert.Equal(319, atviProbe.FrameCount);
        Assert.Equal(Vid1VideoVariant.ThawAtvi, atviProbe.Variant);
        Assert.True(atviProbe.HasAudio);
        Assert.Equal(44100, atviProbe.AudioSampleRate);
        Assert.Equal(2, atviProbe.AudioChannels);
    }

    [Fact]
    public void TryWriteDeterministicVideoStream_AllThawVidSamples_WritesNonEmptyM4v()
    {
        Assert.SkipWhen(!Directory.Exists(ThawGcVidDir), "THAW GameCube VID sample directory not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_video_stream");

        try
        {
            var files = Directory.GetFiles(ThawGcVidDir, "*.vid", SearchOption.TopDirectoryOnly)
                .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(8, files.Length);

            foreach (var file in files)
            {
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".m4v");
                var success = Vid1VideoConverter.TryWriteDeterministicVideoStream(file, outputPath, out var error);

                Assert.True(success, $"{Path.GetFileName(file)}: {error}");
                Assert.True(File.Exists(outputPath), $"{Path.GetFileName(file)} did not write an output file");
                Assert.True(new FileInfo(outputPath).Length > 0, $"{Path.GetFileName(file)} wrote an empty output file");
            }
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void ConvertToMp4_RepresentativeSamples_WritePlayableMp4()
    {
        Assert.SkipWhen(!File.Exists(LongFormSample), "Representative THAW GameCube long-form VID sample not found");
        Assert.SkipWhen(!File.Exists(AtviSample), "Representative THAW GameCube ATVI VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_video_convert");

        try
        {
            foreach (var file in new[] { LongFormSample, AtviSample })
            {
                var result = Vid1VideoConverter.ConvertToMp4(file, outputDir, cancellationToken: TestContext.Current.CancellationToken);
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".mp4");

                Assert.True(result.Success, $"{Path.GetFileName(file)}: {result.ErrorMessage}");
                Assert.True(File.Exists(outputPath), $"{Path.GetFileName(file)} did not write an MP4");
                Assert.True(new FileInfo(outputPath).Length > 0, $"{Path.GetFileName(file)} wrote an empty MP4");
            }
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void DecodeFrames_RepresentativeLongForm_WritesAtLeastOnePng()
    {
        Assert.SkipWhen(!File.Exists(LongFormSample), "Representative THAW GameCube long-form VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_video_frames");

        try
        {
            var result = Vid1VideoConverter.DecodeFrames(LongFormSample, outputDir, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEmpty(Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }
}
