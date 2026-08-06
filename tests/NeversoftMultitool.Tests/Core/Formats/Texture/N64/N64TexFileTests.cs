using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.N64;

/// <summary>
///     Pins the N64 texture decode (2026-08-05) against fixtures whose
///     appearance was verified externally: 'abutton' renders the N64
///     controller's blue A button (RGBA16 + one mip), 's2ddi02t' reproduces
///     the THPS2 SWEET HEART deck art layout (CI4, authored recolor variant
///     of the PS1 original), and 'psxtxt_bfd7c623' matches the PS1
///     skven_l.psx porta-potty prop via the texture-id join — the fixture
///     that established 64-bit row padding (32-bit padding scrambles it).
/// </summary>
public sealed class N64TexFileTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    private static Dictionary<string, byte[]> CarveThps2(TestPaths paths)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets.ToDictionary(static asset => asset.Path, static asset => asset.Data);
    }

    [Theory]
    [InlineData("textures/abutton.tex.n64", "abutton", 16, 16, "RGBA16",
        "aa475cbc0a9bd397da92b4c38627c112a2c8bac94e9abb201c5881f12778d93d")]
    [InlineData("textures/s2ddi02t.tex.n64", "s2ddi02t", 32, 128, "CI4",
        "62f410a0059c4be01a544bd772dca18d7d669bcdbab884f05df7f1882c8a5cc4")]
    [InlineData("textures/psxtxt_bfd7c623.tex.n64", "psxtxt_bfd7c623", 24, 48, "CI4",
        "95a0e896c5fb5c944bcd0c2d899514868ee1ec226e547a442865eda0396773b6")]
    public void DictionaryRecords_DecodeTheVerifiedFixtures(
        string assetPath,
        string expectedName,
        int expectedWidth,
        int expectedHeight,
        string expectedFormat,
        string expectedRgbaSha256)
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets[assetPath]);

        Assert.Equal(expectedName, texture.Name);
        Assert.Equal(expectedWidth, texture.Width);
        Assert.Equal(expectedHeight, texture.Height);
        Assert.Equal(expectedFormat, texture.Format);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(texture.Rgba));
        Assert.True(expectedRgbaSha256 == actualSha, $"RGBA sha mismatch: actual {actualSha}");
    }

    [Fact]
    public void ImageRecord_DecodesTheMenuBackground()
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets["images/003.img.n64"]);

        Assert.Equal(512, texture.Width);
        Assert.Equal(240, texture.Height);
        Assert.Equal("CI8", texture.Format);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(texture.Rgba));
        Assert.True(
            actualSha == "5e745cf04c7d6abf3a76bed7ba77a867e67b3e47367f5c84ba53d28d8912b155",
            $"RGBA sha mismatch: actual {actualSha}");
    }

    /// <summary>
    ///     Full-corpus sweeps: every carved texture and image record in every
    ///     ROM must decode without error.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 1_488, 228)]
    [InlineData(Thps2N64Build, RomName, 2_905, 99)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 2_484, 109)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 2_582, 46)]
    public void EveryCarvedTextureDecodes(
        string buildName,
        string romName,
        int expectedTextures,
        int expectedImages)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        var textures = 0;
        var images = 0;
        foreach (var asset in assets)
        {
            if (asset.Path.EndsWith(".tex.n64", StringComparison.Ordinal))
            {
                var texture = N64TexFile.Decode(asset.Data);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
                textures++;
            }
            else if (asset.Path.EndsWith(".img.n64", StringComparison.Ordinal))
            {
                var texture = N64TexFile.Decode(asset.Data);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
                images++;
            }
        }

        Assert.Equal(expectedTextures, textures);
        Assert.Equal(expectedImages, images);
    }
}
