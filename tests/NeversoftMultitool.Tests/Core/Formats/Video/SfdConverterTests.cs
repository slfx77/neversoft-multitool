using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class SfdConverterTests
{
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
}
