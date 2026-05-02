using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Tests.Helpers;
using static NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skin.ThawPs2SkinFileTestHelper;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skin;

public sealed class ThawPs2SkinFileTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";
    private const string ThawPcBuild = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";
    private const string Thug2Ps2Build = "Tony Hawk's Underground 2 (2004-8-22, PS2 - Final)";

    // ── Detection ──

    [Fact]
    public void IsThawPs2Skin_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin([]));
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin(new byte[16]));
    }

    [Theory]
    [InlineData(new byte[]
    {
        3, 0, 0, 0, 4, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63
    })] // (3,4,1) = THPS4
    [InlineData(new byte[]
    {
        5, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63
    })] // (5,6,1) = THUG
    [InlineData(new byte[]
    {
        6, 0, 0, 0, 6, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 128, 63
    })] // (6,6,1) = THUG2
    public void IsThawPs2Skin_WithStandardPs2Scene_ReturnsFalse(byte[] data)
    {
        Assert.False(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    [Fact]
    public void IsThawPs2Skin_WithThawFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawPs2Build, "acc_backpack01.skin.ps2");
        Assert.SkipWhen(file is null, "Test file not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawPs2SkinFile.IsThawPs2Skin(data));
    }

    // ── Parsing ──

    [Theory]
    [InlineData("acc_backpack01.skin.ps2", 1, 168)] // PC: 168 — exact match
    [InlineData("skater_hawk.skin.ps2", 1, 3460)] // PC: 3463 (3 degenerate); unique non-degen: 3460 — exact
    [InlineData("skater_lasek.skin.ps2", 2, 3070)] // PC: 3070 — exact replay parity
    [InlineData("body_f_torso.skin.ps2", 1, 318)] // PC: 318 — exact match
    [InlineData("pro_vallely_head.skin.ps2", 1,
        605)] // PC-only mesh split remains divergent; entry-backed replay improved
    [InlineData("sec_jimbo_xen.skin.ps2", 1, 7088)] // PC: 7094 (6 degenerate); unique non-degen: 7088 — exact
    public void Parse_ThawSkinFile_MatchesPcTriangleCounts(string filename, int minGroups, int expectedTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(ThawPs2Build, filename);
        Assert.SkipWhen(file is null, $"Test file not found: {filename}");

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
        var ps2File = paths.FindSampleFile(ThawPs2Build, "skater_lasek.skin.ps2");
        var pcFile = paths.FindSampleFile(ThawPcBuild, "skater_lasek.skin.wpc");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");
        Assert.SkipWhen(pcFile is null, "PC file not found");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);

        Assert.Equal(1652, CountUniquePositions(ps2Scene));
        Assert.Equal(CountUniquePositions(pcScene), CountUniquePositions(ps2Scene));
    }

    [Fact]
    public void Parse_SkaterLasek_MatchesPcMaterialPositionCoverage()
    {
        AssertPs2MaterialPositionParity(paths, ThawPs2Build, ThawPcBuild, "skater_lasek");
    }

    [Fact]
    public void DiscoverThawSkeleton_SkaterLasek_FindsThps6Human()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = paths.FindSampleFile(ThawPs2Build, "skater_lasek.skin.ps2");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");

        var skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(ps2File, "skater_lasek", true);

        Assert.NotNull(skeletonPath);
        Assert.EndsWith("thps6_human.ske.ps2", skeletonPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiscoverThawSkeleton_ProVallelyHead_FindsLegacyHeadSkeleton()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = paths.FindSampleFile(ThawPs2Build, "pro_vallely_head.skin.ps2");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");

        var skeletonPath = ThawSkeletonDiscovery.FindSkeletonPath(ps2File, "pro_vallely_head", true);

        Assert.NotNull(skeletonPath);
        Assert.EndsWith("vallely_head.ske.ps2", skeletonPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ApplyPcSkinning_SkaterLasek_TransfersSkinDataOntoPs2Vertices()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = paths.FindSampleFile(ThawPs2Build, "skater_lasek.skin.ps2");
        var pcFile = paths.FindSampleFile(ThawPcBuild, "skater_lasek.skin.wpc");
        var skeletonFile = paths.FindSampleFile(Thug2Ps2Build, "thps6_human.ske.ps2");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");
        Assert.SkipWhen(pcFile is null, "PC file not found");
        Assert.SkipWhen(skeletonFile is null, "THUG2 human skeleton not found");

        var ps2Scene = ThawPs2SkinFile.Parse(ps2File);
        var pcScene = ThawSceneFile.Parse(pcFile);
        var skeleton = Ps2SkeletonFile.Parse(skeletonFile);
        var transferred = ThawPs2SkinningTransfer.Apply(ps2Scene, pcScene, skeleton);
        var skinnedVertices = transferred.Scene.MeshGroups
            .SelectMany(group => group.Meshes)
            .SelectMany(mesh => mesh.Vertices)
            .Where(vertex => vertex.HasSkinData)
            .ToArray();

        Assert.NotEmpty(skinnedVertices);
        Assert.True(transferred.SkinnedVertexCount >= transferred.TotalVertexCount * 0.95f,
            $"Expected >=95% skinning coverage, got {transferred.SkinnedVertexCount}/{transferred.TotalVertexCount}");
        Assert.All(skinnedVertices.Take(256), vertex =>
        {
            var sum = vertex.BoneWeight0 + vertex.BoneWeight1 + vertex.BoneWeight2;
            Assert.InRange(sum, 0.999f, 1.001f);
            Assert.InRange(vertex.BoneIndex0, 0, skeleton.Bones.Length - 1);
        });
    }

    [Fact]
    public void Parse_SkaterHawk_DocumentsLegacySiblingMaterialSplitDivergence()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var ps2File = paths.FindSampleFile(ThawPs2Build, "skater_hawk.skin.ps2");
        var pcFile = paths.FindSampleFile(ThawPcBuild, "skater_hawk.skin.wpc");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");
        Assert.SkipWhen(pcFile is null, "PC file not found");

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
        var ps2File = paths.FindSampleFile(ThawPs2Build, "pro_vallely_head.skin.ps2");
        var pcFile = paths.FindSampleFile(ThawPcBuild, "pro_vallely_head.skin.wpc");
        Assert.SkipWhen(ps2File is null, "PS2 file not found");
        Assert.SkipWhen(pcFile is null, "PC file not found");

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
        AssertPs2MaterialPositionParity(paths, ThawPs2Build, ThawPcBuild, "sec_jimbo");
    }

    [Fact]
    public void Parse_SecJimboXen_MatchesPcMaterialPositionCoverage()
    {
        AssertPs2MaterialPositionParity(paths, ThawPs2Build, ThawPcBuild, "sec_jimbo_xen");
    }

    [Fact]
    public void Parse_ThawSkinFiles_PopulatesAlphaRefsFromDirectTestRegisters()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var jimboFile = paths.FindSampleFile(ThawPs2Build, "sec_jimbo_xen.skin.ps2");
        var hawkFile = paths.FindSampleFile(ThawPs2Build, "skater_hawk.skin.ps2");
        Assert.SkipWhen(jimboFile is null, "Jimbo file not found");
        Assert.SkipWhen(hawkFile is null, "Hawk file not found");

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
        var file = paths.FindSampleFile(ThawPs2Build, "acc_backpack01.skin.ps2");
        Assert.SkipWhen(file is null, "Test file not found");

        var scene = ThawPs2SkinFile.Parse(file);
        var verts = scene.MeshGroups.SelectMany(g => g.Meshes).SelectMany(m => m.Vertices).ToArray();

        // Positions should be in a reasonable range for a character accessory
        foreach (var position in verts.Select(static vertex => vertex.Position))
        {
            Assert.True(Math.Abs(position.X) < 200, $"X position out of range: {position.X}");
            Assert.True(Math.Abs(position.Y) < 200, $"Y position out of range: {position.Y}");
            Assert.True(Math.Abs(position.Z) < 200, $"Z position out of range: {position.Z}");
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

        var files = paths.FindSampleFiles(ThawPs2Build, "*.skin.ps2").ToArray();
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
            MakeVertex(2, 0, true),
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
}
