using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class CueSheetTests
{
    [Theory]
    [InlineData("bogus")]
    [InlineData("00:60:00")]
    [InlineData("00:00:75")]
    [InlineData("-1:00:00")]
    public void Parse_InvalidIndex01Timestamp_ThrowsExplicitly(string timestamp)
    {
        var lines = CreateCue(timestamp);

        var exception = Assert.Throws<InvalidDataException>(() => CueSheet.Parse(lines, ""));

        Assert.Equal($"Invalid cue INDEX 01 timestamp '{timestamp}'.", exception.Message);
    }

    [Fact]
    public void Parse_ValidIndex01Timestamp_ReadsFrames()
    {
        var cue = CueSheet.Parse(CreateCue("99:59:74"), "");

        var track = Assert.Single(cue.Tracks);
        Assert.Equal(449_999L, track.Index01Frames);
    }

    [Fact]
    public void Parse_MissingIndex01_DefaultsToZero()
    {
        string[] lines =
        [
            "FILE \"track.bin\" BINARY",
            "TRACK 01 MODE2/2352"
        ];

        var cue = CueSheet.Parse(lines, "");

        Assert.Equal(0, Assert.Single(cue.Tracks).Index01Frames);
    }

    private static string[] CreateCue(string timestamp) =>
    [
        "FILE \"track.bin\" BINARY",
        "TRACK 01 MODE2/2352",
        $"INDEX 01 {timestamp}"
    ];
}
