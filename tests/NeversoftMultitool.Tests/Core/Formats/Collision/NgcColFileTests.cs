using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Tests.Core.Formats.Collision;

public sealed class NgcColFileTests(TestPaths paths)
{
    private const string ThawGcBuild = "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    // ── Format detection ──

    [Fact]
    public void IsNgcColFile_LittleEndianColHeader_ReturnsFalse()
    {
        var data = new byte[64];
        BitConverter.GetBytes(10).CopyTo(data, 0); // LE version 10
        Assert.False(NgcColFile.IsNgcColFile(data));
    }

    [Fact]
    public void IsNgcColFile_TooSmall_ReturnsFalse()
    {
        Assert.False(NgcColFile.IsNgcColFile(new byte[40]));
    }

    [Fact]
    public void IsNgcColFile_HeaderAndBoundsWithoutBspSize_ReturnsFalse()
    {
        var data = new byte[56];
        BinaryPrimitives.WriteUInt32BigEndian(data, 10);

        Assert.False(NgcColFile.IsNgcColFile(data));
    }

    [Fact]
    public void IsNgcColFile_ExactEmptyFile_ReturnsTrueAndParses()
    {
        var data = new byte[60];
        BinaryPrimitives.WriteUInt32BigEndian(data, 10);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(36), 1.0f);
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(52), 1.0f);

        Assert.True(NgcColFile.IsNgcColFile(data));
        var scene = NgcColFile.Parse(data);
        Assert.Empty(scene.Objects);
        Assert.Equal(60, scene.SerializedSize);
    }

    [Fact]
    public void IsNgcColFile_MinimalSynthetic_ReturnsTrue()
    {
        Assert.True(NgcColFile.IsNgcColFile(BuildMinimalFile()));
    }

    // ── Synthetic round trip + strictness ──

    [Fact]
    public void Parse_MinimalSynthetic_ReadsEveryRegion()
    {
        var scene = NgcColFile.Parse(BuildMinimalFile());

        Assert.Equal(10, scene.Version);
        Assert.Equal(1, scene.SuperSectorRows);
        Assert.Equal(1, scene.SuperSectorCols);
        var obj = Assert.Single(scene.Objects);
        Assert.Equal(0x12345678u, obj.Checksum);
        Assert.Equal((ushort)0, obj.Flags);
        Assert.Equal(3, obj.NumVerts);
        var face = Assert.Single(obj.Faces);
        Assert.Equal(new NgcColFace(0, 0, 0, 1, 2), face);
        Assert.True(obj.BspRoot.IsLeaf);
        Assert.Equal([0], obj.BspRoot.LeafFaceIndices!);
        Assert.True(scene.CornerIntensitiesUniform);
        Assert.True(scene.FaceIndicesWithinCumulativeDeclaredVertexRanges);
        Assert.Equal(1, scene.PoolElementCount);
        Assert.Equal(150, scene.SerializedSize);
        Assert.Equal(8, scene.BspNodeByteCount);
    }

    [Fact]
    public void Parse_FaceVertexIndexOutsideFile_Throws()
    {
        var data = BuildMinimalFile();
        // face i2 at faceStart + 8 (faceStart = 124)
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(124 + 8), 3);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_NonZeroVertexPoolSlot_Throws()
    {
        var data = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(56 + 48), 1);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_NodeSizeNotMultipleOfEight_Throws()
    {
        var data = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(136), 4);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_LeafListPastPoolEnd_Throws()
    {
        var data = BuildMinimalFile();
        // leaf numFaces at nodeBase (140)
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(140), 2);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_PoolFaceIndexOutsideObject_Throws()
    {
        var data = BuildMinimalFile();
        // sole pool element at 148 — object has 1 face, so index 1 is invalid
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(148), 1);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_ObjectFlags_ArePreservedSeparatelyFromU16VertexCount()
    {
        var data = BuildMinimalFile();
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(60), 0xA55A);

        var obj = Assert.Single(NgcColFile.Parse(data).Objects);
        Assert.Equal((ushort)0xA55A, obj.Flags);
        Assert.Equal(3, obj.NumVerts);
    }

    [Theory]
    [InlineData(66)] // use-small-faces byte
    [InlineData(67)] // use-fixed-vertices byte
    [InlineData(123)] // align4 after the 3-byte intensity region
    [InlineData(134)] // 2-byte pad after an odd number of 10-byte faces
    public void Parse_NonZeroRequiredDialectByte_Throws(int offset)
    {
        var data = BuildMinimalFile();
        data[offset] = 1;
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_UnownedTrailingPoolElement_Throws()
    {
        var original = BuildMinimalFile();
        var data = new byte[original.Length + 2];
        original.CopyTo(data, 0);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(data));
    }

    [Fact]
    public void Parse_OverlappingLeafPoolRanges_Throws()
    {
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(BuildOverlappingPoolFile()));
    }

    [Fact]
    public void Parse_NonFiniteOrInvertedBounds_Throws()
    {
        var nonFinite = BuildMinimalFile();
        BinaryPrimitives.WriteSingleBigEndian(nonFinite.AsSpan(24), float.NaN);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(nonFinite));

        var inverted = BuildMinimalFile();
        BinaryPrimitives.WriteSingleBigEndian(inverted.AsSpan(24), 2.0f);
        BinaryPrimitives.WriteSingleBigEndian(inverted.AsSpan(40), 1.0f);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(inverted));

        var wrongW = BuildMinimalFile();
        BinaryPrimitives.WriteSingleBigEndian(wrongW.AsSpan(36), 0.0f);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(wrongW));
    }

    [Fact]
    public void Parse_CountOrNodeSizeOutsideIntRange_ThrowsInvalidData()
    {
        var count = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(count.AsSpan(12), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(count));

        var nodeSize = BuildMinimalFile();
        BinaryPrimitives.WriteUInt32BigEndian(nodeSize.AsSpan(136), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => NgcColFile.Parse(nodeSize));
    }

    // ── Real fixture: loose secret_tape (2 objects, leaf-only BSP) ──

    [Fact]
    public void Parse_SecretTape_PinsStructure()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = FindCanonicalSampleFile("secret_tape.col.ngc");
        Assert.SkipWhen(file is null, "secret_tape.col.ngc not found");

        var scene = NgcColFile.Parse(file);
        Assert.Equal(10, scene.Version);
        Assert.Equal(12, scene.TotalVerts);
        Assert.Equal(16, scene.TotalFaces);
        Assert.Equal(16, scene.PoolElementCount);
        Assert.True(scene.CornerIntensitiesUniform);
        Assert.True(scene.FaceIndicesWithinCumulativeDeclaredVertexRanges);

        Assert.Equal(2, scene.Objects.Length);
        Assert.Equal(0x6D10C4BBu, scene.Objects[0].Checksum);
        Assert.Equal(0x380A9488u, scene.Objects[1].Checksum);
        Assert.Equal(8, scene.Objects[0].NumVerts);
        Assert.Equal(14, scene.Objects[0].Faces.Length);
        Assert.Equal(4, scene.Objects[1].NumVerts);
        Assert.Equal(2, scene.Objects[1].Faces.Length);
        Assert.Equal(8, scene.Objects[1].CumulativeDeclaredVertexBase);
        Assert.Equal(14, scene.Objects[1].FirstFaceIndex);

        // Both trees are single leaves listing every face object-relative.
        Assert.True(scene.Objects[0].BspRoot.IsLeaf);
        Assert.Equal(14, scene.Objects[0].BspRoot.LeafFaceIndices!.Length);
        Assert.True(scene.Objects[1].BspRoot.IsLeaf);
        Assert.Equal([0, 1], scene.Objects[1].BspRoot.LeafFaceIndices!);

        // Object 1's faces index the global vertex numbering (8..11).
        Assert.All(scene.Objects[1].Faces, static face =>
        {
            Assert.InRange(face.V0, 8, 11);
            Assert.InRange(face.V1, 8, 11);
            Assert.InRange(face.V2, 8, 11);
        });
    }

    [Fact]
    public void Serialize_SecretTape_ProducesSchemaManifest()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = FindCanonicalSampleFile("secret_tape.col.ngc");
        Assert.SkipWhen(file is null, "secret_tape.col.ngc not found");

        var json = NgcColJsonExporter.Serialize(file, NgcColFile.Parse(file));
        Assert.Contains("\"schema\": \"neversoft.ngc.col\"", json);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"byteOrder\": \"bigEndian\"", json);
        Assert.Contains("\"formatVersion\": 10", json);
        Assert.Contains("\"checksum\": \"0x6D10C4BB\"", json);
        Assert.Contains("\"vertexStorageStatus\": \"externalRenderScenePool\"", json);
        Assert.Contains("\"geometryExportStatus\": \"unavailableWithoutProvenRenderScenePoolBinding\"", json);
        Assert.Contains("\"cumulativeDeclaredVertexBase\"", json);
        Assert.DoesNotContain("\"firstVertIndex\"", json);
        Assert.Contains("\"cornerIntensities\":", json);
        // Uniform corner intensities stay summarized, not dumped.
        Assert.DoesNotContain("cornerIntensitiesHex", json);
    }

    [Fact]
    public void Parse_RepresentativeLooseFiles_PinWholeAndIntensityHashes()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var wranglerPath = FindCanonicalSampleFile("veh_wrangler.col.ngc");
        var arrowPath = FindCanonicalSampleFile("arrow.col.ngc");
        Assert.SkipWhen(wranglerPath is null || arrowPath is null, "Representative .col.ngc files not found");

        var wrangler = NgcColFile.Parse(wranglerPath);
        Assert.Equal(1_192, wrangler.SerializedSize);
        Assert.Equal("68F6908F089FC9E2D9A3BA92B4ED2246313630115304513251FABBFC947E99AF",
            wrangler.SerializedSha256);
        Assert.Equal(5, wrangler.Objects.Length);
        Assert.Equal(34, wrangler.TotalVerts);
        Assert.Equal(46, wrangler.TotalFaces);

        var arrow = NgcColFile.Parse(arrowPath);
        Assert.Equal(560, arrow.SerializedSize);
        Assert.Equal("09905FF0696AACA7376A5E62221E2BAF59DC38E73900A4E1947AD3D49CC3E230",
            arrow.SerializedSha256);
        Assert.Equal(30, arrow.CornerIntensities.Count(static value => value != 0xFF));
        Assert.Equal("2E417D6641480C7B108D73CC19C5B0286CFBBD6DA705D1E4A65267D2C234DE86",
            arrow.CornerIntensitiesSha256);
    }

    [Fact]
    public void Parse_CanonicalEmptyFile_IsExactSixtyByteDocument()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, ThawGcBuild);
        var file = paths.FindSampleFiles(ThawGcBuild, "*.col.ngc")
            .FirstOrDefault(candidate =>
                !IsArchiveExpandedPath(Path.GetRelativePath(buildRoot, candidate)) &&
                new FileInfo(candidate).Length == 60);
        Assert.SkipWhen(file is null, "Canonical empty .col.ngc file not found");

        var scene = NgcColFile.Parse(file);
        Assert.Equal(60, scene.SerializedSize);
        Assert.Equal("0F4CB81C7CC2207FAB1519D2B2199705A244CC0C06AFA22A6E1A20108387E69D",
            scene.SerializedSha256);
        Assert.Empty(scene.Objects);
        Assert.Equal(0, scene.TotalVerts);
        Assert.Equal(0, scene.TotalFaces);
        Assert.Equal(0, scene.BspNodeByteCount);
        Assert.Equal(0, scene.PoolElementCount);
    }

    [Fact]
    public void GetOutputPath_MapsColNgcToColJson()
    {
        var result = NgcColCommand.GetOutputPath(
            "input.col.ngc", "input.col.ngc", "TestOutput");
        Assert.Equal(Path.Combine("TestOutput", "input.col.json"), result);
    }

    [Fact]
    public void GeometryConversionRouting_ValidColNgc_ReachesTheStrictBindingRoute()
    {
        const string fileName = "minimal.col.ngc";
        var data = BuildMinimalFile();

        Assert.True(NgcColFile.IsNgcColFile(data));
        Assert.Equal(".col.ngc", MeshTypeDetector.MatchSuffix(fileName));
        Assert.True(MeshTypeDetector.IsMeshCandidate(fileName));
        Assert.False(MeshTypeDetector.IsWorldzoneCandidate(fileName));

        var route = MeshTypeDetector.DetectFromBytes(fileName, data, data.Length);
        Assert.Equal(MeshFileKind.Collision, route.Kind);
        Assert.True(route.IsSupported);
        Assert.False(route.RequiresContentProbe);
        Assert.Contains("render-scene pool required", route.DisplayFormat);

        // The scanner admits the candidate by name, then its ScanColFile path
        // requires the exact structurally compatible scene owner before it
        // creates a GUI entry.
        var guiScanCandidate =
            MeshTypeDetector.IsMeshCandidate(fileName) && !MeshTypeDetector.IsObjectDdm(fileName);
        Assert.True(guiScanCandidate);
    }

    [Fact]
    public void Execute_MinimalSynthetic_WritesOnlyMetadataManifestAndSucceeds()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-ngccol-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "[minimal].col.ngc");
        var output = Path.Combine(root, "output");
        var manifest = Path.Combine(output, "[minimal].col.json");
        var data = BuildMinimalFile();
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(input, data);

            var result = NgcColCommand.Execute(input, output, true, CancellationToken.None);

            Assert.Equal(0, result);
            Assert.Equal(manifest, Assert.Single(
                Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories)));

            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            var document = json.RootElement;
            Assert.Equal("neversoft.ngc.col", document.GetProperty("schema").GetString());
            Assert.Equal(1, document.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("[minimal].col.ngc", document.GetProperty("source").GetString());
            Assert.Equal("bigEndian", document.GetProperty("byteOrder").GetString());
            Assert.Equal(data.Length, document.GetProperty("serializedSize").GetInt32());
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(data)),
                document.GetProperty("serializedSha256").GetString());
            Assert.Equal(10, document.GetProperty("formatVersion").GetInt32());
            Assert.Equal("externalRenderScenePool", document.GetProperty("vertexStorageStatus").GetString());
            Assert.Equal(
                "unavailableWithoutProvenRenderScenePoolBinding",
                document.GetProperty("geometryExportStatus").GetString());
            Assert.Equal(3, document.GetProperty("totalVerts").GetInt32());
            Assert.Equal(1, document.GetProperty("totalFaces").GetInt32());

            var obj = Assert.Single(document.GetProperty("objects").EnumerateArray());
            Assert.Equal("0x12345678", obj.GetProperty("checksum").GetString());
            Assert.Equal(3, obj.GetProperty("numVerts").GetInt32());
            Assert.Equal(1, obj.GetProperty("numFaces").GetInt32());
            Assert.False(document.TryGetProperty("vertices", out _));
            Assert.False(obj.TryGetProperty("vertices", out _));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Execute_MissingInput_FailsWithoutCreatingOutput()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-ngccol-{Guid.NewGuid():N}");
        var output = Path.Combine(root, "output");
        try
        {
            var result = NgcColCommand.Execute(
                Path.Combine(root, "missing.col.ngc"), output, false, CancellationToken.None);
            Assert.Equal(1, result);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Execute_MalformedInput_PreservesExistingOutputSentinel()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-ngccol-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "[bad].col.ngc");
        var output = Path.Combine(root, "output");
        var manifest = Path.Combine(output, "[bad].col.json");
        try
        {
            Directory.CreateDirectory(output);
            File.WriteAllBytes(input, [1, 2, 3]);
            File.WriteAllText(manifest, "sentinel");

            var result = NgcColCommand.Execute(input, output, false, CancellationToken.None);
            Assert.Equal(1, result);
            Assert.Equal("sentinel", File.ReadAllText(manifest));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // ── Whole-corpus sweep ──

    [CorpusFact]
    public void Parse_ThawGcCanonicalLooseCorpus_EveryFileParsesWithPinnedTotals()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, ThawGcBuild);
        var discovered = paths.FindSampleFiles(ThawGcBuild, "*.col.ngc")
            .Select(file => new
            {
                Path = file,
                Relative = Path.GetRelativePath(buildRoot, file)
            })
            .ToArray();
        var files = discovered
            .Where(static file => !IsArchiveExpandedPath(file.Relative))
            .OrderBy(static file => file.Relative, StringComparer.Ordinal)
            .ToArray();
        var expandedCopies = discovered
            .Where(static file => IsArchiveExpandedPath(file.Relative))
            .ToArray();
        Assert.SkipWhen(files.Length == 0, "No .col.ngc files found");
        Assert.Equal(722, files.Length);
        Assert.Equal(680, expandedCopies.Length);

        var fileCount = 0;
        var serializedBytes = 0L;
        var objects = 0;
        var verts = 0L;
        var faces = 0L;
        var intensityBytes = 0L;
        var nonFfIntensityBytes = 0L;
        var nodeBytes = 0L;
        var poolBytes = 0L;
        var leaves = 0;
        var interior = 0;
        var uniform = 0;
        var varied = 0;
        var contained = 0;
        var compact = 0;
        var empty = 0;
        var maxDepth = 0;
        using var pathAndHashDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var concatenatedBytesDigest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file.Path);
            var scene = NgcColFile.Parse(bytes);
            fileCount++;
            serializedBytes += bytes.Length;
            objects += scene.Objects.Length;
            verts += scene.TotalVerts;
            faces += scene.TotalFaces;
            intensityBytes += scene.CornerIntensities.Length;
            nonFfIntensityBytes += scene.CornerIntensities.LongCount(static value => value != 0xFF);
            nodeBytes += scene.BspNodeByteCount;
            poolBytes += scene.PoolElementCount * 2L;
            if (scene.Objects.Length == 0) empty++;
            foreach (var obj in scene.Objects)
            {
                var total = obj.BspRoot.CountNodes();
                var leafCount = obj.BspRoot.CountLeaves();
                leaves += leafCount;
                interior += total - leafCount;
                maxDepth = Math.Max(maxDepth, GetTreeDepth(obj.BspRoot));
            }

            if (scene.CornerIntensitiesUniform) uniform++;
            else varied++;
            if (scene.FaceIndicesWithinCumulativeDeclaredVertexRanges) contained++;
            else compact++;

            var normalizedRelative = file.Relative.Replace('\\', '/');
            pathAndHashDigest.AppendData(Encoding.UTF8.GetBytes(normalizedRelative));
            pathAndHashDigest.AppendData([0]);
            pathAndHashDigest.AppendData(SHA256.HashData(bytes));
            concatenatedBytesDigest.AppendData(bytes);
        }

        Assert.Equal(722, fileCount);
        Assert.Equal(7_606_904, serializedBytes);
        Assert.Equal(819, objects);
        Assert.Equal(237_175, verts);
        Assert.Equal(411_057, faces);
        Assert.Equal(1_233_171, intensityBytes);
        Assert.Equal(18_052, nonFfIntensityBytes);
        Assert.Equal(568_552, nodeBytes);
        Assert.Equal(1_597_808, poolBytes);
        Assert.Equal(35_944, leaves);
        Assert.Equal(35_125, interior);
        Assert.Equal(644, uniform);
        Assert.Equal(78, varied);
        Assert.Equal(693, contained);
        Assert.Equal(29, compact);
        Assert.Equal(17, empty);
        Assert.Equal(7, maxDepth);
        Assert.Equal(
            "995FFF67D5150631569CC53175F42A326377765065558399ED8D67D82C89C28B",
            Convert.ToHexString(pathAndHashDigest.GetHashAndReset()));
        Assert.Equal(
            "5871375E46BDEE746036BD37C7057C52E422D8AC2A014EC714AE9A87EC7642BD",
            Convert.ToHexString(concatenatedBytesDigest.GetHashAndReset()));

        // These are archive-expanded duplicates/stale extraction artifacts,
        // so they prove parser acceptance only and never contribute to the
        // authoritative content totals above.
        foreach (var copy in expandedCopies)
            _ = NgcColFile.Parse(copy.Path);
    }

    // ── Synthetic minimal file ──
    // 1 object, 3 verts, 1 face, single-leaf BSP, 1 pool element:
    //   0   header (24B)
    //  24   scene bounds (32B)
    //  56   object record (64B)
    // 120   corner intensities (3B) + 1 align pad
    // 124   face (10B)
    // 134   odd-face-count pad (2B)
    // 136   node array size u32 = 8
    // 140   leaf node (8B)
    // 148   pool: one u16
    private static byte[] BuildMinimalFile()
    {
        var data = new byte[150];
        var span = data.AsSpan();
        BinaryPrimitives.WriteUInt32BigEndian(span, 10);
        BinaryPrimitives.WriteUInt32BigEndian(span[4..], 1);   // objects
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], 3);   // verts
        BinaryPrimitives.WriteUInt32BigEndian(span[12..], 1);  // faces
        BinaryPrimitives.WriteUInt32BigEndian(span[16..], 1);  // ss rows
        BinaryPrimitives.WriteUInt32BigEndian(span[20..], 1);  // ss cols
        BinaryPrimitives.WriteSingleBigEndian(span[36..], 1.0f); // scene min W
        BinaryPrimitives.WriteSingleBigEndian(span[52..], 1.0f); // scene max W

        BinaryPrimitives.WriteUInt32BigEndian(span[56..], 0x12345678); // checksum
        BinaryPrimitives.WriteUInt16BigEndian(span[60..], 0);  // flags
        BinaryPrimitives.WriteUInt16BigEndian(span[62..], 3);  // numVerts
        BinaryPrimitives.WriteUInt16BigEndian(span[64..], 1);  // numFaces
        BinaryPrimitives.WriteSingleBigEndian(span[84..], 1.0f);  // object min W
        BinaryPrimitives.WriteSingleBigEndian(span[100..], 1.0f); // object max W

        span[120] = 0xFF;
        span[121] = 0xFF;
        span[122] = 0xFF;

        // face: flags 0, terrain 0, verts (0,1,2)
        BinaryPrimitives.WriteUInt16BigEndian(span[130..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(span[132..], 2);

        BinaryPrimitives.WriteUInt32BigEndian(span[136..], 8); // node array size
        BinaryPrimitives.WriteUInt16BigEndian(span[140..], 1); // leaf face count
        span[143] = 3;                                          // leaf axis
        // leaf pool offset (u32 at 144) = 0; pool element (u16 at 148) = 0
        return data;
    }

    private static byte[] BuildOverlappingPoolFile()
    {
        var minimal = BuildMinimalFile();
        var data = new byte[166];
        minimal.AsSpan(0, 140).CopyTo(data);

        // Three-node tree at 140: root, then consecutive leaf children.
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(136), 24);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(144), 8); // root child byte offset
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(148), 1);
        data[151] = 3;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(156), 1);
        data[159] = 3;
        // Both leaves point at pool element zero; pool begins at 164.
        return data;
    }

    private string? FindCanonicalSampleFile(string fileName)
    {
        if (paths.SampleBuildsDir is null) return null;
        var buildRoot = Path.Combine(paths.SampleBuildsDir, ThawGcBuild);
        return paths.FindSampleFiles(ThawGcBuild, fileName)
            .FirstOrDefault(file => !IsArchiveExpandedPath(Path.GetRelativePath(buildRoot, file)));
    }

    private static bool IsArchiveExpandedPath(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        if (string.IsNullOrEmpty(directory)) return false;
        return directory.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .Any(static component => component.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
    }

    private static int GetTreeDepth(NgcColBspNode node)
    {
        return node.IsLeaf ? 0 : 1 + Math.Max(GetTreeDepth(node.Less!), GetTreeDepth(node.Greater!));
    }
}
