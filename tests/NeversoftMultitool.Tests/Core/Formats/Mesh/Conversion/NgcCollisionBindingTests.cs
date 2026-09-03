using System.Numerics;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Collision;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class NgcCollisionBindingTests(TestPaths paths)
{
    private const string BuildName =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const uint Checksum = 0x12345678;
    private static readonly Vector3[] Triangle =
    [
        new(0, 0, 0),
        new(1, 0, 0),
        new(0, 1, 0)
    ];

    [Fact]
    public void TryBind_ExactStaticPool_EmitsRealCollisionGeometry()
    {
        var collision = CreateCollision();
        var scene = CreateScene(Triangle, []);

        Assert.True(NgcCollisionBindingResolver.TryBind(collision, scene, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(NgcCollisionPositionPoolKind.StaticScene, binding.PoolKind);
        Assert.Equal(1, binding.RenderableTriangleCount);

        var document = ModelDocument.CreateNative(
            "synthetic_ngc_collision",
            ModelSourceKind.Collision,
            new NgcCollisionNativeSource(
                collision, scene, "synthetic.mdl.ngc", binding.PoolKind.ToString()));
        var added = NgcCollisionGeometryWriter.Populate(document, collision, binding);

        Assert.Equal(1, added);
        Assert.Equal(1, document.TriangleCount);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.Equal(64 / 255f, primitive.Vertices[0].Color.X, 5);
        Assert.Equal(128 / 255f, primitive.Vertices[1].Color.X, 5);
        Assert.Equal(1f, primitive.Vertices[2].Color.X);

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.Equal(1, triangles);
        Assert.Equal("glTF", Encoding.ASCII.GetString(glb, 0, 4));
    }

    [Fact]
    public void TryBind_ExactSkinPool_UsesObjectListsInSourceOrder()
    {
        var collision = CreateCollision();
        var scene = CreateScene([], Triangle);

        Assert.True(NgcCollisionBindingResolver.TryBind(collision, scene, out var binding));
        Assert.NotNull(binding);
        Assert.Equal(NgcCollisionPositionPoolKind.SkinObjectLists, binding.PoolKind);
        Assert.Equal(Triangle, binding.Positions);
    }

    [Fact]
    public void TryBind_AmbiguousStaticAndSkinPools_Rejects()
    {
        Assert.False(NgcCollisionBindingResolver.TryBind(
            CreateCollision(), CreateScene(Triangle, Triangle), out _));
    }

    [Fact]
    public void TryBind_WrongCountChecksumFaceRangeOrBounds_Rejects()
    {
        var collision = CreateCollision();

        Assert.False(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(Triangle[..2], []), out _));
        Assert.False(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(Triangle, [], checksum: Checksum + 1), out _));
        Assert.False(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(Triangle, [], hasRenderChecksum: false), out _));
        Assert.False(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(Triangle, [], renderChecksumIsUniform: false), out _));
        Assert.False(NgcCollisionBindingResolver.TryBind(
            CreateCollision(new NgcColFace(0, 0, 0, 1, 3)), CreateScene(Triangle, []), out _));

        var outside = (Vector3[])Triangle.Clone();
        outside[2] = new Vector3(0, 2, 0);
        Assert.False(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(outside, []), out _));

        var withinAuthoredGridTolerance = (Vector3[])Triangle.Clone();
        withinAuthoredGridTolerance[2] = new Vector3(0, 1.02f, 0);
        Assert.True(NgcCollisionBindingResolver.TryBind(
            collision, CreateScene(withinAuthoredGridTolerance, []), out _));
    }

    [Fact]
    public void TryBind_NonFiniteDegenerateOrAuthoredEmptyGeometry_Rejects()
    {
        var nonFinite = (Vector3[])Triangle.Clone();
        nonFinite[2] = new Vector3(float.NaN, 1, 0);
        Assert.False(NgcCollisionBindingResolver.TryBind(
            CreateCollision(), CreateScene(nonFinite, []), out _));

        var collinear = new[]
        {
            Vector3.Zero,
            Vector3.UnitX,
            Vector3.UnitX * 0.5f
        };
        Assert.False(NgcCollisionBindingResolver.TryBind(
            CreateCollision(), CreateScene(collinear, []), out _));
        Assert.False(NgcCollisionBindingResolver.TryBind(
            CreateEmptyCollision(), CreateEmptyScene(), out _));
    }

    [CorpusFact]
    public void LooseCorpus_ExactOwnerAndStructuralGates_PinAcceptedDeclinedAndEmptyFamilies()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, BuildName);
        var files = paths.FindSampleFiles(BuildName, "*.col.ngc")
            .Where(file => !IsArchiveExpandedPath(Path.GetRelativePath(root, file)))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        Assert.SkipWhen(files.Length == 0, "No canonical loose GameCube collision files found");

        var acceptedStatic = 0;
        var acceptedSkin = 0;
        var declined = 0;
        var empty = 0;
        foreach (var file in files)
        {
            var collision = NgcColFile.Parse(file);
            var resolved = NgcCollisionBindingResolver.TryResolveForCollision(
                new FileSystemAssetSource(file),
                Path.GetFileName(file),
                collision,
                out _,
                out var binding,
                out _);
            if (resolved)
            {
                Assert.NotNull(binding);
                if (binding.PoolKind == NgcCollisionPositionPoolKind.StaticScene)
                    acceptedStatic++;
                else
                    acceptedSkin++;
            }
            else if (collision.TotalFaces == 0)
            {
                empty++;
            }
            else
            {
                declined++;
            }
        }

        Assert.Equal(722, files.Length);
        Assert.Equal(23, acceptedStatic);
        Assert.Equal(187, acceptedSkin);
        Assert.Equal(495, declined);
        Assert.Equal(17, empty);
    }

    [CorpusFact]
    public void ArchiveExpandedCorpus_TypedOwnerAndStructuralGates_PinExactSubset()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var root = Path.Combine(paths.SampleBuildsDir!, BuildName);
        var files = paths.FindSampleFiles(BuildName, "*.col.ngc")
            .Where(file => IsArchiveExpandedPath(Path.GetRelativePath(root, file)))
            .OrderBy(static file => file, StringComparer.Ordinal)
            .ToArray();
        Assert.SkipWhen(files.Length == 0, "No archive-expanded GameCube collision files found");

        var accepted = 0;
        var declined = 0;
        var empty = 0;
        foreach (var file in files)
        {
            var collision = NgcColFile.Parse(file);
            if (collision.TotalFaces == 0)
            {
                empty++;
                continue;
            }

            var ownerDirectory = Path.GetDirectoryName(file)!;
            var typedCollisions = Directory.EnumerateFiles(ownerDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(NgcCollisionBindingResolver.IsCollisionName)
                .ToArray();
            var typedScenes = Directory.EnumerateFiles(ownerDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(NgcCollisionBindingResolver.IsSceneName)
                .ToArray();
            if (typedCollisions.Length != 1
                || !string.Equals(typedCollisions[0], file, StringComparison.OrdinalIgnoreCase)
                || typedScenes.Length != 1
                || !NgcSceneFile.TryParse(File.ReadAllBytes(typedScenes[0]), out var scene)
                || scene == null
                || !NgcCollisionBindingResolver.TryBind(collision, scene, out _))
            {
                declined++;
                continue;
            }

            accepted++;
        }

        Assert.Equal(680, files.Length);
        Assert.Equal(225, accepted);
        Assert.Equal(289, declined);
        Assert.Equal(166, empty);
    }

    [CorpusFact]
    public void Pigeon_StandaloneAndOptInOverlay_BothExportRealGlbs()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var collisionPath = FindCanonicalFile("anl_pigeon.col.ngc");
        Assert.SkipWhen(collisionPath is null, "Canonical anl_pigeon.col.ngc not found");
        var scenePath = Path.Combine(Path.GetDirectoryName(collisionPath)!, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(!File.Exists(scenePath), "Canonical anl_pigeon.skin.ngc not found");

        var parser = new MeshModelParser();
        var collisionDocument = parser.Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(collisionPath),
            FileName = Path.GetFileName(collisionPath),
            OutputStem = "anl_pigeon_collision",
            SourceKind = ModelSourceKind.Collision
        });
        var collisionMetadata = Assert.Single(
            collisionDocument.NativeMetadata.OfType<NgcCollisionRenderMetadata>());
        Assert.Equal("anl_pigeon.skin.ngc", collisionMetadata.CompanionName);
        Assert.Equal(nameof(NgcCollisionPositionPoolKind.SkinObjectLists),
            collisionMetadata.PositionPoolKind);
        Assert.Equal(45, collisionMetadata.TriangleCount);
        var (collisionGlb, collisionTriangles) = ModelExportService.BuildGlbBytes(collisionDocument);
        Assert.NotNull(collisionGlb);
        Assert.Equal(45, collisionTriangles);
        Assert.Equal("glTF", Encoding.ASCII.GetString(collisionGlb, 0, 4));

        var source = new FileSystemAssetSource(scenePath);
        var baseline = parser.Parse(CreateSceneRequest(source, includeCollisionOverlay: false));
        var overlaid = parser.Parse(CreateSceneRequest(source, includeCollisionOverlay: true));
        var overlay = Assert.Single(
            overlaid.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        Assert.Equal("anl_pigeon.col.ngc", overlay.CompanionName);
        Assert.Equal(45, overlay.TriangleCount);
        Assert.Equal(baseline.TriangleCount + 45, overlaid.TriangleCount);
        Assert.Contains(overlaid.Materials,
            static material => material.Name == "collision_overlay"
                               && material.AlphaMode == ModelAlphaMode.Blend);
        var (overlayGlb, overlayTriangles) = ModelExportService.BuildGlbBytes(overlaid);
        Assert.NotNull(overlayGlb);
        Assert.Equal(overlaid.TriangleCount, overlayTriangles);
        Assert.Equal("glTF", Encoding.ASCII.GetString(overlayGlb, 0, 4));
    }

    [CorpusFact]
    public void LooseResolver_MalformedAmbiguousAndWrongDirectoryOwners_FailClosed()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var collisionPath = FindCanonicalFile("anl_pigeon.col.ngc");
        Assert.SkipWhen(collisionPath is null, "Canonical anl_pigeon.col.ngc not found");
        var scenePath = Path.Combine(Path.GetDirectoryName(collisionPath)!, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(!File.Exists(scenePath), "Canonical anl_pigeon.skin.ngc not found");

        var temp = Path.Combine(Path.GetTempPath(), $"nmt-ngc-bind-{Guid.NewGuid():N}");
        var owner = Path.Combine(temp, "owner");
        var wrongOwner = Path.Combine(temp, "wrong-owner");
        try
        {
            Directory.CreateDirectory(owner);
            Directory.CreateDirectory(wrongOwner);
            var collisionCopy = Path.Combine(owner, "bound.col.ngc");
            var skinCopy = Path.Combine(owner, "bound.skin.ngc");
            File.Copy(collisionPath, collisionCopy);
            File.Copy(scenePath, skinCopy);
            var collision = NgcColFile.Parse(collisionCopy);
            var source = new FileSystemAssetSource(collisionCopy);

            Assert.True(NgcCollisionBindingResolver.TryResolveForCollision(
                source, "bound.col.ngc", collision, out _, out _, out _));
            Assert.False(NgcCollisionBindingResolver.TryResolveForCollision(
                new OpaqueForwardingSource(source),
                "bound.col.ngc",
                collision,
                out _,
                out _,
                out _));

            File.WriteAllBytes(skinCopy, [1, 2, 3, 4]);
            Assert.False(NgcCollisionBindingResolver.TryResolveForCollision(
                source, "bound.col.ngc", collision, out _, out _, out _));

            File.Copy(scenePath, skinCopy, overwrite: true);
            File.Copy(scenePath, Path.Combine(owner, "bound.mdl.ngc"));
            Assert.False(NgcCollisionBindingResolver.TryResolveForCollision(
                source, "bound.col.ngc", collision, out _, out _, out _));

            File.Delete(Path.Combine(owner, "bound.mdl.ngc"));
            File.Move(skinCopy, Path.Combine(wrongOwner, "bound.skin.ngc"));
            Assert.False(NgcCollisionBindingResolver.TryResolveForCollision(
                source, "bound.col.ngc", collision, out _, out _, out _));
            Assert.Throws<InvalidDataException>(() => new MeshModelParser().Parse(
                new MeshImportRequest
                {
                    Source = source,
                    FileName = "bound.col.ngc",
                    OutputStem = "bound",
                    SourceKind = ModelSourceKind.Collision
                }));
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [CorpusFact]
    public void SceneOverlay_MalformedExactCompanion_FailsOpenToBaselineGlb()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var collisionPath = FindCanonicalFile("anl_pigeon.col.ngc");
        Assert.SkipWhen(collisionPath is null, "Canonical anl_pigeon.col.ngc not found");
        var scenePath = Path.Combine(Path.GetDirectoryName(collisionPath)!, "anl_pigeon.skin.ngc");
        Assert.SkipWhen(!File.Exists(scenePath), "Canonical anl_pigeon.skin.ngc not found");

        var temp = Path.Combine(Path.GetTempPath(), $"nmt-ngc-overlay-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            var sceneCopy = Path.Combine(temp, "pigeon.skin.ngc");
            var collisionCopy = Path.Combine(temp, "pigeon.col.ngc");
            File.Copy(scenePath, sceneCopy);
            File.Copy(collisionPath, collisionCopy);

            var source = new FileSystemAssetSource(sceneCopy);
            var parser = new MeshModelParser();
            var baseline = parser.Parse(CreateSceneRequest(source, includeCollisionOverlay: false));
            var (baselineGlb, baselineTriangles) = ModelExportService.BuildGlbBytes(baseline);
            Assert.NotNull(baselineGlb);

            File.WriteAllBytes(collisionCopy, [1, 2, 3, 4]);
            var fallback = parser.Parse(CreateSceneRequest(source, includeCollisionOverlay: true));
            var (fallbackGlb, fallbackTriangles) = ModelExportService.BuildGlbBytes(fallback);

            Assert.NotNull(fallbackGlb);
            Assert.Equal(baselineTriangles, fallbackTriangles);
            Assert.Equal(baselineGlb, fallbackGlb);
            Assert.Empty(fallback.NativeMetadata.OfType<CollisionOverlayRenderMetadata>());
        }
        finally
        {
            if (Directory.Exists(temp))
                Directory.Delete(temp, recursive: true);
        }
    }

    [CorpusFact]
    public void HashNamedPakEntries_BindOnlyThroughTheirTypedArchiveOwner()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var archivePath = paths.FindSampleFile(BuildName, "m_zsrgaps2_gameplay.apk.ngc");
        Assert.SkipWhen(archivePath is null, "m_zsrgaps2_gameplay.apk.ngc not found");
        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        Assert.SkipWhen(backend is null, "GameCube APK did not open as a typed archive");

        var collisionEntry = Assert.Single(backend.Entries, entry =>
            NgcCollisionBindingResolver.IsCollisionName(entry.Name));
        var sceneEntry = Assert.Single(backend.Entries, entry =>
            NgcCollisionBindingResolver.IsSceneName(entry.Name));
        var collisionSource = new ArchiveAssetSource(backend, collisionEntry);
        var collision = NgcColFile.Parse(collisionSource.ReadBytes());

        Assert.True(NgcCollisionBindingResolver.TryResolveForCollision(
            collisionSource,
            collisionEntry.Name,
            collision,
            out _,
            out var binding,
            out var companionName));
        Assert.NotNull(binding);
        Assert.Equal(sceneEntry.Name, companionName);

        // Passing the same bytes through a source whose selected entry is not
        // the COL proves that basename/type coincidence cannot escape the
        // archive-table ownership gate.
        var wrongSelectedSource = new ArchiveAssetSource(backend, sceneEntry);
        Assert.False(NgcCollisionBindingResolver.TryResolveForCollision(
            wrongSelectedSource,
            collisionEntry.Name,
            collision,
            out _,
            out _,
            out _));

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = collisionSource,
            FileName = collisionEntry.Name,
            OutputStem = "m_zsrgaps2_collision",
            SourceKind = ModelSourceKind.Collision
        });
        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
        Assert.NotNull(glb);
        Assert.True(triangles > 0);
        Assert.Equal("glTF", Encoding.ASCII.GetString(glb, 0, 4));
    }

    private static MeshImportRequest CreateSceneRequest(
        FileSystemAssetSource source,
        bool includeCollisionOverlay) =>
        new()
        {
            Source = source,
            FileName = source.EntryName,
            OutputStem = "anl_pigeon",
            SourceKind = ModelSourceKind.XbxScene,
            IncludeCollisionOverlay = includeCollisionOverlay
        };

    private string? FindCanonicalFile(string fileName)
    {
        if (paths.SampleBuildsDir == null)
            return null;
        var root = Path.Combine(paths.SampleBuildsDir, BuildName);
        return paths.FindSampleFiles(BuildName, fileName)
            .FirstOrDefault(file =>
                !IsArchiveExpandedPath(Path.GetRelativePath(root, file)));
    }

    private static bool IsArchiveExpandedPath(string relativePath)
    {
        var directory = Path.GetDirectoryName(relativePath);
        return !string.IsNullOrEmpty(directory)
               && directory.Split(
                       [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                       StringSplitOptions.RemoveEmptyEntries)
                   .Any(static component =>
                       component.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
    }

    private static ParsedXbxScene CreateScene(
        Vector3[] staticPositions,
        Vector3[] skinPositions,
        uint checksum = Checksum,
        bool hasRenderChecksum = true,
        bool renderChecksumIsUniform = true) =>
        new()
        {
            Materials = [],
            Sectors = [],
            Links = [],
            NgcPositionPools = new NgcScenePositionPools
            {
                StaticPositions = staticPositions,
                Objects =
                [
                    new NgcSceneObjectPositionPool
                    {
                        ObjectIndex = 0,
                        RenderChecksum = checksum,
                        HasRenderChecksum = hasRenderChecksum,
                        RenderChecksumIsUniform = renderChecksumIsUniform,
                        SkinPositions = skinPositions
                    }
                ]
            }
        };

    private static ParsedXbxScene CreateEmptyScene() =>
        new()
        {
            Materials = [],
            Sectors = [],
            Links = [],
            NgcPositionPools = new NgcScenePositionPools
            {
                StaticPositions = [],
                Objects = []
            }
        };

    private static NgcColScene CreateCollision(NgcColFace? face = null) =>
        new()
        {
            SerializedSize = 0,
            SerializedSha256 = string.Empty,
            Version = 10,
            SuperSectorRows = 1,
            SuperSectorCols = 1,
            SceneBoundsMin = new Vector4(0, 0, 0, 1),
            SceneBoundsMax = new Vector4(1, 1, 1, 1),
            Objects =
            [
                new NgcColObject
                {
                    Checksum = Checksum,
                    Flags = 0,
                    NumVerts = 3,
                    BBoxMin = new Vector4(0, 0, 0, 1),
                    BBoxMax = new Vector4(1, 1, 1, 1),
                    CumulativeDeclaredVertexBase = 0,
                    FirstFaceIndex = 0,
                    UsesSmallFaces = false,
                    UsesFixedVertices = false,
                    BspNodeByteOffset = 0,
                    CornerIntensityByteOffset = 0,
                    Faces = [face ?? new NgcColFace(0, 0, 0, 1, 2)],
                    BspRoot = new NgcColBspNode
                    {
                        NodeByteOffset = 0,
                        Axis = 3,
                        LeafFaceIndices = [0],
                        LeafPoolElementOffset = 0
                    }
                }
            ],
            TotalVerts = 3,
            TotalFaces = 1,
            PoolElementCount = 1,
            BspNodeByteCount = 8,
            BspNodeSha256 = string.Empty,
            FaceIndexPoolSha256 = string.Empty,
            CornerIntensities = [64, 128, 255],
            CornerIntensitiesUniform = false,
            CornerIntensitiesSha256 = string.Empty,
            FaceIndicesWithinCumulativeDeclaredVertexRanges = true
        };

    private static NgcColScene CreateEmptyCollision() =>
        new()
        {
            SerializedSize = 60,
            SerializedSha256 = string.Empty,
            Version = 10,
            SuperSectorRows = 0,
            SuperSectorCols = 0,
            SceneBoundsMin = new Vector4(1000, 1000, 1000, 1),
            SceneBoundsMax = new Vector4(-1000, -1000, -1000, 1),
            Objects = [],
            TotalVerts = 0,
            TotalFaces = 0,
            PoolElementCount = 0,
            BspNodeByteCount = 0,
            BspNodeSha256 = string.Empty,
            FaceIndexPoolSha256 = string.Empty,
            CornerIntensities = [],
            CornerIntensitiesUniform = true,
            CornerIntensitiesSha256 = string.Empty,
            FaceIndicesWithinCumulativeDeclaredVertexRanges = true
        };

    private sealed class OpaqueForwardingSource(AssetSource inner) : AssetSource
    {
        public override string DisplayName => inner.DisplayName;
        public override string EntryName => inner.EntryName;
        public override byte[] ReadBytes() => inner.ReadBytes();
        public override bool CompanionExists(string nameWithExtension) =>
            inner.CompanionExists(nameWithExtension);
        public override byte[]? TryReadCompanion(string nameWithExtension) =>
            inner.TryReadCompanion(nameWithExtension);
        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) =>
            inner.TryReadCompanion(stem, extensions, subdirs);
    }
}
