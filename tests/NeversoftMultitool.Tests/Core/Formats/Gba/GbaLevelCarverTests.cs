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
        Assert.Equal(26, carve.Count);

        // Names come from the ROM's own strings (record +0x00, locations from +0x04;
        // character names likewise from the roster records).
        string[] expected =
        [
            "levels/0_hangar.lvl.gba", "levels/1_school_ii.lvl.gba", "levels/2_marseille.lvl.gba",
            "levels/3_warehouse.lvl.gba", "levels/4_ny_city.lvl.gba", "levels/5_skate_street.lvl.gba",
            "levels/6_rooftops.lvl.gba", "levels/7_wind_tunnel.lvl.gba", "levels/8_pool.lvl.gba",
            "models/00_tony_hawk.chr.gba", "models/01_bob_burnquist.chr.gba",
            "models/02_steve_caballero.chr.gba", "models/03_kareem_campbell.chr.gba",
            "models/04_rune_glifberg.chr.gba", "models/05_eric_koston.chr.gba",
            "models/06_bucky_lasek.chr.gba", "models/07_rodney_mullen.chr.gba",
            "models/08_chad_muska.chr.gba", "models/09_andrew_reynolds.chr.gba",
            "models/10_geoff_rowley.chr.gba", "models/11_elissa_steamer.chr.gba",
            "models/12_jamie_thomas.chr.gba", "models/13_spider_man.chr.gba",
            "models/14_mindy.chr.gba",
            "models/" + GbaLevelCarver.RomEntryName,
            GbaLevelCarver.RomEntryPath
        ];
        Assert.Equal(expected, carve.Select(c => c.Path).ToArray());
        Assert.All(carve.Take(9), c => Assert.Equal(0x15C, c.Data.Length));
        Assert.All(carve.Skip(9).Take(15), c => Assert.Equal(0x4C, c.Data.Length));
        Assert.Equal(rom.Length, carve[^1].Data.Length);

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
        Assert.Equal(26, fs.Entries.Count);
        var record = fs.FindByName("0_hangar.lvl.gba");
        Assert.NotNull(record);
        Assert.Equal(0x15C, fs.ReadEntry(record).Length);
        var character = fs.FindByName("13_spider_man.chr.gba");
        Assert.NotNull(character);
        Assert.Equal(0x4C, fs.ReadEntry(character).Length);
    }

    [Fact]
    public void CarvedLevelSuffixRoutesToTheMeshPipeline()
    {
        var route = MeshTypeDetector.DetectByName("0_hangar.lvl.gba");
        Assert.Equal(MeshFileKind.GbaLevel, route.Kind);
        Assert.False(route.RequiresContentProbe); // carved names are trusted, like N64
        Assert.Equal(ModelSourceKind.GbaLevel, MeshTypeDetector.ToSourceKind(route.Kind));
        Assert.Equal("0_hangar", MeshTypeDetector.GetStem("0_hangar.lvl.gba"));

        // Both carved kinds must be mesh CANDIDATES — the GUI scanner buckets by
        // this predicate, and a kind that routes but is not a candidate never
        // reaches the file list at all.
        Assert.True(MeshTypeDetector.IsMeshCandidate("0_hangar.lvl.gba"));
        Assert.True(MeshTypeDetector.IsMeshCandidate("13_spider_man.chr.gba"));
        Assert.Equal(GbaLevelCarver.LevelRecordSize, 0x15C);

        // A plain .gba ROM is an ARCHIVE, never a mesh file.
        Assert.Equal(MeshFileKind.None, MeshTypeDetector.DetectByName("Tony Hawk (USA).gba").Kind);
    }

    /// <summary>
    ///     A level's collision grid is a rectangle, but its authored art is not:
    ///     School II has a deep notch between its building wings. Cells the art
    ///     never draws used to emit as flat black slabs.
    ///
    ///     "Undrawn" is pure black REACHABLE FROM THE CANVAS EDGE, never merely
    ///     pure black — the drawn art contains black pixels of its own, and
    ///     dropping cells over those would punch holes in real geometry.
    /// </summary>
    [CorpusFact]
    public void UndrawnArtRegionsAreNotEmittedAsBlackGeometry()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        // Six of the nine levels fill their canvas: no surround, so no mask and
        // provably no change to what they emit.
        var covered = 0;
        var notched = 0;
        foreach (var level in GbaLevelImages.FindLevels(rom))
        {
            var art = GbaLevelImages.RenderColourSurface(rom, level);
            Assert.True(art.HasValue);
            var render = art.Value;
            if (GbaLevelArtCoverage.BuildUndrawnMask(render.Rgba, render.Width, render.Height) == null)
                covered++;
            else
                notched++;
        }

        // Four levels' art has no undrawn pixel at all, so no mask is built and
        // their geometry cannot change; the rest are only tested against it.
        Assert.Equal(4, covered);
        Assert.Equal(5, notched);

        // School II keeps its level but loses the slabs over the notch.
        var schoolRecord = GbaLevelCarver.FindRecordOffset(rom, carve[1].Data);
        var native = new GbaLevelNativeSource(carve[1].Data, rom, schoolRecord, "School II", "");
        var document = ModelDocument.CreateNative("1_school_ii", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);
        Assert.Equal(52753, document.TriangleCount);

        // And a level whose art fills its canvas is untouched — the Hangar keeps
        // exactly the geometry it had before any coverage test existed.
        var hangarRecord = GbaLevelCarver.FindRecordOffset(rom, carve[0].Data);
        var hangar = new GbaLevelNativeSource(carve[0].Data, rom, hangarRecord, "Hangar", "");
        var hangarDocument = ModelDocument.CreateNative("0_hangar", ModelSourceKind.GbaLevel, hangar);
        GbaLevelGeometryWriter.Populate(hangarDocument, hangar);
        Assert.Equal(14739, hangarDocument.TriangleCount);
    }

    /// <summary>
    ///     Whether a cell is out-of-bounds kill wall is decided by the surface its
    ///     material's own height function returns, never by the raw base-height
    ///     word. Material 30 stores something else in that word — its cells read as
    ///     absurd heights while standing on the playfield — and the raw reading
    ///     punched holes exactly where real objects are.
    /// </summary>
    [CorpusFact]
    public void KillWallsAreJudgedByTheSurfaceTheMaterialActuallyReturns()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        var rom = File.ReadAllBytes(romPath!);
        var carve = GbaLevelCarver.Carve(rom);

        var gained = 0;
        var rejected = 0;
        for (var level = 0; level < 9; level++)
        {
            var grid = GbaCollisionSurface.TryLoad(
                rom, GbaLevelCarver.FindRecordOffset(rom, carve[level].Data));
            Assert.NotNull(grid);
            for (var gy = 0; gy < grid.Height; gy++)
            for (var gx = 0; gx < grid.Width; gx++)
            {
                var rawSaysWall = grid.CellAt(gx, gy).BaseHeight / 4096.0
                                  > GbaCollisionRenderer.OutOfBoundsHeight;
                var isWall = GbaCollisionRenderer.IsOutOfBounds(rom, grid, gx, gy);
                if (rawSaysWall == isWall)
                    continue;
                if (rawSaysWall)
                    gained++;   // playfield the raw word hid: staircases, benches
                else
                    rejected++; // kill wall the raw word let through
            }
        }

        // 21 in School II (a staircase sampling 8.50 down to 0.50, and its park
        // benches), 38 in NY City, 3 in Skate Street.
        Assert.Equal(62, gained);

        // The other direction is just as real: Marseille's top border row reads a
        // low base word but its surface stands at the 34.375 kill height, so it
        // was being drawn as a wall across the level's edge.
        Assert.Equal(48, rejected);
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
