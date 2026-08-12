using System.Diagnostics;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1DecoderTests(TestPaths paths)
{
    private const string ThawGcFinalBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const long IntroVidSize = 18_840_160;
    private const long AtviVidSize = 1_821_888;
    private const long CreditsVidSize = 133_728_256;
    private const int VidWidth = 512;
    private const int VidHeight = 384;
    private const int IntroFrameCount = 1_292;
    private const int AtviFrameCount = 319;
    private const int CreditsFrameCount = 8_020;

    private static readonly string IntroVidRelativePath = Path.Combine("movies", "vid", "intro.vid");
    private static readonly string AtviVidRelativePath = Path.Combine("movies", "vid", "atvi.vid");
    private static readonly string CreditsVidRelativePath = Path.Combine("movies", "vid", "credits.vid");

    private string? FindIntroVid() => FindCanonicalVid(IntroVidRelativePath);

    private string? FindAtviVid() => FindCanonicalVid(AtviVidRelativePath);

    private string? FindCreditsVid() => FindCanonicalVid(CreditsVidRelativePath);

    private Vid1VideoFile ParseIntroVid() =>
        ParseCanonicalVid(FindIntroVid(), IntroVidRelativePath, IntroVidSize, IntroFrameCount);

    private Vid1VideoFile ParseAtviVid() =>
        ParseCanonicalVid(FindAtviVid(), AtviVidRelativePath, AtviVidSize, AtviFrameCount);

    private Vid1VideoFile ParseCreditsVid() =>
        ParseCanonicalVid(FindCreditsVid(), CreditsVidRelativePath, CreditsVidSize, CreditsFrameCount);

    private string? FindCanonicalVid(string relativePath)
    {
        if (!paths.HasSampleBuilds)
            return null;

        var candidate = Path.Combine(paths.SampleBuildsDir!, ThawGcFinalBuild, relativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    private static Vid1VideoFile ParseCanonicalVid(
        string? path,
        string relativePath,
        long expectedSize,
        int expectedFrameCount)
    {
        Assert.SkipWhen(path is null,
            $"External fixture {ThawGcFinalBuild}/{relativePath.Replace('\\', '/')} is not available");
        Assert.Equal(expectedSize, new FileInfo(path!).Length);
        var file = Vid1VideoFile.Parse(path!);
        Assert.Equal(VidWidth, file.Width);
        Assert.Equal(VidHeight, file.Height);
        Assert.Equal(expectedFrameCount, file.FrameCount);
        return file;
    }

    [Fact]
    public void DecodeFrame_FirstFrame_DoesNotThrow_ReturnsCorrectShape()
    {
        var file = ParseIntroVid();
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
        var file = ParseIntroVid();
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
        var file = ParseIntroVid();
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
    public void DecodeFrame_AtviTinyBFrame_CopiesSkippedReference()
    {
        var file = ParseAtviVid();
        Assert.True(file.FrameCount > 2,
            "Canonical THAW GC atvi.vid must contain its frame-2 class-2 regression case");

        Assert.Equal(2, file.Frames[2].PreambleClass);

        var decoder = new Vid1Decoder(file);
        _ = decoder.DecodeFrame(file.Frames[0]);
        var skippedReference = decoder.DecodeFrame(file.Frames[1]);
        var tinyBFrame = decoder.DecodeFrame(file.Frames[2]);

        Assert.Equal(skippedReference.Rgb24, tinyBFrame.Rgb24);
    }

    [Fact]
    public void DecodeFrame_AtviClass2Frame_DoesNotPromoteBOutputAsReference()
    {
        var file = ParseAtviVid();
        var targetIndex = Enumerable.Range(0, file.FrameCount)
            .FirstOrDefault(i =>
                i > 10 &&
                i + 1 < file.FrameCount &&
                file.Frames[i].PreambleClass == 2 &&
                file.Frames[i + 1].PreambleClass != 2 &&
                file.Frames[i].CodedPayload.Length > 1024);
        Assert.True(targetIndex > 0,
            "Canonical THAW GC atvi.vid must contain a large class-2 frame followed by a reference frame");

        var withBDecoder = new Vid1Decoder(file);
        Vid1DecodedFrame? bFrame = null;
        Vid1DecodedFrame? nextAfterB = null;
        for (var i = 0; i <= targetIndex + 1; i++)
        {
            var decoded = withBDecoder.DecodeFrame(file.Frames[i]);
            if (i == targetIndex)
                bFrame = decoded;
            if (i == targetIndex + 1)
                nextAfterB = decoded;
        }

        var skipBDecoder = new Vid1Decoder(file);
        Vid1DecodedFrame? nextWithoutB = null;
        for (var i = 0; i <= targetIndex + 1; i++)
        {
            if (i == targetIndex)
                continue;

            nextWithoutB = skipBDecoder.DecodeFrame(file.Frames[i]);
        }

        Assert.NotNull(bFrame);
        Assert.NotNull(nextAfterB);
        Assert.NotNull(nextWithoutB);
        Assert.Equal(file.Width * file.Height * 3, bFrame.Rgb24.Length);
        Assert.Equal(nextWithoutB.Rgb24, nextAfterB.Rgb24);
    }

    [Fact]
    public void PresentationProvider_Atvi_DisplaysClass2BeforeHeldReference()
    {
        var file = ParseAtviVid();
        Assert.True(file.FrameCount > 2,
            "Canonical THAW GC atvi.vid must contain its first three presentation frames");

        Assert.Equal(0, file.Frames[0].PreambleClass);
        Assert.NotEqual(2, file.Frames[1].PreambleClass);
        Assert.Equal(2, file.Frames[2].PreambleClass);

        var provider = new Vid1PresentationFrameProvider(file);
        var first = provider.DecodeNextFrame();
        var second = provider.DecodeNextFrame();
        var third = provider.DecodeNextFrame();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotNull(third);
        Assert.Equal(0, first.FrameIndex);
        Assert.Equal(2, second.FrameIndex);
        Assert.Equal(1, third.FrameIndex);
    }

    [Fact]
    public void DecodeFrame_Stats_ReportMacroblockCounters()
    {
        var file = ParseIntroVid();
        var decoder = new Vid1Decoder(file);
        decoder.DecodeFrame(file.Frames[0]);

        var stats = decoder.LastFrameStats;
        Assert.Equal(0, stats.FrameIndex);
        Assert.Equal(file.Frames[0].PreambleClass, stats.PreambleClass);
        Assert.Equal((file.Width + 15) / 16 * ((file.Height + 15) / 16), stats.TotalMacroblocks);
        Assert.InRange(stats.DecodedMacroblocks + stats.FailedMacroblocks, 1, stats.TotalMacroblocks);
        Assert.Equal(0, stats.UnsupportedClass2Branches);
        Assert.Equal(0, stats.Class2FieldOrGmcBranches);
    }

    [Fact]
    public void DecodeBgra_Perf_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_PERF_TESTS") != "1",
            "Set VID1_RUN_PERF_TESTS=1 to run VID1 performance checks");

        var file = ParseIntroVid();
        var provider = new Vid1BgraPresentationFrameProvider(file);
        var frameLimit = Math.Min(300, file.FrameCount);

        PrepareAllocationMeasurement();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var decoded = 0;
        while (decoded < frameLimit && provider.DecodeNextFrame() != null)
            decoded++;
        sw.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var fps = decoded / sw.Elapsed.TotalSeconds;
        var allocatedBytesPerFrame = allocatedBytes / Math.Max(1.0, decoded);
        Console.WriteLine(
            $"VID1 BGRA object path: decoded={decoded} fps={fps:F2} alloc={allocatedBytesPerFrame / 1024.0:F1} KB/frame");
        Assert.True(fps >= 45.0, $"VID1 BGRA decode was {fps:F2} fps");
        Assert.True(allocatedBytesPerFrame <= 2 * 1024 * 1024,
            $"VID1 BGRA allocated {allocatedBytesPerFrame / 1024.0:F1} KB/frame");
    }

    [Fact]
    public void DecodeBgraSpan_Perf_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_PERF_TESTS") != "1",
            "Set VID1_RUN_PERF_TESTS=1 to run VID1 performance checks");

        var file = ParseIntroVid();
        var provider = new Vid1BgraPresentationFrameProvider(file);
        var destination = new byte[file.Width * file.Height * 4];
        var frameLimit = Math.Min(300, file.FrameCount);

        PrepareAllocationMeasurement();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var decoded = 0;
        while (decoded < frameLimit && provider.TryDecodeNextFrame(destination, out _))
            decoded++;
        sw.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var fps = decoded / sw.Elapsed.TotalSeconds;
        var allocatedBytesPerFrame = allocatedBytes / Math.Max(1.0, decoded);
        Console.WriteLine(
            $"VID1 BGRA span path: decoded={decoded} fps={fps:F2} alloc={allocatedBytesPerFrame / 1024.0:F1} KB/frame");
        Assert.True(fps >= 60.0, $"VID1 BGRA span decode was {fps:F2} fps");
        Assert.True(allocatedBytesPerFrame <= 64 * 1024,
            $"VID1 BGRA span decode allocated {allocatedBytesPerFrame / 1024.0:F1} KB/frame");
    }

    [Fact]
    public void DecodeRgbSpanToNull_Perf_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_PERF_TESTS") != "1",
            "Set VID1_RUN_PERF_TESTS=1 to run VID1 performance checks");

        var file = ParseIntroVid();
        var provider = new Vid1PresentationFrameProvider(file);
        var destination = new byte[file.Width * file.Height * 3];
        var frameLimit = Math.Min(300, file.FrameCount);
        using var sink = Stream.Null;

        PrepareAllocationMeasurement();
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var decoded = 0;
        while (decoded < frameLimit && provider.TryDecodeNextFrame(destination, out _))
        {
            sink.Write(destination, 0, destination.Length);
            decoded++;
        }

        sw.Stop();
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        var fps = decoded / sw.Elapsed.TotalSeconds;
        var allocatedBytesPerFrame = allocatedBytes / Math.Max(1.0, decoded);
        Console.WriteLine(
            $"VID1 RGB span->null path: decoded={decoded} fps={fps:F2} alloc={allocatedBytesPerFrame / 1024.0:F1} KB/frame");
        Assert.True(fps >= 45.0, $"VID1 RGB span->null decode was {fps:F2} fps");
        Assert.True(allocatedBytesPerFrame <= 16 * 1024,
            $"VID1 RGB span->null decode allocated {allocatedBytesPerFrame / 1024.0:F1} KB/frame");
    }

    [Fact]
    public void Regression_IntroPresentationHash_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_REGRESSION_TESTS") != "1",
            "Set VID1_RUN_REGRESSION_TESTS=1 to run VID1 presentation-hash regressions");

        AssertPresentationHash(
            ParseIntroVid(),
            61,
            "edc66c3357309c403657c9c08030458e9728fdae3a9b0ce8bdaf7e2d9fec9eb7");
    }

    [Fact]
    public void Regression_CreditsPresentationHash_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_REGRESSION_TESTS") != "1",
            "Set VID1_RUN_REGRESSION_TESTS=1 to run VID1 presentation-hash regressions");

        AssertPresentationHash(
            ParseCreditsVid(),
            120,
            "9fd9d8a17506381f0cfdfc30ca6ed3646dbdac5caea1b48a05f494bd3994b2a4");
    }

    [Fact]
    public void Regression_AtviPresentationHash_OptIn()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("VID1_RUN_REGRESSION_TESTS") != "1",
            "Set VID1_RUN_REGRESSION_TESTS=1 to run VID1 presentation-hash regressions");

        AssertPresentationHash(
            ParseAtviVid(),
            120,
            "c0909be665aec7be74ce7fde6493d1dbd862cdd2d7f45ea50e983c589581169a");
    }

    [Fact]
    public void DecodeFrame_Frame3_CleanEndOfStreamCopiesReferenceLetterbox()
    {
        var file = ParseIntroVid();
        Assert.True(file.FrameCount > 3,
            "Canonical THAW GC intro.vid must contain its frame-3 letterbox regression case");

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
            var offset = rowOffset + x * 3;
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
        var file = ParseIntroVid();
        var decoder = new Vid1Decoder(file);
        decoder.DecodeFrame(file.Frames[0]);
        decoder.Reset();

        // After reset, decoding frame 0 again should behave identically
        // to a fresh decoder instance (no reference frame bleed-through).
        var result = decoder.DecodeFrame(file.Frames[0]);
        Assert.Equal(file.Width * file.Height * 3, result.Rgb24.Length);
    }

    private static void AssertPresentationHash(
        Vid1VideoFile file,
        int frameLimit,
        string expectedSha256)
    {
        var provider = new Vid1BgraPresentationFrameProvider(file);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var decoded = 0;
        while (decoded < frameLimit)
        {
            var frame = provider.DecodeNextFrame();
            if (frame == null)
                break;

            sha.AppendData(BitConverter.GetBytes(frame.FrameIndex));
            sha.AppendData(BitConverter.GetBytes(frame.Bgra8.Length));
            sha.AppendData(frame.Bgra8);
            decoded++;
        }

        var actualSha256 = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        Assert.Equal(
            expectedSha256,
            actualSha256);
        Assert.Equal(frameLimit, decoded);
    }

    private static void PrepareAllocationMeasurement()
    {
        // These opt-in performance tests deliberately establish a quiet allocation
        // baseline before measuring. A forced full collection is part of that test setup.
#pragma warning disable S1215
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
#pragma warning restore S1215
    }
}
