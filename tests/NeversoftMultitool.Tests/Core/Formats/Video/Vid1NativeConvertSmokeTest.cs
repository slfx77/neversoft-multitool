using NeversoftMultitool.Core.Formats.Video;
using NeversoftMultitool.Tests.Helpers;
using NeversoftMultitool.Core.Formats.Vid1;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class Vid1NativeConvertSmokeTest(TestPaths paths)
{
    private string? FindIntroVid()
    {
        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains("American Wasteland", StringComparison.OrdinalIgnoreCase)
                              && Path.GetFileName(d).Contains("GC", StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;

        var candidate = Path.Combine(buildDir, "movies", "vid", "intro.vid");
        return File.Exists(candidate) ? candidate : null;
    }

    [Fact]
    public void ConvertToMp4_IntroVid_ProducesNonEmptyFile()
    {
        var input = FindIntroVid();
        if (input == null) return;

        // Put output somewhere predictable; clean slate each run.
        var outputDir = Path.Combine(
            Path.GetDirectoryName(typeof(Vid1NativeConvertSmokeTest).Assembly.Location)!,
            "Vid1NativeSmoke");
        if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        Directory.CreateDirectory(outputDir);

        var result = Vid1VideoConverter.ConvertToMp4(input, outputDir);

        Assert.True(result.Success, $"ConvertToMp4 failed: {result.ErrorMessage}");
        Assert.NotNull(result.OutputPath);
        Assert.True(File.Exists(result.OutputPath!), $"output file missing: {result.OutputPath}");

        var size = new FileInfo(result.OutputPath!).Length;
        Assert.True(size > 100_000,
            $"MP4 file suspiciously small ({size} bytes) — pipeline likely failed silently");
    }
}
