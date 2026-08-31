using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Psp;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2;

/// <summary>
///     THUG2 sets a GS-swizzle flag in the version-2 IMG MXL word (bits 20-21),
///     which the parser previously rejected outright ("IMG MXL must be zero") —
///     2,629 THUG2 PS2 sprites failed to decode at all, invisibly, because the
///     v2 corpus sweep only counted successes. Fixed 2026-08-26: the flagged
///     payload carries the same Conv8to32/Conv4to32 rearrange the TEX v3-5 path
///     undoes for MXL bit 30, applied at the stored buffer's dimensions before
///     the existing bottom-anchored de-stride.
///     Refereed by the Remix PSP twins (PspImgFile, a separate decoder sharing
///     no pixel code): the PSP oracle was VALIDATED first on the unflagged
///     class at 917/937 pixel-exact, after Xbox twins were measured and
///     REJECTED as an oracle (only 641/2,240 unflagged files match them —
///     cross-platform re-authoring plus 16-bit loadscreens). Against that
///     oracle the shipped rule is exact on 1,185/1,185 comparable flagged
///     files, while leaving the data linear matches 62 and reading it as the
///     PSMCT16 upload variant 370. Corroborated visually, since a wrong
///     rearrange cannot produce readable glyphs: the timer font's digits and
///     the ESRB panel's fine print both decode legibly.
/// </summary>
public class Ps2ImgV2GsSwizzleTests(TestPaths paths)
{
    private const string Thug2Ps2Build = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";
    private const string RemixPspBuild = "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";

    /// <summary>
    ///     The swizzle bits are accepted; a real mip count in the low bits is
    ///     still refused, because the version-2 single-image grammar cannot
    ///     express a mip chain (THUG sprite.cpp asserts MXL == 0).
    /// </summary>
    [Theory]
    [InlineData(0x00000000u, true)]
    [InlineData(0x00100000u, true)]
    [InlineData(0x00200000u, true)]
    [InlineData(0x00300000u, true)]
    [InlineData(0x00000001u, false)]
    [InlineData(0x40000000u, false)]
    public void Parse_MxlWord_AcceptsOnlySwizzleBits(uint mxlWord, bool expectSuccess)
    {
        // 2x2 PSMCT32: no palette, no swizzle applied, so only the gate is under test.
        var data = new byte[32 + 2 * 2 * 4];
        BitConverter.GetBytes(2u).CopyTo(data, 0);
        BitConverter.GetBytes(0u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);   // TW -> 2
        BitConverter.GetBytes(1u).CopyTo(data, 12);  // TH -> 2
        BitConverter.GetBytes(0u).CopyTo(data, 16);  // PSMCT32
        BitConverter.GetBytes(0u).CopyTo(data, 20);
        BitConverter.GetBytes(mxlWord).CopyTo(data, 24);
        BitConverter.GetBytes((ushort)2).CopyTo(data, 28);
        BitConverter.GetBytes((ushort)2).CopyTo(data, 30);

        var result = Ps2TexFile.Parse(data);

        Assert.Equal(expectSuccess, result.Success);
        if (!expectSuccess)
            Assert.Contains("MXL", result.ErrorMessage!, StringComparison.Ordinal);
    }

    /// <summary>
    ///     One fixture per (flag, PSM) class, covering both stored-region
    ///     shapes: POT buffers that are the image outright, and non-POT buffers
    ///     that additionally de-stride. newtimerfont's digits and esrb's rating
    ///     text are the legible-art anchors.
    /// </summary>
    [Theory]
    // flag 0x00100000, PSMT4, non-POT (de-strided)
    [InlineData(@"images\mainmenusprites\sharedsprites\level_top_piece.img.ps2", 254, 32,
        "EDC59FBDA877184345E92CB1A9A69C0419BAAEE47C3A693C7EA740FAE9BB4FAE")]
    // flag 0x00200000, PSMT8, non-POT — legible "00123456789:" timer digits
    [InlineData(@"fonts\timerfont\newtimerfont.img.ps2", 400, 50,
        "EC7FE83CC3EADF0789EF548D6E423A1A571DF61C706FFAE5B73B3C56B7AF61BC")]
    // flag 0x00200000, PSMT4, non-POT — legible ESRB rating panel
    [InlineData(@"images\multiplayersprites\esrb.img.ps2", 128, 87,
        "7859334D82965DB254579761386FA48BED7DF9AB8EF34FC60A6755B8DB0A9299")]
    // flag 0x00200000, PSMT8, POT — the "WORLD DESTRUCTION TOUR" logo
    [InlineData(@"images\mainmenusprites\wdt_logo_big.img.ps2", 256, 256,
        "3A3BB47D6DB0224391CA17C014CAEC85F10DE858C9654A9C32C2F51201E5C0D8")]
    // flag 0x00200000, PSMT4, POT — graffiti menu background
    [InlineData(@"images\mainmenusprites\new_bg_1.img.ps2", 512, 256,
        "CA18DB511114F1FEAB19745AB2A211A445FC4DE0FB97490BDC6692C4E48BFFF7")]
    public void Parse_GsSwizzledFixtures_DecodeToPinnedRgba(
        string relativePath, int width, int height, string rgbaSha256)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(paths.SampleBuildsDir!, Thug2Ps2Build, "DATAP", relativePath);
        Assert.SkipWhen(!File.Exists(path), $"{relativePath} not present");

