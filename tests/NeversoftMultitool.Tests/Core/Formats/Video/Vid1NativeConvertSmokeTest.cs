using NeversoftMultitool.Core.Formats.Vid1;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1NativeConvertSmokeTest(TestPaths paths)
{
    private const string ThawGcFinalBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const long IntroVidSize = 18_840_160;
    private const int IntroWidth = 512;
    private const int IntroHeight = 384;
    private const int IntroFrameCount = 1_292;
    private static readonly string IntroVidRelativePath =
        Path.Combine("movies", "vid", "intro.vid");

    private string? FindIntroVid()
    {
        if (!paths.HasSampleBuilds)
            return null;

        var candidate = Path.Combine(paths.SampleBuildsDir!, ThawGcFinalBuild, IntroVidRelativePath);
        return File.Exists(candidate) ? candidate : null;
    }

    [CorpusFact]
    public void ConvertToMp4_IntroVid_ProducesNonEmptyFile()
    {
        var input = FindIntroVid();
        Assert.SkipWhen(input is null,
            $"External fixture {ThawGcFinalBuild}/{IntroVidRelativePath.Replace('\\', '/')} is not available");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() is null, "ffmpeg not found on PATH");

        Assert.Equal(IntroVidSize, new FileInfo(input!).Length);
        var file = Vid1VideoFile.Parse(input!);
        Assert.Equal(IntroWidth, file.Width);
        Assert.Equal(IntroHeight, file.Height);
        Assert.Equal(IntroFrameCount, file.FrameCount);

        var outputDir = FormatProbeTestHelper.CreateTempDirectory("vid1_native_convert");

        try
        {
            var result = Vid1VideoConverter.ConvertToMp4(
                input!,
                outputDir,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(result.Success, $"ConvertToMp4 failed: {result.ErrorMessage}");
            Assert.NotNull(result.OutputPath);
            Assert.Equal(Path.Combine(outputDir, "intro.mp4"), result.OutputPath);
            Assert.True(File.Exists(result.OutputPath!), $"output file missing: {result.OutputPath}");

            var size = new FileInfo(result.OutputPath!).Length;
            Assert.True(size > 100_000,
                $"MP4 file suspiciously small ({size} bytes) — pipeline likely failed silently");
        }
        finally
        {
            Directory.Delete(outputDir, true);
        }
    }
}
