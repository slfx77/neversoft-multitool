using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class ThawTexFileTests(TestPaths paths)
{
    private string TexDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "TEX");

    // ── IsThawTex ──

    [Fact]
    public void IsThawTex_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawTexFile.IsThawTex([]));
    }

    [Fact]
    public void IsThawTex_WrongMagic_ReturnsFalse()
    {
        Assert.False(ThawTexFile.IsThawTex(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void IsThawTex_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(TexDir, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(!File.Exists(file), "acc_backpack01.tex.wpc not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawTexFile.IsThawTex(data));
    }

    [Fact]
    public void IsThawTex_XbxTexFile_ReturnsFalse()
    {
        // XbxTexFile has version=1 as u32, not 0xABADD00D magic
        Assert.False(ThawTexFile.IsThawTex(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }));
    }

    // ── Parse known files ──

    [Fact]
    public void Parse_AccBackpack01_ExtractsTexture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(TexDir, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(!File.Exists(file), "acc_backpack01.tex.wpc not found");

        var result = ThawTexFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Textures);

        var tex = result.Textures[0];
        Assert.Equal(0xA85AD56Bu, tex.Checksum);
        Assert.Equal(64, tex.Width);
        Assert.Equal(64, tex.Height);
        Assert.NotNull(tex.Pixels);
        Assert.Equal(64 * 64 * 4, tex.Pixels!.Length);
    }

    [Fact]
    public void Parse_TexturesHaveValidDimensions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(TexDir, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(!File.Exists(file), "acc_backpack01.tex.wpc not found");

        var result = ThawTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        foreach (var tex in result.Textures)
        {
            Assert.True(tex.Width > 0 && tex.Width <= 2048, $"Width {tex.Width} out of range");
            Assert.True(tex.Height > 0 && tex.Height <= 2048, $"Height {tex.Height} out of range");
            Assert.NotNull(tex.Pixels);
            Assert.Equal(tex.Width * tex.Height * 4, tex.Pixels!.Length);
        }
    }

    // ── Batch parse all TEX files ──

    [Fact]
    public void BatchParse_AllThawTexWpc_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(TexDir), "TEX directory not found");

        var files = Directory.GetFiles(TexDir, "*.tex.wpc", SearchOption.TopDirectoryOnly);
        Assert.SkipWhen(files.Length == 0, "No TEX files found");

        var failures = new List<string>();
        var totalTextures = 0;

        foreach (var file in files)
        {
            var result = ThawTexFile.Parse(file);
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
}
