using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Rendering.Level2d;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>Pins THPS3 GBA's older, all-8bpp authored level surfaces.</summary>
public sealed class GbaThps3LevelArtTests(TestPaths paths)
{
    private byte[]? LoadThps3()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
            "Tony Hawk's Pro Skater 3 (USA, Europe).gba");
        return path == null ? null : File.ReadAllBytes(path);
    }

    [CorpusFact]
    public void FindsAllNineAuthoredSurfaces()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");

        var levels = GbaThps3LevelArt.FindLevels(rom);
        Assert.Equal(9, levels.Count);
        Assert.Equal(0x0B1450, levels[0].LevelRecordOffset);
        Assert.Equal((3024, 2416), (levels[0].PixelWidth, levels[0].PixelHeight));
        Assert.Equal((3024, 2432), (levels[8].PixelWidth, levels[8].PixelHeight));
    }

    [CorpusFact]
    public void EverySurfaceRendersInFullColour()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");

        var levels = GbaThps3LevelArt.FindLevels(rom);
        Assert.Equal(9, levels.Count);
        foreach (var level in levels)
        {
            var render = GbaThps3LevelArt.RenderColourSurface(rom, level);
            Assert.NotNull(render);
            Assert.Equal(level.PixelWidth, render.Value.Width);
            Assert.Equal(level.PixelHeight, render.Value.Height);
            Assert.Equal(render.Value.Width * render.Value.Height * 4, render.Value.Rgba.Length);

            var colours = new HashSet<int>();
            for (var i = 0; i < render.Value.Rgba.Length && colours.Count < 64; i += 4)
                colours.Add(render.Value.Rgba[i]
                            | render.Value.Rgba[i + 1] << 8
                            | render.Value.Rgba[i + 2] << 16);
            Assert.True(colours.Count >= 32, $"level {level.Index} has only {colours.Count} colours");
        }
    }

    [CorpusFact]
    public void FoundryPixelBufferIsPinned()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");

        var level = Assert.Single(GbaThps3LevelArt.FindLevels(rom), entry => entry.Index == 0);
        var render = GbaThps3LevelArt.RenderColourSurface(rom, level);
        Assert.NotNull(render);
        Assert.Equal(
            "1B8E2B6DAF6811F1C53BE65D578915B003261D0A1539862BE66B3B84E50674E5",
            Convert.ToHexString(SHA256.HashData(render.Value.Rgba)));
        Assert.Equal(256 * 4, GbaThps3LevelArt.TryGetPalette(rom, level)?.Length);
    }

    [CorpusFact]
    public void CarvedRecordsOpenInTheLevelsView()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");

        Assert.True(GbaLevelCarver.IsVvLevelRom(rom));
        var carve = GbaLevelCarver.Carve(rom);
        Assert.Equal(10, carve.Count); // nine level records plus rom.gbarom
        Assert.Equal(GbaLevelCarver.RomEntryPath, carve[^1].Path);

        var rendered = 0;
        foreach (var (path, data) in carve.Take(9))
        {
            Assert.Equal(GbaThps3LevelArt.LevelRecordStride, data.Length);
            var source = GbaLevel2dSource.TryCreate(data, rom, Path.GetFileName(path));
            Assert.NotNull(source);
            Assert.Equal([Level2dLayer.Art, Level2dLayer.CollisionHeightfield], source.Layers);
            if (source.Render(Level2dLayer.Art) != null)
                rendered++;
            Assert.NotNull(source.Render(Level2dLayer.CollisionHeightfield));
        }

        Assert.Equal(9, rendered);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "Tony Hawk's Downhill Jam (USA).gba")]
    public void OtherLayoutsAreDeclined(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        Assert.Empty(GbaThps3LevelArt.FindLevels(File.ReadAllBytes(path)));
    }
}
