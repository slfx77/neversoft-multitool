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
    public void RendersLevel0ColourSurface_FromRomTileArt()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        // The true full-colour surface. +0x24/+0x26 are the size in 8×8 TILES
        // (258×168 for the Hangar) so the image is 2064×1344; the maps are half that
        // in METATILES, each naming a 2×2 tile block via the +0x30 table. Reading the
        // maps as direct tile indices (the old bug) rendered a quarter-size mush.
        var render = GbaLevelImages.RenderColourSurface(rom, levels[0]);
        Assert.NotNull(render);
        Assert.Equal(2064, render.Value.Width);
        Assert.Equal(1344, render.Value.Height);
        Assert.Equal(render.Value.Width * render.Value.Height * 4, render.Value.Rgba.Length);
        Assert.Equal(
            "26d167f8e5b61939e4fb8a154cd6c869e6a08d318c07c64c031a1309a4f5b1b6",
            Convert.ToHexStringLower(SHA256.HashData(render.Value.Rgba)));

        // Every level composites a colour surface.
        Assert.All(levels, l => Assert.NotNull(GbaLevelImages.RenderColourSurface(rom, l)));
    }

    [Fact]
    public void RendersLevel0CollisionSurface_WithRealCellShapes()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        // The shape-aware surface render: ramps slope and quarter-pipes curve because
        // each cell's real height function is executed (GbaCollisionSurface), instead
        // of the flat-box reading that turned transitions into walls and staircases.
        // The Hangar's one-cell out-of-bounds kill wall ring (182 cells at 34.375
        // world units) is omitted so the playfield is visible.
        var trueRecord = (int)(levels[0].RecordAddress - 0x08000000) - 0x144;
        var render = GbaCollisionRenderer.Render(rom, trueRecord);
        Assert.NotNull(render);
        // Height scale matches the ENGINE's proportions (1 world unit of height =
        // 1/3 of a cell's horizontal span, per its art transform) — the first cut
        // doubled it, rendering every ramp twice as tall as the game draws it.
        Assert.Equal(1955, render.Value.Width);
        Assert.Equal(1019, render.Value.Height);
        Assert.Equal(182, render.Value.OmittedCells);
        Assert.Equal(render.Value.Width * render.Value.Height * 4, render.Value.Rgba.Length);
        Assert.Equal(
            "61131d272c04a953af06fa49c0a4b4211c26145bc19fc09cc08052a41b43fd00",
            Convert.ToHexStringLower(SHA256.HashData(render.Value.Rgba)));

        // Every level renders (accurate surface geometry across the corpus).
        Assert.All(levels, l => Assert.NotNull(
            GbaCollisionRenderer.Render(rom, (int)(l.RecordAddress - 0x08000000) - 0x144)));
    }

    [Fact]
    public void RendersLevel0CollisionOverArt_WithTheEngineArtTransform()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        // The collision grid drawn over the level's own art. The projection is the
        // engine's art transform — artX = X0 + 16(wy−wx), artY = Y0 + 8(wx+wy) − 16z —
        // whose per-level origin is a stored ROM field (record +0x64/+0x68, 24.8
        // fixed; all 9 levels decode to whole pixels). Fitted dynamically against
        // skater/shadow anchors at ~1px median residual on three demo levels.
        var colour = GbaLevelImages.RenderColourSurface(rom, levels[0]);
        Assert.NotNull(colour);
        var trueRecord = (int)(levels[0].RecordAddress - 0x08000000) - 0x144;
        var overlay = GbaCollisionRenderer.RenderArtOverlay(
            rom, trueRecord, colour.Value.Width, colour.Value.Height, colour.Value.Rgba);
        Assert.NotNull(overlay);
        Assert.Equal(colour.Value.Width, overlay.Value.Width);
        Assert.Equal(colour.Value.Height, overlay.Value.Height);
        Assert.Equal(182, overlay.Value.OmittedCells); // the kill-wall ring stays untinted
        Assert.Equal(
            "0fff000d91bb3a2b1b26c44eb5ef41d3d7972ab2f31b23e5d95bf3835456dec1",
            Convert.ToHexStringLower(SHA256.HashData(overlay.Value.Rgba)));

        // The overlay must differ from the plain art (the grid was actually drawn)…
        Assert.NotEqual(
            Convert.ToHexStringLower(SHA256.HashData(colour.Value.Rgba)),
            Convert.ToHexStringLower(SHA256.HashData(overlay.Value.Rgba)));
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
