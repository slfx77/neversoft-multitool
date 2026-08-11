using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxSplineClawLocatorTests(TestPaths paths)
{
    private const string FinalBuild = "Spider-Man (2000-9-1, PSX - Final)";
    private const string PrototypeBuild = "Spider-Man (2000-2-18, PSX - Prototype)";

    [Fact]
    public void Locate_RetailDocock_PrefersUniqueSelfContainedStructuralKit()
    {
        var docockPath = paths.FindSampleFile(FinalBuild, "docock.psx");
        Assert.SkipWhen(docockPath == null, "Spider-Man final docock.psx not available");

        var claw = PsxSplineClawLocator.Locate(new FileSystemAssetSource(docockPath!));

        Assert.NotNull(claw);
        Assert.Single(claw!.File.Objects);
        var mesh = Assert.Single(claw.File.Meshes);
        Assert.Equal(0, claw.ObjectIndex);
        Assert.Equal(0, claw.MeshIndex);
        Assert.Equal(40, mesh.Faces.Sum(static face => face.IsQuad ? 2 : 1));
        Assert.NotNull(claw.TextureProvider(mesh.Faces[0].TextureHash));
    }

    [Fact]
    public void Locate_PrototypeDocock_CarriesActualBankObjectAndMeshIndices()
    {
        var archivePath = paths.FindSampleFile(PrototypeBuild, "CD.WAD");
        Assert.SkipWhen(archivePath == null, "Spider-Man 2/18 CD.WAD not available");
        var backend = ArchiveAssetBackend.TryOpen(archivePath!);
        Assert.NotNull(backend);
        try
        {
            var entry = backend!.FindEntry("docock.psx");
            Assert.NotNull(entry);
            var claw = PsxSplineClawLocator.Locate(
                new ArchiveAssetSource(backend, entry!));

            Assert.NotNull(claw);
            Assert.True(claw!.File.Objects.Count > 1);
            Assert.True(claw.File.Meshes.Count > 1);
            Assert.Equal(1, claw.MeshIndex);
            Assert.InRange(claw.ObjectIndex, 0, claw.File.Objects.Count - 1);
            Assert.Equal(claw.MeshIndex, claw.File.Objects[claw.ObjectIndex].MeshIndex);
            var mesh = claw.File.Meshes[claw.MeshIndex];
            Assert.Equal(22, mesh.Vertices.Count);
            Assert.Equal(40, mesh.Faces.Sum(static face => face.IsQuad ? 2 : 1));
        }
        finally
        {
            backend?.FileSystem.Dispose();
        }
    }

    [Fact]
    public void Locate_RenamedStandaloneKit_IsFilenameIndependentAndCached()
    {
        var docockPath = paths.FindSampleFile(FinalBuild, "docock.psx");
        var clawPath = paths.FindSampleFile(FinalBuild, "claw.psx");
        Assert.SkipWhen(
            docockPath == null || clawPath == null,
            "Spider-Man final loose docock/claw samples not available");

        using var directory = new TempDirectory("nmt-appendage-");
        var characterPath = Path.Combine(directory.Path, "renamed_character.psx");
        File.Copy(docockPath!, characterPath);
        File.Copy(clawPath!, Path.Combine(directory.Path, "runtime_payload.psx"));
        var source = new FileSystemAssetSource(characterPath);

        var first = PsxSplineClawLocator.Locate(source);
        var second = PsxSplineClawLocator.Locate(source);

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Locate_FileSystemScopeMutation_InvalidatesMissingAndPositiveCacheEntries()
    {
        var docockPath = paths.FindSampleFile(FinalBuild, "docock.psx");
        var clawPath = paths.FindSampleFile(FinalBuild, "claw.psx");
        Assert.SkipWhen(
            docockPath == null || clawPath == null,
            "Spider-Man final loose docock/claw samples not available");

        using var directory = new TempDirectory("nmt-appendage-mutation-");
        var characterPath = Path.Combine(directory.Path, "character.psx");
        File.Copy(docockPath!, characterPath);
        var source = new FileSystemAssetSource(characterPath);

        Assert.Null(PsxSplineClawLocator.Locate(source));

        File.Copy(clawPath!, Path.Combine(directory.Path, "payload_a.psx"));
        Assert.NotNull(PsxSplineClawLocator.Locate(source));

        File.Copy(clawPath!, Path.Combine(directory.Path, "payload_b.psx"));
        Assert.Null(PsxSplineClawLocator.Locate(source));
    }

    [Fact]
    public void Locate_TwoRenamedStandaloneKits_IsAmbiguous()
    {
        var docockPath = paths.FindSampleFile(FinalBuild, "docock.psx");
        var clawPath = paths.FindSampleFile(FinalBuild, "claw.psx");
        Assert.SkipWhen(
            docockPath == null || clawPath == null,
            "Spider-Man final loose docock/claw samples not available");

        using var directory = new TempDirectory("nmt-appendage-ambiguous-");
        var characterPath = Path.Combine(directory.Path, "character.psx");
        File.Copy(docockPath!, characterPath);
        File.Copy(clawPath!, Path.Combine(directory.Path, "payload_a.psx"));
        File.Copy(clawPath!, Path.Combine(directory.Path, "payload_b.psx"));

        Assert.Null(PsxSplineClawLocator.Locate(
            new FileSystemAssetSource(characterPath)));
    }

    private sealed class TempDirectory : IDisposable
    {
        internal TempDirectory(string prefix)
        {
            Path = Directory.CreateTempSubdirectory(prefix).FullName;
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
