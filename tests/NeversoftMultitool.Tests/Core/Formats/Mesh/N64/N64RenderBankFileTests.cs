using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the N64 render-bank decode (2026-08-06). The format is a packed
///     F3DEX2 display list over a plain 16-byte vertex array — not the
///     "bit-packed vertex codec" long assumed. Two fixtures cover the two
///     pool layouts: THPS2 stores vertices byte-plane transposed, THPS1 plain,
///     and the decoder picks between them using each node's own float bounds.
/// </summary>
public sealed class N64RenderBankFileTests(TestPaths paths)
{
    private static Dictionary<string, byte[]> CarveBanks(TestPaths paths, string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets
            .Where(static asset => asset.Path.StartsWith("group2/", StringComparison.Ordinal))
            .ToDictionary(static asset => asset.Path, static asset => asset.Data);
    }

    [Fact]
    public void TransposedPool_DecodesTheThps2Bank()
    {
        var banks = CarveBanks(paths,
            "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)", "Tony Hawk's Pro Skater 2 (USA).z64");

        // group2/022.bin is the bank models/000 points at.
        var meshes = N64RenderBankFile.Parse(banks["group2/022.bin"]);

        var mesh = Assert.Single(meshes);
        Assert.Equal(473, mesh.Vertices.Count);
        // Blob B carries one u32 per triangle; the display list must expand to
        // exactly that many, which is the format's own cross-check.
        Assert.Equal(570, mesh.Triangles.Count);

        // 56-byte bounds accompany the transposed layout, and the decoded
        // extents must sit inside the authored bounds.
        Assert.Equal(14, mesh.Bounds.Length);
        Assert.All(mesh.Vertices, v =>
        {
            Assert.InRange(v.X, (short)mesh.Bounds[0], (short)mesh.Bounds[3]);
            Assert.InRange(v.Y, (short)mesh.Bounds[1], (short)mesh.Bounds[4]);
            Assert.InRange(v.Z, (short)mesh.Bounds[2], (short)mesh.Bounds[5]);
        });
    }

    [Fact]
    public void PlainPool_DecodesTheThps1Bank()
    {
        var banks = CarveBanks(paths,
            "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)", "Tony Hawk's Pro Skater (USA).z64");

        var meshes = N64RenderBankFile.Parse(banks["group2/001.bin"]);

        // A level bank: many mesh nodes, 32-byte bounds, untransposed pools.
        // The record's root lists 441 children; 4 are not 3-child mesh nodes.
        Assert.Equal(437, meshes.Count);
        Assert.Equal(8, meshes[0].Bounds.Length);
        Assert.Equal(7_173, meshes.Sum(static m => m.Vertices.Count));
        Assert.Equal(5_527, meshes.Sum(static m => m.Triangles.Count));
    }

    /// <summary>
    ///     Every triangle must reference a loaded cache slot inside the pool —
    ///     the invariant that proves the display-list walk stays in sync with
    ///     the vertex cursor across a whole ROM.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 11_049, 190_287, 150_140)]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
        "Tony Hawk's Pro Skater 2 (USA).z64", 13_651, 256_496, 204_131)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 9_707, 261_309, 223_348)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64",
        7_498, 302_296, 234_190)]
    public void EveryBankDecodes_WithInBoundsIndices(
        string buildName,
        string romName,
        int expectedMeshes,
        int expectedVertices,
        int expectedTriangles)
    {
        var banks = CarveBanks(paths, buildName, romName);

        long meshCount = 0;
        long vertices = 0;
        long triangles = 0;
        foreach (var (path, data) in banks)
        {
            foreach (var mesh in N64RenderBankFile.Parse(data))
            {
                meshCount++;
                vertices += mesh.Vertices.Count;
                triangles += mesh.Triangles.Count;
                foreach (var tri in mesh.Triangles)
                {
                    Assert.True(
                        tri.V0 >= 0 && tri.V0 < mesh.Vertices.Count &&
                        tri.V1 >= 0 && tri.V1 < mesh.Vertices.Count &&
                        tri.V2 >= 0 && tri.V2 < mesh.Vertices.Count,
                        $"{path}: triangle ({tri.V0},{tri.V1},{tri.V2}) outside pool of {mesh.Vertices.Count}");
                }
            }
        }

        Assert.Equal(expectedMeshes, meshCount);
        Assert.Equal(expectedVertices, vertices);
        Assert.Equal(expectedTriangles, triangles);
    }
}
