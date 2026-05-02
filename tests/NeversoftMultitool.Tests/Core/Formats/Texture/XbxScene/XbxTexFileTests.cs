using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class XbxTexFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    // ── IsTexFile ──

    [Fact]
    public void IsTexFile_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(XbxTexFile.IsTexFile(data));
    }

    [Fact]
    public void IsTexFile_EmptyData_ReturnsFalse()
    {
        Assert.False(XbxTexFile.IsTexFile([]));
    }

    [Fact]
    public void IsTexFile_WrongVersion_ReturnsFalse()
    {
        Assert.False(XbxTexFile.IsTexFile(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    // ── Parse known files ──

    [Fact]
    public void Parse_AP_Has195Textures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(195, result.Textures.Count);
    }

    [Fact]
    public void Parse_AP_TexturesHaveValidDimensions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        foreach (var tex in result.Textures)
        {
            Assert.True(tex.Width > 0 && tex.Width <= 2048, $"Width {tex.Width} out of range");
            Assert.True(tex.Height > 0 && tex.Height <= 2048, $"Height {tex.Height} out of range");
            Assert.NotNull(tex.Pixels);
            Assert.Equal(tex.Width * tex.Height * 4, tex.Pixels!.Length);
        }
    }

    [Fact]
    public void Parse_AP_TexturesHaveNonZeroChecksums()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        Assert.All(result.Textures, tex => Assert.True(tex.Checksum != 0, "Checksum should not be zero"));
    }

    // ── PNG output ──

    [Fact]
    public void SaveAllAsPng_AP_ProducesPngFiles()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        var outputDir = Path.Combine(Path.GetTempPath(), "xbxtex_test");

        try
        {
            var count = XbxTexFile.SaveAllAsPng(result, outputDir, "AP");
            Assert.Equal(195, count);

            var pngs = Directory.GetFiles(Path.Combine(outputDir, "AP"), "*.png");
            Assert.Equal(195, pngs.Length);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    // ── Batch parse all TEX files ──

    [Fact]
    public void Parse_AllTexFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.tex.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No TEX files found");

        var failures = new List<string>();
        var totalTextures = 0;

        foreach (var file in files)
        {
            var result = XbxTexFile.Parse(file);
            if (!result.Success)
            {
                failures.Add($"{Path.GetFileName(file)}: {result.ErrorMessage}");
                continue;
            }

            totalTextures += result.Textures.Count;
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} files failed:\n" +
            string.Join("\n", failures.Take(20)));
        Assert.True(totalTextures > 0, "Should have extracted textures");
    }

    // ── IMG tests ──

    [Fact]
    public void IsImgFile_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "black.img.xbx");
        Assert.SkipWhen(file is null, "black.img.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(XbxImgFile.IsImgFile(data));
    }

    [Fact]
    public void IsImgFile_EmptyData_ReturnsFalse()
    {
        Assert.False(XbxImgFile.IsImgFile([]));
    }

    [Fact]
    public void Parse_BlackImg_Succeeds()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "black.img.xbx");
        Assert.SkipWhen(file is null, "black.img.xbx not found");

        var result = XbxImgFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Textures);

        var tex = result.Textures[0];
        Assert.True(tex.Width > 0, "Width should be positive");
        Assert.True(tex.Height > 0, "Height should be positive");
        Assert.NotNull(tex.Pixels);
    }

    // ── Batch parse all IMG files ──

    [Fact]
    public void Parse_AllImgFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.img.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No IMG files found");

        var failures = new List<string>();

        foreach (var file in files)
        {
            var result = XbxImgFile.Parse(file);
            if (!result.Success)
                failures.Add($"{Path.GetFileName(file)}: {result.ErrorMessage}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} files failed:\n" +
            string.Join("\n", failures.Take(20)));
    }
}
