using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the GBA isometric level reconstruction against Tony Hawk's Pro Skater 2
///     (GBA): the 9 distinct levels in the ROM level table, and that level 0 (Hangar)
///     composites to a stable coverage bitmap. All decode from the ROM — no capture.
/// </summary>
public sealed class GbaLevelImagesTests(TestPaths paths)
{
    private byte[]? LoadThps2()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        return path == null ? null : File.ReadAllBytes(path);
    }

    [Fact]
    public void FindsNineLevels_WithPinnedAssets()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");

        var levels = GbaLevelImages.FindLevels(rom);
        Assert.Equal(9, levels.Count);

        // (objectListAddress, elementLibraryAddress, elementCount) per distinct level.
        (uint Obj, uint Elem, int Tiles)[] expected =
        [
            (0x08754E60, 0x0873DC78, 253), (0x0875A020, 0x087414EC, 369), (0x087589A0, 0x0873ED10, 325),
            (0x08754860, 0x0873D1B8, 180), (0x08755A60, 0x0873A284, 637), (0x08757920, 0x087404B4, 254),
            (0x0875C060, 0x08742FA8, 209), (0x0875BCA0, 0x0873A0BC, 23), (0x0875BDC0, 0x08739D14, 52)
        ];
        Assert.Equal(
            expected,
            levels.Select(l => (l.ObjectListAddress, l.ElementLibraryAddress, l.ElementCount)).ToArray());
    }

    [Fact]
    public void RendersLevel0_Hangar_ToPinnedCoverage()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        var bitmap = GbaLevelImages.RenderLevel(rom, levels[0]);
        Assert.NotNull(bitmap);
        Assert.Equal(1969, bitmap.Value.Width);
        Assert.Equal(1110, bitmap.Value.Height);
        Assert.Equal(bitmap.Value.Width * bitmap.Value.Height, bitmap.Value.Coverage.Length);
        Assert.Contains(bitmap.Value.Coverage, b => b != 0); // tiles were placed

        var sha = Convert.ToHexStringLower(SHA256.HashData(bitmap.Value.Coverage));
        Assert.Equal("4ab43a0e1ebd2c99f2573062c08f6ad925e47976f0caac5826ad83ee131efc20", sha);
    }

    [Fact]
    public void ExtractsLevel0Palette_TheRealColourSource()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        var palette = GbaLevelImages.TryGetPalette(rom, levels[0]);
        Assert.NotNull(palette);
        Assert.Equal(256 * 4, palette.Length);
        // Index 0 is the green transparent key; this palette re-quantises the demo
        // screenshot byte-exact, so it is the true colour source.
        Assert.Equal(new byte[] { 0, 255, 0, 255 }, palette[..4]);
        Assert.Equal(
            "324b614decfa9c113e512868547ff67a15d97f4d6c6dee896ecbd8db73d4b260",
            Convert.ToHexStringLower(SHA256.HashData(palette)));

        // Every level carries a full 256-colour palette.
        Assert.All(levels, l => Assert.Equal(256 * 4, GbaLevelImages.TryGetPalette(rom, l)?.Length));
    }

    // Only THPS2 packs BIOS-LZ77 tile libraries + the isometric level table; the
    // later carts moved their art elsewhere, so no level table is found.
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", 9)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", 0)]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", 0)]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", 0)]
    public void LevelCountAcrossTheGbaLine(string build, int expected)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);
        Assert.Equal(expected, GbaLevelImages.FindLevels(rom).Count);
    }
}
