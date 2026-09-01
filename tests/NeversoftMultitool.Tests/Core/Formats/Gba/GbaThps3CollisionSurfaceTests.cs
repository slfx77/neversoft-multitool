using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins THPS3 GBA's collision grid and executes the authored height-object
///     functions from the cartridge. This is intentionally separate from both
///     THPS2's 32-byte material records and the revised THPS4+ layout.
/// </summary>
public sealed class GbaThps3CollisionSurfaceTests(TestPaths paths)
{
    private static readonly (int Width, int Height, int Records, int HeightObjects)[] Layout =
    [
        (16, 50, 527, 81),
        (57, 57, 1548, 310),
        (44, 43, 793, 80),
        (40, 50, 653, 93),
        (50, 46, 1111, 223),
        (37, 47, 704, 51),
        (54, 44, 1296, 194),
        (20, 30, 147, 18),
        (16, 50, 177, 42)
    ];

    private byte[]? LoadThps3()
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)",
            "Tony Hawk's Pro Skater 3 (USA, Europe).gba");
        return path == null ? null : File.ReadAllBytes(path);
    }

    [CorpusFact]
    public void EveryParentClosesAsItsDedicatedCollisionComplex()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");
        var levels = GbaThps3LevelArt.FindLevels(rom);
        Assert.Equal(Layout.Length, levels.Count);

        foreach (var level in levels)
        {
            var grid = GbaThps3CollisionSurface.TryLoad(rom, level);
            Assert.NotNull(grid);
            var expected = Layout[level.Index];
            Assert.Equal((expected.Width, expected.Height), (grid.Width, grid.Height));
            Assert.Equal(expected.Records, grid.RecordCount);
            Assert.Equal(expected.HeightObjects, grid.HeightObjectCount);
            Assert.InRange(grid.ReferencedRecordCount, 1, grid.RecordCount);
            Assert.IsAssignableFrom<IGbaCollisionGrid>(grid);

            // The record-offset overload is the path used by carved level files.
            var byRecord = GbaThps3CollisionSurface.TryLoad(rom, level.LevelRecordOffset);
            Assert.NotNull(byRecord);
            Assert.Equal((grid.Width, grid.Height, grid.RecordCount, grid.HeightObjectCount),
                (byRecord.Width, byRecord.Height, byRecord.RecordCount, byRecord.HeightObjectCount));
        }
    }

    [CorpusFact]
    public void EveryReferencedHeightFunctionExecutes()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");
        var levels = GbaThps3LevelArt.FindLevels(rom);
        Assert.Equal(Layout.Length, levels.Count);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var cells = 0;
        var seenCounts = new List<int>();
        var runtimeObjectCounts = new List<int>();
        var runtimeCellCounts = new List<int>();
        var highestAtOrBelowThirty = new List<int>();
        var lowestAboveThirty = new List<int>();
        var aboveThirtyCounts = new List<int>();
        Span<byte> bytes = stackalloc byte[4];
        foreach (var level in levels)
        {
            var grid = GbaThps3CollisionSurface.TryLoad(rom, level);
            Assert.NotNull(grid);
            var seen = new bool[grid.HeightObjectCount];
            var highestLive = int.MinValue;
            var lowestWall = int.MaxValue;
            var wallCount = 0;
            for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                seen[grid.HeightObjectAt(x, y)] = true;
                var sampled = grid.SampleCell(rom, x, y, 3);
                foreach (var height in sampled)
                {
                    BitConverter.TryWriteBytes(bytes, height);
                    digest.AppendData(bytes);
                }
                var cellMaximum = sampled.Max();
                if (cellMaximum <= 30 * 4096)
                    highestLive = Math.Max(highestLive, cellMaximum);
                else
                {
                    lowestWall = Math.Min(lowestWall, cellMaximum);
                    wallCount++;
                }
                cells++;
            }
            seenCounts.Add(seen.Count(value => value));
            runtimeObjectCounts.Add(grid.RuntimeDependentHeightObjectCount);
            runtimeCellCounts.Add(grid.RuntimeDependentCellCount);
            highestAtOrBelowThirty.Add(highestLive);
            lowestAboveThirty.Add(lowestWall);
            aboveThirtyCounts.Add(wallCount);
        }

        Assert.Equal(15_756, cells);
        var hash = Convert.ToHexString(digest.GetHashAndReset());
        Assert.Equal("54,213,58,54,154,39,116,16,27", string.Join(',', seenCounts));
        Assert.Equal("9160B83902DC22C80CDB194FED53358711A394DDE70E47738E991386AB333A62", hash);
        Assert.Equal("0,8,1,5,10,0,18,0,0", string.Join(',', runtimeObjectCounts));
        Assert.Equal("0,179,196,28,259,0,43,0,0", string.Join(',', runtimeCellCounts));

        // THPS3 has the same clean kill-wall split used by the shared renderer:
        // the highest retained cell is 29.0 units, while the lowest omitted cell
        // is 36.9998 units. Pin the per-level extrema so the 30-unit policy can
        // never silently eat authored playfield geometry.
        Assert.Equal("79872,91136,34816,83968,66560,118784,83968,65536,0",
            string.Join(',', highestAtOrBelowThirty));
        Assert.Equal("204800,256000,256000,256000,182272,220933,256000,256000,151551",
            string.Join(',', lowestAboveThirty));
        Assert.Equal("218,511,512,859,492,160,201,42,302", string.Join(',', aboveThirtyCounts));
    }

    [Fact]
    public void ShapeDispatcherIncludesSquareAndDiagonalTransforms()
    {
        Assert.Equal((0x0400, 0x0900), GbaThps3CollisionSurface.ShapeTransform(0, 0x0400, 0x0900));
        Assert.Equal((0x26FF, 0x0400), GbaThps3CollisionSurface.ShapeTransform(1, 0x0400, 0x0900));
        Assert.Equal((0x0900, 0x2BFF), GbaThps3CollisionSurface.ShapeTransform(3, 0x0400, 0x0900));

        var diagonal = new HashSet<(int, int)>();
        for (var shape = 9; shape <= 12; shape++)
            Assert.True(diagonal.Add(GbaThps3CollisionSurface.ShapeTransform(shape, 0x0400, 0x0900)));
        Assert.Equal(4, diagonal.Count);
    }

    [CorpusFact]
    public void FirstLevelConvertsToATexturedCollisionMesh()
    {
        var rom = LoadThps3();
        Assert.SkipWhen(rom == null, "THPS3 GBA ROM sample not available");
        var carve = GbaLevelCarver.Carve(rom);
        var level = carve[0];
        var trueRecord = GbaLevelCarver.FindRecordOffset(rom, level.Data);
        Assert.True(trueRecord >= 0);

        var native = new GbaLevelNativeSource(level.Data, rom, trueRecord, "Foundry", "");
        var document = ModelDocument.CreateNative("foundry", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);

        Assert.Equal(21_790, document.TriangleCount);
        Assert.Single(document.Materials);
        Assert.Single(document.Textures);
        Assert.True(document.Textures[0].PngBytes?.Length > 1_000);

        var collision = GbaCollisionRenderer.Render(rom, GbaThps3LevelArt.FindLevels(rom)[0]);
        Assert.NotNull(collision);
        Assert.True(collision.Value.Width > 100);
        Assert.True(collision.Value.Height > 100);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "Tony Hawk's Pro Skater 4 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "Tony Hawk's Downhill Jam (USA).gba")]
    public void OtherEnginesAreNotClaimed(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path);
        Assert.Empty(GbaThps3LevelArt.FindLevels(rom));

        // A THPS3 level-record offset must not accidentally close over a foreign
        // cartridge's unrelated data.
        Assert.Null(GbaThps3CollisionSurface.TryLoad(rom, 0x0B1450));
    }
}
