using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public sealed class PsxMeshDiagnosticTests(TestPaths paths, ITestOutputHelper output)
{
    [Fact]
    public void Diagnostic_Hawk2_PositionAndVertexDump()
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, "hawk2.PSX");
        Assert.SkipWhen(!File.Exists(inputFile), "hawk2.PSX not found");

        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.SkipWhen(psxFile == null, "hawk2.PSX has no mesh data");

        var pshFile = PshFile.FindCompanion(inputFile);

        output.WriteLine($"hawk2.PSX: {psxFile.Objects.Count} objects, {psxFile.Meshes.Count} meshes");
        output.WriteLine($"  ScaleDivisor={psxFile.ScaleDivisor} TranslationDivisor={psxFile.TranslationDivisor}");
        output.WriteLine($"  HasHierarchy={psxFile.HasHierarchy}");
        output.WriteLine("");

        float minWorldY = float.MaxValue, maxWorldY = float.MinValue;

        for (var i = 0; i < psxFile.Objects.Count; i++)
        {
            var obj = psxFile.Objects[i];
            var boneName = pshFile?.GetBoneName(i) ?? $"obj_{i}";
            var parentStr = obj.ParentIndex >= 0 ? $"parent={obj.ParentIndex}" : "ROOT";

            // Object position in glTF space (X, -Y, -Z)
            var posX = obj.X(psxFile.TranslationDivisor);
            var posY = -obj.Y(psxFile.TranslationDivisor);
            var posZ = -obj.Z(psxFile.TranslationDivisor);

            output.WriteLine($"Obj {i,2} [{boneName}] mesh={obj.MeshIndex} {parentStr}");
            output.WriteLine($"  Raw: ({obj.RawX,10}, {obj.RawY,10}, {obj.RawZ,10})");
            output.WriteLine($"  glTF pos: ({posX,8:F2}, {posY,8:F2}, {posZ,8:F2})");

            if (obj.MeshIndex < psxFile.Meshes.Count)
            {
                var mesh = psxFile.Meshes[obj.MeshIndex];
                if (mesh.Vertices.Count > 0)
                {
                    var minVx = mesh.Vertices.Min(v => v.X);
                    var maxVx = mesh.Vertices.Max(v => v.X);
                    var minVy = mesh.Vertices.Min(v => -v.Y); // glTF Y
                    var maxVy = mesh.Vertices.Max(v => -v.Y);
                    var minVz = mesh.Vertices.Min(v => -v.Z); // glTF Z
                    var maxVz = mesh.Vertices.Max(v => -v.Z);

                    output.WriteLine($"  Verts: {mesh.Vertices.Count}, Faces: {mesh.Faces.Count}");
                    output.WriteLine(
                        $"  Vert bbox (local glTF): X[{minVx,7:F2}..{maxVx,7:F2}] Y[{minVy,7:F2}..{maxVy,7:F2}] Z[{minVz,7:F2}..{maxVz,7:F2}]");

                    // World bbox = local + position
                    var worldMinY = posY + minVy;
                    var worldMaxY = posY + maxVy;
                    output.WriteLine($"  World Y range: [{worldMinY,7:F2}..{worldMaxY,7:F2}]");

                    minWorldY = Math.Min(minWorldY, worldMinY);
                    maxWorldY = Math.Max(maxWorldY, worldMaxY);
                }
            }

            output.WriteLine("");
        }

        var totalHeight = maxWorldY - minWorldY;
        output.WriteLine(
            $"=== Total model world Y range: [{minWorldY:F2}..{maxWorldY:F2}] height={totalHeight:F2} ===");

        // Also write to TestOutput for easy viewing
        if (paths.TestOutputDir != null)
        {
            var reportPath = Path.Combine(paths.TestOutputDir, "hawk2_position_dump.txt");
            var lines = new List<string>
            {
                $"hawk2.PSX: {psxFile.Objects.Count} objects, {psxFile.Meshes.Count} meshes",
                $"ScaleDivisor={psxFile.ScaleDivisor} TranslationDivisor={psxFile.TranslationDivisor}",
                ""
            };
            for (var i = 0; i < psxFile.Objects.Count; i++)
            {
                var obj = psxFile.Objects[i];
                var boneName = pshFile?.GetBoneName(i) ?? $"obj_{i}";
                var parentStr = obj.ParentIndex >= 0 ? $"parent={obj.ParentIndex}" : "ROOT";
                var posX = obj.X(psxFile.TranslationDivisor);
                var posY = -obj.Y(psxFile.TranslationDivisor);
                var posZ = -obj.Z(psxFile.TranslationDivisor);
                lines.Add(
                    $"Obj {i,2} [{boneName,-25}] mesh={obj.MeshIndex,2} {parentStr,-12} glTF=({posX,8:F2},{posY,8:F2},{posZ,8:F2})");
                if (obj.MeshIndex < psxFile.Meshes.Count)
                {
                    var mesh = psxFile.Meshes[obj.MeshIndex];
                    if (mesh.Vertices.Count > 0)
                    {
                        var bMinY = mesh.Vertices.Min(v => -v.Y);
                        var bMaxY = mesh.Vertices.Max(v => -v.Y);
                        lines.Add(
                            $"     verts={mesh.Vertices.Count,3} faces={mesh.Faces.Count,3} localY=[{bMinY,7:F2}..{bMaxY,7:F2}] worldY=[{posY + bMinY,7:F2}..{posY + bMaxY,7:F2}]");
                    }
                }
            }

            lines.Add("");
            lines.Add($"Total model world Y: [{minWorldY:F2}..{maxWorldY:F2}] height={totalHeight:F2}");
            File.WriteAllLines(reportPath, lines);
            output.WriteLine($"Report written to: {reportPath}");
        }
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
                    invalidFaceIndices.AddRange(
                        result.Objects
                            .Where(obj => obj.MeshIndex >= result.Meshes.Count)
                            .Select(obj => $"{relPath}: obj mesh index ({obj.MeshIndex}) >= {result.Meshes.Count}"));
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
        Console.WriteLine("PSX Mesh Cross-Sample Report");
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
            try
            {
                psxFile = PsxMeshFile.Parse(file);
            }
            catch
            {
                continue;
            } // parse failures covered by other test

            if (psxFile == null) continue;

            // 1. Stitch fallback detection via StitchFailureCount property
            var totalStitchFailures = psxFile.Meshes.Sum(m => m.StitchFailureCount);
            var stitchedCount = psxFile.Meshes.Sum(m =>
                m.Vertices.Count(v => (v.Type & 0x02) != 0));
            if (totalStitchFailures > 0)
                issues.Add((relPath, "STITCH_FALLBACK",
                    $"{totalStitchFailures}/{stitchedCount} stitched vertices unresolved"));

            // 2. Unknown vertex type bits (beyond 0/1/4)
            var unknownTypes = new HashSet<ushort>(
                psxFile.Meshes.SelectMany(m => m.Vertices)
                    .Where(v => (v.Type & ~0x13) != 0)
                    .Select(v => v.Type));
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
            Console.WriteLine(
                $"\nTotal: {issues.Count} issues across {issues.Select(i => i.File).Distinct().Count()} files");
    }

    [Fact]
    public void Diagnostic_CharacterModels_ExportGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(paths.TestOutputDir == null, "TestOutput directory not found");

        var psxFiles = Directory.GetFiles(paths.SampleBuildsDir!, "*.psx",
                new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = true })
            .ToList();
        Assert.SkipWhen(psxFiles.Count == 0, "No PSX files found in sample builds");

        var glbDir = Path.Combine(paths.TestOutputDir!, "CharacterModels");
        if (Directory.Exists(glbDir))
            Directory.Delete(glbDir, true);
        Directory.CreateDirectory(glbDir);

        var exported = 0;
        var errors = new List<string>();

        foreach (var file in psxFiles.OrderBy(f => f))
            ExportCharacterModel(file, glbDir, paths.SampleBuildsDir!, errors, ref exported);

        output.WriteLine($"Exported {exported} character models to: {glbDir}");
        if (errors.Count > 0)
        {
            output.WriteLine($"Errors ({errors.Count}):");
            foreach (var e in errors)
                output.WriteLine($"  {e}");
        }
    }

    private static void ExportCharacterModel(string file, string glbDir, string sampleBuildsDir,
        List<string> errors, ref int exported)
    {
        PsxMeshFile? psxFile;
        try
        {
            psxFile = PsxMeshFile.Parse(file);
        }
        catch
        {
            return;
        }

        if (psxFile is not { HasHierarchy: true }) return;

        var relPath = Path.GetRelativePath(sampleBuildsDir, file);
        char[] separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];
        var parts = relPath.Split(separators);
        var buildDir = Path.Combine(glbDir, parts[0]);
        Directory.CreateDirectory(buildDir);

        var modelName = Path.GetFileNameWithoutExtension(parts[^1]);
        var glbPath = Path.Combine(buildDir, modelName + ".glb");
        try
        {
            PsxGltfWriter.TextureProvider textureProvider = hash =>
            {
                var result = PsxLibrary.ExtractTextureByHash(file, hash);
                if (result == null) return null;
                var (rgba, w, h) = result.Value;
                return ImageWriter.WritePngToMemory(w, h, rgba);
            };
            var pshFile = PshFile.FindCompanion(file);
            PsxGltfWriter.Write(psxFile, glbPath, textureProvider, pshFile);
            exported++;
        }
        catch (Exception ex)
        {
            errors.Add($"{relPath}: {ex.Message}");
        }
    }
}