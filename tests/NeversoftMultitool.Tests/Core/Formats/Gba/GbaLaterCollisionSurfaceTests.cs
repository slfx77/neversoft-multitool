using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Gba;

/// <summary>
///     Pins the collision complexes shared by THPS4 through American Sk8land.
///     Sampling executes the cartridges' own surface-object functions.
/// </summary>
public sealed class GbaLaterCollisionSurfaceTests(TestPaths paths)
{
    private sealed record CorpusExpectation(
        string Layout,
        string HeightSha,
        int BoundaryCells,
        int MinimumBoundary,
        int MaximumPlayfield,
        int FirstMeshTriangles,
        int RenderWidth,
        int RenderHeight,
        int RenderOmitted,
        string RenderSha);

    private static readonly IReadOnlyDictionary<string, CorpusExpectation> Expectations =
        new Dictionary<string, CorpusExpectation>(StringComparer.Ordinal)
        {
            ["Tony Hawk's Pro Skater 4 (USA, Europe).gba"] = new(
                "47x37/935/93,44x43/636/93,54x44/1046/102,43x43/741/88," +
                "54x44/515/41,50x46/948/95,39x47/1022/138,30x20/69/12",
                "DD4B0474390A5118E0324D764F908FD4A7CD80718BEB4BCADBD2294EF31BBEBB",
                1836, 124928, 122879, 54637, 3620, 2097, 226,
                "52D5F127F6C8402C4CE61EE2A99774FC1CD9C10093DCE39661AD881AD50F94DE"),
            ["Tony Hawk's Underground (USA, Europe).gba"] = new(
                "40x40/1024/48,40x40/435/34,40x40/197/22,40x40/961/82," +
                "40x40/673/67,40x26/175/22,40x40/926/74,40x40/453/36," +
                "40x40/775/64,40x40/382/56",
                "52AFAC10224A4A105F1D1E7BC08AFB5A0FAF4F394E1FE36CE159CD8901DF0946",
                2563, 217088, 114688, 51219, 3350, 1835, 231,
                "A1D5FDE021F7AC1E56017D2C35A0F4F497D748D1E0F9C2C92F993A93AC782222"),
            ["Tony Hawk's Underground 2 (USA, Europe).gba"] = new(
                "40x40/866/77,40x40/880/95,40x40/741/68,40x40/835/81," +
                "57x57/1207/199,40x40/693/59,40x40/708/64",
                "0BB885B12D9AC02787FF61D79AB069E8DAC0F282B1A6A35DC0C77DBE2C339055",
                3067, 241664, 81920, 50523, 3440, 1827, 241,
                "E9A6EF0EA15459AFA04CC0092F0B23555745684D0960590E68561B978C943666"),
            ["Tony Hawk's American Sk8land (USA).gba"] = new(
                "25x25/77/15,40x40/702/100,40x40/27/3,40x40/345/27," +
                "40x40/346/27,40x40/407/41,40x40/450/52,40x40/492/57," +
                "60x65/1398/112,50x65/979/104,40x40/614/107,50x87/1469/100",
                "422A459FD969C793F37A397D06C4EDDA2A4DCF81987CA178316628B479EC23CB",
                4879, 124927, 122879, 19570, 2270, 1107, 64,
                "F53000F0182597408D15AD272683BE245F46B93CB4D41993D1BA7F53A294359B")
        };

    public static TheoryData<string, string, int, GbaLaterCollisionSurface.CellRevision> Carts => new()
    {
        {
            "Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)",
            "Tony Hawk's Pro Skater 4 (USA, Europe).gba", 8,
            GbaLaterCollisionSurface.CellRevision.Thps4
        },
        {
            "Tony Hawk's Underground (2003-10-27, GBA - Final)",
            "Tony Hawk's Underground (USA, Europe).gba", 10,
            GbaLaterCollisionSurface.CellRevision.Underground
        },
        {
            "Tony Hawk's Underground 2 (2004-10-4, GBA - Final)",
            "Tony Hawk's Underground 2 (USA, Europe).gba", 7,
            GbaLaterCollisionSurface.CellRevision.Underground
        },
        {
            "Tony Hawk's American Sk8land (2005-10-18, GBA - Final)",
            "Tony Hawk's American Sk8land (USA).gba", 12,
            GbaLaterCollisionSurface.CellRevision.Underground
        }
    };

