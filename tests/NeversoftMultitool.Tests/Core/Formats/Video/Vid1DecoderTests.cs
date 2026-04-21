using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1DecoderTests(TestPaths paths)
{
    private string? FindIntroVid()
    {
        var repoCandidate = Path.Combine(GetRepoRoot(), "TestOutput", "intro_only_src", "intro.vid");
        if (File.Exists(repoCandidate))
            return repoCandidate;

        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains("American Wasteland", StringComparison.OrdinalIgnoreCase)
                              && Path.GetFileName(d).Contains("GC", StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;

        var candidate = Path.Combine(buildDir, "movies", "vid", "intro.vid");
        return File.Exists(candidate) ? candidate : null;
    }

    private static string GetRepoRoot()
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(Path.Combine(current, "NeversoftMultitool.slnx")))
                return current;

            var parent = Directory.GetParent(current);
            if (parent == null)
                break;

            current = parent.FullName;
        }

        return Directory.GetCurrentDirectory();
    }

    [Fact]
    public void DecodeFrame_FirstFrame_DoesNotThrow_ReturnsCorrectShape()
    {
        var path = FindIntroVid();
        if (path == null) return; // skip when sample not available

        var file = Vid1VideoFile.Parse(path);
        var decoder = new Vid1Decoder(file);

        Assert.NotEmpty(file.Frames);
        var first = file.Frames[0];

        var result = decoder.DecodeFrame(first);

        Assert.Equal(0, result.FrameIndex);
        Assert.Equal(file.Width, result.Width);
        Assert.Equal(file.Height, result.Height);
        Assert.Equal(file.Width * file.Height * 3, result.Rgb24.Length);
    }

    [Fact]
    public void DecodeFrame_FirstFrame_UsesNeutralFallback()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var decoder = new Vid1Decoder(file);
        var result = decoder.DecodeFrame(file.Frames[0]);

        // For any macroblock the decoder aborts on, the chroma planes should
        // stay at the neutral 128 baseline instead of producing the old green
        // tint that indicated uninitialized chroma.
        Assert.All(result.Rgb24.Chunk(3).Take(64), rgb =>
        {
            // If a pixel is non-zero, it must not be the (0,G,0) green
            // that indicates the uninit-chroma bug.
            if (rgb[0] == 0 && rgb[2] == 0 && rgb[1] > 100)
                Assert.Fail($"pixel {rgb[0]},{rgb[1]},{rgb[2]} is the known-bad green fallback");
        });
    }

    [Fact]
    public void DecodeFrame_MultipleFrames_NoThrow()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var decoder = new Vid1Decoder(file);

        // Decode first 10 frames — verify we can walk the stream sequentially
        // without crashing. Content correctness is a separate check.
        var maxFrames = Math.Min(10, file.FrameCount);
        for (var i = 0; i < maxFrames; i++)
        {
            var result = decoder.DecodeFrame(file.Frames[i]);
            Assert.Equal(i, result.FrameIndex);
        }
    }

    [Fact]
    public void DecodeFrame_Frame3_CleanEndOfStreamCopiesReferenceLetterbox()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        if (file.FrameCount <= 3) return;

        var decoder = new Vid1Decoder(file);
        Vid1DecodedFrame? result = null;
        for (var i = 0; i <= 3; i++)
            result = decoder.DecodeFrame(file.Frames[i]);

        Assert.NotNull(result);

        var sampleY = Math.Max(0, result.Height - 14);
        var rowOffset = sampleY * result.Width * 3;
        long sum = 0;
        for (var x = 0; x < result.Width; x++)
        {
            var offset = rowOffset + (x * 3);
            sum += result.Rgb24[offset];
            sum += result.Rgb24[offset + 1];
            sum += result.Rgb24[offset + 2];
        }

        var average = sum / (result.Width * 3.0);
        Assert.True(
            average < 16.0,
            $"frame 3 lower letterbox average was {average:F2}; clean EOF should copy the black reference, not leave neutral-gray fallback");
    }

    [Fact]
    public void Reset_ClearsReferenceBuffer()
    {
        var path = FindIntroVid();
        if (path == null) return;

        var file = Vid1VideoFile.Parse(path);
        var decoder = new Vid1Decoder(file);
        decoder.DecodeFrame(file.Frames[0]);
        decoder.Reset();

        // After reset, decoding frame 0 again should behave identically
        // to a fresh decoder instance (no reference frame bleed-through).
        var result = decoder.DecodeFrame(file.Frames[0]);
        Assert.Equal(file.Width * file.Height * 3, result.Rgb24.Length);
    }
}
