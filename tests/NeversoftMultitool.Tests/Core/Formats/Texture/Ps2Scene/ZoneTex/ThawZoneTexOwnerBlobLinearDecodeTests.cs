using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Validates THAW PS2 worldzone texture decode against PC .tex.wpc ground truth.
///     The PC PNGs at TestOutput/z_bh_pc/textures/ were decoded by the existing xbxtex
///     command and represent what each checksum should look like. This test catches the
///     "checkerboard" regression class where the owner-blob decoder applied an extra
///     swizzle on top of already-linear data (FUN_0019cd48 reads pixels linearly).
/// </summary>
public class ThawZoneTexOwnerBlobLinearDecodeTests(TestPaths paths)
{
    private const string ThawBuild = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    /// <summary>Per-channel mean-absolute-error budget. PC ground truth is DXT-compressed
    ///     and PS2 paletted, so an exact RGBA match isn't expected — but visibly broken
    ///     output (checkerboard, banding) blows past this threshold easily.</summary>
    private const double MaxMeanAbsoluteError = 32.0;

    public static IEnumerable<TheoryDataRow<string, uint, int, int>> AnchorAndFailingRows()
    {
        // Anchor rows — currently decode pixel-perfect against PC ground truth.
        // Must remain green after the fix. Cover both PSMT4 and PSMT8 at 128x128
        // since the gate keeps Conv4to32 / Conv8to32 active for those dimensions.
        yield return new("anchor_grass_PSMT4_128x128",      0x2AC5ACDEu, 128, 128);
        yield return new("anchor_PSMT8_128x128_023292D1",   0x023292D1u, 128, 128);
        yield return new("anchor_PSMT8_128x128_25423C23",   0x25423C23u, 128, 128);

        // Currently-failing rows — checkerboard / banding artifacts visible in
        // TestOutput/z_bh_v12/worldzone_debug/textures/. PC ground truth available
        // in TestOutput/z_bh_pc/textures/004537A0/.
        yield return new("door_PSMT8_64x128",               0xD2411F1Au,  64, 128);
        yield return new("pillar_PSMT8_64x128",             0x6CC9E390u,  64, 128);
        yield return new("PSMT8_64x128_6F102B17",           0x6F102B17u,  64, 128);
        yield return new("PSMT8_64x128_3A60C402",           0x3A60C402u,  64, 128);
        yield return new("PSMT8_64x128_15E6005A",           0x15E6005Au,  64, 128);
        yield return new("PSMT8_64x128_93BC556C",           0x93BC556Cu,  64, 128);
        yield return new("PSMT8_64x128_B0F5C21F",           0xB0F5C21Fu,  64, 128);
        yield return new("PSMT8_64x128_C5807499",           0xC5807499u,  64, 128);
        yield return new("PSMT8_64x128_CD2F89B8",           0xCD2F89B8u,  64, 128);
        yield return new("PSMT8_32x128_D96E1609",           0xD96E1609u,  32, 128);
    }

    [Theory]
    [MemberData(nameof(AnchorAndFailingRows))]
    public void DecodedTexture_MatchesPcGroundTruth(string label, uint checksum, int width, int height)
    {
        _ = label; // surfaced via the row id; unused inside the test body.

        var pakPath = paths.FindSampleFile(ThawBuild, "z_bh.pak.ps2");
        Assert.SkipWhen(pakPath is null, "z_bh.pak.ps2 not available under Sample/Builds");

        var pcPngPath = ResolvePcGroundTruthPath("z_bh_pc", checksum);
        Assert.SkipWhen(pcPngPath is null,
            $"PC ground truth PNG for {checksum:X8} not present under TestOutput/z_bh_pc/textures/");

        Assert.True(ZoneTextureCatalog.TryBuild(pakPath, out var catalog),
            "ZoneTextureCatalog.TryBuild should succeed for z_bh.pak.ps2");
        Assert.NotNull(catalog);

        var actual = catalog!
            .Entries
            .Select(static entry => entry.Checksum)
            .Distinct()
            .Where(cs => cs == checksum)
            .Select(cs => DecodeOne(catalog!, cs))
            .FirstOrDefault(static texture => texture is not null);
        Assert.NotNull(actual);
        Assert.Equal(width, actual!.Width);
        Assert.Equal(height, actual.Height);
        Assert.NotNull(actual.Pixels);

        var (mae, mrR, mrG, mrB, mrA) = MeanAbsoluteRgbaError(actual.Pixels!, width, height, pcPngPath!);
        Assert.True(mae <= MaxMeanAbsoluteError,
            $"Texture 0x{checksum:X8} ({width}x{height}) MAE={mae:F2} " +
            $"(R={mrR:F2} G={mrG:F2} B={mrB:F2} A={mrA:F2}) " +
            $"exceeds budget {MaxMeanAbsoluteError}. " +
            $"PC ground truth: {pcPngPath}");
    }

