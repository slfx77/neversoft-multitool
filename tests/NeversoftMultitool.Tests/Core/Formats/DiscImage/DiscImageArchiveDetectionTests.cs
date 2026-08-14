using NeversoftMultitool.Core.Formats.DiscImage;

namespace NeversoftMultitool.Tests.Core.Formats.DiscImage;

public sealed class DiscImageArchiveDetectionTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "NeversoftMultitoolTests",
        Guid.NewGuid().ToString("N"));

    public DiscImageArchiveDetectionTests()
    {
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* best-effort test cleanup */
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void IsDiscImage_CueWithMissingLaterTrack_ReturnsFalse()
    {
        WriteTrack("track01.bin");
        var cuePath = WriteCue(
            "FILE \"track01.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00",
            "FILE \"missing-track02.bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    INDEX 01 00:00:00");

        Assert.False(DiscImageArchive.IsDiscImage(cuePath));
    }

    [Fact]
    public void IsDiscImage_CueWithAllTrackFilesPresent_ReturnsTrue()
    {
        WriteTrack("track01.bin");
        WriteTrack("track02.bin");
        var cuePath = WriteCue(
            "FILE \"track01.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00",
            "FILE \"track02.bin\" BINARY",
            "  TRACK 02 AUDIO",
            "    INDEX 01 00:00:00");

        Assert.True(DiscImageArchive.IsDiscImage(cuePath));
    }

    [Fact]
    public void IsDiscImage_SingleFileMultiTrackCue_RemainsTrue()
    {
        WriteTrack("shared.bin");
        var cuePath = WriteCue(
            "FILE \"shared.bin\" BINARY",
            "  TRACK 01 MODE2/2352",
            "    INDEX 01 00:00:00",
            "  TRACK 02 AUDIO",
            "    INDEX 01 00:00:01");

        Assert.True(DiscImageArchive.IsDiscImage(cuePath));
    }

    [Fact]
    public void IsDiscImage_GdiWithMissingLaterTrack_ReturnsFalse()
    {
        WriteTrack("track01.bin");
        var gdiPath = WriteGdi(
            "2",
            "1 0 4 2352 track01.bin 0",
            "2 45000 0 2352 missing-track02.raw 0");

        Assert.False(DiscImageArchive.IsDiscImage(gdiPath));
    }

    [Fact]
    public void IsDiscImage_GdiWithAllTrackFilesPresent_ReturnsTrue()
    {
        WriteTrack("track01.bin");
        WriteTrack("track02.raw");
        var gdiPath = WriteGdi(
            "2",
            "1 0 4 2352 track01.bin 0",
            "2 45000 0 2352 track02.raw 0");

        Assert.True(DiscImageArchive.IsDiscImage(gdiPath));
    }

    private string WriteCue(params string[] lines)
    {
        var path = Path.Combine(_tempDir, "game.cue");
        File.WriteAllLines(path, lines);
        return path;
    }

    private string WriteGdi(params string[] lines)
    {
        var path = Path.Combine(_tempDir, "game.gdi");
        File.WriteAllLines(path, lines);
        return path;
    }

    private void WriteTrack(string fileName)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, fileName), new byte[2352]);
    }
}
