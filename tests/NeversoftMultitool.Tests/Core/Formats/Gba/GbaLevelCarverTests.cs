using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the GBA level carve (the archive route that lets the Mesh tab browse a
///     THPS2 ROM) and the textured-3D-level conversion built on it.
/// </summary>
public sealed class GbaLevelCarverTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    [Fact]
    public void CarvesNineNamedLevelsPlusTheRomCompanion()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);

        Assert.True(GbaLevelCarver.IsVvLevelRom(rom));
        var carve = GbaLevelCarver.Carve(rom);
        Assert.Equal(10, carve.Count);

        // Names come from the ROM's own strings (record +0x00), locations from +0x04.
        string[] expected =
        [
            "levels/0_hangar.lvl.gba", "levels/1_school_ii.lvl.gba", "levels/2_marseille.lvl.gba",
            "levels/3_warehouse.lvl.gba", "levels/4_ny_city.lvl.gba", "levels/5_skate_street.lvl.gba",
            "levels/6_rooftops.lvl.gba", "levels/7_wind_tunnel.lvl.gba", "levels/8_pool.lvl.gba",
            GbaLevelCarver.RomEntryPath
        ];
        Assert.Equal(expected, carve.Select(c => c.Path).ToArray());
        Assert.All(carve.Take(9), c => Assert.Equal(0x15C, c.Data.Length));
        Assert.Equal(rom.Length, carve[9].Data.Length);

        var levels = GbaLevelCarver.ListLevels(rom);
        Assert.Equal("Warehouse", levels[3].Name);
        Assert.Equal("Troy, NY", levels[3].Location); // Vicarious Visions' hometown
    }

    [Fact]
    public void ArchiveFileSystemOpensTheRomAsALevelTree()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");

        using var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        Assert.Equal(10, fs.Entries.Count);
        var record = fs.FindByName("0_hangar.lvl.gba");
        Assert.NotNull(record);
        Assert.Equal(0x15C, fs.ReadEntry(record).Length);
    }

    [Fact]
    public void CarvedLevelSuffixRoutesToTheMeshPipeline()
    {
        var route = MeshTypeDetector.DetectByName("0_hangar.lvl.gba");
        Assert.Equal(MeshFileKind.GbaLevel, route.Kind);
        Assert.False(route.RequiresContentProbe); // carved names are trusted, like N64
        Assert.Equal(ModelSourceKind.GbaLevel, MeshTypeDetector.ToSourceKind(route.Kind));
        Assert.Equal("0_hangar", MeshTypeDetector.GetStem("0_hangar.lvl.gba"));

        // A plain .gba ROM is an ARCHIVE, never a mesh file.
        Assert.Equal(MeshFileKind.None, MeshTypeDetector.DetectByName("Tony Hawk (USA).gba").Kind);
    }

    [CorpusFact]
    public void ConvertsTheHangarToATexturedLevelModel()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var trueRecord = GbaLevelCarver.FindRecordOffset(rom, carve[0].Data);
        Assert.True(trueRecord >= 0);

        var native = new GbaLevelNativeSource(carve[0].Data, rom, trueRecord, "Hangar", "Meacham Field, TX");
        var document = ModelDocument.CreateNative("0_hangar", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);

        // The engine-exact surface with skirts: pinned so a decode regression shows.
        Assert.Equal(14739, document.TriangleCount);
        Assert.Single(document.Materials);
        Assert.Single(document.Textures);
        Assert.NotNull(document.Textures[0].PngBytes);
        Assert.True(document.Textures[0].PngBytes!.Length > 100_000); // the level art, not a stub
    }
}
