using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ArchiveMeshRigSelectionTests(TestPaths paths)
{
    private const string Thug2XboxBuild =
        "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    [Fact]
    public void RealThug2SkeletonArchive_PigeonMatchesLooseRigAndPreparedGlbAfterDisposal()
    {
        var archivePath = paths.FindSampleFile(Thug2XboxBuild, "skeletons.prx");
        var looseSkeletonPath = paths.FindSampleFile(Thug2XboxBuild, "anl_pigeon.ske.xbx");
        var skinPath = paths.FindSampleFile(Thug2XboxBuild, "Anl_Pigeon.skin.xbx");
        Assert.SkipWhen(
            archivePath == null || looseSkeletonPath == null || skinPath == null,
            "THUG2 Xbox skeletons.prx or pigeon skin/skeleton fixtures are unavailable");

        Ps2Skeleton archiveSkeleton;
        byte[] archiveSkeletonBytes;
        string displayName;
        using (var catalog = ArchiveSourceRigCatalog.Open(
                   archivePath!,
                   SkeletonAssetLoader.IsSkeletonFileName,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(58, catalog.Candidates.Count);
            var candidate = Assert.Single(catalog.Candidates, static item =>
                item.Source.Entry.FullName.Equals(
                    "skeletons/anl_pigeon.ske.xbx",
                    StringComparison.OrdinalIgnoreCase));

            displayName = candidate.DisplayName;
            archiveSkeletonBytes = candidate.Source.ReadBytes();
            archiveSkeleton = SkeletonAssetLoader.Load(candidate.Source);
        }

        Assert.EndsWith(
            "skeletons.prx::skeletons/anl_pigeon.ske.xbx",
            displayName,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(File.ReadAllBytes(looseSkeletonPath!), archiveSkeletonBytes);
        Assert.Equal(4, archiveSkeleton.Bones.Length);

        var direct = ParsePigeon(skinPath!, looseSkeletonPath, preparedSkeleton: null);
        var fromArchive = ParsePigeon(
            skinPath!, skeletonPath: null, preparedSkeleton: archiveSkeleton);
        var exporter = new GltfModelExporter();
        var (directGlb, directTriangles) = exporter.BuildGlbBytes(direct);
        var (archiveGlb, archiveTriangles) = exporter.BuildGlbBytes(fromArchive);

        Assert.Equal(45, directTriangles);
        Assert.Equal(directTriangles, archiveTriangles);
        Assert.Equal(directGlb, archiveGlb);

        var glb = ModelRoot.ReadGLB(new MemoryStream(archiveGlb!));
        Assert.Equal(4, Assert.Single(glb.LogicalSkins).JointsCount);
        Assert.Equal(46, glb.LogicalMeshes
            .SelectMany(static mesh => mesh.Primitives)
            .Sum(static primitive => primitive.GetVertexAccessor("POSITION").Count));
    }

    private static ModelDocument ParsePigeon(
        string skinPath,
        string? skeletonPath,
        Ps2Skeleton? preparedSkeleton)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(skinPath),
            FileName = Path.GetFileName(skinPath),
            OutputStem = "Anl_Pigeon",
            SourceKind = ModelSourceKind.XbxScene,
            SkeletonPath = skeletonPath,
            PreparedSkeleton = preparedSkeleton
        });
    }
}
