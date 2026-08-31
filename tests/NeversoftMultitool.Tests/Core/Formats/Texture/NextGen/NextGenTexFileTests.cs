using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Texture.NextGen;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.NextGen;

/// <summary>
///     Neversoft's next-gen <c>FA CE CA A7</c> texture dictionary (THAW, Project 8
///     and Proving Ground on Xbox 360 and PS3), derived 2026-08-26/27.
///     The decoder is validated two ways, because neither alone is sufficient:
///     cross-platform pixel comparison against the PS3 builds (whose payloads are
///     linear, so they referee the Xenos untiling), and LEGIBLE ART — a comparison
///     between two platforms that share a decode path cannot see an orientation
///     error, and indeed 371/371 textures matched while every one was upside-down
///     until a "KEEP OUT / NO TRESPASSING" sign showed it.
/// </summary>
public class NextGenTexFileTests(TestPaths paths)
{
    private const string ThawX360 = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";

    [Fact]
    public void IsNextGenTex_RequiresTheMagic()
    {
        var header = new byte[32];
        header[0] = 0xFA; header[1] = 0xCE; header[2] = 0xCA; header[3] = 0xA7;
        Assert.True(NextGenTexFile.IsNextGenTex(header));

        header[1] = 0x00;
        Assert.False(NextGenTexFile.IsNextGenTex(header));
        Assert.False(NextGenTexFile.IsNextGenTex(new byte[4]));
    }

    /// <summary>
    ///     The VRAM twin's name and, critically, its DIRECTORY: an extracted
    ///     <c>FOO.PAK</c> pairs with the sibling <c>FOO_VRAM.PAK</c>. Appending
    ///     the suffix instead (<c>FOO.PAK_vram.pak</c>) silently falls back to a
    ///     same-directory copy that is not the payload — that mistake cost 49 of
    ///     49 pak-contained textures, all of which decode once it is fixed.
    /// </summary>
    [Theory]
    [InlineData("cutscene.tex.ps3", "cutscene.tvx.ps3")]
    [InlineData("CUTSCENE.TEX.PS3", "CUTSCENE.tvx.PS3")]
    [InlineData("level.stex.ps3", "level.vstex.ps3")]
    public void GetVramTwinFileName_SwapsTheTextureSuffix(string input, string expected)
    {
        Assert.Equal(expected, NextGenTexFile.GetVramTwinFileName(input));
    }

    [Theory]
    [InlineData("BAM_MUGGING_MAIN.PAK", "BAM_MUGGING_MAIN_VRAM.PAK")]
    [InlineData("foo.pak", "foo_VRAM.pak")]
    [InlineData("plain", "plain_VRAM")]
    public void GetVramTwinDirectoryName_InsertsBeforeTheExtension(string input, string expected)
    {
        Assert.Equal(expected, NextGenTexFile.GetVramTwinDirectoryName(input));
    }

    /// <summary>
    ///     Untiling is a PERMUTATION of storage units, so a uniform surface must
    ///     come back uniform whatever the layout — the property that proved a
    ///     mismatching texture held genuinely different art rather than a decode
    ///     error.
    /// </summary>
    [Theory]
    [InlineData(64, 64, 8)]
    [InlineData(64, 64, 16)]
    [InlineData(16, 16, 8)]
    public void UntileBlocks_IsAPermutation(int width, int height, int blockBytes)
    {
        // The stored region is PADDED to whole 32-block macro tiles, so a tiled
        // address can sit past the tight size — which is why decoding reads to
        // the end of the file rather than a computed length.
        var blocksX = (Math.Max(1, (width + 3) / 4) + 31) & ~31;
        var blocksY = (Math.Max(1, (height + 3) / 4) + 31) & ~31;
        var source = new byte[blocksX * blocksY * blockBytes];
        Array.Fill(source, (byte)0xAB);

        var untiled = XenosTiling.UntileBlocks(source, width, height, blockBytes, false);

        var tight = Math.Max(1, (width + 3) / 4) * Math.Max(1, (height + 3) / 4) * blockBytes;
        Assert.Equal(tight, untiled.Length);
        Assert.All(untiled, b => Assert.Equal(0xAB, b));
    }

