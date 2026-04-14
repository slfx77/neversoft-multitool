using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class MdecDecoderTests(TestPaths paths)
{
    private string? FindStrFile(string buildPattern, string fileName)
    {
        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains(buildPattern, StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;
        var strDir = Path.Combine(buildDir, "STR");
        if (!Directory.Exists(strDir)) return null;
        return Directory.GetFiles(strDir)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private string[] GetAllStrFiles()
    {
        if (!paths.HasSampleBuilds) return [];
        return Directory.GetDirectories(paths.SampleBuildsDir!)
            .SelectMany(build =>
            {
                var strDir = Path.Combine(build, "STR");
                return Directory.Exists(strDir)
                    ? Directory.GetFiles(strDir, "*.str", SearchOption.TopDirectoryOnly)
                        .Concat(Directory.GetFiles(strDir, "*.STR", SearchOption.TopDirectoryOnly))
                    : [];
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f =>
            {
                // Skip AFS archives (DC SPEECH.STR)
                try
                {
                    var header = new byte[4];
                    using var fs = File.OpenRead(f);
                    if (fs.Read(header, 0, 4) < 4) return false;
                    return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
    }

    // ── StrDemuxer Tests ───────────────────────────────────────────────

    [Fact]
    public void IsStrFile_ValidStrFile_ReturnsTrue()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        file ??= FindStrFile("Spider-Man (2000-9-1", "ATTRACT.STR");
        Assert.SkipWhen(file == null, "No STR file found");

        var data = File.ReadAllBytes(file!);
        Assert.True(StrDemuxer.IsStrFile(data));
    }

    [Fact]
    public void IsStrFile_AfsArchive_ReturnsFalse()
    {
        // DC SPEECH.STR is actually an AFS archive
        if (!paths.HasSampleBuilds) Assert.Skip("Sample builds not available");

        var dcBuild = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains("DC", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(dcBuild == null, "No DC build found");

        var strDir = Path.Combine(dcBuild!, "STR");
        Assert.SkipWhen(!Directory.Exists(strDir), "DC build has no STR subdirectory");

        var speechStr = Directory.GetFiles(strDir, "SPEECH.STR",
            SearchOption.TopDirectoryOnly).FirstOrDefault();

        if (speechStr == null) Assert.Skip("DC SPEECH.STR not found");

        var data = File.ReadAllBytes(speechStr);
        Assert.False(StrDemuxer.IsStrFile(data));
    }

    [Fact]
    public void IsStrFile_TooSmall_ReturnsFalse()
    {
        Assert.False(StrDemuxer.IsStrFile(new byte[100]));
    }

    [Fact]
    public void IsStrFile_WrongAlignment_ReturnsFalse()
    {
        Assert.False(StrDemuxer.IsStrFile(new byte[2337]));
    }

    [Fact]
    public void EnumerateFrames_ApocalypseIntro_HasFrames()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var frames = StrDemuxer.EnumerateFrames(data).Take(5).ToList();

        Assert.NotEmpty(frames);
        Assert.Equal(320, frames[0].Width);
        Assert.Equal(240, frames[0].Height);
        Assert.True(frames[0].Data.Length > 0);
    }

    [Fact]
    public void CountFrames_ReturnsPositiveCount()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var count = StrDemuxer.CountFrames(data);

        Assert.True(count > 10, $"Expected >10 frames, got {count}");
    }

    [Fact]
    public void HasAudio_TypicalStrFile_ReturnsTrue()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        Assert.True(StrDemuxer.HasAudio(data));
    }

    [Fact]
    public void ExtractAudioSectors_ReturnsAlignedData()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var audio = StrDemuxer.ExtractAudioSectors(data);

        Assert.True(audio.Length > 0, "Expected audio sectors");
        Assert.Equal(0, audio.Length % 2336); // Must be sector-aligned
    }

    // ── MdecDecoder Tests ──────────────────────────────────────────────

    [Fact]
    public void DecodeFrame_FirstFrame_ProducesNonZeroRgb()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        file ??= FindStrFile("Spider-Man (2000-9-1", "ATTRACT.STR");
        Assert.SkipWhen(file == null, "No STR file found for decode test");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).First();

        var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);

        Assert.Equal(frame.Width * frame.Height * 3, rgb.Length);

        // Verify not all black (at least some non-zero pixels)
        var nonZero = 0;
        for (var i = 0; i < rgb.Length; i++)
            if (rgb[i] != 0)
                nonZero++;

        Assert.True(nonZero > rgb.Length / 10,
            $"Expected >10% non-zero pixels, got {nonZero}/{rgb.Length} ({100.0 * nonZero / rgb.Length:F1}%)");
    }

    [Fact]
    public void DecodeFrame_OutputDimensions_MatchInput()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).First();

        var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);

        // RGB24: 3 bytes per pixel
        Assert.Equal(320 * 240 * 3, rgb.Length);
    }

    [Fact]
    public void DecodeFrame_MultipleFrames_AllDecode()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var errors = new List<string>();
        var decoded = 0;

        foreach (var frame in StrDemuxer.EnumerateFrames(data).Take(30))
        {
            try
            {
                var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);
                Assert.Equal(frame.Width * frame.Height * 3, rgb.Length);
                decoded++;
            }
            catch (Exception ex)
            {
                errors.Add($"Frame {frame.FrameNumber}: {ex.Message}");
            }
        }

        Assert.True(decoded > 0, "No frames decoded");
        Assert.True(errors.Count == 0,
            $"Failed to decode {errors.Count}/{decoded + errors.Count} frames:\n{string.Join("\n", errors)}");
    }

    // ── StrProbeResult Tests ───────────────────────────────────────────

    [Fact]
    public void Probe_ValidFile_ReturnsMetadata()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var result = StrConverter.Probe(file!);

        Assert.NotNull(result);
        Assert.Equal(320, result!.Width);
        Assert.Equal(240, result.Height);
        Assert.True(result.FrameCount > 0);
        Assert.True(result.FileSize > 0);
    }

    [Fact]
    public void Probe_InvalidFile_ReturnsNull()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mdec_test_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempFile, new byte[100]);
            var result = StrConverter.Probe(tempFile);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Batch Demux Test ───────────────────────────────────────────────

    [Fact]
    public void Demux_AllStrFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllStrFiles();
        Assert.SkipWhen(files.Length == 0, "No STR files found");

        var errors = new List<string>();
        var demuxed = 0;
        var skippedNonVideo = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);

                // Some .str files aren't MDEC video (THPS2 level data, DC formats) — skip them
                if (!StrDemuxer.IsStrFile(data))
                {
                    skippedNonVideo++;
                    continue;
                }

                var frameCount = StrDemuxer.CountFrames(data);
                Assert.True(frameCount > 0, $"{Path.GetFileName(file)} has 0 frames");

                // Decode first frame to verify decode pipeline
                var firstFrame = StrDemuxer.EnumerateFrames(data).FirstOrDefault();
                Assert.NotNull(firstFrame);
                Assert.True(firstFrame!.Width > 0 && firstFrame.Width % 16 == 0,
                    $"{Path.GetFileName(file)} has invalid width {firstFrame.Width}");
                Assert.True(firstFrame.Height > 0 && firstFrame.Height % 16 == 0,
                    $"{Path.GetFileName(file)} has invalid height {firstFrame.Height}");

                var rgb = MdecDecoder.DecodeFrame(firstFrame.Data, firstFrame.Width, firstFrame.Height);
                Assert.Equal(firstFrame.Width * firstFrame.Height * 3, rgb.Length);

                demuxed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed to demux+decode {errors.Count}/{files.Length - skippedNonVideo} video files:\n{string.Join("\n", errors)}");
        Assert.True(demuxed > 0, "No files were demuxed");
    }
}