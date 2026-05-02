using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Geom;

public sealed class Ps2GeomFileTests(TestPaths paths)
{
    private string? GetThawPakDir()
    {
        if (!paths.HasSampleBuilds) return null;
        var dir = Path.Combine(paths.SampleBuildsDir!,
            "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)", "PAK");
        return Directory.Exists(dir) ? dir : null;
    }

    [Fact]
    public void ParsePakMdl_ExtractedHollywoodZoneModel_ParsesSubstantialGeometry()
    {
        var pakDir = GetThawPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "z_ho.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "z_ho.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_ZHo_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);

            var extractedDir = Path.Combine(tempDir, "z_ho.pak");
            var mdlPath = Directory.GetFiles(extractedDir, "*.mdl", SearchOption.TopDirectoryOnly)
                .Single(path => Path.GetFileName(path).Equals("003B9540.mdl", StringComparison.OrdinalIgnoreCase));

            var data = File.ReadAllBytes(mdlPath);
            Assert.True(Ps2GeomFile.IsPakMdl(data));

            var scene = Ps2GeomFile.ParsePakMdl(data);
            var totalVertices = scene.Leaves.Sum(leaf => leaf.Vertices.Length);
            var originCenteredGiantHelpers = scene.Leaves.Count(leaf =>
            {
                var min = new Vector3(float.MaxValue);
                var max = new Vector3(float.MinValue);
                foreach (var position in leaf.Vertices.Select(static vertex => vertex.Position))
                {
                    min = Vector3.Min(min, position);
                    max = Vector3.Max(max, position);
                }

                var size = max - min;
                var center = (min + max) * 0.5f;
                return leaf.Vertices.Length <= 8
                       && leaf.Vertices.All(static vertex => !vertex.HasNormal)
                       && Math.Max(size.X, Math.Max(size.Y, size.Z)) > 20_000f
                       && Math.Abs(center.X) <= 10f
                       && Math.Abs(center.Y) <= 10f
                       && Math.Abs(center.Z) <= 10f;
            });

            Assert.True(scene.Leaves.Count > 100, $"Expected >100 mesh leaves, found {scene.Leaves.Count}");
            Assert.True(totalVertices > 20_000, $"Expected >20K vertices, found {totalVertices}");
            Assert.True(originCenteredGiantHelpers == 0,
                $"Expected no origin-centered helper leaves, found {originCenteredGiantHelpers}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ParsePakMdl_ExtractedHollywoodZoneModel_PreservesMipRegisters()
    {
        var pakDir = GetThawPakDir();
        Assert.SkipWhen(pakDir == null, "THAW PAK files not available");

        var pakPath = Path.Combine(pakDir!, "z_ho.pak.ps2");
        Assert.SkipWhen(!File.Exists(pakPath), "z_ho.pak.ps2 not found");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_ZHoMip_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(tempDir);
            PakArchive.ExtractFiles(pakPath, tempDir, token: TestContext.Current.CancellationToken);

            var extractedDir = Path.Combine(tempDir, "z_ho.pak");
            var mdlPath = Directory.GetFiles(extractedDir, "*.mdl", SearchOption.TopDirectoryOnly)
                .Single(path => Path.GetFileName(path).Equals("003B9540.mdl", StringComparison.OrdinalIgnoreCase));

            var scene = Ps2GeomFile.ParsePakMdl(File.ReadAllBytes(mdlPath));

            Assert.Contains(scene.Leaves, static leaf => leaf.DmaTex1 != 0);
            Assert.Contains(scene.Leaves, static leaf => leaf.DmaMipTbp1 != 0);
            Assert.Contains(scene.Leaves, static leaf => ((leaf.DmaTex1 >> 2) & 0x7) != 0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
