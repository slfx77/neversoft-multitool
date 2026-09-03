using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class CollisionOverlayResolverTests(TestPaths paths)
{
    [Fact]
    public void CandidateNames_AreRestrictedToProvenSameCoordinateFamilies()
    {
        Assert.Equal(
            ["park.col.ps2", "park.col"],
            CollisionOverlayResolver.CandidateNamesFor("park.geom.ps2", ModelSourceKind.Ps2Geom));
        Assert.Equal(
            ["park.col.xbx", "park.col"],
            CollisionOverlayResolver.CandidateNamesFor("park.scn.xbx", ModelSourceKind.XbxScene));
        Assert.Equal(
            ["park.col.wpc", "park.col"],
            CollisionOverlayResolver.CandidateNamesFor("park.scn.wpc", ModelSourceKind.XbxScene));
        Assert.Equal(
            ["park.col"],
            CollisionOverlayResolver.CandidateNamesFor("park.scn", ModelSourceKind.XbxScene));
        Assert.Equal(
            ["park.col.xen"],
            CollisionOverlayResolver.CandidateNamesFor("park.scn.xen", ModelSourceKind.XbxScene));
        Assert.Equal(
            ["Alccol.dat"],
            CollisionOverlayResolver.CandidateNamesFor("Alcscn.dat", ModelSourceKind.XbxScene));
        Assert.Equal(
            ["park.psx"],
            CollisionOverlayResolver.CandidateNamesFor("park.ddm", ModelSourceKind.Ddm));

        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.scn.ngc", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.scn.ps3", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.mdl.xen", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.geom.ps2", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.scn.dat", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "scn.dat", ModelSourceKind.XbxScene));
        Assert.Empty(CollisionOverlayResolver.CandidateNamesFor(
            "park.ddx", ModelSourceKind.Ddm));
    }

    [Fact]
    public void Thps2xDdmPair_RequiresTheCompleteAuthoredLevelFamily()
    {
        var complete = new MemoryAssetSource(
            "park.ddm",
            ("park.psx", [0]),
            ("park_o.ddm", [0]),
            ("park_t.trg", [0]));
        var missingObjects = new MemoryAssetSource(
            "park.ddm",
            ("park.psx", [0]),
            ("park_t.trg", [0]));
        var missingTrigger = new MemoryAssetSource(
            "park.ddm",
            ("park.psx", [0]),
            ("park_o.ddm", [0]));

        Assert.True(CollisionOverlayResolver.HasSupportedCompanion(
            complete, complete.EntryName, ModelSourceKind.Ddm));
        Assert.False(CollisionOverlayResolver.HasSupportedCompanion(
            missingObjects, missingObjects.EntryName, ModelSourceKind.Ddm));
        Assert.False(CollisionOverlayResolver.HasSupportedCompanion(
            missingTrigger, missingTrigger.EntryName, ModelSourceKind.Ddm));
    }

    [Fact]
    public void Thps2xCollisionWriter_RestoresSerializedScaleAndIncludesInvisibleFaces()
    {
        var sourceMesh = new PsxMesh
        {
            Vertices =
            [
                new PsxVertex { X = 0f, Y = 0f, Z = 0f },
                new PsxVertex { X = 1f, Y = 0f, Z = 0f },
                new PsxVertex { X = 0f, Y = 1f, Z = 0f },
                new PsxVertex { X = 1f, Y = 1f, Z = 0f }
            ],
            Normals = [],
            Faces =
            [
                new PsxFace { Index0 = 0, Index1 = 1, Index2 = 2 }
            ],
            InvisibleFaces =
            [
                new PsxFace
                {
                    IsQuad = true,
                    Index0 = 0,
                    Index1 = 1,
                    Index2 = 2,
                    Index3 = 3
                }
            ]
        };
        var collision = new PsxMeshFile
        {
            Version = 0x06,
            Objects =
            [
                new PsxMeshObject
                {
                    MeshIndex = 0,
                    RawX = 4096,
                    RawY = -8192,
                    RawZ = 12_288
                }
            ],
            Meshes = [sourceMesh],
            MeshNameHashes = [0],
            TextureHashes = [],
            ScaleDivisor = 2f,
            TranslationDivisor = 2f
        };
        var document = new ModelDocument
        {
            Name = "park",
            SourceKind = ModelSourceKind.DdmPlacedLevel
        };

        var added = Thps2XPsxCollisionGeometryWriter.PopulateOverlay(
            document, collision);

        Assert.Equal(3, added);
        Assert.Equal(3, document.TriangleCount);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.Equal(
            [
                new Vector3(-1f, 2f, 3f),
                new Vector3(-1f, 0f, 3f),
                new Vector3(-3f, 2f, 3f)
            ],
            primitive.Vertices.Take(3).Select(static vertex => vertex.Position));
    }

    [Fact]
    public void Thps2xDdmOverlay_RejectsWrongRevisionAndStandalonePlacement()
    {
        var wrongRevision = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(wrongRevision, 0x00020004);
        var truncatedV6 = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(truncatedV6, 0x00020006);
        foreach (var bytes in new[] { wrongRevision, truncatedV6 })
        {
            var source = new MemoryAssetSource(
                "park.ddm",
                ("park.psx", bytes),
                ("park_o.ddm", [0]),
                ("park_t.trg", [0]));

            var placed = CreateLevelDocument(ModelSourceKind.DdmPlacedLevel);
            Assert.False(CollisionOverlayResolver.TryPopulate(
                placed, source, source.EntryName, ModelSourceKind.Ddm));
            Assert.Equal(1, placed.TriangleCount);
            Assert.DoesNotContain(placed.Meshes,
                static mesh => mesh.Name == "collision_overlay");
        }

        var validNameOnly = new MemoryAssetSource(
            "park.ddm",
            ("park.psx", truncatedV6),
            ("park_o.ddm", [0]),
            ("park_t.trg", [0]));
        var standalone = CreateLevelDocument(ModelSourceKind.Ddm);
        Assert.False(CollisionOverlayResolver.TryPopulate(
            standalone, validNameOnly, validNameOnly.EntryName, ModelSourceKind.Ddm));
        Assert.Equal(1, standalone.TriangleCount);
    }

    [Fact]
    public void HasSupportedCompanion_RequiresExactlyOneCandidate()
    {
        var one = new MemoryAssetSource(
            "park.geom.ps2",
            ("park.col.ps2", CreateTriangleCol()));
        var ambiguous = new MemoryAssetSource(
            "park.geom.ps2",
            ("park.col.ps2", CreateTriangleCol()),
            ("park.col", CreateTriangleCol()));

        Assert.True(CollisionOverlayResolver.HasSupportedCompanion(
            one, one.EntryName, ModelSourceKind.Ps2Geom));
        Assert.False(CollisionOverlayResolver.HasSupportedCompanion(
            ambiguous, ambiguous.EntryName, ModelSourceKind.Ps2Geom));
    }

    [Fact]
    public void FileSystemLookup_DoesNotCrossDirectories()
    {
        var root = Path.Combine(
            Path.GetTempPath(), "nmt_collision_overlay_" + Guid.NewGuid().ToString("N"));
        var sceneDirectory = Path.Combine(root, "scene");
        var otherDirectory = Path.Combine(root, "other");
        Directory.CreateDirectory(sceneDirectory);
        Directory.CreateDirectory(otherDirectory);
        var scenePath = Path.Combine(sceneDirectory, "park.geom.ps2");
        File.WriteAllBytes(scenePath, [0]);
        File.WriteAllBytes(Path.Combine(otherDirectory, "park.col.ps2"), CreateTriangleCol());

        try
        {
            var source = new FileSystemAssetSource(scenePath);
            Assert.False(CollisionOverlayResolver.HasSupportedCompanion(
                source, source.EntryName, ModelSourceKind.Ps2Geom));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void TryPopulate_AddsTranslucentNamedOverlayAndMetadata()
    {
        var source = new MemoryAssetSource(
            "park.geom.ps2",
            ("park.col.ps2", CreateTriangleCol()));
        var document = CreateLevelDocument();

        var populated = CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, ModelSourceKind.Ps2Geom);

        Assert.True(populated);
        Assert.Equal(2, document.TriangleCount);
        Assert.Contains(document.Meshes, static mesh => mesh.Name == "collision_overlay");
        var material = Assert.Single(
            document.Materials,
            static material => material.Name == "collision_overlay");
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
        Assert.InRange(material.BaseColor.W, 0.3f, 0.5f);
        var metadata = Assert.Single(document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal("park.col.ps2", metadata.CompanionName);
        Assert.Equal(1, metadata.ObjectCount);
        Assert.Equal(1, metadata.TriangleCount);
    }

    [Theory]
    [InlineData("park.scn.xen", "park.col.xen", false)]
    [InlineData("park.geom.ps2", "park.col.ps2", true)]
    [InlineData("Alcscn.dat", "Alccol.dat", false)]
    public void TryPopulate_RejectsCollisionWithTheWrongPlatformByteOrder(
        string sceneName,
        string collisionName,
        bool bigEndian)
    {
        var bytes = new byte[32];
        if (bigEndian)
            BinaryPrimitives.WriteInt32BigEndian(bytes, 10);
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes, 10);
        var source = new MemoryAssetSource(sceneName, (collisionName, bytes));
        var document = CreateLevelDocument();
        var sourceKind = sceneName.EndsWith(".geom.ps2", StringComparison.Ordinal)
            ? ModelSourceKind.Ps2Geom
            : ModelSourceKind.XbxScene;

        Assert.False(CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, sourceKind));
        Assert.Equal(1, document.TriangleCount);
        Assert.DoesNotContain(document.Meshes, static mesh => mesh.Name == "collision_overlay");
    }

    [Fact]
    public void Thps4PcDelimiterFreePair_RequiresVersion8AndComposes()
    {
        var valid = new MemoryAssetSource(
            "Alcscn.dat",
            ("Alccol.dat", CreateTriangleCol()));
        var wrongVersion = CreateTriangleCol();
        BinaryPrimitives.WriteInt32LittleEndian(wrongVersion, 9);
        var rejected = new MemoryAssetSource(
            "Alcscn.dat",
            ("Alccol.dat", wrongVersion));

        Assert.True(CollisionOverlayResolver.TryPopulate(
            CreateLevelDocument(), valid, valid.EntryName, ModelSourceKind.XbxScene));
        Assert.False(CollisionOverlayResolver.TryPopulate(
            CreateLevelDocument(), rejected, rejected.EntryName, ModelSourceKind.XbxScene));
    }

    [Fact]
    public void TryPopulate_MalformedOrMissingCompanion_LeavesLevelUntouched()
    {
        foreach (var source in new[]
                 {
                     new MemoryAssetSource("park.geom.ps2"),
                     new MemoryAssetSource("park.geom.ps2", ("park.col.ps2", new byte[32]))
                 })
        {
            var document = CreateLevelDocument();

            Assert.False(CollisionOverlayResolver.TryPopulate(
                document, source, source.EntryName, ModelSourceKind.Ps2Geom));
            Assert.Equal(1, document.TriangleCount);
            Assert.Single(document.Meshes);
            Assert.Single(document.Materials);
        }
    }

    [Fact]
    public void TryPopulate_DegenerateOnlyCompanion_LeavesLevelUntouched()
    {
        var source = new MemoryAssetSource(
            "park.geom.ps2",
            ("park.col.ps2", CreateTriangleCol(degenerate: true)));
        var document = CreateLevelDocument();

        Assert.False(CollisionOverlayResolver.TryPopulate(
            document, source, source.EntryName, ModelSourceKind.Ps2Geom));
        Assert.Equal(1, document.TriangleCount);
        Assert.Single(document.Meshes);
        Assert.Single(document.Materials);
        Assert.Empty(document.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
    }

    [Fact]
    public void RenderPolicy_RebuildsWhenAnOverlayCompanionCouldChangeTheOutput()
    {
        Assert.True(MeshGuiRenderPolicy.RequiresEntryRebuild(
            isPakWorldzone: false,
            hasSupportedLevelObjectCompanion: false,
            supportsExplicitXbxSkeleton: false,
            hasSupportedCollisionOverlayCompanion: true));
    }

    [Fact]
    public void ArchiveLookup_UsesTheSelectedEntryDirectory()
    {
        var (wadPath, tempDirectory) = BuildWadOnDisk(
            ("levels/a/park.geom.ps2", new byte[] { 1 }),
            ("levels/a/park.col.ps2", CreateTriangleCol()),
            ("levels/b/park.col.ps2", new byte[32]));
        ArchiveAssetBackend? backend = null;

        try
        {
            backend = ArchiveAssetBackend.TryOpen(wadPath);
            Assert.NotNull(backend);
            var entry = backend!.FindByPath("levels/a/park.geom.ps2");
            Assert.NotNull(entry);
            var source = new ArchiveAssetSource(backend, entry!);
            var document = CreateLevelDocument();

            Assert.True(CollisionOverlayResolver.TryPopulate(
                document, source, source.EntryName, ModelSourceKind.Ps2Geom));
            Assert.Equal(2, document.TriangleCount);
        }
        finally
        {
            backend?.FileSystem.Dispose();
            Directory.Delete(tempDirectory, true);
        }
    }

    [Fact]
    public void ArchiveLookup_DoesNotUseAUniqueRemoteDirectoryFallback()
    {
        var (wadPath, tempDirectory) = BuildWadOnDisk(
            ("levels/a/park.geom.ps2", new byte[] { 1 }),
            ("levels/b/park.col.ps2", CreateTriangleCol()));
        ArchiveAssetBackend? backend = null;

        try
        {
            backend = ArchiveAssetBackend.TryOpen(wadPath);
            Assert.NotNull(backend);
            var entry = backend!.FindByPath("levels/a/park.geom.ps2");
            Assert.NotNull(entry);
            var source = new ArchiveAssetSource(backend, entry!);

            Assert.False(CollisionOverlayResolver.HasSupportedCompanion(
                source, source.EntryName, ModelSourceKind.Ps2Geom));
            Assert.False(CollisionOverlayResolver.TryPopulate(
                CreateLevelDocument(), source, source.EntryName, ModelSourceKind.Ps2Geom));
        }
        finally
        {
            backend?.FileSystem.Dispose();
            Directory.Delete(tempDirectory, true);
        }
    }

    [CorpusTheory]
    [InlineData(
        "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)",
        "Veh_ElephantTruck_Gate.geom.ps2",
        ModelSourceKind.Ps2Geom)]
    [InlineData(
        "Tony Hawks Underground 2 (2004-10-4, Windows - Final)",
        "Sk5Ed5_Sky.scn.xbx",
        ModelSourceKind.XbxScene)]
    [InlineData(
        "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)",
        "Alcscn.dat",
        ModelSourceKind.XbxScene)]
    [InlineData(
        "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)",
        "skhvn.DDM",
        ModelSourceKind.Ddm)]
    public void Parser_RealExactStemPair_ComposesAndExportsGlb(
        string buildName,
        string fileName,
        ModelSourceKind sourceKind)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFiles(buildName, fileName)
            .FirstOrDefault(path =>
            {
                var source = new FileSystemAssetSource(path);
                return CollisionOverlayResolver.HasSupportedCompanion(
                    source, source.EntryName, sourceKind);
            });
        Assert.SkipWhen(file is null, $"{fileName} with an exact-stem COL companion not found");
        var source = new FileSystemAssetSource(file!);
        var parser = new MeshModelParser();
        var outputStem = Path.GetFileNameWithoutExtension(fileName);
        var isPlacedDdm = sourceKind == ModelSourceKind.Ddm;

        var withoutOverlay = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = fileName,
            OutputStem = outputStem,
            SourceKind = sourceKind,
            HasPlacedPsxCompanion = isPlacedDdm
        });
        var withOverlay = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = fileName,
            OutputStem = outputStem,
            SourceKind = sourceKind,
            HasPlacedPsxCompanion = isPlacedDdm,
            IncludeCollisionOverlay = true
        });

        var metadata = Assert.Single(
            withOverlay.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.True(metadata.TriangleCount > 0);
        Assert.Equal(
            withoutOverlay.TriangleCount + metadata.TriangleCount,
            withOverlay.TriangleCount);
        var (glb, triangles) = ModelExportService.BuildGlbBytes(withOverlay);
        Assert.NotNull(glb);
        Assert.Equal(withOverlay.TriangleCount, triangles);
    }

    [CorpusFact]
    public void Thps2xFinal_AllAuthoredDdmLevelsHaveRenderableV6CollisionOverlays()
    {
        Assert.SkipWhen(paths.SampleBuildsDir == null, "Sample/Builds is not available");
        const string buildName =
            "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)";
        var buildRoot = Path.Combine(paths.SampleBuildsDir!, buildName);
        Assert.SkipWhen(!Directory.Exists(buildRoot), "THPS2X final build is not available");

        var allFiles = Directory.EnumerateFiles(buildRoot, "*", SearchOption.AllDirectories)
            .ToArray();
        var filesByPath = allFiles.ToDictionary(
            Path.GetFullPath,
            StringComparer.OrdinalIgnoreCase);
        var ddms = allFiles
            .Where(static path => path.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var exactPsxPairs = 0;
        var authoredLevelFamilies = 0;
        var supportedLevelFamilies = 0;
        long collisionBytes = 0;
        var objects = 0;
        var meshes = 0;
        var declaredFaces = 0;
        var visibleFaces = 0;
        var invisibleFaces = 0;
        var rejectedFaces = 0;
        var emittedTriangles = 0;

        foreach (var ddmPath in ddms)
        {
            var directory = Path.GetDirectoryName(ddmPath)!;
            var stem = Path.GetFileNameWithoutExtension(ddmPath);
            if (!filesByPath.TryGetValue(
                    Path.GetFullPath(Path.Combine(directory, stem + ".psx")),
                    out var psxPath))
            {
                continue;
            }

            exactPsxPairs++;
            if (!filesByPath.ContainsKey(
                    Path.GetFullPath(Path.Combine(directory, stem + "_o.ddm")))
                || !filesByPath.ContainsKey(
                    Path.GetFullPath(Path.Combine(directory, stem + "_t.trg"))))
            {
                continue;
            }

            authoredLevelFamilies++;
            var source = new FileSystemAssetSource(ddmPath);
            Assert.True(CollisionOverlayResolver.HasSupportedCompanion(
                source, source.EntryName, ModelSourceKind.Ddm));
            supportedLevelFamilies++;

            var collision = Assert.IsType<PsxMeshFile>(PsxMeshFile.Parse(psxPath!));
            Assert.Equal(0x06, collision.Version);
            Assert.False(collision.IsSuperModel);
            collisionBytes += new FileInfo(psxPath!).Length;
            objects += collision.Objects.Count;
            meshes += collision.Meshes.Count;
            declaredFaces += collision.Meshes.Sum(static mesh => mesh.FaceReadInfos.Count);
            visibleFaces += collision.Meshes.Sum(static mesh => mesh.Faces.Count);
            invisibleFaces += collision.Meshes.Sum(static mesh => mesh.InvisibleFaces.Count);
            rejectedFaces += collision.Meshes.Sum(static mesh =>
                mesh.FaceReadInfos.Count - mesh.Faces.Count - mesh.InvisibleFaces.Count);

            var overlay = new ModelDocument
            {
                Name = stem,
                SourceKind = ModelSourceKind.DdmPlacedLevel
            };
            var added = Thps2XPsxCollisionGeometryWriter.PopulateOverlay(
                overlay, collision);
            Assert.True(added > 0, $"{stem} emitted no collision triangles");
            Assert.Equal(added, overlay.TriangleCount);
            emittedTriangles += added;
        }

        Assert.Equal(104, ddms.Length);
        Assert.Equal(104, exactPsxPairs);
        Assert.Equal(24, authoredLevelFamilies);
        Assert.Equal(24, supportedLevelFamilies);
        var actual = (
            CollisionBytes: collisionBytes,
            Objects: objects,
            Meshes: meshes,
            DeclaredFaces: declaredFaces,
            VisibleFaces: visibleFaces,
            InvisibleFaces: invisibleFaces,
            RejectedFaces: rejectedFaces,
            EmittedTriangles: emittedTriangles);
        Assert.Equal(
            (CollisionBytes: 22_993_532L,
                Objects: 19_527,
                Meshes: 19_527,
                DeclaredFaces: 328_531,
                VisibleFaces: 306_154,
                InvisibleFaces: 22_288,
                RejectedFaces: 89,
                EmittedTriangles: 485_549),
            actual);
    }

    private static ModelDocument CreateLevelDocument(
        ModelSourceKind sourceKind = ModelSourceKind.Ps2Geom)
    {
        var document = new ModelDocument
        {
            Name = "park",
            SourceKind = sourceKind
        };
        document.Materials.Add(new RenderMaterial { Name = "level" });
        var mesh = new ModelMesh { Name = "level" };
        var vertices = new List<ModelVertex>();
        var indices = new List<int>();
        ModelDocumentGeometryAdapter.AddTriangle(
            vertices,
            indices,
            Vertex(Vector3.Zero),
            Vertex(Vector3.UnitX),
            Vertex(Vector3.UnitY));
        ModelDocumentGeometryAdapter.AddPrimitive(mesh, "level", 0, vertices, indices);
        ModelDocumentGeometryAdapter.AddMeshNode(document, "level", mesh);
        ModelDocumentGeometryAdapter.FinalizeTriangleCount(document);
        return document;
    }

    private static ModelVertex Vertex(Vector3 position) =>
        new(position, Vector3.UnitZ, Vector4.One, Vector2.Zero);

    private static byte[] CreateTriangleCol(bool degenerate = false)
    {
        const int objectOffset = 32;
        const int vertexOffset = 96;
        const int faceOffset = 144;
        const int bspSizeOffset = faceOffset + 12;
        const int bspLeafOffset = bspSizeOffset + sizeof(uint);
        const int faceIndexOffset = bspLeafOffset + 20;
        var data = new byte[faceIndexOffset + sizeof(ushort)];
        BinaryPrimitives.WriteInt32LittleEndian(data, 8);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 3);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), 1);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(objectOffset), 0x12345678);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(objectOffset + 6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(objectOffset + 8), 1);
        for (var axis = 0; axis < 3; axis++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(objectOffset + 16 + axis * 4), -1f);
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(objectOffset + 32 + axis * 4), 1f);
        }

        WriteVertex(data, vertexOffset, 0f, 0f, 0f);
        WriteVertex(data, vertexOffset + 16, 1f, 0f, 0f);
        WriteVertex(data, vertexOffset + 32, degenerate ? 1f : 0f, degenerate ? 0f : 1f, 0f);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceOffset + 4), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceOffset + 6), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceOffset + 8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspSizeOffset), 20);
        data[bspLeafOffset] = byte.MaxValue;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(bspLeafOffset + 2), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspLeafOffset + 8), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspLeafOffset + 12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(bspLeafOffset + 16), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(faceIndexOffset), 0);
        return data;

        static void WriteVertex(byte[] bytes, int offset, float x, float y, float z)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), x);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 4), y);
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset + 8), z);
            bytes[offset + 12] = 255;
            bytes[offset + 13] = 255;
            bytes[offset + 14] = 255;
            bytes[offset + 15] = 255;
        }
    }

    private static (string WadPath, string TempDirectory) BuildWadOnDisk(
        params (string Name, byte[] Data)[] files)
    {
        var tempDirectory = Path.Combine(
            Path.GetTempPath(), "nmt_collision_overlay_wad_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        using var wad = new MemoryStream();
        using var hed = new MemoryStream();
        using var writer = new BinaryWriter(hed);
        foreach (var (name, data) in files)
        {
            var offset = (uint)wad.Length;
            wad.Write(data);
            writer.Write(Encoding.ASCII.GetBytes(name + "\0"));
            writer.Write(new byte[(4 - hed.Length % 4) % 4]);
            writer.Write(offset);
            writer.Write((uint)data.Length);
        }

        writer.Write((byte)0xFF);
        var wadPath = Path.Combine(tempDirectory, "TEST.WAD");
        File.WriteAllBytes(wadPath, wad.ToArray());
        File.WriteAllBytes(Path.Combine(tempDirectory, "TEST.HED"), hed.ToArray());
        return (wadPath, tempDirectory);
    }

    private sealed class MemoryAssetSource : AssetSource
    {
        private readonly Dictionary<string, byte[]> _companions;

        public MemoryAssetSource(
            string entryName,
            params (string Name, byte[] Data)[] companions)
        {
            EntryName = entryName;
            _companions = companions.ToDictionary(
                static item => item.Name,
                static item => item.Data,
                StringComparer.OrdinalIgnoreCase);
        }

        public override string DisplayName => EntryName;
        public override string EntryName { get; }
        public override byte[] ReadBytes() => [0];

        public override bool CompanionExists(string nameWithExtension) =>
            _companions.ContainsKey(nameWithExtension);

        public override byte[]? TryReadCompanion(string nameWithExtension) =>
            _companions.TryGetValue(nameWithExtension, out var bytes) ? bytes : null;

        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null)
        {
            foreach (var extension in extensions)
            {
                if (_companions.TryGetValue(stem + extension, out var bytes))
                    return bytes;
            }

            return null;
        }
    }
}
