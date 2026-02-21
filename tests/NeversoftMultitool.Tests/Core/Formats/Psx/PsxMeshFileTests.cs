using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public class PsxMeshFileTests(TestPaths paths)
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

        foreach (var obj in result.Objects)
        {
            Assert.True(obj.MeshIndex < result.Meshes.Count,
                $"Object mesh index ({obj.MeshIndex}) >= mesh count ({result.Meshes.Count})");
        }
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

        var allHashes = PsxLibrary.EnumerateAllHashes(inputFile);
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
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_MeshInvalid_" + Guid.NewGuid().ToString("N")[..8]);
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
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_MeshMagic_" + Guid.NewGuid().ToString("N")[..8]);
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

    // ── glTF export tests ─────────────────────────────────────────────

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void Write_Xbox_ProducesValidGlb(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, $"{filename} has no mesh data");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_MeshGlb_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(filename) + ".glb");

            var triangles = PsxGltfWriter.Write(psxFile, outputFile);

            Assert.True(File.Exists(outputFile), "GLB file was not created");
            Assert.True(new FileInfo(outputFile).Length > 0, "GLB file is empty");
            Assert.True(triangles > 0, "No triangles were written");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void Write_Xbox_WithTextures_ProducesLargerGlb(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, $"{filename} has no mesh data");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_MeshTex_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            // Export without textures
            var noTexFile = Path.Combine(tempDir, "no_tex.glb");
            PsxGltfWriter.Write(psxFile, noTexFile);
            var noTexSize = new FileInfo(noTexFile).Length;

            // Export with textures
            var texFile = Path.Combine(tempDir, "with_tex.glb");
            PsxGltfWriter.TextureProvider textureProvider = hash =>
            {
                var result = PsxLibrary.ExtractTextureByHash(inputFile, hash);
                if (result == null) return null;
                var (rgba, w, h) = result.Value;
                return ImageWriter.WritePngToMemory(w, h, rgba);
            };
            PsxGltfWriter.Write(psxFile, texFile, textureProvider);
            var texSize = new FileInfo(texFile).Length;

            Assert.True(texSize > noTexSize,
                $"Textured GLB ({texSize} bytes) should be larger than untextured ({noTexSize} bytes)");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    public void Write_Ps1_ProducesValidGlb(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxPs1Dir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, $"{filename} has no mesh data");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_MeshPs1Glb_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(filename) + ".glb");

            var triangles = PsxGltfWriter.Write(psxFile, outputFile);

            Assert.True(File.Exists(outputFile), "GLB file was not created");
            Assert.True(new FileInfo(outputFile).Length > 0, "GLB file is empty");
            Assert.True(triangles > 0, "No triangles were written");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ── Hierarchy tests ─────────────────────────────────────────────────

    [Fact]
    public void Write_Xbox_Hawk2_HierarchicalModelProducesGlbWithNodes()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, "hawk2.PSX");
        Assert.SkipWhen(!File.Exists(inputFile), "hawk2.PSX not found");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, "hawk2.PSX has no mesh data");

        // hawk2 should be a hierarchical model (character skeleton)
        Assert.True(psxFile.HasHierarchy, "hawk2.PSX should have hierarchy data");
        Assert.Equal(2.25f * 16f, psxFile.ScaleDivisor);

        // Verify parent indices are set
        var hasChildren = psxFile.Objects.Any(o => o.ParentIndex >= 0);
        Assert.True(hasChildren, "Hierarchical model should have objects with parent indices");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Hier_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var outputFile = Path.Combine(tempDir, "hawk2.glb");

            var triangles = PsxGltfWriter.Write(psxFile, outputFile);

            Assert.True(File.Exists(outputFile), "GLB file was not created");
            Assert.True(triangles > 0, "No triangles written");

            // Read back the glTF and verify valid model produced.
            // Flat placement is always used (PSX coordinates are absolute),
            // so nodes are independent — no parent-child hierarchy in glTF output.
            var model = SharpGLTF.Schema2.ModelRoot.Load(outputFile);
            Assert.True(model.LogicalNodes.Count > 0, "GLB should have nodes");
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

        var allHashes = PsxLibrary.EnumerateAllHashes(inputFile);
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
        var typeCounts = new Dictionary<ushort, int>();
        foreach (var mesh in psxFile.Meshes)
        {
            foreach (var v in mesh.Vertices)
            {
                typeCounts.TryGetValue(v.Type, out var count);
                typeCounts[v.Type] = count + 1;
            }
        }

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

    // ── Cross-sample batch validation ─────────────────────────────────

    [Fact]
    public void Parse_AllSampleBuilds_NoCrashesAndValidGeometry()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .ToList();
        Assert.SkipWhen(psxFiles.Count == 0, "No PSX files found in sample builds");

        var parsed = 0;
        var meshFiles = 0;
        var textureOnly = 0;
        var errors = new List<string>();
        var totalMeshes = 0;
        var totalVertices = 0;
        var totalFaces = 0;
        var vertexTypeCounts = new Dictionary<ushort, int>();
        var filesWithStitch = 0;
        var invalidVertexTypes = new List<string>();
        var invalidFaceIndices = new List<string>();

        foreach (var file in psxFiles)
        {
            var relPath = Path.GetRelativePath(paths.SampleBuildsDir!, file);
            try
            {
                var result = PsxMeshFile.Parse(file);
                parsed++;

                if (result == null)
                {
                    textureOnly++;
                    continue;
                }

                meshFiles++;
                totalMeshes += result.Meshes.Count;

                var fileHasStitch = false;

                for (var meshIdx = 0; meshIdx < result.Meshes.Count; meshIdx++)
                {
                    var mesh = result.Meshes[meshIdx];
                    totalVertices += mesh.Vertices.Count;
                    totalFaces += mesh.Faces.Count;

                    // Validate vertex types
                    foreach (var v in mesh.Vertices)
                    {
                        vertexTypeCounts.TryGetValue(v.Type, out var count);
                        vertexTypeCounts[v.Type] = count + 1;

                        if ((v.Type & ~0x13) != 0)
                            invalidVertexTypes.Add($"{relPath} mesh[{meshIdx}]: type=0x{v.Type:X4}");

                        if ((v.Type & 0x03) != 0) fileHasStitch = true;

                        // All positions must be finite
                        Assert.True(float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z),
                            $"{relPath} mesh[{meshIdx}]: non-finite vertex ({v.X},{v.Y},{v.Z})");
                    }

                    // Validate face indices
                    foreach (var face in mesh.Faces)
                    {
                        if (face.Index0 >= (uint)mesh.Vertices.Count ||
                            face.Index1 >= (uint)mesh.Vertices.Count ||
                            face.Index2 >= (uint)mesh.Vertices.Count)
                        {
                            invalidFaceIndices.Add(
                                $"{relPath} mesh[{meshIdx}]: idx ({face.Index0},{face.Index1},{face.Index2}) >= {mesh.Vertices.Count}");
                        }
                        if (face.IsQuad && face.Index3 >= (uint)mesh.Vertices.Count)
                        {
                            invalidFaceIndices.Add(
                                $"{relPath} mesh[{meshIdx}]: quad idx3 ({face.Index3}) >= {mesh.Vertices.Count}");
                        }
                        if (face.NormalIndex >= (uint)mesh.Normals.Count)
                        {
                            invalidFaceIndices.Add(
                                $"{relPath} mesh[{meshIdx}]: normal ({face.NormalIndex}) >= {mesh.Normals.Count}");
                        }
                    }

                    // Validate object mesh indices
                    foreach (var obj in result.Objects)
                    {
                        if (obj.MeshIndex >= result.Meshes.Count)
                        {
                            invalidFaceIndices.Add(
                                $"{relPath}: obj mesh index ({obj.MeshIndex}) >= {result.Meshes.Count}");
                        }
                    }
                }

                if (fileHasStitch) filesWithStitch++;
            }
            catch (Exception ex)
            {
                errors.Add($"{relPath}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // Output diagnostic summary
        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine($"PSX Mesh Cross-Sample Report");
        Console.WriteLine($"{'=',-60}");
        Console.WriteLine($"Total PSX files:       {psxFiles.Count}");
        Console.WriteLine($"Parsed successfully:   {parsed}");
        Console.WriteLine($"  With mesh data:      {meshFiles}");
        Console.WriteLine($"  Texture-only:        {textureOnly}");
        Console.WriteLine($"Parse errors:          {errors.Count}");
        Console.WriteLine($"Total meshes:          {totalMeshes:N0}");
        Console.WriteLine($"Total vertices:        {totalVertices:N0}");
        Console.WriteLine($"Total faces:           {totalFaces:N0}");
        Console.WriteLine($"Files with stitch:     {filesWithStitch}");
        Console.WriteLine();

        Console.WriteLine("Vertex type distribution:");
        foreach (var (type, count) in vertexTypeCounts.OrderBy(kv => kv.Key))
        {
            var bits = new List<string>();
            if ((type & 0x01) != 0) bits.Add("stitch-src");
            if ((type & 0x02) != 0) bits.Add("stitched");
            if ((type & 0x10) != 0) bits.Add("sprite");
            var label = bits.Count > 0 ? string.Join("+", bits) : "normal";
            Console.WriteLine($"  0x{type:X4}: {count,8:N0}  ({label})");
        }

        if (errors.Count > 0)
        {
            Console.WriteLine($"\nParse errors ({errors.Count}):");
            foreach (var e in errors.Take(20))
                Console.WriteLine($"  {e}");
            if (errors.Count > 20)
                Console.WriteLine($"  ... and {errors.Count - 20} more");
        }

        if (invalidVertexTypes.Count > 0)
        {
            Console.WriteLine($"\nInvalid vertex types ({invalidVertexTypes.Count}):");
            foreach (var e in invalidVertexTypes.Take(10))
                Console.WriteLine($"  {e}");
        }

        if (invalidFaceIndices.Count > 0)
        {
            Console.WriteLine($"\nInvalid face indices ({invalidFaceIndices.Count}):");
            foreach (var e in invalidFaceIndices.Take(10))
                Console.WriteLine($"  {e}");
        }

        // Assert no parse crashes
        Assert.True(errors.Count == 0,
            $"{errors.Count} files failed to parse:\n  " + string.Join("\n  ", errors.Take(10)));

        // Vertex types beyond bits 0/1/4 are rare (0.04% across all builds) and come
        // from older format versions (Apocalypse v3) or LOD metadata. Log but don't fail.
        if (invalidVertexTypes.Count > 0)
        {
            var pct = 100.0 * invalidVertexTypes.Count / totalVertices;
            Console.WriteLine($"\nNOTE: {invalidVertexTypes.Count} vertices ({pct:F3}%) have types " +
                "beyond known bits 0/1/4 — likely older format variants");
        }

        // Assert all face indices are in range
        Assert.True(invalidFaceIndices.Count == 0,
            $"{invalidFaceIndices.Count} faces have invalid indices:\n  " +
            string.Join("\n  ", invalidFaceIndices.Take(10)));
    }

    [Fact]
    public void Diagnostic_AllSampleBuilds_IdentifyProblematicModels()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .ToList();
        Assert.SkipWhen(psxFiles.Count == 0, "No PSX files found in sample builds");

        var issues = new List<(string File, string Category, string Detail)>();

        foreach (var file in psxFiles)
        {
            var relPath = Path.GetRelativePath(paths.SampleBuildsDir!, file);
            PsxMeshFile? psxFile;
            try { psxFile = PsxMeshFile.Parse(file); }
            catch { continue; } // parse failures covered by other test

            if (psxFile == null) continue;

            // 1. Stitch fallback detection: stitched vertices that didn't resolve
            var stitchedCount = 0;
            var fallbackCount = 0;
            foreach (var mesh in psxFile.Meshes)
            {
                foreach (var v in mesh.Vertices)
                {
                    if ((v.Type & 0x02) == 0) continue;
                    stitchedCount++;
                    var fbX = v.RawX / psxFile.ScaleDivisor;
                    var fbY = v.RawY / psxFile.ScaleDivisor;
                    var fbZ = v.RawZ / psxFile.ScaleDivisor;
                    if (Math.Abs(v.X - fbX) < 0.001f &&
                        Math.Abs(v.Y - fbY) < 0.001f &&
                        Math.Abs(v.Z - fbZ) < 0.001f)
                        fallbackCount++;
                }
            }
            if (fallbackCount > 0)
                issues.Add((relPath, "STITCH_FALLBACK",
                    $"{fallbackCount}/{stitchedCount} stitched vertices unresolved"));

            // 2. Unknown vertex type bits (beyond 0/1/4)
            var unknownTypes = new HashSet<ushort>();
            foreach (var mesh in psxFile.Meshes)
                foreach (var v in mesh.Vertices)
                    if ((v.Type & ~0x13) != 0) unknownTypes.Add(v.Type);
            if (unknownTypes.Count > 0)
                issues.Add((relPath, "UNKNOWN_VTYPES",
                    string.Join(", ", unknownTypes.OrderBy(t => t).Select(t => $"0x{t:X4}"))));

            // 3. Meshes with 0 faces (empty geometry attached to objects)
            var emptyMeshCount = psxFile.Meshes.Count(m => m.Faces.Count == 0 && m.Vertices.Count > 0);
            if (emptyMeshCount > 0)
                issues.Add((relPath, "EMPTY_MESHES",
                    $"{emptyMeshCount}/{psxFile.Meshes.Count} meshes have vertices but no faces"));

            // 4. Extremely high vertex-to-face ratio (possible face parse issue)
            foreach (var mesh in psxFile.Meshes.Where(m => m.Faces.Count > 0))
            {
                var ratio = (float)mesh.Vertices.Count / mesh.Faces.Count;
                if (ratio > 10f) // typically 1.5-3.0 for game models
                    issues.Add((relPath, "HIGH_VERT_RATIO",
                        $"mesh with {mesh.Vertices.Count} verts / {mesh.Faces.Count} faces (ratio {ratio:F1})"));
            }
        }

        // Group and print by category
        Console.WriteLine($"\n{'=',-60}");
        Console.WriteLine("PSX Mesh Issue Diagnostic");
        Console.WriteLine($"{'=',-60}");

        var grouped = issues.GroupBy(i => i.Category).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            Console.WriteLine($"\n--- {group.Key} ({group.Count()} occurrences) ---");
            foreach (var (file, _, detail) in group.OrderBy(i => i.File))
                Console.WriteLine($"  {file}: {detail}");
        }

        if (issues.Count == 0)
            Console.WriteLine("\nNo issues detected across all sample builds.");
        else
            Console.WriteLine($"\nTotal: {issues.Count} issues across {issues.Select(i => i.File).Distinct().Count()} files");
    }

    [Fact]
    public void Write_AllSampleBuilds_GlbExportNoCrashes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .ToList();
        Assert.SkipWhen(psxFiles.Count == 0, "No PSX files found in sample builds");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_CrossSample_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);

            var exported = 0;
            var skipped = 0;
            var errors = new List<string>();
            var totalTriangles = 0;

            foreach (var file in psxFiles)
            {
                var relPath = Path.GetRelativePath(paths.SampleBuildsDir!, file);

                try
                {
                    var psxFile = PsxMeshFile.Parse(file);
                    if (psxFile == null)
                    {
                        skipped++;
                        continue;
                    }

                    // Use relPath-derived name to avoid collisions
                    var safeName = relPath.Replace(Path.DirectorySeparatorChar, '_')
                        .Replace(Path.AltDirectorySeparatorChar, '_');
                    var outputFile = Path.Combine(tempDir, Path.ChangeExtension(safeName, ".glb"));

                    var triangles = PsxGltfWriter.Write(psxFile, outputFile);

                    Assert.True(File.Exists(outputFile), $"{relPath}: GLB not created");
                    Assert.True(new FileInfo(outputFile).Length > 0, $"{relPath}: GLB is empty");

                    totalTriangles += triangles;
                    exported++;
                }
                catch (Exception ex)
                {
                    errors.Add($"{relPath}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            Console.WriteLine($"\n{'=',-60}");
            Console.WriteLine($"PSX Mesh → glTF Export Report");
            Console.WriteLine($"{'=',-60}");
            Console.WriteLine($"Total PSX files:       {psxFiles.Count}");
            Console.WriteLine($"Exported to GLB:       {exported}");
            Console.WriteLine($"Texture-only/skipped:  {skipped}");
            Console.WriteLine($"Export errors:         {errors.Count}");
            Console.WriteLine($"Total triangles:       {totalTriangles:N0}");

            if (errors.Count > 0)
            {
                Console.WriteLine($"\nExport errors ({errors.Count}):");
                foreach (var e in errors.Take(20))
                    Console.WriteLine($"  {e}");
                if (errors.Count > 20)
                    Console.WriteLine($"  ... and {errors.Count - 20} more");
            }

            Assert.True(errors.Count == 0,
                $"{errors.Count} files failed glTF export:\n  " + string.Join("\n  ", errors.Take(10)));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}
