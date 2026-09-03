using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class SfdConverterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_PsmfStagesOmaAndMapsItsAudio(bool byteInput)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-psmf-map-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(root, "output");
        var inputPath = Path.Combine(root, "movie.pmf");
        var input = PsmfTestBuilder.Create(frameCount: 2, frameSize: 568, splitPayload: true);
        string? temporaryOma = null;
        Directory.CreateDirectory(root);
        File.WriteAllBytes(inputPath, input);

        try
        {
            SfdConverter.FfmpegRunner runner =
                (_, arguments, outputPath, _, _, _, stdinData) =>
                {
                    Assert.Contains("-xerror", arguments, StringComparison.Ordinal);
                    Assert.Contains("-map 0:v:0 -map 1:a:0", arguments, StringComparison.Ordinal);
                    Assert.Contains("-shortest", arguments, StringComparison.Ordinal);
                    Assert.Equal(byteInput, stdinData != null);
                    if (byteInput)
                        Assert.Contains("-i - -i", arguments, StringComparison.Ordinal);
                    else
                        Assert.Contains($"-i \"{inputPath}\" -i", arguments, StringComparison.Ordinal);

                    var match = System.Text.RegularExpressions.Regex.Match(
                        arguments,
                        "\\\"([^\\\"]+\\.oma)\\\"");
                    Assert.True(match.Success, arguments);
                    temporaryOma = match.Groups[1].Value;
                    Assert.True(File.Exists(temporaryOma));
                    Assert.True(File.ReadAllBytes(temporaryOma).AsSpan(0, 4).SequenceEqual("EA3\0"u8));

                    File.WriteAllBytes(outputPath, BuildMp4());
                    return new SfdConvertResult { Success = true, OutputPath = outputPath };
                };

            var result = byteInput
                ? SfdConverter.ConvertToMp4(
                    input,
                    "movie",
                    outputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: TestContext.Current.CancellationToken)
                : SfdConverter.ConvertToMp4(
                    inputPath,
                    outputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotNull(temporaryOma);
            Assert.False(File.Exists(temporaryOma));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_VideoOnlyPsmfUsesExplicitNoAudioMap(bool byteInput)
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-psmf-silent-map-{Guid.NewGuid():N}");
        var outputDirectory = Path.Combine(root, "output");
        var inputPath = Path.Combine(root, "movie.pmf");
        var input = PsmfTestBuilder.CreateVideoOnly();
        Directory.CreateDirectory(root);
        File.WriteAllBytes(inputPath, input);

        try
        {
            SfdConverter.FfmpegRunner runner =
                (_, arguments, outputPath, _, _, _, _) =>
                {
                    Assert.Contains("-map 0:v:0 -an", arguments, StringComparison.Ordinal);
                    Assert.DoesNotContain(".oma", arguments, StringComparison.OrdinalIgnoreCase);
                    File.WriteAllBytes(outputPath, BuildMp4());
                    return new SfdConvertResult { Success = true, OutputPath = outputPath };
                };

            var result = byteInput
                ? SfdConverter.ConvertToMp4(
                    input,
                    "movie",
                    outputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: TestContext.Current.CancellationToken)
                : SfdConverter.ConvertToMp4(
                    inputPath,
                    outputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success, result.ErrorMessage);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_FailedFfmpegPreservesExistingDestination(bool byteInput)
    {
        using var fixture = new ConversionFixture();
        string? stagedPath = null;
        var validMp4 = BuildMp4();

        SfdConverter.FfmpegRunner runner =
            (_, _, outputPath, _, _, _, stdinData) =>
            {
                stagedPath = outputPath;
                Assert.Equal(byteInput, stdinData != null);
                File.WriteAllBytes(outputPath, validMp4);
                return new SfdConvertResult { ErrorMessage = "synthetic ffmpeg failure" };
            };

        var result = fixture.Convert(byteInput, runner, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("synthetic ffmpeg failure", result.ErrorMessage);
        Assert.Equal(fixture.ExistingBytes, File.ReadAllBytes(fixture.OutputPath));
        AssertStagedSiblingWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_SuccessWithInvalidStagePreservesExistingDestination(bool byteInput)
    {
        using var fixture = new ConversionFixture();
        string? stagedPath = null;

        SfdConverter.FfmpegRunner runner =
            (_, _, outputPath, _, _, _, _) =>
            {
                stagedPath = outputPath;
                File.WriteAllBytes(outputPath, [0x01, 0x02, 0x03]);
                return new SfdConvertResult { Success = true, OutputPath = outputPath };
            };

        var result = fixture.Convert(byteInput, runner, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(SfdConverter.InvalidMp4OutputError, result.ErrorMessage);
        Assert.Equal(fixture.ExistingBytes, File.ReadAllBytes(fixture.OutputPath));
        AssertStagedSiblingWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_CancelledAfterFfmpegPreservesExistingDestination(bool byteInput)
    {
        using var fixture = new ConversionFixture();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        string? stagedPath = null;

        SfdConverter.FfmpegRunner runner =
            (_, _, outputPath, _, _, _, _) =>
            {
                stagedPath = outputPath;
                File.WriteAllBytes(outputPath, BuildMp4());
                cancellation.Cancel();
                return new SfdConvertResult { Success = true, OutputPath = outputPath };
            };

        var result = fixture.Convert(byteInput, runner, cancellation.Token);

        Assert.False(result.Success);
        Assert.Equal("Cancelled", result.ErrorMessage);
        Assert.Equal(fixture.ExistingBytes, File.ReadAllBytes(fixture.OutputPath));
        AssertStagedSiblingWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertToMp4_RecognizableStageAtomicallyReplacesDestination(bool byteInput)
    {
        using var fixture = new ConversionFixture();
        string? stagedPath = null;
        var validMp4 = BuildMp4(majorBrand: "mp42");

        SfdConverter.FfmpegRunner runner =
            (_, arguments, outputPath, _, _, _, stdinData) =>
            {
                stagedPath = outputPath;
                Assert.Contains(outputPath, arguments, StringComparison.Ordinal);
                Assert.Equal(byteInput, stdinData != null);
                File.WriteAllBytes(outputPath, validMp4);
                return new SfdConvertResult { Success = true, OutputPath = outputPath };
            };

        var result = fixture.Convert(byteInput, runner, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(fixture.OutputPath, result.OutputPath);
        Assert.Equal(validMp4, File.ReadAllBytes(fixture.OutputPath));
        AssertStagedSiblingWasCleaned(stagedPath, fixture.OutputDirectory);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"streams\":[]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"subtitle\"}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"audio\",\"codec_name\":\"adx\",\"sample_rate\":\"44100\",\"channels\":2}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":0,\"height\":240}]}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":0}]}")]
    public void ParseProbeJson_WithoutUsableVideo_ReturnsNull(string json)
    {
        Assert.Null(SfdConverter.ParseProbeJson(json, "<stdin>", null, 123));
    }

    [Fact]
    public void ParseProbeJson_PssAudioFallbackWithoutVideo_ReturnsNull()
    {
        var pssAudio = new PssAudioExtractor.PssAudioProbeResult(
            "PSX ADPCM",
            48_000,
            2,
            0x800);

        var result = SfdConverter.ParseProbeJson("{\"streams\":[]}", "<stdin>", pssAudio, 123);

        Assert.Null(result);
    }

    [Fact]
    public void ParseProbeJson_UsableVideoPreservesPssAudioFallback()
    {
        const string json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":320,\"height\":240}]}";
        var pssAudio = new PssAudioExtractor.PssAudioProbeResult(
            "PSX ADPCM",
            48_000,
            2,
            0x800);

        var result = SfdConverter.ParseProbeJson(json, "<stdin>", pssAudio, 123);

        Assert.NotNull(result);
        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
        Assert.Null(result.VideoCodec);
        Assert.Equal(0d, result.FrameRate);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal("PSX ADPCM", result.AudioCodec);
        Assert.Equal(48_000, result.AudioSampleRate);
        Assert.Equal(2, result.AudioChannels);
        Assert.Equal(123, result.FileSize);
    }

    [Fact]
    public void ParseProbeJson_UsablePsmfVideoRestoresPrivateAtracMetadata()
    {
        const string json = "{\"streams\":[{\"codec_type\":\"video\",\"width\":480,\"height\":272}]}";
        var psmfAudio = new PsmfAudioProbeResult(
            true,
            0,
            495,
            1325,
            752,
            44_100,
            2,
            1325 * 2048 / 44_100.0);

        var result = SfdConverter.ParseProbeJson(json, "<stdin>", null, 123, psmfAudio);

        Assert.NotNull(result);
        Assert.Equal("atrac3p", result.AudioCodec);
        Assert.Equal(44_100, result.AudioSampleRate);
        Assert.Equal(2, result.AudioChannels);
    }

    [Fact]
    public void ParseProbeJson_InMemoryInput_UsesSuppliedByteLength()
    {
        const string json = """
                            {
                              "format": { "duration": "2.5" },
                              "streams": [
                                {
                                  "codec_type": "video",
                                  "codec_name": "mpeg1video",
                                  "width": 320,
                                  "height": 240,
                                  "r_frame_rate": "30000/1001"
                                },
                                {
                                  "codec_type": "audio",
                                  "codec_name": "adx",
                                  "sample_rate": "44100",
                                  "channels": 2
                                }
                              ]
                            }
                            """;

        var result = SfdConverter.ParseProbeJson(json, "<stdin>", null, 123);

        Assert.NotNull(result);
        Assert.Equal(123, result.FileSize);
        Assert.Equal(TimeSpan.FromSeconds(2.5), result.Duration);
        Assert.Equal(320, result.Width);
        Assert.Equal(240, result.Height);
        Assert.Equal(30000d / 1001d, result.FrameRate);
        Assert.Equal("mpeg1video", result.VideoCodec);
        Assert.Equal("adx", result.AudioCodec);
        Assert.Equal(44100, result.AudioSampleRate);
        Assert.Equal(2, result.AudioChannels);
    }

    private static byte[] BuildMp4(string majorBrand = "isom")
    {
        var data = new byte[24];
        BinaryPrimitives.WriteUInt32BigEndian(data, (uint)data.Length);
        "ftyp"u8.CopyTo(data.AsSpan(4));
        System.Text.Encoding.ASCII.GetBytes(majorBrand).CopyTo(data, 8);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), 0x200);
        "isom"u8.CopyTo(data.AsSpan(16));
        "mp42"u8.CopyTo(data.AsSpan(20));
        return data;
    }

    private static void AssertStagedSiblingWasCleaned(string? stagedPath, string outputDirectory)
    {
        Assert.NotNull(stagedPath);
        Assert.Equal(outputDirectory, Path.GetDirectoryName(stagedPath));
        var leaf = Path.GetFileName(stagedPath);
        Assert.StartsWith(".", leaf, StringComparison.Ordinal);
        Assert.EndsWith(".tmp.mp4", leaf, StringComparison.Ordinal);
        Assert.True(Guid.TryParseExact(leaf[1..^".tmp.mp4".Length], "N", out _));
        Assert.False(File.Exists(stagedPath));
        Assert.False(Directory.Exists(stagedPath));
    }

    private sealed class ConversionFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "nmt-sfd-output-" + Guid.NewGuid().ToString("N"));

        public ConversionFixture()
        {
            InputPath = Path.Combine(_root, "movie.sfd");
            OutputDirectory = Path.Combine(_root, "output");
            OutputPath = Path.Combine(OutputDirectory, "movie.mp4");
            ExistingBytes = [0xAA, 0xBB, 0xCC, 0xDD];

            Directory.CreateDirectory(OutputDirectory);
            File.WriteAllBytes(InputPath, [0x00, 0x00, 0x01, 0xBA]);
            File.WriteAllBytes(OutputPath, ExistingBytes);
        }

        public string InputPath { get; }
        public string OutputDirectory { get; }
        public string OutputPath { get; }
        public byte[] ExistingBytes { get; }

        public SfdConvertResult Convert(
            bool byteInput,
            SfdConverter.FfmpegRunner runner,
            CancellationToken cancellationToken)
        {
            return byteInput
                ? SfdConverter.ConvertToMp4(
                    File.ReadAllBytes(InputPath),
                    "movie",
                    OutputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: cancellationToken)
                : SfdConverter.ConvertToMp4(
                    InputPath,
                    OutputDirectory,
                    () => "synthetic-ffmpeg",
                    _ => null,
                    runner,
                    cancellationToken: cancellationToken);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
    }
}
