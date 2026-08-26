using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the manifest tables the carts' own code carries: which geometry pieces
///     make up a model set, what each draws with, and what the artist called it.
///
///     Every assertion here is an AGREEMENT between two sources that share no
///     machinery. The tables are found in code and accepted only when they reproduce
///     the container's grouping; the class word is checked against a property read
///     from the geometry files themselves; and the recovered names are checked against
///     the shape of the geometry they name. A table that merely looked like records
///     could not satisfy any of them.
/// </summary>
public sealed class NdsModelSetManifestTests(TestPaths paths)
{
    private static readonly (string Build, string Rom, string Gob)[] Carts =
    [
        ("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
            "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
            "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob"),
        ("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
            "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob")
    ];

    [CorpusTheory]
    [MemberData(nameof(ManifestCases))]
    public void RealCart_ManifestsCoverEveryPieceAndCarryTheAuthoredNames(
        string build, string rom, string gobPath, string expected)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var manifests = NdsCartManifests.Read(cart, gob!);
        var pieces = manifests.Values.Sum(m => m.Pieces.Count);
        var named = manifests.Values.Sum(m => m.Pieces.Count(p => p.Name != null));

        // A manifest is only accepted when it reproduces its set's whole group, so
        // this restates the gate from the container's side: no piece may be missing
        // and none may be invented.
        var geometry = new Dictionary<uint, HashSet<uint>>();
        foreach (var entry in gob!.Entries)
        {
            if (NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out var idB))
                (geometry.TryGetValue(idA, out var s) ? s : geometry[idA] = []).Add(idB);
        }

        foreach (var manifest in manifests.Values)
        {
            var group = geometry[manifest.IdA];
            Assert.Equal(group.Count, manifest.Pieces.Count);
            Assert.All(manifest.Pieces, p => Assert.Contains(p.IdB, group));
        }

        Assert.Equal(expected, $"{manifests.Count}/{pieces}/{named}");
    }

    /// <summary>
    ///     The class word and the geometry file agree about which pieces are cameras.
    ///     The record says so with <c>ClassFlags == 3</c>; the file says so with a
    ///     header marker plus a perspective matrix in its display list. Neither reads
    ///     the other, so their agreement is evidence — and the direction that matters
    ///     is that NEITHER side ever claims a camera the other denies.
    ///
    ///     A third signal agrees with both: a class-3 record is exactly a record
    ///     that declares no texture bank, and the 6 + 194 + 329 cameras counted here
    ///     are the 529 bank-less records across the three carts.
    /// </summary>
    [CorpusTheory]
    [MemberData(nameof(CameraAgreementCases))]
    public void RealCart_TheClassWordAndTheGeometryFileAgreeOnCameras(
        string build, string rom, string gobPath, int expectedCameras)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        var manifests = NdsCartManifests.Read(cart!, gob!);

        var cameras = 0;
        var disagreements = 0;
        foreach (var manifest in manifests.Values)
        foreach (var piece in manifest.Pieces)
        {
            var entry = gob!.Entries.FirstOrDefault(
                e => e.Crc == GobNames.Hash($".\\{manifest.IdA:x8}.{piece.IdB:x8}.geometry.bin"));
            if (entry == null || !NdsGeometryFile.TryParseValidated(gob.ReadEntry(entry), out var geometry))
                continue;

            if (piece.IsCamera)
                cameras++;
            if (piece.IsCamera != geometry.IsCameraRig)
                disagreements++;
        }

        Assert.Equal(0, disagreements);
        Assert.Equal(expectedCameras, cameras);
    }

    /// <summary>
    ///     The names are the artists' own, and the geometry backs them up: a piece the
    ///     exporter called a decal or a shadow really is a flat sheet. Measured as the
    ///     ratio of a box's shortest side to its longest, so it cannot be satisfied by
    ///     a name that landed on the wrong record.
    /// </summary>
    [CorpusFact]
    public void RealCart_DecalNamesLandOnFlatGeometry()
    {
        var romPath = paths.FindSampleFile(Carts[0].Build, Carts[0].Rom);
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(Carts[0].Gob)!);
        var manifests = NdsCartManifests.Read(cart!, gob!);

        var decals = new List<float>();
        var others = new List<float>();
        foreach (var manifest in manifests.Values)
        foreach (var piece in manifest.Pieces)
        {
            if (piece.Name == null)
                continue;
            var entry = gob!.Entries.FirstOrDefault(
                e => e.Crc == GobNames.Hash($".\\{manifest.IdA:x8}.{piece.IdB:x8}.geometry.bin"));
            if (entry == null || !NdsGeometryFile.TryParseValidated(gob.ReadEntry(entry), out var geometry))
                continue;
            if (geometry.IsCameraRig)
                continue;

            var e = geometry.DeclaredExtent;
            var longest = MathF.Max(e.X, MathF.Max(e.Y, e.Z));
            var shortest = MathF.Min(e.X, MathF.Min(e.Y, e.Z));
            if (longest < 0.01f)
                continue;

            var thinness = shortest / longest;
            if (piece.Name.Contains("DECAL", StringComparison.OrdinalIgnoreCase)
                || piece.Name.Contains("SHADOW", StringComparison.OrdinalIgnoreCase))
            {
                decals.Add(thinness);
            }
            else
            {
                others.Add(thinness);
            }
        }

        Assert.NotEmpty(decals);
        var decalMedian = Median(decals);
        var otherMedian = Median(others);
        // Two orders of magnitude apart in the corpus (0.0014 against 0.2173); the
        // assertion is deliberately looser than the measurement.
        Assert.True(decalMedian * 20 < otherMedian,
            $"decal-named median thinness {decalMedian} vs {otherMedian} for the rest");
    }

    private static float Median(List<float> values)
    {
        values.Sort();
        return values.Count == 0 ? float.NaN : values[values.Count / 2];
    }

    public static TheoryData<string, string, string, string> ManifestCases()
    {
        var data = new TheoryData<string, string, string, string>();
        // tables / pieces / pieces carrying an authored name
        data.Add(Carts[0].Build, Carts[0].Rom, Carts[0].Gob, "11/876/866");
        data.Add(Carts[1].Build, Carts[1].Rom, Carts[1].Gob, "16/1091/1058");
        data.Add(Carts[2].Build, Carts[2].Rom, Carts[2].Gob, "20/1594/1452");
        return data;
    }

    public static TheoryData<string, string, string, int> CameraAgreementCases()
    {
        var data = new TheoryData<string, string, string, int>();
        data.Add(Carts[0].Build, Carts[0].Rom, Carts[0].Gob, 6);
        data.Add(Carts[1].Build, Carts[1].Rom, Carts[1].Gob, 194);
        data.Add(Carts[2].Build, Carts[2].Rom, Carts[2].Gob, 329);
        return data;
    }
}
