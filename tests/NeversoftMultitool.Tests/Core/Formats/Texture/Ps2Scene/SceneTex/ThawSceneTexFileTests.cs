using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.SceneTex;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.SceneTex;

public sealed class ThawSceneTexFileTests(TestPaths paths)
{
    private const string ThawBuild = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    // ── IsThawSceneTex ──

    [Fact]
    public void IsThawSceneTex_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawSceneTexFile.IsThawSceneTex([]));
    }

    [Fact]
    public void IsThawSceneTex_WrongVersion_ReturnsFalse()
    {
        // Version 3 (standard Ps2TexFile)
        Assert.False(ThawSceneTexFile.IsThawSceneTex(
            new byte[]
            {
                0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00
            }));
    }

    [Fact]
    public void IsThawSceneTex_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawBuild, "acc_backpack01.tex.ps2");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.ps2 not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawSceneTexFile.IsThawSceneTex(data));
    }

    // ── Parse known files ──

    [Fact]
    public void Parse_AccBackpack01_ExtractsTexture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawBuild, "acc_backpack01.tex.ps2");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.ps2 not found");

        var result = ThawSceneTexFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Textures);

        // acc_backpack01 should have checksum 0xA85AD56B, 64x64 PSMT8
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
        var file = paths.FindSampleFile(ThawBuild, "acc_backpack01.tex.ps2");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.ps2 not found");

        var result = ThawSceneTexFile.Parse(file);
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
    public void Parse_MultiTexFile_ExtractsAll()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        // Find a multi-texture file (acc_backpack03 has 2 textures from previous analysis)
        var multiFile = paths.FindSampleFile(ThawBuild, "acc_backpack03.tex.ps2");
        Assert.SkipWhen(multiFile is null, "acc_backpack03.tex.ps2 not found");

        var result = ThawSceneTexFile.Parse(multiFile);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.Textures.Count >= 2, $"Expected 2+ textures, got {result.Textures.Count}");

        Assert.All(result.Textures, tex =>
        {
            Assert.True(tex.Checksum > 0xFFFF, $"Checksum 0x{tex.Checksum:X8} too small");
            Assert.True(tex.Width > 0 && tex.Width <= 2048);
            Assert.True(tex.Height > 0 && tex.Height <= 2048);
        });
    }

    // ── Batch parse all TEX files ──

    [Fact]
    public void BatchParse_AllThawSceneTex_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(ThawBuild, "*.tex.ps2").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .tex.ps2 files found");

        var failures = new List<string>();
        var totalTextures = 0;

        foreach (var file in files)
        {
            var result = ThawSceneTexFile.Parse(file);
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

    [Fact]
    public void ParsePermissive_ExtractedHollywoodZoneTex_ExtractsTexturesAndTex0Map()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var pakPath = paths.FindSampleFile(ThawBuild, "z_ho.pak.ps2");
        Assert.SkipWhen(pakPath is null, "z_ho.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_ZHoTex_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);

            var extractedDir = Path.Combine(tempDir, "z_ho.pak");
            var texPath = Directory.GetFiles(extractedDir, "*.tex", SearchOption.TopDirectoryOnly)
                .Single(path => Path.GetFileName(path).Equals("0009BF70.tex", StringComparison.OrdinalIgnoreCase));

            var texData = File.ReadAllBytes(texPath);
            var result = ThawSceneTexFile.ParsePermissive(texData);
            var tex0Map = ThawSceneTexFile.BuildTbpCbpMap(texData);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.NotEmpty(result.Textures);
            Assert.NotEmpty(tex0Map);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