        var data = File.ReadAllBytes(path);
        Assert.NotEqual(0u, BitConverter.ToUInt32(data, 24)); // the fixture really is flagged

        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        Assert.Equal(rgbaSha256, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
    }

    /// <summary>
    ///     Cross-decoder ground truth: a flagged PS2 sprite must decode
    ///     RGB-identical to the Remix PSP sibling of the same art, which reaches
    ///     its pixels through PspImgFile's independent GE path. Alpha differs by
    ///     platform convention (PS2 0-128 vs PSP 0-255) and is excluded.
    /// </summary>
    [Theory]
    [InlineData(@"fonts\timerfont\newtimerfont.img.ps2", @"fonts\timerfont\newtimerfont.img.psp")]
    [InlineData(@"images\multiplayersprites\esrb.img.ps2", @"images\multiplayersprites\esrb.img.psp")]
    public void Parse_GsSwizzledFile_MatchesRemixPspTwinRgb(string ps2Relative, string pspRelative)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2Path = Path.Combine(paths.SampleBuildsDir!, Thug2Ps2Build, "DATAP", ps2Relative);
        var pspPath = Path.Combine(
            paths.SampleBuildsDir!, RemixPspBuild, "PSP_GAME", "USRDIR", "datap", pspRelative);
        Assert.SkipWhen(!File.Exists(ps2Path) || !File.Exists(pspPath), "Twin pair not present");

        var ps2 = Ps2TexFile.Parse(File.ReadAllBytes(ps2Path));
        var psp = PspImgFile.Parse(File.ReadAllBytes(pspPath));
        Assert.True(ps2.Success, ps2.ErrorMessage);
        Assert.True(psp.Success, psp.ErrorMessage);

        var a = Assert.Single(ps2.Textures);
        var b = Assert.Single(psp.Textures);
        Assert.Equal((b.Width, b.Height), (a.Width, a.Height));
        for (var i = 0; i < a.Pixels!.Length; i += 4)
        {
            Assert.Equal(b.Pixels![i], a.Pixels[i]);
            Assert.Equal(b.Pixels[i + 1], a.Pixels[i + 1]);
            Assert.Equal(b.Pixels[i + 2], a.Pixels[i + 2]);
        }
    }

    /// <summary>
    ///     Corpus census: the MXL word carries exactly three values across every
    ///     shipped version-2 IMG (0, and the two THUG2 swizzle flags), and every
    ///     file decodes — the 2,629 flagged THUG2 sprites included.
    /// </summary>
    [CorpusFact]
    public void Parse_AllVersion2Img_MxlWordCensusAndDecode()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var counts = new Dictionary<uint, int>();
        var failures = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     paths.SampleBuildsDir!, "*.img.ps2", SearchOption.AllDirectories))
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 32 || BitConverter.ToUInt32(data, 0) != 2) continue;

            var word = BitConverter.ToUInt32(data, 24);
            counts[word] = counts.GetValueOrDefault(word) + 1;

            var result = Ps2TexFile.Parse(data);
            if (!result.Success || result.Textures.Count != 1 || result.Textures[0].Pixels == null)
                failures.Add($"{Path.GetFileName(file)}: {result.ErrorMessage ?? "no texture"}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(9929, counts[0x00000000u]);
        Assert.Equal(728, counts[0x00100000u]);
        Assert.Equal(1901, counts[0x00200000u]);
        Assert.Equal(3, counts.Count);
    }
}
