using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Nds;

/// <summary>
///     Pins the DS collision world.
///
///     The parse gate is the file's own arithmetic: every section's stored offset is
///     reproduced by <c>align4(previous + count * stride)</c> at strides 6, 10 and 12.
///     Three strides landing on three stored offsets across every shipped file is not
///     a fit, and the ARM9 states the same strides independently.
///
///     The edge network gets its own assertions because its FIELD ORDER rests on them:
///     <c>next</c> and <c>prev</c> are mutual inverses, and a joined pair shares the
///     vertex — <c>rec[next].v0 == rec.v1</c>. The three other orientations score at
///     chance, which is what says which field is which.
/// </summary>
public sealed class NdsCollisionFileTests(TestPaths paths)
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

    [Fact]
    public void Parse_RefusesAnythingThatIsNotACollisionWorld()
    {
        Assert.False(NdsCollisionFile.IsCollisionWorld([]));
        Assert.False(NdsCollisionFile.IsCollisionWorld("not a collision world at all"u8));
        // Right magic, arithmetic that does not tile: the gate is the tiling.
        var bogus = new byte[64];
        "LWC"u8.CopyTo(bogus);
        bogus[3] = 1;
        BitConverter.GetBytes(4).CopyTo(bogus, 4);      // vertexCount
        BitConverter.GetBytes(32).CopyTo(bogus, 8);     // vertexOffset
        BitConverter.GetBytes(1).CopyTo(bogus, 12);     // triangleCount
        BitConverter.GetBytes(999).CopyTo(bogus, 16);   // triangleOffset — wrong
        Assert.False(NdsCollisionFile.IsCollisionWorld(bogus));
    }

    [CorpusFact]
    public void RealCarts_EveryCollisionWorldTilesExactly()
    {
        var files = 0;
        var faces = 0;
        var edges = 0;
        var volumeFaces = 0;
        var degenerate = 0;

        var linked = 0;
        var sharesVertex = 0;
        var mutualInverse = 0;
        var heads = 0;
        var tails = 0;

        foreach (var (build, rom, gobPath) in Carts)
        {
            var romPath = paths.FindSampleFile(build, rom);
            Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

            using var cart = ArchiveFileSystem.TryOpen(romPath!);
            using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
            foreach (var entry in gob!.Entries)
            {
                var name = GobNames.TryResolve(entry.Crc);
                if (name == null || !name.EndsWith(".lwc", StringComparison.OrdinalIgnoreCase))
                    continue;

                Assert.True(NdsCollisionFile.TryParse(gob.ReadEntry(entry), out var world),
                    $"{name} did not tile");
                files++;
                faces += world!.Faces.Count;
                edges += world.Edges.Count;

                foreach (var face in world.Faces)
                {
                    if (face.IsVolume)
                        volumeFaces++;
                    if (face.V0 == face.V1 || face.V1 == face.V2 || face.V0 == face.V2)
                        degenerate++;
                }

                for (var i = 0; i < world.Edges.Count; i++)
                {
                    var edge = world.Edges[i];
                    if (edge.IsChainHead)
                        heads++;
                    if (edge.IsChainTail)
                        tails++;
                    if (edge.IsChainTail)
                        continue;

                    linked++;
                    var next = world.Edges[edge.Next];
                    if (next.V0 == edge.V1)
                        sharesVertex++;
                    if (next.Previous == i)
                        mutualInverse++;
                }
            }
        }

        // The corpus, and the same 138,507 the derivation predicted.
        Assert.Equal(23, files);
        Assert.Equal(138507, faces);
        Assert.Equal(21177, edges);
        Assert.Equal(4194, volumeFaces);

        // Not zero: 151 faces repeat an index. Recorded rather than asserted away,
        // because an earlier write-up claimed none and that was wrong.
        Assert.Equal(151, degenerate);

        // The linkage, which is what fixes the field order.
        Assert.Equal(17045, linked);
        Assert.Equal(linked, sharesVertex);
        Assert.Equal(linked, mutualInverse);
        // Every chain has exactly one head and one tail.
        Assert.Equal(heads, tails);
        Assert.Equal(4132, heads);
    }

    [CorpusFact]
    public void Sk8land_CollisionSitsWhereTheLevelIs()
    {
        // The scale is not a tuned constant: it is what puts the collision world in
        // the same space as the render geometry. Warehouse is the level where the two
        // meshes cover the same ground, so its boxes should very nearly coincide.
        var romPath = paths.FindSampleFile(Carts[0].Build, Carts[0].Rom);
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(Carts[0].Gob)!);
        var entry = gob!.FindByPath("Level_Warehouse_Collision.lwc");
        Assert.NotNull(entry);
        Assert.True(NdsCollisionFile.TryParse(gob.ReadEntry(entry!), out var world));

        var min = world!.Vertices.Aggregate(System.Numerics.Vector3.Min);
        var max = world.Vertices.Aggregate(System.Numerics.Vector3.Max);

        // The level's own render box, measured earlier from its geometry headers:
        // (-18.7, -159.3, -2.1) .. (171.6, 174.5, 36.9) in the file's Z-up space.
        Assert.InRange(min.X, -20f, -15f);
        Assert.InRange(min.Y, -162f, -157f);
        Assert.InRange(max.X, 169f, 174f);
        Assert.InRange(max.Y, 172f, 177f);
        Assert.InRange(max.Z, 35f, 39f);
    }
}
