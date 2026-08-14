using NeversoftMultitool.Core.Formats.Collision;
using System.Buffers.Binary;

namespace NeversoftMultitool.Tests.Core.Formats.Collision;

public sealed class ColFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    // ── Format Detection ──

    [CorpusFact]
    public void IsColFile_ValidColFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_EmptyData_ReturnsFalse()
    {
        Assert.False(ColFile.IsColFile([]));
    }

    [Fact]
    public void IsColFile_TooSmall_ReturnsFalse()
    {
        Assert.False(ColFile.IsColFile(new byte[16]));
    }

    [Fact]
    public void IsColFile_WrongVersion_ReturnsFalse()
    {
        var data = new byte[32];
        BitConverter.GetBytes(99).CopyTo(data, 0); // version 99
        Assert.False(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_Version9_ReturnsTrue()
    {
        var data = new byte[32];
        BitConverter.GetBytes(9).CopyTo(data, 0);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void IsColFile_Version10_ReturnsTrue()
    {
        var data = new byte[32];
        BitConverter.GetBytes(10).CopyTo(data, 0);
        Assert.True(ColFile.IsColFile(data));
    }

    [Fact]
    public void Parse_DeclaredFloatVertexPastEndOfFile_Throws()
    {
        var data = CreateOneObjectFile(totalVertices: 1, totalLargeVertices: 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("vertex data is truncated", error.Message);
    }

    [Fact]
    public void Parse_DeclaredSmallFacePastEndOfFile_Throws()
    {
        var data = CreateOneObjectFile(totalVertices: 3, totalLargeVertices: 3, length: 136);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16, 4), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(38, 2), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(40, 2), 1);
        data[42] = 1;

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("face data is truncated", error.Message);
    }

    [Fact]
    public void Parse_EmptyHeaderWithNegativeTotalVertexCount_Throws()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), -1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains("total vertex count is negative: -1", error.Message);
    }

    [Theory]
    [InlineData(12, "total large-face")]
    [InlineData(16, "total small-face")]
    [InlineData(20, "total large-vertex")]
    [InlineData(24, "total small-vertex")]
    public void Parse_NegativeAggregateCount_Throws(int fieldOffset, string fieldName)
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(fieldOffset), -1);

        var error = Assert.Throws<InvalidDataException>(() => ColFile.Parse(data));

        Assert.Contains($"{fieldName} count is negative: -1", error.Message);
    }

    [Fact]
    public void Parse_EmptyHeaderWithZeroAggregateCounts_IsAccepted()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteInt32LittleEndian(data, 10);

        var scene = ColFile.Parse(data);

        Assert.Equal(10, scene.Version);
        Assert.Empty(scene.Objects);
    }

    private static byte[] CreateOneObjectFile(
        int totalVertices,
        int totalLargeVertices,
        int length = 96)
    {
        var data = new byte[length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0, 4), 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8, 4), totalVertices);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(20, 4), totalLargeVertices);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32, 4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(88, 4), -1);
        return data;
    }

    // ── Parsing Known Files ──

    [CorpusFact]
    public void Parse_Arrow_HasExpectedStructure()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        Assert.Equal(10, scene.Version);
        Assert.Single(scene.Objects);
        Assert.Equal(30, scene.TotalVertices);
        Assert.Equal(26, scene.TotalTriangles);
    }

    [CorpusTheory]
    [InlineData("Arrow.col.xbx")]
    [InlineData("Anl_Cat.col.xbx")]
    public void Parse_KnownFile_HasObjectsAndFaces(string filename)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, filename);
        Assert.SkipWhen(file is null, $"{filename} not found");

        var scene = ColFile.Parse(file);
        Assert.True(scene.Objects.Length > 0);
        Assert.True(scene.TotalTriangles > 0);
    }

    // ── Vertex Validation ──

    [CorpusFact]
    public void Parse_Arrow_VerticesAreFinite()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        foreach (var obj in scene.Objects)
        {
            foreach (var v in obj.Vertices)
            {
                Assert.True(float.IsFinite(v.X), $"Vertex X should be finite, got {v.X}");
                Assert.True(float.IsFinite(v.Y), $"Vertex Y should be finite, got {v.Y}");
                Assert.True(float.IsFinite(v.Z), $"Vertex Z should be finite, got {v.Z}");
            }
        }
    }

    [CorpusFact]
    public void Parse_Arrow_FaceIndicesInRange()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        foreach (var obj in scene.Objects)
        {
            foreach (var face in obj.Faces)
            {
                Assert.True(face.V0 >= 0 && face.V0 < obj.Vertices.Length,
                    $"V0 index {face.V0} out of range [0, {obj.Vertices.Length})");
                Assert.True(face.V1 >= 0 && face.V1 < obj.Vertices.Length,
                    $"V1 index {face.V1} out of range [0, {obj.Vertices.Length})");
                Assert.True(face.V2 >= 0 && face.V2 < obj.Vertices.Length,
                    $"V2 index {face.V2} out of range [0, {obj.Vertices.Length})");
            }
        }
    }

    // ── Batch Parsing ──

    [CorpusFact]
    public void Parse_AllColFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.col.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No COL files found");

        var failures = new List<string>();
        var totalFiles = 0;
        var totalTriangles = 0;

        foreach (var file in files)
        {
            totalFiles++;
            try
            {
                var data = File.ReadAllBytes(file);
                if (!ColFile.IsColFile(data)) continue;
                var scene = ColFile.Parse(data);
                Assert.NotNull(scene);
                totalTriangles += scene.TotalTriangles;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{totalFiles} files failed:\n" +
            string.Join("\n", failures.Take(20)));

        // Sanity check: we expect ~957 files and >1M triangles
        Assert.True(totalFiles > 900, $"Expected >900 COL files, found {totalFiles}");
        Assert.True(totalTriangles > 1_000_000, $"Expected >1M triangles, found {totalTriangles}");
    }

    // ── glTF Output ──

    [CorpusFact]
    public void Write_Arrow_ProducesValidGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "Arrow.col.xbx");
        Assert.SkipWhen(file is null, "Arrow.col.xbx not found");

        var scene = ColFile.Parse(file);
        var outputDir = Path.Combine(Path.GetTempPath(), "col_test_" + Guid.NewGuid().ToString("N")[..8]);
        var outputFile = Path.Combine(outputDir, "Arrow.glb");

        try
        {
            var triangles = ColGltfWriter.Write(scene, outputFile);
            Assert.Equal(26, triangles);
            Assert.True(File.Exists(outputFile), "GLB file should exist");
            Assert.True(new FileInfo(outputFile).Length > 100, "GLB should not be empty");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    [Fact]
    public void Write_EmptyScene_ProducesNoFile()
    {
        var scene = new ColScene
        {
            Version = 10,
            Objects = []
        };

        var outputDir = Path.Combine(Path.GetTempPath(), "col_test_" + Guid.NewGuid().ToString("N")[..8]);
        var outputFile = Path.Combine(outputDir, "empty.glb");

        try
        {
            var triangles = ColGltfWriter.Write(scene, outputFile);
            Assert.Equal(0, triangles);
            Assert.False(File.Exists(outputFile), "Empty scene should not produce a file");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    // ── Batch glTF Output ──

    [CorpusFact]
    public void Write_AllColFiles_ZeroGlbFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.col.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No COL files found");

        var outputDir = Path.Combine(Path.GetTempPath(), "col_batch_" + Guid.NewGuid().ToString("N")[..8]);
        var failures = new List<string>();
        var converted = 0;

        try
        {
            foreach (var file in files)
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    if (!ColFile.IsColFile(data)) continue;

                    var scene = ColFile.Parse(data);
                    var stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(file));
                    var outputFile = Path.Combine(outputDir, stem + ".glb");
                    ColGltfWriter.Write(scene, outputFile);
                    converted++;
                }
                catch (Exception ex)
                {
                    failures.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }

            Assert.True(failures.Count == 0,
                $"{failures.Count}/{files.Length} files failed glTF conversion:\n" +
                string.Join("\n", failures.Take(20)));
            Assert.True(converted > 900, $"Expected >900 conversions, got {converted}");
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }
}
