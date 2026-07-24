using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshRegressionFixtureTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string Thps1ProtoBuild = "Tony Hawk's Pro Skater (1999-4-9, PSX - Prototype)";
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";

    // Baselines re-locked 2026-07-10: these fixtures silently SKIPPED for a
    // while because the pinned paths kept a stale PSX\ subfolder the sample
    // layout no longer uses. While they slept, the parser gained the
    // decomp-verified face rules (universal b7 invisible-drop, per-file v3
    // LOD vote) — hence the level face/triangle counts dropped — and the
    // dump snapshot gained per-vertex Normals and per-face BlendRate fields,
    // which changed every snapshot hash. Counts were re-verified against the
    // current parser before re-locking.
    // Hashes re-locked 2026-07-20: the snapshot gained a per-mesh NameHash
    // field (level-object placement diagnostics); all counts unchanged.
    public static TheoryData<string, ushort, bool, int, int, int, int, int, int, int, string>
        LockedCharacterFixtures =>
        new()
        {
            {
                @"Apocalypse (1998-11-17, PSX - Final)\CD\bruce.psx",
                0x0003, false, 15, 15, 329, 278, 74, 76, 474,
                "308a92946e12d977e55708c1eb251d1b369edcdde844cc54327e5d957a029c79"
            },
            {
                @"Spider-Man (2000-9-1, PSX - Final)\CD\blackcat.psx",
                0x0004, true, 18, 18, 298, 338, 74, 79, 434,
                "34573c654323826caf061b4576a5edeacb414ab87fcc02686c64a7b72dd89a6a"
            },
            {
                @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\HAWK2.PSX",
                0x0004, true, 19, 19, 402, 376, 79, 85, 573,
                "bce8a07dd7a43b4bbb03ba3be28fbf030b566bdcbfc35280f91a64eaa99a97b2"
            },
            {
                @"Spider-Man (2001-2-14, DC - Prototype)\BLACKCAT.PSX",
                0x0006, true, 18, 18, 762, 1151, 96, 103, 1303,
                "d80bcbf663c190758af184d2f9fa08d13059a13aa904e1bcd4fc0bb17459cd31"
            }
        };

    public static TheoryData<string> LockedCharacterFixturePaths =>
        new()
        {
            @"Apocalypse (1998-11-17, PSX - Final)\CD\bruce.psx",
            @"Spider-Man (2000-9-1, PSX - Final)\CD\blackcat.psx",
            @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\HAWK2.PSX",
            @"Spider-Man (2001-2-14, DC - Prototype)\BLACKCAT.PSX"
        };

    // Accepted faces sit below the raw record count since the universal b7
    // invisible-drop (collision-only faces are parsed but not emitted); when
    // these fixtures were first pinned the two counts were equal.
    public static TheoryData<string, ushort, int, int, int, int, int> LevelRegressionFixtures =>
        new()
        {
            {
                @"Spider-Man (2000-9-1, PSX - Final)\CD\l1a1_g.psx",
                0x0004, 138, 3883, 2834, 2871, 5162
            },
            {
                @"Spider-Man (2001-2-14, DC - Prototype)\L1A1_G.PSX",
                0x0006, 137, 3861, 2838, 2875, 5176
            }
        };

    [Theory]
    [MemberData(nameof(LockedCharacterFixtures))]
    public void Parse_LockedCharacterFixtures_MatchExpectedCounts(string relativePath, ushort expectedVersion,
        bool expectedHasHierarchy, int expectedObjects, int expectedMeshes, int expectedVertices, int expectedFaces,
        int expectedAttachments, int expectedStitchRefs, int expectedTriangles, string expectedSnapshotHash)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(relativePath);
        var psxFile = PsxMeshFile.Parse(filePath);
        Assert.NotNull(psxFile);

        Assert.Equal(expectedVersion, psxFile.Version);
        Assert.Equal(expectedHasHierarchy, psxFile.HasHierarchy);
        Assert.Equal(expectedObjects, psxFile.Objects.Count);
        Assert.Equal(expectedMeshes, psxFile.Meshes.Count);
        Assert.Equal(expectedVertices, psxFile.Meshes.Sum(mesh => mesh.Vertices.Count));
        Assert.Equal(expectedFaces, psxFile.Meshes.Sum(mesh => mesh.Faces.Count));
        Assert.Equal(expectedFaces, psxFile.Meshes.Sum(mesh => mesh.FaceReadInfos.Count));
        Assert.Equal(expectedAttachments, psxFile.AttachmentVertices.Count);
        Assert.Equal(expectedStitchRefs,
            psxFile.Meshes.Sum(mesh => mesh.Vertices.Count(v => PsxMeshSemantics.IsExactStitchedReference(v.Type))));
        Assert.Equal(0, psxFile.Meshes.Sum(mesh => mesh.StitchFailureCount));
        Assert.Equal(expectedTriangles,
            psxFile.Meshes.Sum(mesh => mesh.Faces.Sum(face => face.IsQuad ? 2 : 1)));
        Assert.Equal(expectedSnapshotHash, ComputeSnapshotHash(psxFile, Path.GetFileName(filePath)));
    }

    [Theory]
    [InlineData(
        @"Apocalypse (1998-11-17, PSX - Final)\CD\bruce.psx",
        PsxMeshFormatRevision.ApocalypseV3)]
    [InlineData(
        @"Spider-Man (2000-9-1, PSX - Final)\CD\blackcat.psx",
        PsxMeshFormatRevision.NeversoftV4)]
    [InlineData(
        @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\HAWK2.PSX",
        PsxMeshFormatRevision.NeversoftV4)]
    [InlineData(
        @"Spider-Man (2001-2-14, DC - Prototype)\BLACKCAT.PSX",
        PsxMeshFormatRevision.NeversoftV6)]
    public void Parse_LockedCharacterFixtures_ClassifyMeshRevision(
        string relativePath,
        PsxMeshFormatRevision expectedRevision)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(relativePath);
        var psxFile = PsxMeshFile.Parse(filePath);
        Assert.NotNull(psxFile);

        Assert.Equal(expectedRevision, psxFile.FormatRevision);
    }

    [Theory]
    [MemberData(nameof(LockedCharacterFixturePaths))]
    public void Resolve_LockedCharacterFixtures_StitchedFaceVerticesMatchAttachmentWorldPositions(string relativePath)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(relativePath);
        var psxFile = PsxMeshFile.Parse(filePath);
        Assert.NotNull(psxFile);

        foreach (var (mesh, meshIndex) in psxFile.Meshes.Select((mesh, meshIndex) => (mesh, meshIndex)))
        {
            foreach (var face in mesh.Faces)
            {
                var slotCount = face.IsQuad ? 4 : 3;
                for (var slot = 0; slot < slotCount; slot++)
                {
                    var vertexIndex = GetFaceVertexIndex(face, slot);
                    var vertex = mesh.Vertices[(int)vertexIndex];
                    if (!PsxMeshSemantics.IsExactStitchedReference(vertex.Type)) continue;

                    var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);
                    Assert.True(resolved.UsedAttachment);
                    Assert.True(resolved.AttachmentResolved);
                    Assert.True(resolved.AttachmentIndex.HasValue);

                    var attachment = psxFile.AttachmentVertexMap[resolved.AttachmentIndex.Value];
                    var expectedWorldPosition =
                        attachment.LocalPosition +
                        PsxCharacterMeshResolver.GetObjectOffset(psxFile, attachment.MeshIndex);
                    Assert.True(Vector3.Distance(resolved.WorldPosition, expectedWorldPosition) < 0.0001f,
                        $"Resolved stitched vertex did not match attachment source in {relativePath}");
                }
            }
        }
    }

    [Fact]
    public void CharacterRouting_HierarchicalModelsKeepObjectOrder()
    {
        var psxFile = new PsxMeshFile
        {
            Version = 4,
            HasHierarchy = true,
            IsSuperModel = true,
            TranslationDivisor = 1f,
            Objects =
            [
                new PsxMeshObject { RawX = 4096, MeshIndex = 2 },
                new PsxMeshObject { RawX = 8192, MeshIndex = 0 },
                new PsxMeshObject { RawX = 12288, MeshIndex = 1 }
            ],
            Meshes =
            [
                CreateSingleVertexMesh(20f),
                CreateSingleVertexMesh(30f),
                CreateSingleVertexMesh(10f)
            ],
            MeshNameHashes = [],
            TextureHashes = [],
            MeshToObjectIndex = [1, 2, 0]
        };

        Assert.True(PsxMeshSemantics.UsesCharacterObjectOrder(psxFile));
        Assert.Equal(0, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 0));
        Assert.Equal(1, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 1));
        Assert.Equal(2, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 2));
        Assert.Equal(0, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 0));
        Assert.Equal(1, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 1));
        Assert.Equal(2, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 2));

        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex: 0, vertexIndex: 0);
        Assert.Equal(0, resolved.SourceObjectIndex);
        Assert.Equal(new Vector3(21f, 0f, 0f), resolved.WorldPosition);

        var meshTwoOffset = PsxCharacterMeshResolver.GetObjectOffset(psxFile, meshIndex: 2);
        Assert.Equal(new Vector3(3f, 0f, 0f), meshTwoOffset);
    }

    [Fact]
    public void CharacterRouting_HierLevelFilesAreNotCharacterOrdered()
    {
        // Level files carry a HIER chunk for their placed animated objects
        // (THPS1-proto skdown/skvans), but HIER alone does not set the engine's
        // region IsSuper flag. They must keep item-path obj.MeshIndex routing
        // rather than positional part order.
        var psxFile = new PsxMeshFile
        {
            Version = 3,
            HasHierarchy = true,
            IsSuperModel = false,
            TranslationDivisor = 1f,
            Objects =
            [
                new PsxMeshObject { RawX = 4096, MeshIndex = 2 },
                new PsxMeshObject { RawX = 8192, MeshIndex = 0 },
                new PsxMeshObject { RawX = 12288, MeshIndex = 1 }
            ],
            Meshes =
            [
                CreateSingleVertexMesh(20f),
                CreateSingleVertexMesh(30f),
                CreateSingleVertexMesh(10f)
            ],
            MeshNameHashes = [],
            TextureHashes = [],
            MeshToObjectIndex = [1, 2, 0]
        };

        Assert.False(PsxMeshSemantics.UsesCharacterObjectOrder(psxFile));
        Assert.Equal(2, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 0));
    }

    [Theory]
    [InlineData("skvans.psx")]
    [InlineData("skvans_t.psx")]
    [InlineData("skdown.psx")]
    public void CharacterRouting_HierOnlyLevelFixturesRemainNonSuper(string fileName)
    {
        var path = paths.FindSampleFile(Thps1ProtoBuild, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);

        Assert.True(psxFile.HasHierarchy);
        Assert.False(psxFile.IsSuperModel);
        Assert.Equal(psxFile.TranslationDivisor, psxFile.ScaleDivisor);
        Assert.False(PsxMeshSemantics.UsesCharacterObjectOrder(psxFile));
    }

    [Fact]
    public void CharacterRouting_FlatSuperModelsUseMeshIndex()
    {
        var psxFile = new PsxMeshFile
        {
            Version = 3,
            HasHierarchy = false,
            TranslationDivisor = 1f,
            Objects =
            [
                new PsxMeshObject { RawX = 4096, MeshIndex = 2 },
                new PsxMeshObject { RawX = 8192, MeshIndex = 0 },
                new PsxMeshObject { RawX = 12288, MeshIndex = 1 }
            ],
            Meshes =
            [
                CreateSingleVertexMesh(20f),
                CreateSingleVertexMesh(30f),
                CreateSingleVertexMesh(10f)
            ],
            MeshNameHashes = [],
            TextureHashes = [],
            MeshToObjectIndex = [1, 2, 0]
        };

        Assert.False(PsxMeshSemantics.UsesCharacterObjectOrder(psxFile));
        Assert.Equal(2, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 0));
        Assert.Equal(0, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 1));
        Assert.Equal(1, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, 2));
        Assert.Equal(1, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 0));
        Assert.Equal(2, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 1));
        Assert.Equal(0, PsxCharacterMeshResolver.GetObjectIndex(psxFile, 2));

        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex: 2, vertexIndex: 0);
        Assert.Equal(0, resolved.SourceObjectIndex);
        Assert.Equal(new Vector3(11f, 0f, 0f), resolved.WorldPosition);
    }

    [Theory]
    [InlineData(SpiderManBuild, "spidey.psx")]
    [InlineData(Thps2ProtoBuild, "mullen.psx")]
    public void CharacterRouting_SampleSwappedMeshIndexDoesNotOverrideHierarchicalBindOrder(string buildName,
        string fileName)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);
        Assert.True(PsxMeshSemantics.UsesCharacterObjectOrder(psxFile));

        var mismatches = psxFile.Objects
            .Select((obj, objectIndex) => (obj, objectIndex))
            .Where(pair => pair.objectIndex < psxFile.Meshes.Count && pair.obj.MeshIndex != pair.objectIndex)
            .ToArray();
        Assert.NotEmpty(mismatches);

        foreach (var (obj, objectIndex) in mismatches)
        {
            Assert.Equal(objectIndex, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex));
            Assert.NotEqual(obj.MeshIndex, PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex));
            Assert.Equal(objectIndex, psxFile.MeshToObjectIndex[objectIndex]);
            Assert.Equal(objectIndex, PsxCharacterMeshResolver.GetObjectIndex(psxFile, objectIndex));
            Assert.Equal(
                PsxMeshSemantics.GetObjectOffset(psxFile, obj),
                PsxCharacterMeshResolver.GetObjectOffset(psxFile, objectIndex));
        }
    }

    [Theory]
    [InlineData(SpiderManBuild, "spidey.psx")]
    [InlineData(Thps2ProtoBuild, "mullen.psx")]
    public void CharacterBindUnits_HierarchicalObjectsKeepQ12PlacementScale(string buildName, string fileName)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);
        Assert.True(psxFile.HasHierarchy);
        Assert.Equal(psxFile.TranslationDivisor * 16f, psxFile.ScaleDivisor);

        var obj = psxFile.Objects.First(static o => o.RawX != 0 || o.RawY != 0 || o.RawZ != 0);
        var offset = PsxMeshSemantics.GetObjectOffset(psxFile, obj);
        Assert.Equal(obj.RawX / (4096f * psxFile.TranslationDivisor), offset.X, 5);
        Assert.Equal(obj.RawY / (4096f * psxFile.TranslationDivisor), offset.Y, 5);
        Assert.Equal(obj.RawZ / (4096f * psxFile.TranslationDivisor), offset.Z, 5);
    }

    [Theory]
    [InlineData(Thps1ProtoBuild, "hawk.psx", false, true)]
    [InlineData(ApocalypseBuild, "bruce.psx", false, false)]
    public void CharacterBindUnits_NoHierarchySuperScaleTracksRuntimeRevision(
        string buildName,
        string fileName,
        bool expectedHasHierarchy,
        bool expectedSuperVertexShift)
    {
        var path = paths.FindSampleFile(buildName, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);

        Assert.Equal(expectedHasHierarchy, psxFile.HasHierarchy);
        var expectedScale = expectedSuperVertexShift
            ? psxFile.TranslationDivisor * 16f
            : psxFile.TranslationDivisor;
        Assert.Equal(expectedScale, psxFile.ScaleDivisor);
    }

    [Fact]
    public void CharacterAlternates_SpideyDuplicateHandLeavesAreDetected()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "spidey.psx");
        Assert.SkipWhen(path == null, "spidey.psx not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);

        var alternates = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);

        Assert.Equal([6, 11], alternates.OrderBy(static i => i).ToArray());
    }

    [Fact]
    public void CharacterAlternates_PcSpAlt01BakedSeamHandIsStillDetected()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var path = RequireSampleBuildFile(
            @"Spider-Man (2001-9-17, PC - Final)\Setup\data\data\sp_alt01.PSX");
        var psxFile = PsxMeshFile.Parse(path);
        Assert.NotNull(psxFile);
        Assert.Equal((ushort)0x0004, psxFile.Version);

        // This alternate left hand stores its four seam vertices as ordinary
        // type-0 positions rather than the type-2 references used by the
        // otherwise equivalent final-PSX hand. The narrow two-leaf mixed
        // fallback must recognize it without relaxing larger part groups.
        Assert.DoesNotContain(psxFile.Meshes[6].Vertices,
            static vertex => PsxMeshSemantics.IsExactStitchedReference(vertex.Type));

        var alternates = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);

        Assert.Equal([6, 11], alternates.OrderBy(static i => i).ToArray());
    }

    [Fact]
    public void CharacterAlternates_Thps2HeadVariantsAreDetected()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var path = RequireSampleBuildFile(
            @"Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)\CD\sk2def.psx");
        var psxFile = PsxMeshFile.Parse(path);
        Assert.NotNull(psxFile);
        Assert.Equal((ushort)0x0004, psxFile.Version);

        // These hashes resolve to default_head, skin_head, baldblack_head and
        // slick_head. They share one placement and are interchangeable heads,
        // not four simultaneous character parts.
        Assert.Equal(0x1DFD3265u, psxFile.MeshNameHashes[9]);
        Assert.Equal(0x4D7DBE2Au, psxFile.MeshNameHashes[16]);
        Assert.Equal(0xCA5AD4F7u, psxFile.MeshNameHashes[17]);
        Assert.Equal(0x9061B849u, psxFile.MeshNameHashes[18]);

        var alternates = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);

        Assert.Equal([16, 17, 18], alternates.OrderBy(static i => i).ToArray());
    }

    [Fact]
    public void CharacterAlternates_Thps2HeadVariantsNormalizeConflictingSelections()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var path = RequireSampleBuildFile(
            @"Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)\CD\sk2def.psx");
        var source = new FileSystemAssetSource(path);
        var psxFile = PsxMeshFile.Parse(path);
        Assert.NotNull(psxFile);

        var initial = PsxVisibilityResolver.Resolve(
            source, "sk2def.psx", psxFile, overrides: null);
        Assert.Equal(3, initial.Groups.Count);
        Assert.All(initial.Groups, static group =>
        {
            Assert.False(group.IsEnabled);
            Assert.NotNull(group.ExclusiveSetId);
        });
        var exclusiveSetId = Assert.Single(initial.Groups
            .Select(static group => group.ExclusiveSetId)
            .Distinct(StringComparer.Ordinal));
        Assert.StartsWith("psx.altset.", exclusiveSetId, StringComparison.Ordinal);

        var conflictingOverrides = initial.Groups.ToDictionary(
            static group => group.Id,
            group => group.Id == initial.Groups[0].Id
                     || group.Id == initial.Groups[1].Id,
            StringComparer.Ordinal);
        var normalized = PsxVisibilityResolver.Resolve(
            source, "sk2def.psx", psxFile, conflictingOverrides);

        var selected = Assert.Single(normalized.Groups, static group => group.IsEnabled);
        Assert.Equal(initial.Groups[0].Id, selected.Id);
        Assert.Equal([9, 17, 18], normalized.HiddenObjectIndices.Order().ToArray());
    }

    [Theory]
    [InlineData("docock.psx")]
    [InlineData("superock.psx")]
    public void CharacterAlternates_OckAppendageSegmentsRemainSimultaneous(string fileName)
    {
        var path = paths.FindSampleFile(SpiderManBuild, fileName);
        Assert.SkipWhen(path == null, $"{fileName} not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);
        Assert.Equal((ushort)0x0004, psxFile.Version);

        // Ock's many same-pivot leaves are simultaneous multi-joint appendage
        // segments. They are not a two-leaf pose pair or a four-head variant
        // set, even where individual bounds overlap substantially.
        var alternates = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);

        Assert.Empty(alternates);
    }

    [Fact]
    public void CharacterAlternates_ControlSharedPivotPartsAreAllEmitted()
    {
        var path = paths.FindSampleFile(SpiderManBuild, "control.psx");
        Assert.SkipWhen(path == null, "control.psx not found in sample builds");

        var psxFile = PsxMeshFile.Parse(path!);
        Assert.NotNull(psxFile);

        // The left/right grip pair (objects 2/3) and left/right thumbstick
        // pair (objects 4/5) share parent pivots, but occupy disjoint geometry.
        // They are simultaneous controller parts, not alternate hand poses.
        var alternates = PsxMeshSemantics.FindAlternateLeafObjectIndices(psxFile);
        Assert.Empty(alternates);

        var document = new ModelDocument { Name = "control" };
        PsxGeometryWriter.PopulatePsx(document, psxFile, null);

        Assert.Equal(1_560, document.TriangleCount);
    }

    [Fact]
    public void Parse_XboxHawk2_RemainsVersion4RegressionFixture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(
            @"Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)\hawk2.PSX");
        var psxFile = PsxMeshFile.Parse(filePath);
        Assert.NotNull(psxFile);

        Assert.Equal((ushort)0x0004, psxFile.Version);
        Assert.Equal(19, psxFile.Objects.Count);
        Assert.Equal(19, psxFile.Meshes.Count);
        Assert.Equal(402, psxFile.Meshes.Sum(mesh => mesh.Vertices.Count));
        Assert.Equal(376, psxFile.Meshes.Sum(mesh => mesh.Faces.Count));
        Assert.Equal(0, psxFile.Meshes.Sum(mesh => mesh.StitchFailureCount));
    }

    [Theory]
    [MemberData(nameof(LevelRegressionFixtures))]
    public void Parse_LevelRegressionFixtures_KeepFaceAndTriangleCounts(string relativePath, ushort expectedVersion,
        int expectedObjects, int expectedVertices, int expectedFaces, int expectedRawFaces, int expectedTriangles)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(relativePath);
        var psxFile = PsxMeshFile.Parse(filePath);
        Assert.NotNull(psxFile);

        Assert.Equal(expectedVersion, psxFile.Version);
        Assert.False(psxFile.HasHierarchy);
        Assert.Equal(expectedObjects, psxFile.Objects.Count);
        Assert.Equal(expectedVertices, psxFile.Meshes.Sum(mesh => mesh.Vertices.Count));
        Assert.Equal(expectedFaces, psxFile.Meshes.Sum(mesh => mesh.Faces.Count));
        Assert.Equal(expectedRawFaces, psxFile.Meshes.Sum(mesh => mesh.FaceReadInfos.Count));
        Assert.Equal(expectedTriangles,
            psxFile.Meshes.Sum(mesh => mesh.Faces.Sum(face => face.IsQuad ? 2 : 1)));
        Assert.Empty(psxFile.AttachmentVertices);
        Assert.Equal(0, psxFile.Meshes.Sum(mesh => mesh.StitchFailureCount));
    }

    private string RequireSampleBuildFile(string relativePath)
    {
        var filePath = Path.Combine(paths.SampleBuildsDir!, relativePath);
        if (!File.Exists(filePath))
        {
            // Sample layouts have shifted over time (fixtures were pinned
            // against a PSX\ subfolder; current builds ship files under CD\
            // or at the build root). Resolve by build + filename so the
            // locked fixtures don't silently skip — a wrong pick cannot pass
            // unnoticed because every fixture asserts counts and a snapshot
            // hash.
            var normalized = relativePath.Replace('\\', '/');
            var buildName = normalized[..normalized.IndexOf('/')];
            var fallback = paths.FindSampleFile(buildName, Path.GetFileName(normalized));
            if (fallback != null)
                return fallback;
        }

        Assert.SkipWhen(!File.Exists(filePath), $"Fixture not found: {relativePath}");
        return filePath;
    }

    private static string ComputeSnapshotHash(PsxMeshFile psxFile, string fileName)
    {
        var snapshot = PsxMeshDumpSnapshotBuilder.Build(psxFile, fileName);
        var json = PsxMeshDumpSnapshotBuilder.Serialize(snapshot);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    private static uint GetFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static PsxMesh CreateSingleVertexMesh(float x)
    {
        return new PsxMesh
        {
            Vertices =
            [
                new PsxVertex
                {
                    X = x,
                    Y = 0f,
                    Z = 0f
                }
            ],
            Normals = [],
            Faces = [],
            VertexCount = 1
        };
    }
}
