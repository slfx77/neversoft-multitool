using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Ps2Scene;

public sealed class ThawPs2SkinFileTests(TestPaths paths)
{
    private string ThawSkinDir =>
        Path.Combine(paths.SampleBuildsDir!, "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "SKIN");

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
    [InlineData("acc_backpack01.skin.ps2", 1, 157)]
    [InlineData("skater_hawk.skin.ps2", 1, 2491)]
    public void Parse_ThawSkinFile_ProducesExpectedTriangles(string filename, int minGroups, int expectedTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(ThawSkinDir, filename);
        Assert.SkipWhen(!File.Exists(file), $"Test file not found: {filename}");

        var scene = ThawPs2SkinFile.Parse(file);

        Assert.True(scene.MeshGroups.Count >= minGroups);
        var totalVerts = scene.MeshGroups.SelectMany(g => g.Meshes).Sum(m => m.Vertices.Length);
        Assert.True(totalVerts > 0, "Scene should have vertices");

        // Count triangles using the same strip logic as the glTF writer
        var triangles = scene.MeshGroups
            .SelectMany(g => g.Meshes)
            .Sum(m => CountStripTriangles(m.Vertices));
        Assert.Equal(expectedTriangles, triangles);
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
                totalTriangles += scene.MeshGroups
                    .SelectMany(g => g.Meshes)
                    .Sum(m => CountStripTriangles(m.Vertices));
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failures:\n{string.Join("\n", failures)}");
        Assert.True(totalTriangles > 100_000,
            $"Expected >100K triangles, got {totalTriangles}");
    }

    private static int CountStripTriangles(Ps2Vertex[] verts)
    {
        var count = 0;
        for (var i = 2; i < verts.Length; i++)
        {
            if (!verts[i].IsStripRestart)
                count++;
        }
        return count;
    }
}
