using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the flag-0x2000 second pass added 2026-08-03: after a successful
///     TRG BackgroundCreate join, bank objects carrying the decomp-verified
///     "distant backdrop" item flag that are parked with the joined sky
///     objects are claimed as sky too. l1a1 is the only level this changes
///     corpus-wide (final + Apr-29 proto): its TRG registers just two of the
///     shared daytime-NY three-layer set, so the 264-triangle skyline
///     (0x62D17F19) previously exported as a stray world-placed mesh 6,400
///     units from the sky anchor.
/// </summary>
public sealed class PsxSkyDomeClassifierTests(TestPaths paths)
{
    private const string FinalBuild = "Spider-Man (2000-9-1, PSX - Final)";

    [Fact]
    public void L1a1_Final_ClaimsTheUnregisteredSkylineAsSky()
    {
        var path = paths.FindSampleFile(FinalBuild, "l1a1_g.psx");
        Assert.SkipWhen(path == null, "Spider-Man final l1a1_g.psx sample not available");

        var document = ParseDocument(path!);

        // Pinned 2026-08-03 (flag-0x2000 sky claim): sky meshes 2 -> 3,
        // world-scene meshes 143 -> 142 (the 264-tri skyline moved from a
        // world placement to the camera-locked sky anchor), triangles
        // unchanged. Total mesh counts are deliberately not pinned here —
        // overlay/transparency splits move them for unrelated reasons.
        Assert.Equal(7_398, document.TriangleCount);
        var skyMeshes = document.Meshes
            .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(3, skyMeshes.Length);
        Assert.Contains(skyMeshes, static mesh => mesh.Name == "sky__mesh_00000003");

        // The skyline renders ONLY as sky — its 264 triangles must not remain
        // world-placed anywhere else (the pre-fix stray mesh_00000003 node).
        var skylineCopies = document.Meshes
            .Where(static mesh =>
                mesh.Primitives.Sum(static primitive => primitive.TriangleCount) == 264)
            .ToArray();
        var skyline = Assert.Single(skylineCopies);
        Assert.Equal("sky__mesh_00000003", skyline.Name);
    }

    [Theory]
    // Guard: the second pass must not move any other level. l2a2 owns a
    // complete two-layer set (both joined, no flag-0x2000 stragglers); lda1
    // registers all three layers itself, so the flagged skyline is already
    // claimed by the join.
    [InlineData("l2a2_g.psx", 2, 3_992)]
    [InlineData("lda1_g.psx", 3, 3_661)]
    public void OtherLevels_KeepTheirSkyAndTriangleCounts(
        string fileName,
        int expectedSkyMeshes,
        int expectedTriangles)
    {
        var path = paths.FindSampleFile(FinalBuild, fileName);
        Assert.SkipWhen(path == null, $"Spider-Man final {fileName} sample not available");

        var document = ParseDocument(path!);

        Assert.Equal(expectedTriangles, document.TriangleCount);
        Assert.Equal(expectedSkyMeshes, document.Meshes.Count(static mesh =>
            mesh.Name.StartsWith("sky__", StringComparison.Ordinal)));
    }

    private static ModelDocument ParseDocument(string path)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(path),
            FileName = Path.GetFileName(path),
            OutputStem = Path.GetFileNameWithoutExtension(path),
            SourceKind = ModelSourceKind.Psx,
            IncludeLevelObjects = true
        });
    }
}