    [Fact]
    public void DecodeTexture_UsesAlphaBearingMipWhenBaseLevelAlphaIsEmpty()
    {
        var pakPath = paths.FindSampleFile(ThawBuild, "z_sm.pak.ps2");
        Assert.SkipWhen(pakPath is null, "z_sm.pak.ps2 not available under Sample/Builds");

        var pcPngPath = ResolvePcGroundTruthPath("z_sm_pc", 0x75AF4E14u);
        Assert.SkipWhen(pcPngPath is null,
            "PC ground truth PNG for 75AF4E14 not present under TestOutput/z_sm_pc/textures/");

        Assert.True(ZoneTextureCatalog.TryBuild(pakPath, out var catalog),
            "ZoneTextureCatalog.TryBuild should succeed for z_sm.pak.ps2");
        Assert.NotNull(catalog);

        var actual = DecodeOne(catalog!, 0xE6ED88DEu);
        Assert.NotNull(actual);
        Assert.Equal(128, actual!.Width);
        Assert.Equal(64, actual.Height);
        Assert.NotNull(actual.Pixels);

        var (mae, mrR, mrG, mrB, mrA) = MeanAbsoluteRgbaError(
            actual.Pixels!, actual.Width, actual.Height, pcPngPath!);
        Assert.True(mae <= MaxMeanAbsoluteError,
            $"Texture 0xE6ED88DE should decode from the alpha-bearing 128x64 mip. " +
            $"MAE={mae:F2} (R={mrR:F2} G={mrG:F2} B={mrB:F2} A={mrA:F2}) " +
            $"exceeds budget {MaxMeanAbsoluteError}. PC ground truth: {pcPngPath}");
    }

    private static Ps2Texture? DecodeOne(ZoneTextureCatalog catalog, uint checksum)
    {
        var provider = catalog.CreateTextureResolver();
        var png = provider(checksum);
        if (png is null) return null;

        // Re-read dimensions/pixels from the produced PNG. Some THAW PS2 owner-blob
        // records carry unusable top-level alpha and need to export the first
        // alpha-bearing mip instead.
        var entry = catalog.Entries.First(e => e.Checksum == checksum);
        using var image = Image.Load<Rgba32>(png);

        var rgba = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(rgba);
        return new Ps2Texture(checksum, image.Width, image.Height,
            (uint)((entry.Tex0 >> 20) & 0x3F),
            (uint)((entry.Tex0 >> 51) & 0xF),
            rgba);
    }

    private string? ResolvePcGroundTruthPath(string outputStem, uint checksum)
    {
        // PC ground truth lives at <repoRoot>/TestOutput/<outputStem>/textures/<dir>/<CHECKSUM>.png.
        // TestPaths.TestOutputDir resolves to tests/TestOutput, so also check the sibling
        // TestOutput directory at the repo root.
        var name = $"{checksum:X8}.png";
        foreach (var candidateRoot in EnumerateTestOutputCandidates())
        {
            var pcRoot = Path.Combine(candidateRoot, outputStem, "textures");
            if (!Directory.Exists(pcRoot)) continue;
            var found = Directory.EnumerateFiles(pcRoot, name, SearchOption.AllDirectories).FirstOrDefault();
            if (found != null) return found;
        }

        return null;
    }

    private IEnumerable<string> EnumerateTestOutputCandidates()
    {
        if (paths.TestOutputDir != null)
            yield return paths.TestOutputDir;

        // Walk up from TestOutputDir looking for a sibling TestOutput at a higher level
        // (repo root has its own TestOutput in addition to tests/TestOutput).
        var dir = paths.TestOutputDir != null ? Path.GetDirectoryName(paths.TestOutputDir) : null;
        for (var i = 0; dir != null && i < 6; i++)
        {
            dir = Path.GetDirectoryName(dir);
            if (dir == null) yield break;
            var candidate = Path.Combine(dir, "TestOutput");
            if (Directory.Exists(candidate))
                yield return candidate;
        }
    }

    private static (double Total, double R, double G, double B, double A) MeanAbsoluteRgbaError(
        byte[] actualRgba, int width, int height, string expectedPngPath)
    {
        using var expectedImage = Image.Load<Rgba32>(expectedPngPath);
        if (expectedImage.Width != width || expectedImage.Height != height)
            return (double.PositiveInfinity, 0, 0, 0, 0);

        var expected = new byte[width * height * 4];
        expectedImage.CopyPixelDataTo(expected);

        long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
        var pixelCount = width * height;
        var visibleCount = 0;
        for (var i = 0; i < pixelCount; i++)
        {
            // Alpha is always compared. RGB is ignored for pixels that are fully
            // transparent in BOTH images — the PS2 decoder keeps the palette[0]
            // RGB even for alpha=0 entries while xbxtex normalizes RGB to zero,
            // so the channels disagree at invisible pixels. Only visible-pixel
            // RGB drift is meaningful.
            sumA += Math.Abs(actualRgba[i * 4 + 3] - expected[i * 4 + 3]);
            if (actualRgba[i * 4 + 3] == 0 && expected[i * 4 + 3] == 0)
                continue;
            sumR += Math.Abs(actualRgba[i * 4] - expected[i * 4]);
            sumG += Math.Abs(actualRgba[i * 4 + 1] - expected[i * 4 + 1]);
            sumB += Math.Abs(actualRgba[i * 4 + 2] - expected[i * 4 + 2]);
            visibleCount++;
        }

        var mrR = visibleCount > 0 ? sumR / (double)visibleCount : 0;
        var mrG = visibleCount > 0 ? sumG / (double)visibleCount : 0;
        var mrB = visibleCount > 0 ? sumB / (double)visibleCount : 0;
        var mrA = sumA / (double)pixelCount;
        return ((mrR + mrG + mrB + mrA) / 4.0, mrR, mrG, mrB, mrA);
    }
}
