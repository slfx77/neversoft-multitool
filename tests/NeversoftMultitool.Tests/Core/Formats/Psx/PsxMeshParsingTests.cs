using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public sealed class PsxMeshParsingTests(TestPaths paths)
{
    // ── Parse tests (Xbox PSX files — version 0x04) ───────────────────

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void Parse_Xbox_ReturnsValidMeshData(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var result = PsxMeshFile.Parse(inputFile);

        // These files should have mesh data (they're level/model files, not texture-only)
        Assert.SkipWhen(result == null, $"{filename} has no mesh data (texture-only)");

        Assert.True(result.Version is 0x03 or 0x04 or 0x06,
            $"Unexpected version: 0x{result.Version:X}");
        Assert.NotEmpty(result.Objects);
        Assert.NotEmpty(result.Meshes);
        Assert.Equal(result.Meshes.Count, result.MeshNameHashes.Length);
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    [InlineData("items.psx")]
    public void Parse_Ps1_ReturnsValidMeshData(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxPs1Dir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var result = PsxMeshFile.Parse(inputFile);

        Assert.SkipWhen(result == null, $"{filename} has no mesh data (texture-only)");

        Assert.True(result.Version is 0x03 or 0x04 or 0x06,
            $"Unexpected version: 0x{result.Version:X}");
        Assert.NotEmpty(result.Objects);
        Assert.NotEmpty(result.Meshes);
    }

    // ── Mesh content validation ───────────────────────────────────────

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void Parse_Xbox_MeshesHaveValidGeometry(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var result = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(result == null, $"{filename} has no mesh data");

        foreach (var mesh in result.Meshes)
        {
            // Each mesh should have at least one vertex and one normal
            Assert.NotEmpty(mesh.Vertices);
            Assert.NotEmpty(mesh.Normals);

            // Validate vertex indices in faces don't exceed vertex count
            foreach (var face in mesh.Faces)
            {
                Assert.True(face.Index0 < (uint)mesh.Vertices.Count,
                    $"Face vertex index 0 ({face.Index0}) >= vertex count ({mesh.Vertices.Count})");
                Assert.True(face.Index1 < (uint)mesh.Vertices.Count,
                    $"Face vertex index 1 ({face.Index1}) >= vertex count ({mesh.Vertices.Count})");
                Assert.True(face.Index2 < (uint)mesh.Vertices.Count,
                    $"Face vertex index 2 ({face.Index2}) >= vertex count ({mesh.Vertices.Count})");
                if (face.IsQuad)
                    Assert.True(face.Index3 < (uint)mesh.Vertices.Count,
                        $"Face vertex index 3 ({face.Index3}) >= vertex count ({mesh.Vertices.Count})");

                Assert.True(face.NormalIndex < (uint)mesh.Normals.Count,
                    $"Face normal index ({face.NormalIndex}) >= normal count ({mesh.Normals.Count})");
            }
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void Parse_Xbox_ObjectMeshIndicesAreValid(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var result = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(result == null, $"{filename} has no mesh data");

        Assert.All(result.Objects, obj =>
            Assert.True(obj.MeshIndex < result.Meshes.Count,
                $"Object mesh index ({obj.MeshIndex}) >= mesh count ({result.Meshes.Count})"));
    }

    // ── Texture hash consistency ──────────────────────────────────────

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void Parse_Xbox_TextureHashesMatchPsxLibrary(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var meshFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(meshFile == null, $"{filename} has no mesh data");

        var allHashes = PsxHashEnumerator.EnumerateAllHashes(inputFile);
        Assert.NotNull(allHashes);

        // Texture hashes from mesh parser should match those from PsxLibrary
        var meshTexHashes = meshFile.TextureHashes.Where(h => h != 0).ToHashSet();
        var libTexHashes = allHashes.TextureNameHashes.Where(h => h != 0).ToHashSet();

        Assert.True(meshTexHashes.SetEquals(libTexHashes),
            $"Texture hashes differ: mesh parser has {meshTexHashes.Count}, PsxLibrary has {libTexHashes.Count}");
    }

    // ── Null/invalid file handling ────────────────────────────────────

    [Fact]
    public void Parse_InvalidFile_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_MeshInvalid_" + Guid.NewGuid().ToString("N")[..8]);
        var tempFile = Path.Combine(tempDir, "invalid.psx");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00]);

            var result = PsxMeshFile.Parse(tempFile);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Parse_WrongMagic_ReturnsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_MeshMagic_" + Guid.NewGuid().ToString("N")[..8]);
        var tempFile = Path.Combine(tempDir, "bad_magic.psx");
        try
        {
            Directory.CreateDirectory(tempDir);
            // Valid version (0x06) but wrong magic (0x0099 instead of 0x0002)
            File.WriteAllBytes(tempFile, [0x06, 0x00, 0x99, 0x00]);

            var result = PsxMeshFile.Parse(tempFile);
            Assert.Null(result);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ── Mesh name hash resolution ─────────────────────────────────────

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void Parse_Xbox_MeshNameHashesMatchPsxLibrary(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var meshFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(meshFile == null, $"{filename} has no mesh data");

        var allHashes = PsxHashEnumerator.EnumerateAllHashes(inputFile);
        Assert.NotNull(allHashes);

        // Mesh name hashes should match exactly (same count, same order)
        Assert.Equal(allHashes.MeshNameHashes.Length, meshFile.MeshNameHashes.Length);
        for (var i = 0; i < meshFile.MeshNameHashes.Length; i++)
        {
            Assert.Equal(allHashes.MeshNameHashes[i], meshFile.MeshNameHashes[i]);
        }
    }

    // ── Character model vertex type validation ────────────────────────

    [Fact]
    public void Parse_Xbox_Hawk2_VertexTypesAreValidBitmasks()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, "hawk2.PSX");
        Assert.SkipWhen(!File.Exists(inputFile), "hawk2.PSX not found");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, "hawk2.PSX has no mesh data");

        // Collect vertex type distribution across all meshes
        var typeCounts = psxFile.Meshes
            .SelectMany(m => m.Vertices)
            .GroupBy(v => v.Type)
            .ToDictionary(g => g.Key, g => g.Count());

        // Must have type-0 (normal) vertices
        Assert.True(typeCounts.ContainsKey(0), "Expected type-0 (normal) vertices");

        // Character models should have stitch vertices (type bit 0 or bit 1)
        var hasStitchSource = typeCounts.Keys.Any(t => (t & 0x01) != 0);
        var hasStitched = typeCounts.Keys.Any(t => (t & 0x02) != 0);
        Assert.True(hasStitchSource || hasStitched,
            "Character model should have stitch vertices (type bit 0 or bit 1)");

        // Verify all types are valid bitmask combinations (bits 0, 1, 4 only)
        foreach (var type in typeCounts.Keys)
        {
            Assert.True((type & ~0x13) == 0,
                $"Unexpected vertex type 0x{type:X4} — only bits 0, 1, 4 should be set");
        }
    }

    [Fact]
    public void Parse_Xbox_Hawk2_StitchedVerticesHaveReasonablePositions()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, "hawk2.PSX");
        Assert.SkipWhen(!File.Exists(inputFile), "hawk2.PSX not found");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, "hawk2.PSX has no mesh data");

        var stitchedCount = 0;
        var fallbackCount = 0;

        foreach (var mesh in psxFile.Meshes)
        {
            foreach (var v in mesh.Vertices)
            {
                if ((v.Type & 0x02) == 0) continue; // only stitched
                stitchedCount++;

                // Detect failed attachment lookups: if the vertex fell through to the
                // fallback path, Y = rawY / scaleDivisor. For a valid stitch attachment
                // index (small number 0-50), this produces Y ≈ 0.0-1.4 when scaleDivisor=36.
                // A successfully resolved stitch vertex will almost never have Y == rawY/36
                // exactly, so we check for this signature.
                var fallbackY = v.RawY / psxFile.ScaleDivisor;
                if (Math.Abs(v.Y - fallbackY) < 0.001f)
                {
                    var fallbackX = v.RawX / psxFile.ScaleDivisor;
                    var fallbackZ = v.RawZ / psxFile.ScaleDivisor;
                    if (Math.Abs(v.X - fallbackX) < 0.001f && Math.Abs(v.Z - fallbackZ) < 0.001f)
                        fallbackCount++;
                }

                // All stitched vertices should have finite positions (not NaN/Infinity)
                Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z),
                    $"Stitched vertex has non-finite position: ({v.X},{v.Y},{v.Z})");
            }
        }

        Assert.True(stitchedCount > 0, "Character model should have stitched vertices");
        // All stitched vertices should resolve from the attachment dictionary, not fall through
        Assert.True(fallbackCount == 0,
            $"{fallbackCount}/{stitchedCount} stitched vertices fell through to fallback " +
            "(attachment lookup failed — indices may not match stitch source ordering)");
    }
}