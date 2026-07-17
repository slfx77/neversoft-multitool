using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxAlternateLeafCorpusRegressionTests(TestPaths paths)
{
    private const string SpiderManBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string SpiderManPrototypeBuild = "Spider-Man (2000-2-18, PSX - Prototype)";
    private const string EnterElectroBuild = "Spider-Man 2 - Enter Electro (2001-8-15, PSX - Final)";
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private static readonly int[] MercenaryGunObjects = [12, 13];
    private static readonly int[] LowDetailHeadObjects = [15, 16];

    [Fact]
    public void EnterElectroMercenaryGunPair_FromCdWad_IsAnAlternateVariant()
    {
        var file = ReadArchiveMesh(EnterElectroBuild, "CD.WAD", "mercthug.psx");
        if (file == null)
            return;

        Assert.Equal(0xBB7612A9u, file.MeshNameHashes[12]); // thug_gun_01
        Assert.Equal(25, file.Meshes[12].Vertices.Count);
        Assert.Equal(25, file.Meshes[12].Faces.Count);
        Assert.Equal(25, file.Meshes[13].Vertices.Count);
        Assert.Equal(25, file.Meshes[13].Faces.Count);
        Assert.All(MercenaryGunObjects, objectIndex =>
        {
            var mesh = file.Meshes[PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex)];
            Assert.DoesNotContain(mesh.Vertices,
                static vertex => PsxMeshSemantics.IsExactStitchedReference(vertex.Type));
            Assert.All(mesh.Faces, static face => Assert.True(face.IsTextured));
        });

        Assert.Equal(
            [13],
            PsxMeshSemantics.FindAlternateLeafObjectIndices(file)
                .OrderBy(static index => index)
                .ToArray());
    }

    [Fact]
    public void Thps2LowDetailHeadPair_FromCdWad_IsAnAlternateVariant()
    {
        var file = ReadArchiveMesh(Thps2Build, "CD.WAD", "sk2def_l.psx");
        if (file == null)
            return;

        Assert.Equal(0x4D7DBE2Au, file.MeshNameHashes[15]); // skin_head
        Assert.Equal(0xCA5AD4F7u, file.MeshNameHashes[16]); // baldblack_head
        Assert.All(LowDetailHeadObjects, objectIndex =>
        {
            var mesh = file.Meshes[PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex)];
            Assert.DoesNotContain(mesh.Vertices,
                static vertex => PsxMeshSemantics.IsExactStitchedReference(vertex.Type));
            Assert.All(mesh.Faces, static face => Assert.True(face.IsTextured));
        });

        Assert.Equal(
            [16],
            PsxMeshSemantics.FindAlternateLeafObjectIndices(file)
                .OrderBy(static index => index)
                .ToArray());
    }

    [Theory]
    [InlineData(EnterElectroBuild, "CD.WAD", "daazeve.psx")]
    [InlineData(EnterElectroBuild, "CD.WAD", "gelectro.psx")]
    [InlineData(EnterElectroBuild, "CD.WAD", "xavier.psx")]
    [InlineData(SpiderManBuild, "CD.WAD", "softspot.psx")]
    [InlineData(Thps2Build, "BMXCD.WAD", "hoff.psx")]
    public void DistinctSharedPivotParts_FromArchives_RemainSimultaneous(
        string buildName,
        string archiveName,
        string entryName)
    {
        var file = ReadArchiveMesh(buildName, archiveName, entryName);
        if (file == null)
            return;

        Assert.Empty(PsxMeshSemantics.FindAlternateLeafObjectIndices(file));
    }

    [Fact]
    public void PrototypeLizardSevenBoxEditorRig_FromCdWad_IsNotASplineAppendage()
    {
        var file = ReadArchiveMesh(SpiderManPrototypeBuild, "CD.WAD", "lizard.psx");
        if (file == null)
            return;

        Assert.Equal(24, file.Objects.Count);
        Assert.All(Enumerable.Range(17, 7), objectIndex =>
        {
            var mesh = file.Meshes[PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex)];
            Assert.Equal(8, mesh.Vertices.Count);
            Assert.Equal(6, mesh.Faces.Count);
            Assert.All(mesh.Faces, static face =>
            {
                Assert.True(face.IsQuad);
                Assert.False(face.IsTextured);
            });
        });
        Assert.DoesNotContain(0xAF6C87FEu, file.MeshNameHashes);

        Assert.Empty(PsxSplineAppendageGeometry.FindControllerChains(file));
    }

    private PsxMeshFile? ReadArchiveMesh(
        string buildName,
        string archiveName,
        string entryName)
    {
        var archivePath = paths.FindSampleFile(buildName, archiveName);
        Assert.SkipWhen(archivePath == null, $"{archiveName} sample archive not available");
        if (archivePath == null)
            return null;

        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        Assert.NotNull(backend);
        if (backend == null)
            return null;

        try
        {
            var entry = backend.FindEntry(entryName);
            Assert.NotNull(entry);
            return entry == null
                ? null
                : PsxMeshFile.Parse(new ArchiveAssetSource(backend, entry).ReadBytes());
        }
        finally
        {
            backend.FileSystem.Dispose();
        }
    }
}