    private byte[]? Load(string build, string file)
    {
        var path = paths.FindSampleFile(build, file);
        return path == null ? null : File.ReadAllBytes(path);
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void EveryParentClosesAsACollisionComplex(
        string build, string file, int expectedLevels, GbaLaterCollisionSurface.CellRevision revision)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");
        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.Equal(expectedLevels, levels.Count);

        foreach (var level in levels)
        {
            var grid = GbaLaterCollisionSurface.TryLoad(rom, level);
            Assert.True(grid != null,
                $"level {level.Index}, parent 0x{level.LevelRecordOffset:X}, art 0x{level.ArtRecordOffset:X}");
            Assert.Equal(revision, grid.Revision);
            Assert.InRange(grid.Width, 4, 128);
            Assert.InRange(grid.Height, 4, 128);
            Assert.InRange(grid.RecordCount, 1, grid.Width * grid.Height);
            Assert.True(grid.SurfaceCount > 0);
        }
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void EveryReferencedSurfaceFunctionExecutes(
        string build, string file, int expectedLevels, GbaLaterCollisionSurface.CellRevision revision)
    {
        _ = revision;
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");
        var levels = GbaLaterLevelArt.FindLevels(rom);
        Assert.Equal(expectedLevels, levels.Count);

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var facts = new List<string>();
        var boundaryCells = 0;
        var minimumBoundary = int.MaxValue;
        var maximumPlayfield = int.MinValue;
        Span<byte> bytes = stackalloc byte[4];
        foreach (var level in levels)
        {
            var grid = GbaLaterCollisionSurface.TryLoad(rom, level);
            Assert.True(grid != null,
                $"level {level.Index}, parent 0x{level.LevelRecordOffset:X}, art 0x{level.ArtRecordOffset:X}");
            facts.Add($"{grid.Width}x{grid.Height}/{grid.RecordCount}/{grid.SurfaceCount}");
            var seen = new bool[grid.SurfaceCount];
            for (var y = 0; y < grid.Height; y++)
            for (var x = 0; x < grid.Width; x++)
            {
                var surface = grid.HeightSurfaceAt(x, y);
                seen[surface] = true;
                var heights = grid.SampleCell(rom, x, y, 3);
                var maximum = int.MinValue;
                foreach (var height in heights)
                {
                    BitConverter.TryWriteBytes(bytes, height);
                    digest.AppendData(bytes);
                    maximum = Math.Max(maximum, height);
                }
                if (maximum / 4096.0 > GbaCollisionRenderer.OutOfBoundsHeight)
                {
                    boundaryCells++;
                    minimumBoundary = Math.Min(minimumBoundary, maximum);
                }
                else
                    maximumPlayfield = Math.Max(maximumPlayfield, maximum);
            }
            Assert.DoesNotContain(false, seen);
        }

        var hash = Convert.ToHexString(digest.GetHashAndReset());
        var expected = Expectations[file];
        Assert.Equal(expected.Layout, string.Join(",", facts));
        Assert.Equal(expected.HeightSha, hash);
        Assert.Equal(expected.BoundaryCells, boundaryCells);
        Assert.Equal(expected.MinimumBoundary, minimumBoundary);
        Assert.Equal(expected.MaximumPlayfield, maximumPlayfield);
    }

    [Fact]
    public void ShapeDispatcherIncludesSquareAndDiagonalTransforms()
    {
        Assert.Equal((0x0400, 0x0900), GbaLaterCollisionSurface.ShapeTransform(0, 0x0400, 0x0900));
        Assert.Equal((0x26FF, 0x0400), GbaLaterCollisionSurface.ShapeTransform(1, 0x0400, 0x0900));
        Assert.Equal((0x0900, 0x2BFF), GbaLaterCollisionSurface.ShapeTransform(3, 0x0400, 0x0900));

        var diagonal = new HashSet<(int, int)>();
        for (var shape = 9; shape <= 12; shape++)
            Assert.True(diagonal.Add(GbaLaterCollisionSurface.ShapeTransform(shape, 0x0400, 0x0900)));
        Assert.Equal(4, diagonal.Count);
    }

    [Fact]
    public void HeightUnitsAndKillWallThresholdMatchTheMeasuredCorpusGap()
    {
        const int fixedOne = 0x1000;
        Assert.Equal(3 * fixedOne, GbaThumbCpu.CellSpan);
        Assert.Equal(30.0, GbaCollisionRenderer.OutOfBoundsHeight);

        var boundaryRaw = (int)(GbaCollisionRenderer.OutOfBoundsHeight * fixedOne);
        Assert.All(Expectations.Values, expected =>
        {
            Assert.True(expected.MaximumPlayfield <= boundaryRaw);
            Assert.True(expected.MinimumBoundary > boundaryRaw);
        });
    }

    [Fact]
    public void InterpreterModelsOnlyExplicitRuntimeAddresses()
    {
        // ldr r0,[pc,#4]; ldr r0,[r0]; bx lr; pad; .word 0x02001234
        byte[] wordReader =
        [
            0x01, 0x48, 0x00, 0x68, 0x70, 0x47, 0x00, 0x00,
            0x34, 0x12, 0x00, 0x02
        ];
        var cpu = new GbaThumbCpu();
        var unknown = Assert.Throws<InvalidDataException>(() =>
            cpu.Run(wordReader, 0x08000000, 0, 0, new byte[40], 0x08004321));
        Assert.Contains("0x02001234", unknown.ToString(), StringComparison.Ordinal);
        Assert.Equal(0x08004321, cpu.Run(
            wordReader, 0x08000000, 0, 0, new byte[40],
            runtimeObjectBank: 0x08004321,
            runtimeObjectBankAddress: 0x02001234));

        // Changing only the second instruction to LDRB proves byte snapshots are
        // just as exact: the named address works; the adjacent byte does not.
        var byteReader = (byte[])wordReader.Clone();
        byteReader[2] = 0x00;
        byteReader[3] = 0x78;
        Assert.Equal(0x5A, cpu.Run(
            byteReader, 0x08000000, 0, 0, new byte[40],
            runtimeBytes: new Dictionary<uint, byte> { [0x02001234] = 0x5A }));
        byteReader[8]++;
        Assert.Throws<InvalidDataException>(() => cpu.Run(
            byteReader, 0x08000000, 0, 0, new byte[40],
            runtimeBytes: new Dictionary<uint, byte> { [0x02001234] = 0x5A }));
    }

    [CorpusTheory]
    [MemberData(nameof(Carts))]
    public void FirstLevelConvertsToATexturedCollisionMesh(
        string build, string file, int expectedLevels, GbaLaterCollisionSurface.CellRevision revision)
    {
        _ = expectedLevels;
        _ = revision;
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");
        var carve = GbaLevelCarver.Carve(rom);
        var level = Assert.Single(carve,
            item => Path.GetFileName(item.Path).Equals("0_level.lvl.gba", StringComparison.Ordinal));
        var trueRecord = GbaLevelCarver.FindRecordOffset(rom, level.Data);
        Assert.True(trueRecord >= 0);

        var native = new GbaLevelNativeSource(level.Data, rom, trueRecord, "level0", "");
        var document = ModelDocument.CreateNative("level0", ModelSourceKind.GbaLevel, native);
        GbaLevelGeometryWriter.Populate(document, native);

        var collision = GbaCollisionRenderer.Render(rom, GbaLaterLevelArt.FindLevels(rom)[0]);
        Assert.NotNull(collision);
        var expected = Expectations[file];
        Assert.Equal(expected.FirstMeshTriangles, document.TriangleCount);
        Assert.Equal(expected.RenderWidth, collision.Value.Width);
        Assert.Equal(expected.RenderHeight, collision.Value.Height);
        Assert.Equal(expected.RenderOmitted, collision.Value.OmittedCells);
        Assert.Equal(expected.RenderSha, Convert.ToHexString(SHA256.HashData(collision.Value.Rgba)));
        Assert.Single(document.Materials);
        Assert.Single(document.Textures);
        Assert.True(document.Textures[0].PngBytes?.Length > 1_000);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "Tony Hawk's Pro Skater 2 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", "Tony Hawk's Pro Skater 3 (USA, Europe).gba")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "Tony Hawk's Downhill Jam (USA).gba")]
    public void OtherEnginesAreNotClaimed(string build, string file)
    {
        var rom = Load(build, file);
        Assert.SkipWhen(rom == null, $"{build} ROM sample not available");
        Assert.Empty(GbaLaterLevelArt.FindLevels(rom));
    }
}