    [Fact]
    public void SubMacroTileSurfaces_StartThirtyTwoBlocksIn()
    {
        // Derived from the measured block permutation; before it, every sub-32
        // texture failed and every larger one passed.
        Assert.Equal(32 * 8, XenosTiling.GetSurfaceByteOffset(16, 16, 8));
        Assert.Equal(32 * 16, XenosTiling.GetSurfaceByteOffset(8, 8, 16));
        Assert.Equal(0, XenosTiling.GetSurfaceByteOffset(32, 32, 8));
        Assert.Equal(0, XenosTiling.GetSurfaceByteOffset(256, 64, 8));
        // A surface short on EITHER axis is sub-macro-tile.
        Assert.Equal(32 * 8, XenosTiling.GetSurfaceByteOffset(256, 16, 8));
    }

    /// <summary>
    ///     One THAW X360 dictionary pinned by RGBA hash per texture. FCBC3132 is
    ///     the "KEEP OUT / NO TRESPASSING" sign whose legibility settled the
    ///     bottom-up row order, so these hashes lock the orientation that a
    ///     cross-platform comparison cannot check.
    /// </summary>
    [CorpusFact]
    public void Parse_ThawX360Dictionary_DecodesToPinnedPixels()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawX360, "45EDAA46.stex.xen");
        Assert.SkipWhen(file == null, "45EDAA46.stex.xen not present");

        var result = NextGenTexFile.Parse(File.ReadAllBytes(file!));

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(8, result.Textures.Count);

        var expected = new Dictionary<uint, (int Width, int Height, string Sha)>
        {
            [0x0900939E] = (512, 512, "C97C6F0F14504FBCEF50578990F432C169E95F0316ED6B78299492007D885DE7"),
            [0x5C1FAA8C] = (128, 32, "38491630733F027340FB50799CC98A378E19BEEEA1EA54D2E504F5B2A31786A8"),
            [0xB334B4A5] = (64, 128, "84BF7E2AE7CFE89AD8F78AC689E5EBBD2E890BA1088638EAC18163FF1759D5E4"),
            [0xFCBC3132] = (128, 128, "BAEDC37319B9C57F711D44E2EE81EC0CCD78B8A4E19A9937D07E956F1F2ABF2A"),
        };

        foreach (var (checksum, want) in expected)
        {
            var texture = result.Textures.Single(t => t.Checksum == checksum);
            Assert.Equal((want.Width, want.Height), (texture.Width, texture.Height));
            Assert.NotNull(texture.Pixels);
            Assert.Equal(want.Sha, Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
        }
    }

    /// <summary>
    ///     Whole-corpus structural sweep across the five shipping builds. This is
    ///     the check that catches layout regressions: a byte-typed loop counter
    ///     once wrapped at 255 and rejected every dictionary whose record table
    ///     sat past that offset, which no single-fixture test noticed.
    /// </summary>
    [CorpusFact]
    public void Parse_NextGenTextureCorpus_ParsesEveryFile()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        string[] builds =
        [
            ThawX360,
            "Tony Hawk's Project 8 (2006-11-7, X360 - Final)",
            "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)",
            "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)",
            "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)"
        ];

        var files = 0;
        var textures = 0;
        var emptyDictionaries = 0;
        var failures = new List<string>();

        foreach (var build in builds)
        {
            var root = Path.Combine(paths.SampleBuildsDir!, build);
            if (!Directory.Exists(root)) continue;

            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (!name.EndsWith(".tex.xen", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".stex.xen", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".tex.ps3", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".stex.ps3", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var data = File.ReadAllBytes(file);
                if (!NextGenTexFile.IsNextGenTex(data)) continue;

                files++;
                var result = NextGenTexFile.Parse(data, NextGenVramTwinLocator.TryLoad(file, data));
                if (!result.Success)
                {
                    failures.Add($"{name}: {result.ErrorMessage}");
                    continue;
                }

                if (result.Textures.Count == 0) emptyDictionaries++;
                textures += result.Textures.Count;
            }
        }

        Assert.SkipWhen(files == 0, "Next-gen builds not present");
        Assert.True(failures.Count == 0,
            $"{failures.Count} parse failures:\n{string.Join("\n", failures.Take(10))}");
        Assert.Equal(12_335, files);
        Assert.Equal(90_477, textures);
        Assert.Equal(789, emptyDictionaries);
    }
}
