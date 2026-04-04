using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Tests.Helpers;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshGltfTests(TestPaths paths)
{
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

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_MeshPs1Glb_" + Guid.NewGuid().ToString("N")[..8]);
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
        // Positions use base scale (2.25), vertices use 2.25×16 for characters
        Assert.Equal(2.25f, psxFile.TranslationDivisor);

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

            var model = ModelRoot.Load(outputFile);
            Assert.True(model.LogicalMeshes.Count > 0, "GLB should have meshes");
            Assert.True(model.LogicalSkins.Count > 0, "Character GLB should include a skin");
            Assert.True(model.LogicalNodes.Any(node => node.Skin != null),
                "Character GLB should have a node bound to the exported skin");
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // ── Cross-sample batch glTF export ────────────────────────────────

    [Fact]
    public void Write_AllSampleBuilds_GlbExportNoCrashes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .ToList();
        Assert.SkipWhen(psxFiles.Count == 0, "No PSX files found in sample builds");

        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_CrossSample_" + Guid.NewGuid().ToString("N")[..8]);
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
            Console.WriteLine("PSX Mesh → glTF Export Report");
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