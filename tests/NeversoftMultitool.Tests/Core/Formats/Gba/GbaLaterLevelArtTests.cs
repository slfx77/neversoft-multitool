using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Rendering.Level2d;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the isometric level art of the four later Vicarious Visions GBA carts
///     (THPS4, THUG, THUG2, American Sk8land), which share one record shape.
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

        // Every level's map is exactly its stated pixel size in 64-pixel cells, and
        // that is the identity the pointer-offset search is validated on.
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
            var render = GbaLaterLevelArt.Render(rom, level);
            if (render == null) continue;
            rendered++;
            Assert.Equal(level.MapWidth * 64, render.Value.Width);
            Assert.Equal(level.MapHeight * 64, render.Value.Height);
            // Ink coverage: a real level is neither blank nor solid.
            var ink = 0;
            for (var i = 0; i < render.Value.Rgba.Length; i += 4)
                if (render.Value.Rgba[i] > 0x80) ink++;
            var total = render.Value.Width * render.Value.Height;
            // One Sk8land record places almost nothing (it shares its element pool
            // with the five that follow), so the floor only rules out a blank render.
            Assert.InRange(ink / (double)total, 0.001, 0.9);
        }

        Assert.True(rendered >= expectedLevels - 2, $"only {rendered}/{expectedLevels} rendered");
    }

    public static TheoryData<string, string, string> FirstLevelPins => new()
    {
        {
            "Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba",
            "4815FDE9F0B1D16449D9A78F57D979C799174BBD6AB1DA3CF07E904BEF2574D7"
        },
        {
            "Tony Hawk's Underground (2003-10-27, GBA - Final)", "Tony Hawk's Underground (USA, Europe).gba",
            "1A2AD73E75D1AEC84066D3F84B54D692C9D7105B9EDD3FCFD316A330847B011A"
        },
        {
            "Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "Tony Hawk's Underground 2 (USA, Europe).gba",
            "7E5BAF8CCC0EA6992A0C84A68158599BF6DBBCE8FC249156105538CADA51B8B7"
        },
        {
            "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "Tony Hawk's American Sk8land (USA).gba",
            "F9607DD435ED22DACC9B8C09ED82265F48A24FC2EA61D8681531CF1DF468A388"
        }
    };

    /// <summary>
    ///     One render per cartridge, pinned by pixel hash. The art is one bit deep, so
    ///     a wrong element size or metatile order still produces a plausible-looking
    ///     picture — only a hash catches a silent change.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(FirstLevelPins))]
    public void FirstLevelRenderIsPinned(string build, string file, string sha)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");

        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.NotEmpty(levels);
        var render = GbaLaterLevelArt.Render(rom, levels[0]);
        Assert.NotNull(render);
        Assert.Equal(sha, Convert.ToHexString(SHA256.HashData(render.Value.Rgba)));
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
            // No collision grid on these cartridges, so the art is the only layer.
            Assert.Equal([Level2dLayer.Art], source.Layers);
            if (source.Render(Level2dLayer.Art) != null) rendered++;
        }

        Assert.Equal(expectedLevels, rendered);
    }
}
