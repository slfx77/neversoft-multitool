using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using NeversoftMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SpiderManPsxArchiveMeshRegressionTests(TestPaths paths)
{
    private const string BuildName = "Spider-Man (2000-9-1, PSX - Final)";
    private const string PcBuildName = "Spider-Man (2001-9-17, PC - Final)";

    [Theory]
    [InlineData("l1a3_g.psx", 5036, 107)]
    [InlineData("l2a1_g.psx", 8470, 101)]
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
        var parsed = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(parsed);
        var usedTextureHashes = parsed.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.IsTextured && face.TextureHash != 0)
            .Select(static face => face.TextureHash)
            .ToHashSet();

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
        var embeddedTextureHashes = document.Textures
            .Where(static texture => texture.NativeChecksum.HasValue)
            .Select(static texture => texture.NativeChecksum!.Value)
            .ToHashSet();
        Assert.True(usedTextureHashes.SetEquals(embeddedTextureHashes),
            "Every texture used by the level faces should resolve from the geometry/library pair");

        if (entryName.Equals("l1a3_g.psx", StringComparison.OrdinalIgnoreCase))
        {
            // L1A3 has several checkpoint-specific restart states. A static
            // preview cannot choose one without gameplay context, so it must
            // retain the complete authored 5,036-triangle level.
            var trgBytes = source.TryReadCompanion("l1a3_t.trg");
            Assert.NotNull(trgBytes);
            using (var stream = new MemoryStream(trgBytes!, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                var trg = TrgFile.Parse(reader, "l1a3_t.trg");
                Assert.True(trg.Nodes.Count(static node => node.Type == "RESTART") > 1);
            }

            // The metal-panel art specifically reported for the girders is
            // entry 6 in l1a3_l.psx. Keep the exact opaque identifier pinned:
            // it is a build-tool id, not a recoverable filename hash.
            Assert.Contains(0x3CE37DB3u, usedTextureHashes);
            Assert.Contains(document.Textures,
                static texture => texture.NativeChecksum == 0x3CE37DB3u
                                  && texture.PngBytes is { Length: > 0 });
        }
    }

    [Fact]
    public void Lda2_FromCdWad_PreservesAdditiveSpotlightTextureVariant()
    {
        const uint spotlightTextureHash = 0x06A567C0u;
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("lda2_g.psx");
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);

        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        var spotlight = file!.Meshes[88];
        Assert.Equal(24, spotlight.Faces.Count);
        Assert.All(spotlight.Faces, face =>
        {
            Assert.Equal((ushort)0x08C3, face.Flags);
            Assert.True(face.IsSemiTransparent);
            Assert.Equal(1, face.BlendRate);
            Assert.Equal(spotlightTextureHash, face.TextureHash);
        });

        var document = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "lda2_g",
            SourceKind = ModelSourceKind.Psx
        });
        var matchingMaterials = document.Materials
            .Where(material => material.TextureIndex is { } textureIndex &&
                               document.Textures[textureIndex].NativeChecksum == spotlightTextureHash)
            .ToArray();
        var opaque = Assert.Single(matchingMaterials, material => material.AlphaMode == ModelAlphaMode.Opaque);
        var additive = Assert.Single(
            matchingMaterials,
            material => material.AlphaMode == ModelAlphaMode.Blend &&
                        material.Name.EndsWith("__st1", StringComparison.Ordinal));
        Assert.NotEqual(opaque.TextureIndex, additive.TextureIndex);

        var opaqueTexture = document.Textures[opaque.TextureIndex!.Value];
        var additiveTexture = document.Textures[additive.TextureIndex!.Value];
        Assert.Equal(spotlightTextureHash, opaqueTexture.NativeChecksum);
        Assert.Equal(spotlightTextureHash, additiveTexture.NativeChecksum);
        using var opaqueImage = Image.Load<Rgba32>(opaqueTexture.PngBytes!);
        using var additiveImage = Image.Load<Rgba32>(additiveTexture.PngBytes!);
        var opaquePixels = new Rgba32[opaqueImage.Width * opaqueImage.Height];
        var additivePixels = new Rgba32[additiveImage.Width * additiveImage.Height];
        opaqueImage.CopyPixelDataTo(opaquePixels);
        additiveImage.CopyPixelDataTo(additivePixels);
        Assert.All(opaquePixels, static pixel => Assert.Equal(byte.MaxValue, pixel.A));
        Assert.Contains(additivePixels, static pixel => pixel.A < byte.MaxValue);
        Assert.All(additivePixels, static pixel =>
        {
            Assert.Equal(byte.MaxValue, pixel.R);
            Assert.Equal(byte.MaxValue, pixel.G);
            Assert.Equal(byte.MaxValue, pixel.B);
        });
    }

    [Theory]
    [InlineData("firedome.psx", 60)]
    [InlineData("torch.psx", 273)]
    public void SurfaceEffects_FromCdWad_ApplyTag6BaseUvs(
        string entryName,
        int expectedWibbledFaces)
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry(entryName);
        Assert.NotNull(entry);

        var file = PsxMeshFile.Parse(backend.ReadEntryBytes(entry!));
        Assert.NotNull(file);
        var wibbledFaces = file!.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.TextureWibble != null)
            .ToArray();
        Assert.Equal(expectedWibbledFaces, wibbledFaces.Length);
        Assert.All(wibbledFaces, static face => Assert.True((face.Flags & 0x20) != 0));

        if (entryName.Equals("firedome.psx", StringComparison.OrdinalIgnoreCase))
        {
            var face = file.Meshes[1].Faces[0];
            Assert.Equal((short)0, face.TextureWibble!.UVelocity);
            Assert.Equal((short)16384, face.TextureWibble.VVelocity);
            Assert.Equal(595, face.TextureWibble.Frequency);
            Assert.Equal(new PsxTextureCoordinate(21, 59), face.GetTextureCoordinate(0));
            Assert.Equal(new PsxTextureCoordinate(0, 59), face.GetTextureCoordinate(1));
            Assert.Equal(new PsxTextureCoordinate(159, 0), face.GetTextureCoordinate(2));
        }
        else
        {
            const uint flameTextureHash = 0x36C168FAu;

            // Torch's shell is not a missing texture or an arbitrary green
            // recolour: every tag-6 face uses the same grayscale intensity
            // texture, authored olive vertex modulation, and native PS1 ABR1
            // B+F blending. A source-alpha preview would darken the body
            // behind these faces, so the live viewer restores additive blend
            // semantics from the exported __st1 material suffix.
            Assert.Empty(file.ColourPulses);
            Assert.All(wibbledFaces, static face =>
            {
                Assert.Equal(flameTextureHash, face.TextureHash);
                Assert.True(face.IsSemiTransparent);
                Assert.Equal(1, face.BlendRate);
            });
            Assert.NotNull(file.GouraudPalette);
            Assert.Equal(
                new Vector4(102f / 255f, 90f / 255f, 24f / 255f, 1f),
                file.GouraudPalette![12]);
        }
    }

    [Theory]
    [InlineData("firedome.psx", 7)]
    [InlineData("l1a4_g.psx", 60)]
    public void PulsingColours_FromCdWad_BakeAuthoredInitialPhase(
        string entryName,
        int expectedPulseCount)
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry(entryName);
        Assert.NotNull(entry);

        var file = PsxMeshFile.Parse(backend.ReadEntryBytes(entry!));
        Assert.NotNull(file);
        Assert.Equal(expectedPulseCount, file!.ColourPulses.Count);
        Assert.NotNull(file.GouraudPalette);

        if (entryName.Equals("firedome.psx", StringComparison.OrdinalIgnoreCase))
        {
            var pulse = Assert.Single(file.ColourPulses, static item => item.ColourIndex == 4);
            Assert.Equal((byte)15, pulse.InitialTimeAccumulator);
            Assert.Equal(3, pulse.Keys.Length);
            // The raw RGBs slot is (10,20,10), but the authored pulse starts
            // halfway toward its red fire key. It must not remain dark.
            var fireFace = file.Meshes[1].Faces[15];
            var fireColor = PsxGeometryHelpers.ComputePsxFaceColors(
                file.Version, fireFace, file.GouraudPalette).C0;
            Assert.True(fireColor.X > 0.4f);
        }
        else
        {
            var pulse = Assert.Single(file.ColourPulses, static item => item.ColourIndex == 196);
            Assert.Equal(new PsxColourPulseKey(148, 40, 17, 33), pulse.Keys[0]);
            // RGBs slots 196..255 are all zero on disc and are initialized by
            // tag 7 before the level renders.
            Assert.Equal(148f / 255f, file.GouraudPalette![196].X);
            Assert.Equal(40f / 255f, file.GouraudPalette[196].Y);
            Assert.Equal(17f / 255f, file.GouraudPalette[196].Z);

            var pulseIndices = file.ColourPulses
                .Select(static item => item.ColourIndex)
                .ToHashSet();
            Assert.Contains(
                file.Meshes.SelectMany(static mesh => mesh.Faces),
                face => face.IsGouraud &&
                        (pulseIndices.Contains(face.R) ||
                         pulseIndices.Contains(face.G) ||
                         pulseIndices.Contains(face.B) ||
                         face.IsQuad && pulseIndices.Contains(face.Mode)));
            Assert.All(file.ColourPulses, item =>
            {
                var color = file.GouraudPalette[item.ColourIndex];
                Assert.True(color.X > 0f || color.Y > 0f || color.Z > 0f);
            });
        }
    }

    [Fact]
    public void ItemsQuestionMark_FromCdWad_BakesStoredBlueColourPulse()
    {
        const uint questionMarkMeshHash = 0x7F648179u;
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("items.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        Assert.Equal(20, file!.ColourPulses.Count);
        Assert.NotNull(file.GouraudPalette);
        Assert.Equal(questionMarkMeshHash, file.MeshNameHashes[5]);

        var questionMark = file.Meshes[5];
        Assert.Equal(32, questionMark.Faces.Count);
        Assert.All(questionMark.Faces, static face =>
        {
            Assert.True(face.IsTextured);
            Assert.True(face.IsGouraud);
            Assert.Equal(2u, face.TextureHash);
        });

        // RGBs slots 28..33 contain teal/orange scratch values on disc. Tag 7
        // replaces them with a phase-staggered blue pulse before rendering.
        var expectedPulseKeys = new[]
        {
            new PsxColourPulseKey(50, 100, 255, 32),
            new PsxColourPulseKey(0, 0, 255, 32),
            new PsxColourPulseKey(0, 0, 255, 32)
        };
        foreach (var colourIndex in Enumerable.Range(28, 6))
        {
            var pulse = Assert.Single(
                file.ColourPulses,
                item => item.ColourIndex == colourIndex);
            Assert.Equal(expectedPulseKeys, pulse.Keys);
        }

        var expectedPalette = new[]
        {
            new Vector4(50f / 255f, 100f / 255f, 1f, 1f),
            new Vector4(25f / 255f, 50f / 255f, 1f, 1f),
            new Vector4(0f, 0f, 1f, 1f),
            new Vector4(0f, 0f, 1f, 1f),
            new Vector4(0f, 0f, 1f, 1f),
            new Vector4(25f / 255f, 50f / 255f, 1f, 1f)
        };
        for (var index = 0; index < expectedPalette.Length; index++)
            Assert.Equal(expectedPalette[index], file.GouraudPalette![28 + index]);

        Assert.All(questionMark.Faces, face =>
        {
            Assert.InRange(face.R, (byte)28, (byte)33);
            Assert.InRange(face.G, (byte)28, (byte)33);
            Assert.InRange(face.B, (byte)28, (byte)33);
            if (face.IsQuad)
                Assert.InRange(face.Mode, (byte)28, (byte)33);
        });

        var nativeQuestionColor = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, questionMark.Faces[0], file.GouraudPalette).C0;
        var expectedLinearQuestionColor =
            PsxGeometryHelpers.DisplayRgbToLinear(nativeQuestionColor);
        var document = ParseDocument(source, entry.Name);
        AssertDocumentContainsColor(document, expectedLinearQuestionColor);
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
    public void SymDark_FromCdWad_UsesDisplayRgbForUntexturedGouraudFaces()
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("sym_dark.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        Assert.Equal(41, file!.GouraudPalette?.Length);

        // These solid regions are authored PSX primitives, not textured
        // packets that the reader failed to classify.  Bit 0 is clear on all
        // 217 of them and their 16-byte G3 records contain neither a texture
        // hash nor UV payload.  Keep this distinction narrow: neighboring
        // textured GT3 packets set bit 0 and consume the full 28-byte record.
        var allFaces = file.Meshes.SelectMany(static mesh => mesh.Faces).ToArray();
        Assert.Equal(364, allFaces.Length);
        Assert.Equal(147, allFaces.Count(static candidate => candidate.IsTextured));
        Assert.Equal(217, allFaces.Count(static candidate => !candidate.IsTextured));
        Assert.Equal(116, allFaces.Count(static candidate => candidate.Flags == 0x2810));
        Assert.Equal(61, allFaces.Count(static candidate => candidate.Flags == 0x2800));
        Assert.Equal(24, allFaces.Count(static candidate => candidate.Flags == 0x2010));
        Assert.Equal(16, allFaces.Count(static candidate => candidate.Flags == 0x2000));
        Assert.All(allFaces.Where(static candidate => !candidate.IsTextured), static candidate =>
        {
            Assert.Equal(0, candidate.Flags & 1);
            Assert.Equal(0u, candidate.TextureHash);
        });

        var suspectMesh = file.Meshes[6];
        var texturedRead = suspectMesh.FaceReadInfos[2];
        Assert.Equal((ushort)0x2813, texturedRead.Flags);
        Assert.Equal((ushort)28, texturedRead.Length);
        Assert.Equal(28, texturedRead.BytesConsumed);
        Assert.True(texturedRead.IsLengthAligned);
        Assert.True(suspectMesh.Faces[2].IsTextured);
        Assert.Equal(0x6B0E2246u, suspectMesh.Faces[2].TextureHash);

        foreach (var faceIndex in new[] { 7, 23 })
        {
            var untexturedRead = suspectMesh.FaceReadInfos[faceIndex];
            Assert.Equal((ushort)0x2810, untexturedRead.Flags);
            Assert.Equal((ushort)16, untexturedRead.Length);
            Assert.Equal(16, untexturedRead.BytesConsumed);
            Assert.Equal(0, untexturedRead.UnderreadBytes);
            Assert.Equal(0, untexturedRead.OverreadBytes);
            Assert.True(untexturedRead.IsLengthAligned);
            Assert.False(suspectMesh.Faces[faceIndex].IsTextured);
        }

        // This untextured G3 face uses the brightest authored purple entries.
        // RGBs bytes are direct display RGB here, not 128-neutral texture
        // modulation: 251 must remain 251/255 instead of clipping to 1.0.
        var face = suspectMesh.Faces[7];
        Assert.Equal((ushort)0x2810, face.Flags);
        Assert.True(face.IsGouraud);
        Assert.False(face.IsTextured);
        Assert.Equal((byte)40, face.R);
        Assert.Equal((byte)39, face.G);
        Assert.Equal((byte)39, face.B);

        var colors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, face, file.GouraudPalette);
        Assert.Equal(new Vector4(128f / 255f, 128f / 255f, 251f / 255f, 1f), colors.C0);
        Assert.Equal(new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f), colors.C1);
        Assert.Equal(colors.C1, colors.C2);

        var document = ParseDocument(source, entry.Name);
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(colors.C0));
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(colors.C1));
    }

    [Fact]
    public void L1A3_FromCdWad_PreservesAuthoredFlatFluorescentFixtureSides()
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

        // These four thin boxes were previously misidentified in this test as
        // the reported metal supports. Their 0x18221C83 texture is actually the
        // fluorescent-light art: the PSX source authors only the lit face with
        // that texture, while the four shallow fixture sides are 0x4000
        // flat-color primitives with no hash or UV payload.
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

        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
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

        var document = ParseDocument(source, entry.Name);
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(flatColor));
    }

    private static ModelDocument ParseDocument(AssetSource source, string fileName)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = fileName,
            OutputStem = Path.GetFileNameWithoutExtension(fileName),
            SourceKind = ModelSourceKind.Psx
        });
    }

    private static void AssertDocumentContainsColor(ModelDocument document, Vector4 expected)
    {
        Assert.Contains(
            document.Meshes
                .SelectMany(static mesh => mesh.Primitives)
                .SelectMany(static primitive => primitive.Vertices),
            vertex => Vector4.Distance(vertex.Color, expected) < 1e-6f);
    }
}
