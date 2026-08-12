using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class NgcSceneFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    // ── IsNgcScene ──

    [Fact]
    public void IsNgcScene_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        Assert.True(NgcSceneFile.IsNgcScene(File.ReadAllBytes(file)));
    }

    [Fact]
    public void IsNgcScene_EmptyData_ReturnsFalse()
    {
        Assert.False(NgcSceneFile.IsNgcScene([]));
    }

    [Theory]
    [InlineData(48)]
    [InlineData(63)]
    public void IsNgcScene_SentinelWithoutCompleteHeader_IsRejected(int length)
    {
        var data = CreateSyntheticScene(length);

        Assert.False(NgcSceneFile.IsNgcScene(data));
        Assert.Throws<InvalidDataException>(() => NgcSceneFile.Parse(data));
    }

    [Fact]
    public void Parse_CompleteEmptyHeader_IsAccepted()
    {
        var data = CreateSyntheticScene(64);

        Assert.True(NgcSceneFile.IsNgcScene(data));
        var scene = NgcSceneFile.Parse(data);
        Assert.Empty(scene.Materials);
        Assert.Empty(scene.Sectors);
        Assert.Empty(scene.Links);
    }

    [Fact]
    public void IsNgcScene_XboxSceneData_ReturnsFalse()
    {
        // Xbox version triple (1,1,1) header, no 0xAAFFEEFF sentinel
        var data = new byte[64];
        BitConverter.GetBytes(1u).CopyTo(data, 0);
        BitConverter.GetBytes(1u).CopyTo(data, 4);
        BitConverter.GetBytes(1u).CopyTo(data, 8);
        Assert.False(NgcSceneFile.IsNgcScene(data));
    }

    private static byte[] CreateSyntheticScene(int length)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x2C), 0xAAFFEEFF);
        return data;
    }

    // ── Parse known files (values validated against PC/PS2 Rosetta pairs) ──

    [Fact]
    public void Parse_Pigeon_MatchesPcRosettaPair()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        var scene = NgcSceneFile.Parse(File.ReadAllBytes(file));

        var sector = Assert.Single(scene.Sectors);
        var mesh = Assert.Single(sector.Meshes);

        // 29 skin positions expand to 46 vertices at UV seams and yield 45 real
        // triangles — both exact matches with the PC pair (anl_pigeon.skin.wpc).
        Assert.Equal(46, mesh.Vertices.Length);
        Assert.Equal(29, mesh.Vertices.Select(v => v.Position).Distinct().Count());
        Assert.Equal(45, mesh.FaceIndices.Length / 3);
        Assert.True(mesh.IsPreTriangulated);

        // Wingtip at (±25.3125, 14.3125, 0.875): skin positions are s16/32
        Assert.Contains(mesh.Vertices, v =>
            Math.Abs(v.Position.X - -25.3125f) < 0.001f &&
            Math.Abs(v.Position.Y - 14.3125f) < 0.001f);

        // Bounding sphere from the object header is in model units
        Assert.Equal(25.3125f, sector.BsphereRadius, 3);

        // Skinned: all vertices carry bone weights that sum to 1
        Assert.All(mesh.Vertices, v =>
        {
            Assert.True(v.HasSkinData);
            var sum = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2;
            Assert.True(Math.Abs(sum - 1f) < 0.01f, $"weight sum {sum}");
        });
    }

    [Fact]
    public void Parse_Pigeon_MaterialReferencesTextureByIndex()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(file is null, "anl_pigeon.skin.ngc not found");

        var scene = NgcSceneFile.Parse(File.ReadAllBytes(file));

        var material = Assert.Single(scene.Materials);
        Assert.Equal(0x86402E3Du, material.Checksum);
        var pass = Assert.Single(material.Passes);
        // Texture INDEX 0 into the companion .tex.ngc, stored as index+1
        Assert.Equal(1u, pass.TextureChecksum);
    }

    // ── Batch parse ──

    [CorpusFact]
    public void BatchParse_AllSkinFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.skin.ngc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .skin.ngc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = NgcSceneFile.Parse(File.ReadAllBytes(f));
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} SKIN files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }

    [CorpusFact]
    public void BatchParse_AllMdlFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.mdl.ngc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No .mdl.ngc files found");

        var failures = new List<string>();
        var totalTris = 0;

        foreach (var f in files)
        {
            try
            {
                var scene = NgcSceneFile.Parse(File.ReadAllBytes(f));
                totalTris += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(f)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} MDL files failed:\n  " +
            string.Join("\n  ", failures.Take(20)));
        Assert.True(totalTris > 0, "Expected total triangles > 0");
    }
}
