using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Rendering.Level2d;

namespace NeversoftMultitool.Tests.Core.Rendering.Level2d;

/// <summary>
///     Pins the 2D level seam: the layers a GBA level offers, and that each one is
///     byte-for-byte the picture the existing renderers already produce.
/// </summary>
/// <remarks>
///     The SHAs are the ones <c>GbaLevelImagesTests</c> fixes for the same buffers,
///     so a drift here is a drift in the seam, not in the decoders. The tile-detail
///     render is deliberately NOT a layer: it hashes coverage bytes rather than RGBA
///     and is a blueprint of a different asset than the one on screen.
/// </remarks>
public sealed class GbaLevel2dSourceTests(TestPaths paths)
{
    private const string ArtSha = "26d167f8";
    private const string CollisionSha = "61131d27";
    private const string OverlaySha = "0fff000d";

    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void HangarRendersItsThreeLayersExactly()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var source = GbaLevel2dSource.TryCreate(carve[0].Data, rom, "0_hangar.lvl.gba");
        Assert.NotNull(source);

        // The ROM names its own level; the carved entry name is only the join key.
        Assert.Equal("Hangar", source.DisplayName);
        Assert.Equal(
            [Level2dLayer.Art, Level2dLayer.CollisionHeightfield, Level2dLayer.CollisionOverArt],
            source.Layers);

        var art = source.Render(Level2dLayer.Art);
        Assert.NotNull(art);
        Assert.Equal(2064, art.Value.Width);
        Assert.Equal(1344, art.Value.Height);
        Assert.StartsWith(ArtSha, Sha(art.Value.Rgba), StringComparison.Ordinal);

        var collision = source.Render(Level2dLayer.CollisionHeightfield);
        Assert.NotNull(collision);
        Assert.Equal(1955, collision.Value.Width);
        Assert.Equal(1019, collision.Value.Height);
        Assert.StartsWith(CollisionSha, Sha(collision.Value.Rgba), StringComparison.Ordinal);

        // The overlay is drawn at the art's size, over a copy of it.
        var overlay = source.Render(Level2dLayer.CollisionOverArt);
        Assert.NotNull(overlay);
        Assert.Equal(art.Value.Width, overlay.Value.Width);
        Assert.Equal(art.Value.Height, overlay.Value.Height);
        Assert.StartsWith(OverlaySha, Sha(overlay.Value.Rgba), StringComparison.Ordinal);
        Assert.NotEqual(Sha(art.Value.Rgba), Sha(overlay.Value.Rgba));
    }

    [CorpusFact]
    public void EveryCarvedLevelRendersEveryLayer()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);
        var levels = GbaLevelCarver.ListLevels(rom);

        Assert.Equal(9, levels.Count);
        for (var i = 0; i < levels.Count; i++)
        {
            var entryName = Path.GetFileName(carve[i].Path);
            var source = GbaLevel2dSource.TryCreate(carve[i].Data, rom, entryName);
            Assert.NotNull(source);
            Assert.Equal(levels[i].Name, source.DisplayName);

            foreach (var layer in source.Layers)
            {
                var render = source.Render(layer);
                Assert.True(render.HasValue, $"{entryName} {layer}");
                Assert.Equal(render.Value.Width * render.Value.Height * 4, render.Value.Rgba.Length);
            }
        }
    }

    /// <summary>
    ///     Fail closed on a record that is not in this ROM: the 2D view must show
    ///     nothing rather than render some other level's picture.
    /// </summary>
    [Fact]
    public void ARecordFromAnotherRomIsRefused()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        var foreign = new byte[GbaLevelCarver.LevelRecordSize];
        for (var i = 0; i < foreign.Length; i++) foreign[i] = (byte)(i * 7 + 3);

        Assert.Null(GbaLevel2dSource.TryCreate(foreign, rom, "impostor.lvl.gba"));
    }

    [Fact]
    public void OnlyCarvedGbaLevelsAdvertiseA2dView()
    {
        Assert.True(GbaLevel2dSource.Supports("0_hangar.lvl.gba"));
        Assert.False(GbaLevel2dSource.Supports("13_spider_man.chr.gba"));
        Assert.False(GbaLevel2dSource.Supports("l1a1_g.psx"));
        Assert.False(GbaLevel2dSource.Supports("skateshop.bsp"));
    }

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
