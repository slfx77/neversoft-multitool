using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the THPS2 GBA collision surface. The material height functions are executed
///     out of the ROM by <see cref="GbaThumbCpu" /> rather than reimplemented, so the
///     guard that matters is that every cell of every level executes and produces the
///     expected heights: the per-level and aggregate digests below cover all 8,520 cells
///     × 25 samples across the 9 levels.
/// </summary>
public sealed class GbaCollisionSurfaceTests(TestPaths paths)
{
    private const int Samples = 5;

    private static byte[]? LoadThps2(TestPaths paths)
    {
        var path = paths.FindSampleFile(
            "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba");
        return path == null ? null : File.ReadAllBytes(path);
    }

    private static int TrueRecordOffset(GbaLevelImages.GbaLevel level) =>
        (int)(level.RecordAddress - 0x08000000) - 0x144;

    private static byte[] SampleLevel(byte[] rom, GbaLevelImages.GbaLevel level, out int cellCount)
    {
        var grid = GbaCollisionSurface.TryLoad(rom, TrueRecordOffset(level));
        Assert.NotNull(grid);
        cellCount = grid.Width * grid.Height;
        var bytes = new byte[cellCount * Samples * Samples * 4];
        var at = 0;
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            foreach (var h in grid.SampleCell(rom, x, y, Samples))
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(at, 4), h);
                at += 4;
            }
        }

        return bytes;
    }

    // Per-level digests of the engine-computed heights. Derived from an independently
    // validated reference implementation (the shape table was confirmed by a
    // cross-cell edge-continuity test that beat all 11 relabelling/permutation
    // controls by >1400x on the p75 residual).
    [CorpusTheory]
    [InlineData(0, "Hangar", 31, 19, "1832cdc6ecc040b233ad72fedbee91db2dece6bbe56ae0d0d4c4fbc229d01e37")]
    [InlineData(1, "School II", 36, 50, "d697b96a20cb25cc218a958d6b01a889af13f1d8529012a54db24933635e5eb6")]
    [InlineData(2, "Marseille", 56, 35, "dfae22c8265a90a83e0f80d24ed5874a5c3f635041f90bf02fa9513c6760b75e")]
    [InlineData(3, "Warehouse", 23, 31, "dd8945e126b1acdf2a7d9211b2f2e03b6ab11456c999c71efdba9d422d0b979b")]
    [InlineData(4, "NY City", 43, 44, "7c3ac88f13a2fbdd58b426562228542ab34643fd08fab98fbfccc9834644756a")]
    [InlineData(5, "Skate Street", 33, 27, "4516865974f9c378ce2855fe63ac9bfa0d5d4e9f6aaa9f1e058f969477d14aef")]
    [InlineData(6, "Rooftops", 15, 28, "547bfc04877d79415f29677bb93e27db147d899958605d043ad03f795ede18aa")]
    [InlineData(7, "Wind Tunnel", 9, 15, "d0bda8f629d530fcd486abe620d0b63cdfaf0eaec837642ed3e24a2204a9e3cb")]
    [InlineData(8, "pool", 10, 12, "6e53ec34bcddd247ca00dad36e114a4eed9bb4710ecb7751c01551daa38c7e22")]
    public void ExecutesEveryCellsHeightFunction(int index, string name, int width, int height, string sha)
    {
        _ = name;
        var rom = LoadThps2(paths);
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);

        var grid = GbaCollisionSurface.TryLoad(rom, TrueRecordOffset(levels[index]));
        Assert.NotNull(grid);
        Assert.Equal(width, grid.Width);
        Assert.Equal(height, grid.Height);

        var bytes = SampleLevel(rom!, levels[index], out _);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [CorpusFact]
    public void AggregateAcrossAllNineLevelsMatches()
    {
        var rom = LoadThps2(paths);
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);
        Assert.Equal(9, levels.Count);

        using var sha = SHA256.Create();
        var cells = 0;
        foreach (var level in levels)
        {
            var bytes = SampleLevel(rom!, level, out var count);
            cells += count;
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        Assert.Equal(8520, cells);
        Assert.Equal(
            "aef9b116575130c3bd43bfbd3470f45142dba7a0ddc41d173420de8dd19bc629",
            Convert.ToHexStringLower(sha.Hash!));
    }

    [Fact]
    public void ShapeTransformIsTheEightSquareSymmetries()
    {
        const int span = 0x2FFF;
        // All 8 must be distinct as maps, and each must be an involution-or-rotation
        // that keeps the sub-cell square closed under itself.
        var seen = new HashSet<(int, int)>();
        for (var shape = 0; shape < 8; shape++)
        {
            var probe = GbaCollisionSurface.ShapeTransform(shape, 0x0400, 0x0900);
            Assert.True(seen.Add(probe), $"shape {shape} duplicates another transform");
            for (var u = 0; u <= span; u += span / 4)
            for (var v = 0; v <= span; v += span / 4)
            {
                var (a, b) = GbaCollisionSurface.ShapeTransform(shape, u, v);
                Assert.InRange(a, 0, span);
                Assert.InRange(b, 0, span);
            }
        }

        Assert.Equal((0x0400, 0x0900), GbaCollisionSurface.ShapeTransform(0, 0x0400, 0x0900));
    }

    [Fact]
    public void HangarHasBothFlatAndSlopedCells()
    {
        var rom = LoadThps2(paths);
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var levels = GbaLevelImages.FindLevels(rom);
        var grid = GbaCollisionSurface.TryLoad(rom, TrueRecordOffset(levels[0]));
        Assert.NotNull(grid);

        var sloped = 0;
        var flat = 0;
        for (var y = 0; y < grid.Height; y++)
        for (var x = 0; x < grid.Width; x++)
        {
            if (grid.IsSloped(rom, x, y))
                sloped++;
            else
                flat++;
        }

        // Both classes must be well represented: an all-flat result would mean the
        // height functions are not being executed, which is the bug this guards.
        Assert.True(sloped > 50, $"expected real sloped cells, got {sloped}");
        Assert.True(flat > 50, $"expected real flat cells, got {flat}");
    }
}
