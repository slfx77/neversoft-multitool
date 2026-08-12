using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class SfdConverterTests
{
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
