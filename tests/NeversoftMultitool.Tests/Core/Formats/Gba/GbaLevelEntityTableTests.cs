using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the structural facts of the entity table BEFORE any decode exists, so a
///     later decode has something to be checked against rather than fitted to.
/// </summary>
public sealed class GbaLevelEntityTableTests(TestPaths paths)
{
    private string? RomPath => paths.FindSampleFile(
        "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");

    private byte[] Rom()
    {
        var romPath = RomPath;
        Assert.SkipWhen(romPath == null, "THPS2 GBA ROM sample not available");
        return File.ReadAllBytes(romPath!);
    }

    /// <summary>
    ///     The ROM's level table holds MORE records than the art scanner reports.
    ///     <see cref="GbaLevelImages.FindLevels" /> deduplicates by
    ///     <c>(objectList, elementLibrary)</c> because variants of a level share their
    ///     art — but each variant carries its own entity table, so counting from the
    ///     deduplicated list silently loses 19 of the 510 records.
    /// </summary>
    [Fact]
    public void FourteenLevelRecordsCarryFiveHundredAndTenEntities()
    {
        var rom = Rom();
        var records = GbaLevelEntityTable.FindLevelRecordOffsets(rom);

        Assert.Equal(14, records.Count);
        Assert.Equal(9, GbaLevelImages.FindLevels(rom).Count);

        var counts = records.Select(r => GbaLevelEntityTable.TryRead(rom, r)!.Count).ToArray();
        Assert.Equal([62, 79, 97, 54, 79, 71, 33, 9, 7, 2, 6, 2, 2, 7], counts);
        Assert.Equal(510, counts.Sum());
    }

    /// <summary>
    ///     Each table ends exactly where the next begins for 13 of the 14, which is
    ///     what makes the <c>4 + 16*count</c> reading of the header exact rather than
    ///     merely consistent.
    /// </summary>
    [Fact]
    public void TheTablesTileTheRegionWithoutGaps()
    {
        var rom = Rom();
        var tables = GbaLevelEntityTable.FindLevelRecordOffsets(rom)
            .Select(r => (Offset: GbaLevelEntityTable.TableOffset(rom, r),
                Count: GbaLevelEntityTable.TryRead(rom, r)!.Count))
            .OrderBy(t => t.Offset)
            .ToList();

        var adjacent = 0;
        for (var i = 0; i + 1 < tables.Count; i++)
        {
            if (tables[i].Offset + 4 + tables[i].Count * GbaLevelEntityTable.RecordBytes
                == tables[i + 1].Offset)
                adjacent++;
        }

        Assert.Equal(13, adjacent);
    }

    /// <summary>
    ///     The two fields that ARE established: every record sits inside its own
    ///     level's collision grid at 48 raw units per cell. The grids differ wildly
    ///     (9x15 up to 56x35), so 510 of 510 landing in range is not a coincidence
    ///     available to a wrong scale.
    /// </summary>
    [CorpusFact]
    public void EveryEntityLandsInsideItsOwnLevelsGrid()
    {
        var rom = Rom();
        var checked_ = 0;
        foreach (var rec in GbaLevelEntityTable.FindLevelRecordOffsets(rom))
        {
            var grid = GbaCollisionSurface.TryLoad(rom, rec);
            Assert.NotNull(grid);
            foreach (var e in GbaLevelEntityTable.TryRead(rom, rec)!)
            {
                Assert.InRange(e.CellX, 0, grid.Width - 1);
                Assert.InRange(e.CellY, 0, grid.Height - 1);
                checked_++;
            }
        }

        Assert.Equal(510, checked_);
    }

    /// <summary>
    ///     The field shapes a decode has to explain. None of these is a decode: they
    ///     are the constraints one would have to satisfy.
    /// </summary>
    [Fact]
    public void TheUndecodedFieldsHaveShapesWorthPinning()
    {
        var rom = Rom();
        var all = GbaLevelEntityTable.FindLevelRecordOffsets(rom)
            .SelectMany(r => GbaLevelEntityTable.TryRead(rom, r)!)
            .ToList();
        Assert.Equal(510, all.Count);

        // Field 2 takes negative values, so it is a coordinate and not a size.
        Assert.Equal(20, all.Count(e => e.Field2 < 0));

        // Fields 3/4/5 never do — whatever they are, they are magnitudes.
        Assert.DoesNotContain(all, e => e.Field3 <= 0 || e.Field4 <= 0 || e.Field5 <= 0);

        // Field 7 is quantized to 22.5-degree steps and uses only five of the 16.
        Assert.All(all, e => Assert.Equal(0, e.Field7 % 0x1000));
        Assert.Equal(
            [0x0000, 0x2000, 0x3000, 0x8000, 0xC000],
            all.Select(e => e.Field7).Distinct().Order());

        // Field 6 is banded on decimal thousands, which reads as an authoring id.
        // Eight bands, not the six a census of the deduplicated levels reports —
        // the variant records the art scanner drops carry a 7000 band of their own.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7],
            all.Select(e => e.Field6 / 1000).Distinct().Order());
        Assert.Equal(7400, all.Max(e => e.Field6));

        // Cubes: records whose three magnitudes are equal. 92 at 48 raw units
        // (exactly one collision cell) and 18 at 36.
        var cubes = all.Where(e => e.Field3 == e.Field4 && e.Field4 == e.Field5).ToList();
        Assert.Equal(92, cubes.Count(e => e.Field3 == 48));
        Assert.Equal(18, cubes.Count(e => e.Field3 == 36));

        // The high two field-7 values are a property of the id band, not of the
        // record: 45 of the 167 sub-1000 records carry one, against nearly every
        // record above it.
        var subThousand = all.Where(e => e.Field6 < 1000).ToList();
        Assert.Equal(167, subThousand.Count);
        Assert.Equal(45, subThousand.Count(e => e.Field7 is 0x8000 or 0xC000));
    }

    [Fact]
    public void ARecordWithNoResolvableTableReadsAsNothing()
    {
        var rom = Rom();
        Assert.Null(GbaLevelEntityTable.TryRead(rom, -1));
        Assert.Null(GbaLevelEntityTable.TryRead(rom, rom.Length - 4));

        // A record whose +0x150 is not a ROM pointer yields null rather than a
        // plausible-looking table read from wherever the bytes happen to point.
        var zeros = new byte[0x200];
        Assert.Null(GbaLevelEntityTable.TryRead(zeros, 0));
    }
}
