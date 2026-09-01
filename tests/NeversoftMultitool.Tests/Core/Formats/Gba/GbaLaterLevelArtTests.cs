using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Rendering.Level2d;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the full-colour isometric tile surfaces of the four later Vicarious
///     Visions GBA carts (THPS4, THUG, THUG2, American Sk8land).
/// </summary>
public sealed class GbaLaterLevelArtTests(TestPaths paths)
{
    public static TheoryData<string, string, int> Carts => new()
    {
        { "Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba", 8 },
        { "Tony Hawk's Underground (2003-10-27, GBA - Final)", "Tony Hawk's Underground (USA, Europe).gba", 10 },
        { "Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "Tony Hawk's Underground 2 (USA, Europe).gba", 7 },
        { "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "Tony Hawk's American Sk8land (USA).gba", 12 }
    };

    private byte[]? Load(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        return path == null ? null : File.ReadAllBytes(path);
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void FindsTheArtTable(string build, string file, int expectedLevels)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.Equal(expectedLevels, levels.Count);

        // The separate occlusion map is exactly its stated pixel size in 64-pixel
        // cells; that identity validates the join back to the parent level record.
        foreach (var level in levels)
        {
            Assert.True(level.MapWidth > 0 && level.MapHeight > 0);
            Assert.Equal(level.MapWidth, (level.PixelWidth + 32) / 64);
            Assert.Equal(level.MapHeight, (level.PixelHeight + 32) / 64);
        }
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void EveryLevelRendersAtItsStatedSize(string build, string file, int expectedLevels)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.Equal(expectedLevels, levels.Count);

        var rendered = 0;
        foreach (var level in levels)
        {
            var render = GbaLaterLevelArt.RenderColourSurface(rom, level);
            if (render == null) continue;
            rendered++;
            Assert.Equal(level.PixelWidth, render.Value.Width);
            Assert.Equal(level.PixelHeight, render.Value.Height);
            Assert.Equal(render.Value.Width * render.Value.Height * 4, render.Value.Rgba.Length);

            // A real surface contains substantial palette-indexed colour, rather
            // than the two tones of the old occlusion-mask misidentification.
            var painted = 0;
            var colours = new HashSet<int>();
            for (var i = 0; i < render.Value.Rgba.Length; i += 4)
            {
                var colour = render.Value.Rgba[i]
                             | render.Value.Rgba[i + 1] << 8
                             | render.Value.Rgba[i + 2] << 16;
                if (colour != 0x161212)
                    painted++;
                if (colours.Count < 64)
                    colours.Add(colour);
            }
            var total = render.Value.Width * render.Value.Height;
            Assert.InRange(painted / (double)total, 0.001, 1.0);
            Assert.True(colours.Count >= 16, $"level {level.Index} has only {colours.Count} colours");
        }

        Assert.Equal(expectedLevels, rendered);
    }

    public static TheoryData<string, string, string> FirstLevelPins => new()
    {
        {
            "Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba",
            "21E6200DDC088657C7B51B4AC6B671CAE447A51337D21C9CC61778B2B4DA9E14"
        },
        {
            "Tony Hawk's Underground (2003-10-27, GBA - Final)", "Tony Hawk's Underground (USA, Europe).gba",
            "4B4C4DB0B1849F86F15CE35C35CF1EBC3B575104C42049DDB4C0CC24ED0A7C8D"
        },
        {
            "Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "Tony Hawk's Underground 2 (USA, Europe).gba",
            "C083C407D1187B7329E2BF4F5903CE03FEB9315BECC35D4F39E11948F5A8CF02"
        },
        {
            "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "Tony Hawk's American Sk8land (USA).gba",
            "F56958B4377D04AB70A68492BAA9E2B2A3304B889E381C4325F804A7CC55D10C"
        }
    };

    /// <summary>
    ///     One colour render per cartridge, pinned by pixel hash. A wrong map command,
    ///     palette remap, flip bit, plane order, or 4bpp row direction can remain
    ///     superficially plausible; the full-buffer hash catches those silent changes.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(FirstLevelPins))]
    public void FirstLevelRenderIsPinned(string build, string file, string sha)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.NotEmpty(levels);
        var render = GbaLaterLevelArt.RenderColourSurface(rom, levels[0]);
        Assert.NotNull(render);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(render.Value.Rgba)));
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void PaletteAndOcclusionAssetRemainSeparatelyIdentified(
        string build, string file, int expectedLevels)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.Equal(expectedLevels, levels.Count);
        Assert.All(levels, level => Assert.Equal(
            256 * 4, GbaLaterLevelArt.TryGetPalette(rom, level)?.Length));

        var mask = GbaLaterLevelArt.RenderOcclusionMask(rom, levels[0]);
        Assert.NotNull(mask);
        var colours = new HashSet<int>();
        for (var i = 0; i < mask.Value.Rgba.Length; i += 4)
            colours.Add(mask.Value.Rgba[i]
                        | mask.Value.Rgba[i + 1] << 8
                        | mask.Value.Rgba[i + 2] << 16);
        Assert.InRange(colours.Count, 2, 3);
    }

    /// <summary>
    ///     The three cartridges that do NOT share this record shape must find nothing:
    ///     THPS2 is the older container, and THPS3 and Downhill Jam are each different
    ///     again. A locator that claimed them would be reading pointer runs as art.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", "Tony Hawk's Pro Skater 3 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "Tony Hawk's Downhill Jam (USA).gba")]
    public void OtherCartridgesAreDeclined(string build, string file)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");
        Assert.Empty(GbaLaterLevelArt.FindLevels(rom));
    }

    /// <summary>
    ///     The carve/2D route end to end: a later cartridge carves one entry per level
    ///     plus the ROM companion, and each carved record binds back to its own level
    ///     and renders. This is what the Levels tab walks.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void CarvedRecordsBindAndRender(string build, string file, int expectedLevels)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        Assert.True(GbaLevelCarver.IsVvLevelRom(rom));
        var carve = GbaLevelCarver.Carve(rom);
        Assert.Equal(expectedLevels + 1, carve.Count); // levels + rom.gbarom
        Assert.Contains(carve, e => e.Path == GbaLevelCarver.RomEntryPath);

        var rendered = 0;
        foreach (var (path, data) in carve)
        {
            if (path == GbaLevelCarver.RomEntryPath) continue;
            Assert.EndsWith(GbaLevelCarver.LevelSuffix, path);
            Assert.Equal(GbaLaterLevelArt.ArtRecordStride, data.Length);

            var source = GbaLevel2dSource.TryCreate(data, rom, Path.GetFileName(path));
            Assert.NotNull(source);
            Assert.Equal(
                [Level2dLayer.Art, Level2dLayer.CollisionHeightfield], source.Layers);
            Assert.True(GbaLevelCarver.FindRecordOffset(rom, data) >= 0);
            if (source.Render(Level2dLayer.Art) != null
                && source.Render(Level2dLayer.CollisionHeightfield) != null)
                rendered++;
        }

        Assert.Equal(expectedLevels, rendered);
    }
}
