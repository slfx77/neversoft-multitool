using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using NeversoftMultitool.Tests.Helpers;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skin;

internal static class ThawPs2SkinFileTestHelper
{
    public static int CountUniqueTriangles(IEnumerable<Ps2Mesh> meshes)
    {
        var seen = new HashSet<(Vector3, Vector3, Vector3)>();
        var count = 0;
        foreach (var mesh in meshes)
            count += CountStripTriangles(mesh.Vertices, mesh.StartsOnOddOutputSlot, seen);
        return count;
    }

    public static int CountUniquePositions(ParsedPs2Scene scene)
    {
        return scene.MeshGroups
            .SelectMany(group => group.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Select(vertex => vertex.Position)
            .Distinct()
            .Count();
    }

    public static int CountUniquePositions(ParsedXbxScene scene)
    {
        return scene.Sectors
            .SelectMany(sector => sector.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Select(vertex => vertex.Position)
            .Distinct()
            .Count();
    }

    public static void AssertPs2MaterialPositionParity(
        TestPaths paths, string ps2BuildName, string pcBuildName, string stem)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = paths.FindSampleFile(ps2BuildName, $"{stem}.skin.ps2");
        var pcFile = paths.FindSampleFile(pcBuildName, $"{stem}.skin.wpc");
        Assert.SkipWhen(ps2File is null, $"PS2 file not found: {stem}");
        Assert.SkipWhen(pcFile is null, $"PC file not found: {stem}");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);
        var ps2ByMaterial = BuildPs2PositionMap(ps2Scene);
        var pcByMaterial = BuildPcPositionMap(pcScene);

        Assert.Equal(pcByMaterial.Keys.OrderBy(k => k), ps2ByMaterial.Keys.OrderBy(k => k));
        foreach (var (materialChecksum, pcPositions) in pcByMaterial)
            Assert.Equal(0, CountMissingPositions(pcPositions, ps2ByMaterial[materialChecksum]));
    }

    public static int CountStripTriangles(Ps2Mesh mesh)
    {
        return CountStripTriangles(mesh.Vertices, mesh.StartsOnOddOutputSlot);
    }

    public static int CountStripTriangles(
        Ps2Vertex[] verts,
        bool startsOnOddOutputSlot = false,
        HashSet<(Vector3, Vector3, Vector3)>? dedup = null)
    {
        var count = 0;
        var stripStart = 0;
        var parityBias = startsOnOddOutputSlot ? 1 : 0;

        for (var i = 0; i < verts.Length; i++)
        {
            if (verts[i].IsStripRestart)
            {
                continue;
            }

            if (i - stripStart < 2)
                continue;

            Ps2Vertex a;
            Ps2Vertex b;
            Ps2Vertex c;
            if (((i - stripStart + parityBias) & 1) == 0)
            {
                a = verts[i - 2];
                b = verts[i - 1];
                c = verts[i];
            }
            else
            {
                a = verts[i - 1];
                b = verts[i - 2];
                c = verts[i];
            }

            if (IsDegenerate(a, b, c))
                continue;

            if (dedup is not null)
            {
                var key = SortedTriangleKey(a.Position, b.Position, c.Position);
                if (!dedup.Add(key))
                    continue;
            }

            count++;
        }

        return count;
    }

    public static Ps2Vertex MakeVertex(float x, float y, bool isStripRestart = false)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitY,
            128, 128, 128, 128,
            0f, 0f,
            true,
            false,
            false,
            isStripRestart);
    }

    public static Dictionary<uint, HashSet<Vector3>> BuildPs2PositionMap(ParsedPs2Scene scene)
    {
        var map = new Dictionary<uint, HashSet<Vector3>>();
        foreach (var group in scene.MeshGroups)
        {
            foreach (var mesh in group.Meshes)
            {
                if (!map.TryGetValue(mesh.MaterialChecksum, out var positions))
                {
                    positions = [];
                    map[mesh.MaterialChecksum] = positions;
                }

                foreach (var vertex in mesh.Vertices)
                    positions.Add(vertex.Position);
            }
        }

        return map;
    }

    public static Dictionary<uint, HashSet<Vector3>> BuildPcPositionMap(ParsedXbxScene scene)
    {
        var map = new Dictionary<uint, HashSet<Vector3>>();
        foreach (var sector in scene.Sectors)
        {
            foreach (var mesh in sector.Meshes)
            {
                if (!map.TryGetValue(mesh.MaterialChecksum, out var positions))
                {
                    positions = [];
                    map[mesh.MaterialChecksum] = positions;
                }

                foreach (var vertex in mesh.Vertices)
                    positions.Add(vertex.Position);
            }
        }

        return map;
    }

    public static int CountMissingPositions(
        IReadOnlyCollection<Vector3> expected,
        IReadOnlySet<Vector3> actual)
    {
        return expected.Count(position => !actual.Contains(position));
    }

    private static (Vector3, Vector3, Vector3) SortedTriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);

        static int Compare(Vector3 x, Vector3 y)
        {
            var cmp = x.X.CompareTo(y.X);
            if (cmp != 0)
                return cmp;

            cmp = x.Y.CompareTo(y.Y);
            return cmp != 0 ? cmp : x.Z.CompareTo(y.Z);
        }
    }

    private static bool IsDegenerate(in Ps2Vertex a, in Ps2Vertex b, in Ps2Vertex c)
    {
        const float epsilon = 1e-8f;

        if (Vector3.DistanceSquared(a.Position, b.Position) <= epsilon ||
            Vector3.DistanceSquared(b.Position, c.Position) <= epsilon ||
            Vector3.DistanceSquared(a.Position, c.Position) <= epsilon)
        {
            return true;
        }

        var cross = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
        return cross.LengthSquared() <= epsilon;
    }
}