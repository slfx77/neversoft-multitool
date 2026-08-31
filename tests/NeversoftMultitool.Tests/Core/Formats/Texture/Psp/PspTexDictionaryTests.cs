using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Texture.Ps2;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Psp;

/// <summary>
///     THUG2 Remix PSP .tex.psp dictionaries are the PS2 TEX v5 format
///     VERBATIM — GS-swizzled pixel payloads and CSM1 CLUTs included — proven
///     2026-08-26 by decoding re-authored PSP-only files through the PS2 rule
///     (legible sticker sheets and floor decals; every PSP-native linear/GE
///     candidate scrambled), with 470 of 697 shared cutscene members
///     byte-identical to their .tex.ps2 twins. The builds also ship deliberate
///     4-byte all-zero placeholder files (4,210 of Remix's 5,450; every PRE-
///     contained .tex.psp is one), which parse as an empty success.
/// </summary>
public class PspTexDictionaryTests(TestPaths paths)
{
    private const string RemixBuild = "Tony Hawk's Underground 2 Remix (2005-2-15, PSP - Final)";

    [Fact]
    public void Parse_ZeroStub_IsEmptySuccess()
    {
        var result = Ps2TexFile.Parse(new byte[4]);

        Assert.True(result.Success);
        Assert.Empty(result.Textures);
    }

    /// <summary>
    ///     A re-authored PSP-only dictionary (absent from the PS2 disc in this
    ///     form) decodes through the PS2 v5 rule; 48AFD562 is the 256x256 PSMT8
    ///     sticker sheet whose text ("diaper cannon", "EQUILIZER") is legible
    ///     only under GS unswizzle + CSM1.
    /// </summary>
    [Theory]
    [InlineData(0x48AFD562u, 256, 256,
        "B637AE12EBD6ED4B6B83C99AE682EB38A92A8EFA6D28C7E0261EDDD300D07F4A")]
    [InlineData(0xFB859618u, 64, 64,
        "98DFDC90D1CFAA025CB64E02BBFD4E2649F6D9055B36668B4A2EF96844D4ADA0")]
    public void Parse_ReauthoredPspDictionary_DecodesUnderThePs2Rule(
        uint checksum, int width, int height, string rgbaSha256)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var path = Path.Combine(
            paths.SampleBuildsDir!, RemixBuild,
            "PSP_GAME", "USRDIR", "datap", "cutscenes", "bo_2a.cut", "7ce44b8a.tex.psp");
        Assert.SkipWhen(!File.Exists(path), "bo_2a cutscene tex not present");

        var result = Ps2TexFile.Parse(path);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = result.Textures.Single(t => t.Checksum == checksum);
        Assert.Equal(width, texture.Width);
        Assert.Equal(height, texture.Height);
        Assert.Equal(rgbaSha256, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
    }

    /// <summary>
    ///     Full Remix census: 5,450 .tex.psp = 4,210 zero stubs + 1,240 real
    ///     v5 dictionaries, every one parsing. 76 of the dictionaries are
    ///     28-byte authored empties (one group, zero textures); the rest carry
    ///     2,553 full texture records plus 82 MXL-bit-31 duplicate records.
    /// </summary>
    [CorpusFact]
    public void Parse_AllRemixTexPsp_StubsAndDictionariesCensus()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, RemixBuild, "PSP_GAME", "USRDIR", "datap");
        Assert.SkipWhen(!Directory.Exists(root), "Remix PSP build not present");

        var files = Directory.EnumerateFiles(root, "*.tex.psp", SearchOption.AllDirectories)
            .OrderBy(static f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(5450, files.Count);

        var stubs = 0;
        var parsed = 0;
        var emptyDictionaries = 0;
        var textures = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            var result = Ps2TexFile.Parse(data);
            if (!result.Success)
            {
                failures.Add($"{Path.GetRelativePath(root, file)}: {result.ErrorMessage}");
                continue;
            }

            if (data.Length == 4)
            {
                stubs++;
                Assert.Empty(result.Textures);
            }
            else
            {
                parsed++;
                if (result.Textures.Count == 0) emptyDictionaries++;
                textures += result.Textures.Count;
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(4210, stubs);
        Assert.Equal(1240, parsed);
        Assert.Equal(76, emptyDictionaries);
        Assert.Equal(2635, textures);
    }
}
