using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SpiderManPsxArchiveMeshRegressionTests(TestPaths paths)
{
    private const string BuildName = "Spider-Man (2000-9-1, PSX - Final)";
    private const string PcBuildName = "Spider-Man (2001-9-17, PC - Final)";

    [Theory]
    [InlineData("l1a3_g.psx", 5036, 94)]
    [InlineData("l2a1_g.psx", 8470, 98)]
    public void LevelMesh_FromCdWad_ResolvesCompanionTextures(
        string entryName,
        int expectedTriangles,
        int expectedTextures)
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry(entryName);
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx
        });

        Assert.Equal(expectedTriangles, document.TriangleCount);
        Assert.Equal(expectedTextures, document.Textures.Count);
        Assert.DoesNotContain(document.Textures, texture => texture.PngBytes is not { Length: > 0 });
    }

    [Fact]
    public void L2A1_FromCdWad_ClassifiesOpaqueOrderingTableOverlays()
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("l2a1_g.psx");
        Assert.NotNull(entry);

        var file = PsxMeshFile.Parse(backend.ReadEntryBytes(entry!));
        Assert.NotNull(file);
        var overlays = PsxCoplanarOverlayDetector.Find(file!);

        Assert.Equal(75, overlays.Count);
        Assert.Contains(new PsxFaceInstanceKey(47, 0), overlays);
    }

    [Fact]
    public void L1A3_FromCdWad_PreservesAuthoredFlatSupportBeamSides()
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("l1a3_g.psx");
        Assert.NotNull(entry);

        var file = PsxMeshFile.Parse(backend.ReadEntryBytes(entry!));
        Assert.NotNull(file);

        // These four metal supports are the exact structures reported as
        // untextured. The PSX source authors only the end cap with a texture;
        // its four long sides are 0x4000 flat-color primitives with no hash or
        // UV payload. Preserve that authored state and its direct 8-bit color
        // instead of inventing texture coordinates from the PC port.
        foreach (var meshIndex in new[] { 60, 64, 168, 172 })
        {
            var faces = file!.Meshes[meshIndex].Faces;
            Assert.Equal(5, faces.Count);
            Assert.Equal((ushort)0x4003, faces[0].Flags);
            Assert.True(faces[0].IsTextured);
            Assert.Equal(0x18221C83u, faces[0].TextureHash);

            foreach (var side in faces.Skip(1))
            {
                Assert.Equal((ushort)0x4000, side.Flags);
                Assert.False(side.IsTextured);
                Assert.Equal(0u, side.TextureHash);
                Assert.All(side.TextureCoordinates,
                    static coordinate => Assert.Equal(default, coordinate));

                var color = PsxGeometryHelpers.ComputePsxFaceColors(
                    file.Version, side, file.GouraudPalette).C0;
                Assert.Equal(side.R / 255f, color.X);
                Assert.Equal(side.G / 255f, color.Y);
                Assert.Equal(side.B / 255f, color.Z);
            }
        }

        var document = new ModelDocument { Name = "l1a3_g" };
        PsxGeometryWriter.PopulatePsx(document, file!, null);
        var untextured = Assert.Single(document.Materials, material => material.Name == "untextured");
        Assert.Equal(1f, untextured.BaseColor.X);
        Assert.Equal(1f, untextured.BaseColor.Y);
        Assert.Equal(1f, untextured.BaseColor.Z);
        Assert.Equal(1f, untextured.BaseColor.W);
    }

    [Fact]
    public void PcL1A1_FromDataPkr_UsesRgbPaletteForV6GouraudColors()
    {
        var pkrPath = paths.FindSampleFile(PcBuildName, "data.pkr");
        Assert.SkipWhen(pkrPath == null, "Spider-Man PC data.pkr sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pkrPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("L1A1_G.psx");
        Assert.NotNull(entry);

        var file = PsxMeshFile.Parse(backend.ReadEntryBytes(entry!));
        Assert.NotNull(file);
        Assert.Equal((ushort)0x06, file!.Version);
        Assert.Equal(256, file.GouraudPalette?.Length);

        // This exact face uses two strongly colored RGBs entries. Treating its
        // bytes as direct intensities turns both colors gray and loses the PC
        // port's authored baked lighting.
        var face = file.Meshes[23].Faces[0];
        Assert.True(face.IsGouraud);
        Assert.Equal((byte)158, face.R);
        Assert.Equal((byte)188, face.B);
        var colors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, face, file.GouraudPalette);

        Assert.Equal(file.GouraudPalette![158], colors.C0);
        Assert.Equal(file.GouraudPalette[188], colors.C2);
        Assert.NotEqual(colors.C0.X, colors.C0.Y);
        Assert.NotEqual(colors.C2.Y, colors.C2.Z);

        // Flat v6 colors are direct 8-bit diffuse values. PS1-style /128
        // modulation would clamp this common yellow tuple to white.
        var flatFace = file.Meshes[28].Faces[0];
        Assert.False(flatFace.IsGouraud);
        Assert.True(flatFace.IsTextured);
        Assert.Equal((byte)255, flatFace.R);
        Assert.Equal((byte)253, flatFace.G);
        Assert.Equal((byte)175, flatFace.B);
        var flatColor = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, flatFace, file.GouraudPalette).C0;
        Assert.Equal(1f, flatColor.X);
        Assert.Equal(253f / 255f, flatColor.Y);
        Assert.Equal(175f / 255f, flatColor.Z);
    }
}
