using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshRegressionFixtureTests(TestPaths paths)
{
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
}