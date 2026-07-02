using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public sealed class Vid1VideoFileTests(TestPaths paths)
{
    // Properties evaluate eagerly when referenced (even inside Assert.SkipWhen(!File.Exists(...))),
    // so guard SampleBuildsDir to avoid Path.Combine throwing on CI when sample data is absent.
    private string ThawGcVidDir =>
        paths.SampleBuildsDir is null ? string.Empty : Path.Combine(
            paths.SampleBuildsDir,
            "Tony Hawk's American Wasteland (2005-8-22, GC - Final)",
            "movies",
            "vid");

    private string LongFormSample => Path.Combine(ThawGcVidDir, "intro.vid");
    private string AtviSample => Path.Combine(ThawGcVidDir, "atvi.vid");

    [Fact]
    public void TryParse_SyntheticVid1_ReturnsExpectedMetadata()
    {
        var data = Vid1VideoTestBuilder.CreateVideoVid1(
            frames:
            [
                new Vid1SyntheticVideoFrameSpec(
                    0x61AB,
                    PreambleClass: 1,
                    IntraDcThresholdIndex: 5,
                    Quantizer: 11,
                    ForwardCode: 4,
                    CurrentFrameStateWord: 0xAABBCCDD,
                    HasSpecialCallerGate: true,
                    CodedPayload: [0x01, 0x02, 0x03, 0x04]),
                new Vid1SyntheticVideoFrameSpec(
                    0xA0CD,
                    PreambleClass: 2,
                    IntraDcThresholdIndex: 6,
                    Quantizer: 13,
                    ForwardCode: 3,
                    BackwardCode: 2,
                    AlternateFrameStateWord: 0x10203040,
                    CodedPayload: [0x11, 0x22, 0x33, 0x44, 0x55])
            ]);

        var success = Vid1VideoFile.TryParse(data, "intro.vid", out var file, out var error);

        Assert.True(success, error);
        Assert.NotNull(file);
        Assert.Equal(512, file!.Width);
        Assert.Equal(384, file.Height);
        Assert.Equal(2, file.FrameCount);
        Assert.Equal(2, file.Frames.Count);
        Assert.Equal(Vid1VideoVariant.ThawLongForm, file.Variant);
        Assert.Equal(30000 / 1001.0, file.FrameRate, 5);

        var frame0 = file.Frames[0];
        Assert.Equal(0x61AB, frame0.Tag16);
        Assert.Equal(1, frame0.PreambleClass);
        Assert.Equal(5, frame0.IntraDcThresholdIndex);
        Assert.Equal(11, frame0.Quantizer);
        Assert.Equal(4, frame0.ForwardCode);
        Assert.Equal(0xAABBCCDDu, frame0.CurrentFrameStateWord);
        Assert.True(frame0.HasSpecialCallerGate);

        var frame1 = file.Frames[1];
        Assert.Equal(0xA0CD, frame1.Tag16);
        Assert.Equal(2, frame1.PreambleClass);
        Assert.Equal(6, frame1.IntraDcThresholdIndex);
        Assert.Equal(13, frame1.Quantizer);
        Assert.Equal(3, frame1.ForwardCode);
        Assert.Equal(2, frame1.BackwardCode);
        Assert.Equal(0x10203040u, frame1.AlternateFrameStateWord);
        Assert.False(frame1.HasSpecialCallerGate);
    }

    [Fact]
    public void TryParse_NotVid1File_Fails()
    {
        var data = Vid1VideoTestBuilder.BuildChunk("NOPE", new byte[0x18]);

        var success = Vid1VideoFile.TryParse(data, "invalid.vid", out _, out var error);

        Assert.False(success);
        Assert.Contains("Not a VID1 file", error);
    }

    [Fact]
    public void TryParse_MissingHead_Fails()
    {
        var rootChunk = Vid1VideoTestBuilder.BuildChunk("VID1", new byte[0x18]);
        var frameChunk = Vid1VideoTestBuilder.CreateFrameChunk(
            new Vid1SyntheticVideoFrameSpec(
                0x2107,
                PreambleClass: 0,
                Quantizer: 7,
                CurrentFrameStateWord: 0x11223344,
                HasSpecialCallerGate: true));
        var data = rootChunk.Concat(frameChunk).ToArray();

        var success = Vid1VideoFile.TryParse(data, "invalid.vid", out _, out var error);

        Assert.False(success);
        Assert.Contains("HEAD", error);
    }

    [Fact]
    public void TryParse_SmallVidh_Fails()
    {
        var rootChunk = Vid1VideoTestBuilder.BuildChunk("VID1", new byte[0x18]);
        var shortVidhChunk = Vid1VideoTestBuilder.BuildChunk("VIDH", new byte[0x10]);
        var headPayload = new byte[4 + shortVidhChunk.Length];
        shortVidhChunk.CopyTo(headPayload.AsSpan(4));
        var headChunk = Vid1VideoTestBuilder.BuildChunk("HEAD", headPayload);
        var data = rootChunk.Concat(headChunk).ToArray();

        var success = Vid1VideoFile.TryParse(data, "invalid.vid", out _, out var error);

        Assert.False(success);
        Assert.Contains("VIDH chunk is too small", error);
    }

    [Fact]
    public void TryParse_NoFrames_Fails()
    {
        var rootChunk = Vid1VideoTestBuilder.BuildChunk("VID1", new byte[0x18]);
        var headPayload = new byte[4 + Vid1VideoTestBuilder.CreateVidhChunk().Length];
        Vid1VideoTestBuilder.CreateVidhChunk().CopyTo(headPayload.AsSpan(4));
        var headChunk = Vid1VideoTestBuilder.BuildChunk("HEAD", headPayload);
        var data = rootChunk.Concat(headChunk).ToArray();

        var success = Vid1VideoFile.TryParse(data, "invalid.vid", out _, out var error);

        Assert.False(success);
        Assert.Contains("video frames were not found", error);
    }

    [Fact]
    public void Parse_RepresentativeLongFormSample_ReturnsExpectedMetadata()
    {
        Assert.SkipWhen(!File.Exists(LongFormSample), "Representative THAW GameCube long-form VID sample not found");

        var file = Vid1VideoFile.Parse(LongFormSample);

        Assert.Equal(512, file.Width);
        Assert.Equal(384, file.Height);
        Assert.Equal(1292, file.FrameCount);
        Assert.Equal(1292, file.Frames.Count);
        Assert.Equal(Vid1VideoVariant.ThawLongForm, file.Variant);
        Assert.InRange(file.FrameRate, 29.9, 30.1);
    }

    [Fact]
    public void Parse_RepresentativeAtviSample_ReturnsExpectedMetadata()
    {
        Assert.SkipWhen(!File.Exists(AtviSample), "Representative THAW GameCube ATVI VID sample not found");

        var file = Vid1VideoFile.Parse(AtviSample);

        Assert.Equal(512, file.Width);
        Assert.Equal(384, file.Height);
        Assert.Equal(319, file.FrameCount);
        Assert.Equal(319, file.Frames.Count);
        Assert.Equal(Vid1VideoVariant.ThawAtvi, file.Variant);
        Assert.InRange(file.FrameRate, 29.9, 30.1);
    }

    [Fact]
    public void Parse_AllThawGcSamples_ClassifiesLongFormAndAtvi()
    {
        Assert.SkipWhen(!Directory.Exists(ThawGcVidDir), "THAW GameCube VID sample directory not found");

        var files = Directory.GetFiles(ThawGcVidDir, "*.vid", SearchOption.TopDirectoryOnly)
            .OrderBy(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(8, files.Length);

        var parsedFiles = files.Select(Vid1VideoFile.Parse).ToArray();

        Assert.Equal(7, parsedFiles.Count(static file => file.Variant == Vid1VideoVariant.ThawLongForm));
        Assert.Single(parsedFiles, static file => file.Variant == Vid1VideoVariant.ThawAtvi);
    }
}
