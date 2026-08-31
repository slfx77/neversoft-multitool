using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Psp;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psp;

/// <summary>
///     THUG2 Remix PSP .img.psp decoding (PspImgFile): the v2 IMG family
///     re-encoded for the PSP GE. Layout proven 2026-08-26 by a pixel-exact
///     sweep against PS2/Xbox siblings (926/926 comparable twins exact; the
///     three VRAM-padded swizzled files exact against their Xbox twins) plus
///     legible-art checks (button glyph sheets, the full-ASCII dialog font, the
///     "TONY HAWK'S UNDERGROUND 2 REMIX" loadscreen). One SHA-pinned fixture
///     per decode-variant class, because a wrong stride/placement can survive
///     an eyeball check (the .fnt nibble-order lesson).
/// </summary>
public class PspImgFileTests(TestPaths paths)
{
    private const string RemixBuild = "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";
    private const string Thug2XboxBuild = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    [Fact]
    public void IsPspImg_RequiresVersion2AndBuildWord()
    {
        var header = new byte[32];
        BitConverter.GetBytes(2u).CopyTo(header, 0);
        BitConverter.GetBytes(PspImgFile.PspBuildWord).CopyTo(header, 4);
        Assert.True(PspImgFile.IsPspImg(header));

        BitConverter.GetBytes(0u).CopyTo(header, 4);
        Assert.False(PspImgFile.IsPspImg(header)); // PS2 v2 word

        BitConverter.GetBytes(PspImgFile.PspBuildWord).CopyTo(header, 4);
        BitConverter.GetBytes(4u).CopyTo(header, 0);
        Assert.False(PspImgFile.IsPspImg(header)); // P8 PSP v4 family

        Assert.False(PspImgFile.IsPspImg(header.AsSpan(0, 16))); // truncated
    }

    /// <summary>
    ///     One fixture per decode-variant class: swizzled with row padding
    ///     (fonts), swizzled exact-fit, linear tight, linear POT-VRAM buffer
    ///     with the art as the last tight bytes, swizzled POT-VRAM last-tight,
    ///     and direct-colour PSMCT32 (the Remix title loadscreen, whose
    ///     trademark text is legible only under the correct decode).
    /// </summary>
    [Theory]
    [InlineData(@"fonts\buttons\buttonsps2.img.psp", 700, 50,
        "EDF03B2D16CA56DF975D4844F524642D57D1859198C4E7EAA32F486E6B78C79C")]
    [InlineData(@"images\multiplayersprites\globe.img.psp", 512, 512,
        "616ADD86AD7029F25AECAC812F50B345C96BB4BA2D5573C069B7FB1E446CC6BE")]
    [InlineData(@"images\bits\apm_crate02_xploder02.img.psp", 64, 64,
        "08BA96102B9081E1F343BC890D98EB23A1D16828581A37536213F05D799A1E71")]
    [InlineData(@"images\bits\police_line2.img.psp", 64, 64,
        "2C2836593DA405D8F897D8197BBBD3B73023FF5B4823E533753412BF245C6702")]
    [InlineData(@"images\particles\dt_nj_light01.img.psp", 32, 32,
        "100D7930C02FCA035A23429663298A1D58E9A90A76010050EED58C3802B76E30")]
    [InlineData(@"images\loadscrn.img.psp", 480, 272,
        "F885279B242BE9C67BCA6E61247427584E7F28BA41D5607C18741634F2AA9EE9")]
    public void Parse_ClassFixtures_DecodeToPinnedRgba(
        string relativePath, int width, int height, string rgbaSha256)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(
            paths.SampleBuildsDir!, RemixBuild, "PSP_GAME", "USRDIR", "datap", relativePath);
        Assert.SkipWhen(!File.Exists(path), $"{relativePath} not present");

        var result = PspImgFile.Parse(File.ReadAllBytes(path));

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        Assert.Equal(rgbaSha256, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
    }

    /// <summary>
    ///     Cross-platform ground truth without any shared code: the swizzled
    ///     VRAM-padded PSP file must decode RGB-identical to its Xbox sibling
    ///     (alpha conventions differ per platform and are excluded).
    /// </summary>
    [Fact]
    public void Parse_SwizzledVramPaddedFile_MatchesXboxSiblingRgb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var pspPath = Path.Combine(
            paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "images", "particles", "dt_nj_light01.img.psp");
        var xbxPath = Path.Combine(
            paths.SampleBuildsDir!, Thug2XboxBuild,
            "data", "images", "Particles", "dt_nj_light01.img.xbx");
        Assert.SkipWhen(!File.Exists(pspPath) || !File.Exists(xbxPath), "Twin pair not present");

        var psp = PspImgFile.Parse(File.ReadAllBytes(pspPath));
        var xbx = XbxImgFile.Parse(xbxPath);
        Assert.True(psp.Success, psp.ErrorMessage);
        Assert.True(xbx.Success, xbx.ErrorMessage);

        var a = Assert.Single(psp.Textures);
        var b = Assert.Single(xbx.Textures);
        Assert.Equal((a.Width, a.Height), (b.Width, b.Height));
        for (var i = 0; i < a.Pixels!.Length; i += 4)
        {
            Assert.Equal(a.Pixels[i], b.Pixels![i]);
            Assert.Equal(a.Pixels[i + 1], b.Pixels[i + 1]);
            Assert.Equal(a.Pixels[i + 2], b.Pixels[i + 2]);
        }
    }

    /// <summary>
    ///     Every Remix .img.psp decodes: the size-identity classification is
    ///     exhaustive over the corpus (measured: 2,764 swizzled-exact, 1,709
    ///     linear-tight, 36 linear-VRAM, 6 swizzled-VRAM within the loose +
    ///     PRE-extracted tree).
    /// </summary>
    [CorpusFact]
    public void Parse_AllRemixImgPsp_DecodeCleanly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, RemixBuild, "PSP_GAME", "USRDIR", "datap");
        Assert.SkipWhen(!Directory.Exists(root), "Remix PSP build not present");

        var files = Directory.EnumerateFiles(root, "*.img.psp", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(4515, files.Count);

        var failures = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            Assert.True(PspImgFile.IsPspImg(data), $"Discriminator rejected {file}");
            var result = PspImgFile.Parse(data);
            if (!result.Success || result.Textures.Count != 1 || result.Textures[0].Pixels == null)
                failures.Add($"{Path.GetRelativePath(root, file)}: {result.ErrorMessage ?? "no texture"}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
    }

    /// <summary>
    ///     Ps2TexFile.Parse dispatches version-2 data by the PSP build word, so
    ///     GUI/CLI callers need no name-based routing for .img.psp.
    /// </summary>
    [Fact]
    public void Ps2TexFile_Version2Dispatch_RoutesPspImgByBuildWord()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(
            paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "fonts", "small.img.psp");
        Assert.SkipWhen(!File.Exists(path), "fonts/small.img.psp not present");

        var viaDispatch = NeversoftMultitool.Core.Formats.Texture.Ps2.Ps2TexFile.Parse(path);
        var direct = PspImgFile.Parse(File.ReadAllBytes(path));

        Assert.True(viaDispatch.Success, viaDispatch.ErrorMessage);
        Assert.True(direct.Success, direct.ErrorMessage);
        Assert.Equal(direct.Textures[0].Pixels, viaDispatch.Textures[0].Pixels);
    }
}
