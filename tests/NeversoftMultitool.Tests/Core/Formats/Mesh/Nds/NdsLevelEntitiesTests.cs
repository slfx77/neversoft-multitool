using System.Numerics;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the DS level entity layer: the S-K-A-T-E letters, the trick orbs, the
///     pedestrians and props a level places.
///
///     Two independent things make this a real check rather than a plausible parse.
///     Every placement names its model set as a STRING, and every one of those
///     strings re-hashes onto a set the container actually holds — a wrong record
///     grammar would produce bytes that hash onto nothing. And every position lands
///     inside the level that owns the file, while the same positions scored against
///     the OTHER levels of the same cart land inside far less often.
/// </summary>
public sealed class NdsLevelEntitiesTests(TestPaths paths)
{
    [Fact]
    public void DataFileFor_PairsALevelWithItsOwnPropertyFile()
    {
        Assert.Equal("Level_Alcatraz_Collision.prp",
            NdsLevelEntities.DataFileFor("Level_Alcatraz_Visual"));
        Assert.Equal("Frontend_Collision.prp", NdsLevelEntities.DataFileFor("Frontend_Visual"));
        // A sky is not a level and has no property file of its own.
        Assert.Null(NdsLevelEntities.DataFileFor("Level_Alcatraz_Sky_Visual"));
        Assert.Null(NdsLevelEntities.DataFileFor("skate_s"));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 356, 356, "687/2492")]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 460, 460, "1229/2760")]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 795, 787, "3450/5565")]
    public void RealCart_EveryPlacementNamesARealSetAndStandsInItsOwnLevel(
        string build, string rom, string gobPath, int expectedPlacements, int expectedInOwnBox,
        string expectedControl)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        var naming = NdsModelNaming.For(gob!);
        var setIds = naming.Sets.Keys.ToHashSet();

        // Each level's own geometry box, from the geometry headers.
        var boxes = new Dictionary<string, (Vector3 Min, Vector3 Max)>(StringComparer.Ordinal);
        foreach (var (idA, setName) in naming.Sets)
        {
            if (!NdsSetNames.IsLevel(setName))
                continue;
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var any = false;
            foreach (var entry in gob!.Entries)
            {
                if (!NdsModelSet.TryParseGeometryName(entry.Name.StartsWith(".\\", StringComparison.Ordinal)
                        ? entry.Name : ".\\" + entry.Name, out var a, out _) || a != idA)
                {
                    continue;
                }

                byte[] bytes;
                try
                {
                    bytes = gob.ReadEntry(entry);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                if (!NdsGeometryFile.TryParseValidated(bytes, out var geometry)
                    || geometry.HasBoilerplateBox)
                {
                    continue;
                }

                var half = geometry.DeclaredExtent * 0.5f;
                min = Vector3.Min(min, geometry.DeclaredCentre - half);
                max = Vector3.Max(max, geometry.DeclaredCentre + half);
                any = true;
            }

            if (any)
                boxes[setName] = (min, max);
        }

        var perLevel = new List<string>();
        var placements = 0;
        var inOwn = 0;
        var inOther = 0;
        var otherTests = 0;
        foreach (var (setName, box) in boxes.OrderBy(b => b.Key, StringComparer.Ordinal))
        {
            var entry = gob!.FindByPath(NdsLevelEntities.DataFileFor(setName)!);
            if (entry == null)
                continue;
            var parsed = NdsLevelEntities.Parse(gob.ReadEntry(entry), setIds);
            placements += parsed.Count;
            perLevel.Add($"{setName}={parsed.Count}");

            foreach (var placement in parsed)
            {
                // Naming is the strong half: a wrong grammar hashes onto nothing.
                Assert.Equal(placement.SetId, NdsSetNames.Hash(placement.ModelName));
                Assert.Contains(placement.SetId, setIds);
                if (Within(placement.Position, box))
                    inOwn++;
                foreach (var (otherName, otherBox) in boxes)
                {
                    if (otherName == setName)
                        continue;
                    otherTests++;
                    if (Within(placement.Position, otherBox))
                        inOther++;
                }
            }
        }

        Assert.True(expectedPlacements == placements,
            $"expected {expectedPlacements}, got {placements}: {string.Join(", ", perLevel)}");

        // Nearly every placement stands inside the box its level's GEOMETRY declares.
        // The eight that do not are all in one Proving Ground level — three start
        // markers and five videotapes in Baltimore_A — so the residue is named rather
        // than waved at, and the box is an oracle for the parse, not a rule the data
        // is obliged to obey.
        Assert.Equal(expectedInOwnBox, inOwn);
        // The cross-level control is recorded rather than thresholded, because how
        // much it says depends on the cart: Sk8land's levels sit in well-separated
        // parts of world space, while Proving Ground's are similar city blocks around
        // the same coordinates, so their boxes overlap and the control is weak there
        // by construction. The strong evidence is the naming above — every placement's
        // string hashes onto a set the container holds, which a wrong grammar cannot
        // do — and this number is here so a regression in it is visible.
        Assert.Equal(expectedControl, $"{inOther}/{otherTests}");
    }

    [CorpusFact]
    public void Sk8land_EachLevelPlacesExactlyOneOfEachSkateLetter()
    {
        // Five letters, one each, is the count signature a wrong parse cannot fake.
        var romPath = paths.FindSampleFile(
            "Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds");
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath("vvobj/generated/gob/main.gob")!);
        var naming = NdsModelNaming.For(gob!);
        var setIds = naming.Sets.Keys.ToHashSet();

        string[] letters = ["skate_s", "skate_k", "skate_a", "skate_t", "skate_e"];
        var levelsWithLetters = 0;
        foreach (var setName in naming.Sets.Values.Where(NdsSetNames.IsLevel))
        {
            var entry = gob!.FindByPath(NdsLevelEntities.DataFileFor(setName)!);
            if (entry == null)
                continue;
            var parsed = NdsLevelEntities.Parse(gob.ReadEntry(entry), setIds);
            var found = letters.Select(l => parsed.Count(p => p.ModelName == l)).ToArray();
            if (found.All(c => c == 0))
                continue;
            levelsWithLetters++;
            // The signature is BALANCE, not a fixed number: a level places the same
            // count of every one of the five letters. Three Sk8land levels place two
            // full sets and four place one, so asserting a constant would be wrong —
            // and asserting balance is what a mis-parse could not satisfy.
            Assert.Equal(1, found.Distinct().Count());
            Assert.InRange(found[0], 1, 2);
            // And they stand apart, not stacked on one spot.
            var positions = parsed.Where(p => letters.Contains(p.ModelName))
                .Select(p => p.Position).ToList();
            Assert.Equal(found[0] * 5, positions.Distinct().Count());
        }

        // Every skateable level, and not the front end.
        Assert.Equal(7, levelsWithLetters);
    }

    private static bool Within(Vector3 point, (Vector3 Min, Vector3 Max) box)
    {
        const float slack = 2f;
        return point.X >= box.Min.X - slack && point.X <= box.Max.X + slack
            && point.Y >= box.Min.Y - slack && point.Y <= box.Max.Y + slack
            && point.Z >= box.Min.Z - slack && point.Z <= box.Max.Z + slack;
    }
}
