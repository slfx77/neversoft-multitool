using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoConverterTests(TestPaths paths)
{
    private const string MissingMp4Error =
        "VID1 native decode completed successfully but did not produce a non-empty regular MP4 file.";

    // Properties evaluate eagerly when referenced (even inside Assert.SkipWhen(!File.Exists(...))),
    // so guard SampleBuildsDir to avoid Path.Combine throwing on CI when sample data is absent.
    private string ThawGcVidDir =>
        paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
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
                320,
                240,
                30,
                1,
                [
                    new Vid1SyntheticVideoFrameSpec(
                        0x2107,
                        0,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_SuccessWithoutNonEmptyStage_PreservesExistingOutput(bool writeEmptyStage)
    {
        var tempRoot = CreateSyntheticConversionFixture(
            out var inputPath,
            out var outputDir,
            out var outputPath,
            out var staleOutput);
        string? stagedOutputPath = null;

        try
        {
            Vid1VideoConverter.NativeDecodePipelineRunner pipeline =
                (string ffmpegPath,
                    Vid1VideoFile _,
                    List<string> audioPaths,
                    string stagePath,
                    IProgress<double>? _,
                    CancellationToken cancellationToken,
                    out string error) =>
                {
                    Assert.Equal("synthetic-ffmpeg", ffmpegPath);
                    Assert.Empty(audioPaths);
                    Assert.False(cancellationToken.IsCancellationRequested);
                    stagedOutputPath = stagePath;
                    if (writeEmptyStage)
                        File.WriteAllBytes(stagePath, []);
                    error = "";
                    return true;
                };

            var result = Vid1VideoConverter.ConvertToMp4(
                inputPath,
                outputDir,
                () => "synthetic-ffmpeg",
                pipeline);

            Assert.False(result.Success);
            Assert.Equal(MissingMp4Error, result.ErrorMessage);
            Assert.Equal(staleOutput, File.ReadAllBytes(outputPath));
            AssertStagedPath(stagedOutputPath, outputDir, outputPath);
            Assert.False(File.Exists(stagedOutputPath));
            Assert.False(Directory.Exists(stagedOutputPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToMp4_NonEmptyStage_ReplacesExistingOutput()
    {
        var tempRoot = CreateSyntheticConversionFixture(
            out var inputPath,
            out var outputDir,
            out var outputPath,
            out _);
        byte[] replacement = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];
        string? stagedOutputPath = null;

        try
        {
            Vid1VideoConverter.NativeDecodePipelineRunner pipeline =
                (string ffmpegPath,
                    Vid1VideoFile _,
                    List<string> audioPaths,
                    string stagePath,
                    IProgress<double>? _,
                    CancellationToken cancellationToken,
                    out string error) =>
                {
                    Assert.Equal("synthetic-ffmpeg", ffmpegPath);
                    Assert.Empty(audioPaths);
                    Assert.False(cancellationToken.IsCancellationRequested);
                    stagedOutputPath = stagePath;
                    File.WriteAllBytes(stagePath, replacement);
                    error = "";
                    return true;
                };

            var result = Vid1VideoConverter.ConvertToMp4(
                inputPath,
                outputDir,
                () => "synthetic-ffmpeg",
                pipeline);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Null(result.ErrorMessage);
            Assert.Equal(outputPath, result.OutputPath);
            Assert.Equal(replacement, File.ReadAllBytes(outputPath));
            AssertStagedPath(stagedOutputPath, outputDir, outputPath);
            Assert.False(File.Exists(stagedOutputPath));
            Assert.False(Directory.Exists(stagedOutputPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToMp4_DirectoryStage_IsRejectedAndRemoved()
    {
        var tempRoot = CreateSyntheticConversionFixture(
            out var inputPath,
            out var outputDir,
            out var outputPath,
            out var staleOutput);
        string? stagedOutputPath = null;

        try
        {
            Vid1VideoConverter.NativeDecodePipelineRunner pipeline =
                (string _,
                    Vid1VideoFile _,
                    List<string> _,
                    string stagePath,
                    IProgress<double>? _,
                    CancellationToken _,
                    out string error) =>
                {
                    stagedOutputPath = stagePath;
                    Directory.CreateDirectory(stagePath);
                    File.WriteAllBytes(Path.Combine(stagePath, "partial.bin"), [0xAA]);
                    error = "";
                    return true;
                };

            var result = Vid1VideoConverter.ConvertToMp4(
                inputPath,
                outputDir,
                () => "synthetic-ffmpeg",
                pipeline);

            Assert.False(result.Success);
            Assert.Equal(MissingMp4Error, result.ErrorMessage);
            Assert.Equal(staleOutput, File.ReadAllBytes(outputPath));
            AssertStagedPath(stagedOutputPath, outputDir, outputPath);
            Assert.False(Directory.Exists(stagedOutputPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToMp4_CancelledAfterPipeline_PreservesExistingOutput()
    {
        var tempRoot = CreateSyntheticConversionFixture(
            out var inputPath,
            out var outputDir,
            out var outputPath,
            out var staleOutput);
        using var cancellation = new CancellationTokenSource();
        string? stagedOutputPath = null;

        try
        {
            Vid1VideoConverter.NativeDecodePipelineRunner pipeline =
                (string _,
                    Vid1VideoFile _,
                    List<string> _,
                    string stagePath,
                    IProgress<double>? _,
                    CancellationToken _,
                    out string error) =>
                {
                    stagedOutputPath = stagePath;
                    File.WriteAllBytes(stagePath, [0x01]);
                    cancellation.Cancel();
                    error = "";
                    return true;
                };

            var result = Vid1VideoConverter.ConvertToMp4(
                inputPath,
                outputDir,
                () => "synthetic-ffmpeg",
                pipeline,
                cancellationToken: cancellation.Token);

            Assert.False(result.Success);
            Assert.Equal("Cancelled", result.ErrorMessage);
            Assert.Equal(staleOutput, File.ReadAllBytes(outputPath));
            AssertStagedPath(stagedOutputPath, outputDir, outputPath);
            Assert.False(File.Exists(stagedOutputPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ConvertToMp4_CompletionProgressFailure_PreservesExistingOutput()
    {
        var tempRoot = CreateSyntheticConversionFixture(
            out var inputPath,
            out var outputDir,
            out var outputPath,
            out var staleOutput);
        string? stagedOutputPath = null;

        try
        {
            Vid1VideoConverter.NativeDecodePipelineRunner pipeline =
                (string _,
                    Vid1VideoFile _,
                    List<string> _,
                    string stagePath,
                    IProgress<double>? _,
                    CancellationToken _,
                    out string error) =>
                {
                    stagedOutputPath = stagePath;
                    File.WriteAllBytes(stagePath, [0x01]);
                    error = "";
                    return true;
                };

            var result = Vid1VideoConverter.ConvertToMp4(
                inputPath,
                outputDir,
                () => "synthetic-ffmpeg",
                pipeline,
                new ThrowOnCompletionProgress());

            Assert.False(result.Success);
            Assert.Equal("completion progress failed", result.ErrorMessage);
            Assert.Equal(staleOutput, File.ReadAllBytes(outputPath));
            AssertStagedPath(stagedOutputPath, outputDir, outputPath);
            Assert.False(File.Exists(stagedOutputPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [CorpusFact]
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

    [CorpusFact]
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
                Assert.True(new FileInfo(outputPath).Length > 0,
                    $"{Path.GetFileName(file)} wrote an empty output file");
            }
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
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
                var result = Vid1VideoConverter.ConvertToMp4(file, outputDir,
                    cancellationToken: TestContext.Current.CancellationToken);
                var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".mp4");

                Assert.True(result.Success, $"{Path.GetFileName(file)}: {result.ErrorMessage}");
                Assert.True(File.Exists(outputPath), $"{Path.GetFileName(file)} did not write an MP4");
                Assert.True(new FileInfo(outputPath).Length > 0, $"{Path.GetFileName(file)} wrote an empty MP4");
            }
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    [CorpusFact]
    public void DecodeFrames_RepresentativeLongForm_WritesAtLeastOnePng()
    {
        Assert.SkipWhen(!File.Exists(LongFormSample), "Representative THAW GameCube long-form VID sample not found");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not found on PATH");

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid_video_frames");

        try
        {
            var result =
                Vid1VideoConverter.DecodeFrames(LongFormSample, outputDir, TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEmpty(Directory.GetFiles(outputDir, "*.png", SearchOption.TopDirectoryOnly));
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }

    private static string CreateSyntheticConversionFixture(
        out string inputPath,
        out string outputDir,
        out string outputPath,
        out byte[] staleOutput)
    {
        var tempRoot = FormatProbeTestHelper.CreateTempDirectory("vid_video_convert_staging");
        inputPath = Path.Combine(tempRoot, "sample.vid");
        outputDir = Path.Combine(tempRoot, "output");
        outputPath = Path.Combine(outputDir, "sample.mp4");
        staleOutput = [0x53, 0x54, 0x41, 0x4C, 0x45];

        File.WriteAllBytes(inputPath, Vid1VideoTestBuilder.CreateVideoVid1());
        Directory.CreateDirectory(outputDir);
        File.WriteAllBytes(outputPath, staleOutput);
        return tempRoot;
    }

    private static void AssertStagedPath(string? stagedOutputPath, string outputDir, string outputPath)
    {
        Assert.NotNull(stagedOutputPath);
        var stage = stagedOutputPath!;
        Assert.Equal(outputDir, Path.GetDirectoryName(stage));
        Assert.NotEqual(outputPath, stage);

        var fileName = Path.GetFileName(stage)!;
        Assert.StartsWith(".", fileName, StringComparison.Ordinal);
        Assert.EndsWith(".tmp.mp4", fileName, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(fileName[1..^".tmp.mp4".Length], "N", out _));
    }

    private sealed class ThrowOnCompletionProgress : IProgress<double>
    {
        public void Report(double value)
        {
            if (value >= 1.0)
                throw new InvalidOperationException("completion progress failed");
        }
    }
}
