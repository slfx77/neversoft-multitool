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

    [CorpusFact]
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

    [CorpusTheory]
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

    /// <summary>
    ///     Sky layers paint in TRG BackgroundCreate registration order: the
    ///     first 0xAB is furthest back, the last is in front (the engine's two
    ///     reversals cancel — see <see cref="PsxSkyDomeClassifier.Result" />).
    ///
    ///     l2a1 registers its DOME first and its SKYLINE second, so the skyline
    ///     belongs in front. The bank stores them the other way round (obj2 =
    ///     skyline, obj3 = dome), which is why traversal order painted the dome
    ///     last and left the buildings' bottoms poking out below its skirt.
    ///     Identifying the layers by triangle count keeps this test independent
    ///     of the rank it is checking.
    /// </summary>
    [CorpusFact]
    public void L2a1_SkylineRegistersAfterTheDome_SoItPaintsInFront()
    {
        var path = paths.FindSampleFile(FinalBuild, "l2a1_g.psx");
        Assert.SkipWhen(path == null, "Spider-Man final l2a1_g.psx sample not available");

        var document = ParseDocument(path!);

        var layers = document.Meshes
            .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
            .Select(static mesh => (
                Triangles: mesh.Primitives.Sum(static primitive => primitive.TriangleCount),
                Layer: mesh.Primitives
                    .SelectMany(static primitive => primitive.NativeMetadata)
                    .OfType<PsxSkyRenderMetadata>()
                    .Select(static metadata => metadata.LayerIndex)
                    .First()))
            .ToArray();

        Assert.Equal(2, layers.Length);
        var dome = Assert.Single(layers, static layer => layer.Triangles == 390);
        var skyline = Assert.Single(layers, static layer => layer.Triangles == 108);
        Assert.Equal(0, dome.Layer);
        Assert.Equal(1, skyline.Layer);
    }

    /// <summary>
    ///     l1a1's third layer has no 0xAB record at all (the shipped authoring
    ///     omission the flag-0x2000 pass covers), so its rank is not measured —
    ///     it is placed in FRONT of every registered layer because the engine
    ///     draws it as ordinary world geometry, and the background pass runs
    ///     before the world pass.
    /// </summary>
    [CorpusFact]
    public void L1a1_ClaimedBackdrop_PaintsInFrontOfTheRegisteredLayers()
    {
        var path = paths.FindSampleFile(FinalBuild, "l1a1_g.psx");
        Assert.SkipWhen(path == null, "Spider-Man final l1a1_g.psx sample not available");

        var document = ParseDocument(path!);

        var byName = document.Meshes
            .Where(static mesh => mesh.Name.StartsWith("sky__", StringComparison.Ordinal))
            .ToDictionary(
                static mesh => mesh.Name,
                static mesh => mesh.Primitives
                    .SelectMany(static primitive => primitive.NativeMetadata)
                    .OfType<PsxSkyRenderMetadata>()
                    .Select(static metadata => metadata.LayerIndex)
                    .First(),
                StringComparer.Ordinal);

        // sky__mesh_00000003 is the 264-triangle skyline claimed by the
        // flag-0x2000 pass; the other two carry real registrations.
        Assert.Equal(3, byName.Count);
        Assert.Equal(2, byName["sky__mesh_00000003"]);
        Assert.True(
            byName["sky__mesh_00000003"] > byName["sky__mesh_00000004"],
            "the claimed backdrop must paint in front of every registered layer");
        Assert.True(byName["sky__mesh_00000004"] > byName["sky__mesh_00000005"]);
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
