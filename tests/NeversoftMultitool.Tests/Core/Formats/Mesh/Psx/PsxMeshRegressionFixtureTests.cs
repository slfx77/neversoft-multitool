using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshRegressionFixtureTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string Thps1ProtoBuild = "Tony Hawk's Pro Skater (1999-4-4, PSX - Prototype)";
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";
    private const string Thps2ProtoBuild = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";

    public static TheoryData<string, ushort, bool, int, int, int, int, int, int, int, string>
        LockedCharacterFixtures =>
        new()
        {
            {
                @"Apocalypse (1998-11-17, PSX - Final)\PSX\bruce.psx",
                0x0003, false, 15, 15, 329, 278, 74, 76, 474,
                "3f5d25b4dd5fe299c6d4e985a032e71dd510fc922a2f93b7027bd192f762bd86"
            },
            {
                @"Spider-Man (2000-9-1, PSX - Final)\PSX\blackcat.psx",
                0x0004, true, 18, 18, 298, 338, 74, 79, 434,
                "0ba39624b84aa32d44c2d31702f62044a8cd9f96db68611779f7f37af1c4050d"
            },
            {
                @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\PSX\HAWK2.PSX",
                0x0004, true, 19, 19, 402, 376, 79, 85, 573,
                "85462258804c6718b6f6ca9d84e5b109d54f7d5c16a97fe8858524d3fc0cc562"
            },
            {
                @"Spider-Man (2001-2-14, DC - Prototype)\PSX\BLACKCAT.PSX",
                0x0006, true, 18, 18, 762, 1151, 96, 103, 1303,
                "4f50cb4340a2efeae86a91846c3b6fdee58a469faa2dbee89589463b7e8502b0"
            }
        };

    public static TheoryData<string> LockedCharacterFixturePaths =>
        new()
        {
            @"Apocalypse (1998-11-17, PSX - Final)\PSX\bruce.psx",
            @"Spider-Man (2000-9-1, PSX - Final)\PSX\blackcat.psx",
            @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\PSX\HAWK2.PSX",
            @"Spider-Man (2001-2-14, DC - Prototype)\PSX\BLACKCAT.PSX"
        };

    public static TheoryData<string, ushort, int, int, int, int> LevelRegressionFixtures =>
        new()
        {
            {
                @"Spider-Man (2000-9-1, PSX - Final)\PSX\l1a1_g.psx",
                0x0004, 138, 3883, 2871, 5236
            },
            {
                @"Spider-Man (2001-2-14, DC - Prototype)\PSX\L1A1_G.PSX",
                0x0006, 137, 3861, 2875, 5250
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
        @"Apocalypse (1998-11-17, PSX - Final)\PSX\bruce.psx",
        PsxMeshFormatRevision.ApocalypseV3)]
    [InlineData(
        @"Spider-Man (2000-9-1, PSX - Final)\PSX\blackcat.psx",
        PsxMeshFormatRevision.NeversoftV4)]
    [InlineData(
        @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\PSX\HAWK2.PSX",
        PsxMeshFormatRevision.NeversoftV4)]
    [InlineData(
        @"Spider-Man (2001-2-14, DC - Prototype)\PSX\BLACKCAT.PSX",
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
    public void Parse_XboxHawk2_RemainsVersion4RegressionFixture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var filePath = RequireSampleBuildFile(
            @"Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)\PSX\HAWK2.PSX");
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
        int expectedObjects, int expectedVertices, int expectedFaces, int expectedTriangles)
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
        Assert.Equal(expectedFaces, psxFile.Meshes.Sum(mesh => mesh.FaceReadInfos.Count));
        Assert.Equal(expectedTriangles,
            psxFile.Meshes.Sum(mesh => mesh.Faces.Sum(face => face.IsQuad ? 2 : 1)));
        Assert.Empty(psxFile.AttachmentVertices);
        Assert.Equal(0, psxFile.Meshes.Sum(mesh => mesh.StitchFailureCount));
    }

    private string RequireSampleBuildFile(string relativePath)
    {
        var filePath = Path.Combine(paths.SampleBuildsDir!, relativePath);
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
