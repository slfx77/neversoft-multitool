using System.Numerics;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;
using ParsedPs2Scene = NeversoftMultitool.Core.Formats.Ps2Scene.Ps2Scene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2SkinFileTests(TestPaths paths)
{
    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

    private string ThawPcSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2006-2-6, PC - Final)", "SKIN");

    // ── Detection ──

    [Fact]
    public void IsThawPs2Skin_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin([]));
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin(new byte[16]));
    }

    [Theory]
    [InlineData(new byte[] { 3, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                             0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63 })]  // (3,4,1) = THPS4
    [InlineData(new byte[] { 5, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                             0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63 })]  // (5,6,1) = THUG
    [InlineData(new byte[] { 6, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
                             0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63 })]  // (6,6,1) = THUG2
    public void IsThawPs2Skin_WithStandardPs2Scene_ReturnsFalse(byte[] data)
    {
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    [Fact]
    public void IsThawPs2Skin_WithThawFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "acc_backpack01.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    // ── Parsing ──

    [Theory]
    [InlineData("acc_backpack01.skin.ps2", 1, 168)]    // PC: 168 — exact match
    [InlineData("skater_hawk.skin.ps2", 1, 3460)]      // PC: 3463 (3 degenerate); unique non-degen: 3460 — exact
    [InlineData("skater_lasek.skin.ps2", 2, 3070)]     // PC: 3070 — exact replay parity
    [InlineData("body_f_torso.skin.ps2", 1, 318)]      // PC: 318 — exact match
    [InlineData("pro_vallely_head.skin.ps2", 1, 605)]  // PC-only mesh split remains divergent; entry-backed replay improved
    [InlineData("sec_jimbo_xen.skin.ps2", 1, 7088)]    // PC: 7094 (6 degenerate); unique non-degen: 7088 — exact
    public void Parse_ThawSkinFile_MatchesPcTriangleCounts(string filename, int minGroups, int expectedTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, filename);
        Assert.SkipWhen(!File.Exists(file), $"Test file not found: {filename}");

        var scene = ThawPs2SkinFile.Parse(file);

        Assert.True(scene.MeshGroups.SelectMany(g => g.Meshes).Count() >= minGroups);
        var totalVerts = scene.MeshGroups.SelectMany(g => g.Meshes).Sum(m => m.Vertices.Length);
        Assert.True(totalVerts > 0, "Scene should have vertices");

        // Target: match PC (.skin.wpc) triangle counts exactly
        // Use dedup set across all meshes, matching the glTF writer's behavior
        var triangles = CountUniqueTriangles(scene.MeshGroups.SelectMany(g => g.Meshes));
        Assert.Equal(expectedTriangles, triangles);
    }

    [Fact]
    public void Parse_SkaterLasek_MatchesPcUniquePositions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = Path.Combine(ThawSkinDir, "skater_lasek.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, "skater_lasek.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), "PS2 file not found");
        Assert.SkipWhen(!File.Exists(pcFile), "PC file not found");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);

        Assert.Equal(1652, CountUniquePositions(ps2Scene));
        Assert.Equal(CountUniquePositions(pcScene), CountUniquePositions(ps2Scene));
    }

    [Fact]
    public void Parse_SkaterLasek_MatchesPcMaterialPositionCoverage()
    {
        AssertPs2MaterialPositionParity("skater_lasek");
    }

    [Fact]
    public void Parse_SkaterHawk_DocumentsLegacySiblingMaterialSplitDivergence()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = Path.Combine(ThawSkinDir, "skater_hawk.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, "skater_hawk.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), "PS2 file not found");
        Assert.SkipWhen(!File.Exists(pcFile), "PC file not found");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);
        var ps2ByMaterial = BuildPs2PositionMap(ps2Scene);
        var pcByMaterial = BuildPcPositionMap(pcScene);
        var sharedMaterials = ps2ByMaterial.Keys.Intersect(pcByMaterial.Keys).OrderBy(k => k).ToArray();
        var pcOnlyMaterials = pcByMaterial.Keys.Except(ps2ByMaterial.Keys).OrderBy(k => k).ToArray();
        var ps2OnlyMaterials = ps2ByMaterial.Keys.Except(pcByMaterial.Keys).OrderBy(k => k).ToArray();

        foreach (var materialChecksum in sharedMaterials)
            Assert.Equal(0, CountMissingPositions(pcByMaterial[materialChecksum], ps2ByMaterial[materialChecksum]));

        Assert.Equal([0x18717436u, 0x18717444u, 0x4D6C149Eu, 0x9F1F8202u], pcOnlyMaterials);
        Assert.Equal([0x18717437u, 0x18717445u, 0x4D6C149Fu, 0x9F1F8203u], ps2OnlyMaterials);
    }

    [Fact]
    public void Parse_ProVallelyHead_DocumentsPcOnlyMaterialDivergence()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = Path.Combine(ThawSkinDir, "pro_vallely_head.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, "pro_vallely_head.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), "PS2 file not found");
        Assert.SkipWhen(!File.Exists(pcFile), "PC file not found");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);
        var ps2ByMaterial = BuildPs2PositionMap(ps2Scene);
        var pcByMaterial = BuildPcPositionMap(pcScene);
        var combinedPs2Positions = ps2ByMaterial.Values.SelectMany(set => set).ToHashSet();

        Assert.DoesNotContain(ps2Scene.Materials, material => material.Checksum == 0x02EA21B0);
        Assert.Contains(ps2Scene.Materials, material => material.Checksum == 0x488D5A5B);
        Assert.Contains(ps2Scene.Materials, material => material.Checksum == 0xCFF2FEB9);
        Assert.Equal(326, CountUniquePositions(ps2Scene));
        Assert.Equal(400, CountUniquePositions(pcScene));
        Assert.Equal(0, CountMissingPositions(pcByMaterial[0xCFF2FEB9], combinedPs2Positions));
        Assert.Equal(22, CountMissingPositions(pcByMaterial[0x488D5A5B], ps2ByMaterial[0x488D5A5B]));
        Assert.Equal(54, CountMissingPositions(pcByMaterial[0x02EA21B0], new HashSet<Vector3>()));
    }

    [Fact]
    public void Parse_SecJimbo_MatchesPcMaterialPositionCoverage()
    {
        AssertPs2MaterialPositionParity("sec_jimbo");
    }

    [Fact]
    public void Parse_SecJimboXen_MatchesPcMaterialPositionCoverage()
    {
        AssertPs2MaterialPositionParity("sec_jimbo_xen");
    }

    [Fact]
    public void Parse_ThawSkinFiles_PopulatesAlphaRefsFromDirectTestRegisters()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var jimboFile = Path.Combine(ThawSkinDir, "sec_jimbo_xen.skin.ps2");
        var hawkFile = Path.Combine(ThawSkinDir, "skater_hawk.skin.ps2");
        Assert.SkipWhen(!File.Exists(jimboFile), "Jimbo file not found");
        Assert.SkipWhen(!File.Exists(hawkFile), "Hawk file not found");

        var jimboByChecksum = ThawPs2SkinFile.Parse(jimboFile).Materials.ToDictionary(mat => mat.Checksum);
        var hawkByChecksum = ThawPs2SkinFile.Parse(hawkFile).Materials.ToDictionary(mat => mat.Checksum);

        Assert.Equal(1, jimboByChecksum[0x96820C03].AlphaRef);
        Assert.Equal(1, jimboByChecksum[0x8448A70A].AlphaRef);
        Assert.Equal(1, jimboByChecksum[0x4936422A].AlphaRef);
        Assert.Equal(10, jimboByChecksum[0x6C8A2B17].AlphaRef);
        Assert.Equal(10, jimboByChecksum[0x4B685E6C].AlphaRef);

        Assert.Equal(1, hawkByChecksum[0xC9B52576].AlphaRef);
    }

    [Fact]
    public void Parse_AccBackpack01_HasReasonablePositions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, "acc_backpack01.skin.ps2");
        Assert.SkipWhen(!File.Exists(file), "Test file not found");

        var scene = ThawPs2SkinFile.Parse(file);
        var verts = scene.MeshGroups.SelectMany(g => g.Meshes).SelectMany(m => m.Vertices).ToArray();

        // Positions should be in a reasonable range for a character accessory
        foreach (var v in verts)
        {
            Assert.True(Math.Abs(v.Position.X) < 200, $"X position out of range: {v.Position.X}");
            Assert.True(Math.Abs(v.Position.Y) < 200, $"Y position out of range: {v.Position.Y}");
            Assert.True(Math.Abs(v.Position.Z) < 200, $"Z position out of range: {v.Position.Z}");
        }

        // Normals should be unit-length
        foreach (var v in verts.Where(v => v.HasNormal))
        {
            var len = v.Normal.Length();
            Assert.True(len > 0.9f && len < 1.1f, $"Normal not unit length: {len}");
        }
    }

    [Fact]
    public void BatchParse_AllThawSkinFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!Directory.Exists(ThawSkinDir), "THAW SKIN directory not found");

        var files = Directory.GetFiles(ThawSkinDir, "*.skin.ps2");
        Assert.SkipWhen(files.Length == 0, "No .skin.ps2 files found");

        var failures = new List<string>();
        var totalTriangles = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (!ThawPs2SkinFile.IsThawPs2Skin(data))
                    continue;

                var scene = ThawPs2SkinFile.Parse(data);
                totalTriangles += CountUniqueTriangles(
                    scene.MeshGroups.SelectMany(g => g.Meshes));
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures)}");
        Assert.True(totalTriangles > 208_000,
            $"Expected >208K triangles, got {totalTriangles}");
    }

    [Fact]
    public void CountStripTriangles_RestartVertex_SkipsCurrentTriangleWithoutResettingStrip()
    {
        var verts = new[]
        {
            MakeVertex(0, 0),
            MakeVertex(1, 0),
            MakeVertex(0, 1),
            MakeVertex(2, 0, isStripRestart: true),
            MakeVertex(3, 0),
            MakeVertex(2, 1)
        };

        Assert.Equal(3, CountStripTriangles(verts));
    }

    [Fact]
    public void CountStripTriangles_DegenerateTriangles_AreSkipped()
    {
        var verts = new[]
        {
            MakeVertex(0, 0),
            MakeVertex(1, 0),
            MakeVertex(2, 0)
        };

        Assert.Equal(0, CountStripTriangles(verts));
    }

    private static int CountUniqueTriangles(IEnumerable<Ps2Mesh> meshes)
    {
        var seen = new HashSet<(Vector3, Vector3, Vector3)>();
        var count = 0;
        foreach (var mesh in meshes)
            count += CountStripTriangles(mesh.Vertices, mesh.StartsOnOddOutputSlot, seen);
        return count;
    }

    private static int CountUniquePositions(ParsedPs2Scene scene)
    {
        return scene.MeshGroups
            .SelectMany(group => group.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Select(vertex => vertex.Position)
            .Distinct()
            .Count();
    }

    private static int CountUniquePositions(ParsedXbxScene scene)
    {
        return scene.Sectors
            .SelectMany(sector => sector.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Select(vertex => vertex.Position)
            .Distinct()
            .Count();
    }

    private static Dictionary<uint, HashSet<Vector3>> BuildPs2PositionMap(ParsedPs2Scene scene)
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

    private static Dictionary<uint, HashSet<Vector3>> BuildPcPositionMap(ParsedXbxScene scene)
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

    private void AssertPs2MaterialPositionParity(string stem)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = Path.Combine(ThawSkinDir, $"{stem}.skin.ps2");
        var pcFile = Path.Combine(ThawPcSkinDir, $"{stem}.skin.wpc");
        Assert.SkipWhen(!File.Exists(ps2File), $"PS2 file not found: {stem}");
        Assert.SkipWhen(!File.Exists(pcFile), $"PC file not found: {stem}");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);
        var ps2ByMaterial = BuildPs2PositionMap(ps2Scene);
        var pcByMaterial = BuildPcPositionMap(pcScene);

        Assert.Equal(pcByMaterial.Keys.OrderBy(k => k), ps2ByMaterial.Keys.OrderBy(k => k));
        foreach (var (materialChecksum, pcPositions) in pcByMaterial)
            Assert.Equal(0, CountMissingPositions(pcPositions, ps2ByMaterial[materialChecksum]));
    }

    private static int CountMissingPositions(
        IReadOnlyCollection<Vector3> expected,
        IReadOnlySet<Vector3> actual)
    {
        return expected.Count(position => !actual.Contains(position));
    }

    private static int CountStripTriangles(Ps2Mesh mesh)
    {
        return CountStripTriangles(mesh.Vertices, mesh.StartsOnOddOutputSlot);
    }

    private static int CountStripTriangles(Ps2Vertex[] verts, bool startsOnOddOutputSlot = false,
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

            Ps2Vertex a, b, c;
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

    private static (Vector3, Vector3, Vector3) SortedTriangleKey(Vector3 a, Vector3 b, Vector3 c)
    {
        if (Compare(a, b) > 0) (a, b) = (b, a);
        if (Compare(b, c) > 0) (b, c) = (c, b);
        if (Compare(a, b) > 0) (a, b) = (b, a);
        return (a, b, c);

        static int Compare(Vector3 x, Vector3 y)
        {
            var cmp = x.X.CompareTo(y.X);
            if (cmp != 0) return cmp;
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

    private static Ps2Vertex MakeVertex(float x, float y, bool isStripRestart = false)
    {
        return new Ps2Vertex(
            new Vector3(x, y, 0),
            Vector3.UnitY,
            128, 128, 128, 128,
            0f, 0f,
            hasNormal: true,
            hasColor: false,
            hasUV: false,
            isStripRestart);
    }
}
