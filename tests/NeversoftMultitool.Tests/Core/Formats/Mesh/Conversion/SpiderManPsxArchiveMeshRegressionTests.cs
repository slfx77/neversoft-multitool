using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using NeversoftMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System.Numerics;
using GltfModelRoot = SharpGLTF.Schema2.ModelRoot;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class SpiderManPsxArchiveMeshRegressionTests(TestPaths paths)
{
    private const string BuildName = "Spider-Man (2000-9-1, PSX - Final)";
    private const string PcBuildName = "Spider-Man (2001-9-17, PC - Final)";

    [Theory]
    [InlineData("l1a3_g.psx", 5036, 101)]
    [InlineData("l2a1_g.psx", 8470, 100)]
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

        var parser = new MeshModelParser();
        var initialDocument = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx
        });
        var document = parser.Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx,
            VisibilityOverrides = initialDocument.VisibilityGroups.ToDictionary(
                static group => group.Id,
                static _ => true,
                StringComparer.Ordinal)
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
            // L1A3 exposes its selected restart state as dynamic groups. The
            // explicit all-visible selection above must still retain the full
            // authored 5,036-triangle level and every companion texture.
            var trgBytes = source.TryReadCompanion("l1a3_t.trg");
            Assert.NotNull(trgBytes);
            using (var stream = new MemoryStream(trgBytes!, writable: false))
            using (var reader = new BinaryReader(stream))
            {
                var trg = TrgFile.Parse(reader, "l1a3_t.trg");
                Assert.True(trg.Nodes.Count(static node => node.Type == "RESTART") > 1);
            }
            Assert.NotEmpty(initialDocument.VisibilityGroups);
            Assert.Contains(initialDocument.VisibilityGroups,
                static group => !group.DefaultEnabled);

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
    public void Lda2_FromCdWad_PreservesAdditiveSpotlightMaterial()
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
        // All-STP ABR1 can retain the authored RGB at full source alpha because
        // the material suffix selects additive blending. The normalized opaque
        // and additive image payloads match, so content deduplication should
        // share them while the distinct material carries the rendering mode.
        Assert.Equal(opaque.TextureIndex, additive.TextureIndex);

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
        Assert.Equal(opaquePixels, additivePixels);
        Assert.All(additivePixels, static pixel => Assert.Equal(byte.MaxValue, pixel.A));
        Assert.Contains(additivePixels,
            static pixel => pixel.R != byte.MaxValue ||
                            pixel.G != byte.MaxValue ||
                            pixel.B != byte.MaxValue);
    }

    [Fact]
    public void Lda3_FromCdWad_DecodesEveryBillboardPaletteKeySlot()
    {
        const uint billboardTextureHash = 0x856A83ABu;
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("lda3_g.psx");
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);

        var libraryBytes = source.TryReadCompanion("lda3_l.psx");
        Assert.NotNull(libraryBytes);
        var header = Assert.Single(
            PsxLibrary.EnumerateTextures(libraryBytes!),
            pair => pair.NameHash == billboardTextureHash).Header;
        Assert.Equal((uint)16, header.PalSize);

        using (var stream = new MemoryStream(libraryBytes!, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            reader.ReadBytes(4); // PSX magic, already validated by enumeration
            PsxLibrary.SkipModelData(reader);
            PsxLibrary.ReadTextureInfo(reader);
            var palettes = PsxLibrary.ReadPalettes(reader, 16);
            var palette = Assert.Single(palettes, item => item.TexId == header.TexId);
            Assert.Equal(
                [0, 2, 4, 14, 15],
                palette.ColorData
                    .Select(static (color, index) => (color, index))
                    .Where(static item => (item.color & 0x7FFF) == 0x7C1F)
                    .Select(static item => item.index));
        }

        var document = ParseDocument(source, entry.Name);
        var material = Assert.Single(document.Materials, material =>
            material.TextureIndex is { } textureIndex &&
            document.Textures[textureIndex].NativeChecksum == billboardTextureHash);
        Assert.Equal(ModelAlphaMode.Mask, material.AlphaMode);
        using var image = Image.Load<Rgba32>(
            document.Textures[material.TextureIndex!.Value].PngBytes!);
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);

        Assert.Equal(128, image.Width);
        Assert.Equal(128, image.Height);
        Assert.Equal(9366, pixels.Count(static pixel => pixel.A == 0));
        Assert.Equal(7018, pixels.Count(static pixel => pixel.A == byte.MaxValue));
        Assert.DoesNotContain(pixels, static pixel =>
            pixel.A != 0 && pixel.R == byte.MaxValue && pixel.G == 0 && pixel.B == byte.MaxValue);
    }

    [Fact]
    public void L1A4_FromCdWad_MultipleRestartsKeepRoomGeometryVisible()
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("l1a4_g.psx");
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);

        var trgBytes = source.TryReadCompanion("l1a4_t.trg");
        Assert.NotNull(trgBytes);
        using (var stream = new MemoryStream(trgBytes!, writable: false))
        using (var reader = new BinaryReader(stream))
        {
            var trg = TrgFile.Parse(reader, "l1a4_t.trg");
            Assert.Equal(2, trg.Nodes.Count(static node => node.Type == "RESTART"));
        }

        var authoredTriangleCount = file!.Objects
            .Where(obj => obj.MeshIndex < file.Meshes.Count)
            .Sum(obj => file.Meshes[obj.MeshIndex].Faces.Sum(
                static face => face.IsQuad ? 2 : 1));
        Assert.Equal(3859, authoredTriangleCount);

        var document = ParseDocument(source, entry.Name);
        Assert.NotEmpty(document.VisibilityGroups);
        var allVisible = new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "l1a4_g",
            SourceKind = ModelSourceKind.Psx,
            VisibilityOverrides = document.VisibilityGroups.ToDictionary(
                static group => group.Id,
                static _ => true,
                StringComparer.Ordinal)
        });
        Assert.Equal(3838, allVisible.TriangleCount);
        Assert.Equal(3828, document.TriangleCount);
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

        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
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

        var document = ParseDocument(source, entry.Name);
        var animatedVertices = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Where(static vertex => vertex.TextureWibble.HasValue)
            .ToArray();
        Assert.NotEmpty(animatedVertices);
        Assert.All(animatedVertices, static vertex =>
        {
            Assert.True(vertex.TextureWibble!.Value.TextureWidth > 0);
            Assert.True(vertex.TextureWibble.Value.TextureHeight > 0);
        });

        var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.NotNull(glbBytes);
        using var glbStream = new MemoryStream(glbBytes, writable: false);
        var glb = GltfModelRoot.ReadGLB(glbStream);
        Assert.Contains(
            glb.LogicalMeshes.SelectMany(static mesh => mesh.Primitives),
            primitive => primitive.GetVertexAccessor(
                PsxAnimatedVertexColor1Texture1.MotionAttributeName) != null);
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
            PsxGeometryHelpers.DisplayRgbToLinear(
                nativeQuestionColor,
                isPs1TexturedModulation: true);
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

        // Mesh 6 faces 22/23 meet along vertices 0 and 26. The authored
        // textured GT4 and untextured G3 reuse the exact same RGBs indices at
        // both endpoints, and the texture contains decoded 5-bit neutral grey
        // (131) there. Preserve packet values above 128 so modulation reaches
        // the untextured display colour instead of clipping to a blue seam.
        var texturedBoundary = suspectMesh.Faces[22];
        var untexturedBoundary = suspectMesh.Faces[23];
        Assert.Equal((ushort)0x2803, texturedBoundary.Flags);
        Assert.Equal((ushort)0x2810, untexturedBoundary.Flags);
        var sharedVertices = FaceVertexIndices(texturedBoundary)
            .Intersect(FaceVertexIndices(untexturedBoundary))
            .ToArray();
        Assert.Equal([0u, 26u], sharedVertices.Order().ToArray());

        var texturedBoundaryColors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, texturedBoundary, file.GouraudPalette);
        var untexturedBoundaryColors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, untexturedBoundary, file.GouraudPalette);
        var decodedNeutralLinear = PsxGeometryHelpers.DisplayRgbToLinear(
            new Vector4(131f / 255f, 131f / 255f, 131f / 255f, 1f));
        var foundExtendedMultiplier = false;
        foreach (var vertexIndex in sharedVertices)
        {
            var texturedSlot = FindFaceSlot(texturedBoundary, vertexIndex);
            var untexturedSlot = FindFaceSlot(untexturedBoundary, vertexIndex);
            Assert.Equal(
                FaceColorIndex(texturedBoundary, texturedSlot),
                FaceColorIndex(untexturedBoundary, untexturedSlot));

            var texturedLinear = PsxGeometryHelpers.DisplayRgbToLinear(
                FaceColor(texturedBoundaryColors, texturedSlot),
                isPs1TexturedModulation: true);
            var untexturedLinear = PsxGeometryHelpers.DisplayRgbToLinear(
                FaceColor(untexturedBoundaryColors, untexturedSlot));
            var modulatedEdge = texturedLinear * decodedNeutralLinear;
            Assert.InRange(Vector3.Distance(
                new Vector3(modulatedEdge.X, modulatedEdge.Y, modulatedEdge.Z),
                new Vector3(untexturedLinear.X, untexturedLinear.Y, untexturedLinear.Z)), 0f, 1e-5f);
            foundExtendedMultiplier |= texturedLinear.X > 1f ||
                                       texturedLinear.Y > 1f ||
                                       texturedLinear.Z > 1f;
        }
        Assert.True(foundExtendedMultiplier);

        var document = ParseDocument(source, entry.Name);
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(colors.C0));
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(colors.C1));
    }

    [Fact]
    public void L7A3_FromCdWad_BlendsOnlyRailingTextureStpPixels()
    {
        const uint railingTextureHash = 0x44DB120Eu;
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("l7a3_g.psx");
        Assert.NotNull(entry);
        var source = new ArchiveAssetSource(backend, entry!);

        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        var railingFaces = file!.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(face => face.TextureHash == railingTextureHash)
            .ToArray();
        Assert.Equal(284, railingFaces.Length);
        Assert.Equal(54, railingFaces.Count(static face => face.IsSemiTransparent));
        Assert.All(railingFaces.Where(static face => face.IsSemiTransparent), static face =>
        {
            Assert.Equal(0, face.BlendRate);
            Assert.True(face.IsTextured);
        });

        var document = ParseDocument(source, entry.Name);
        var matchingMaterials = document.Materials
            .Where(material => material.TextureIndex is { } textureIndex &&
                               document.Textures[textureIndex].NativeChecksum == railingTextureHash)
            .ToArray();
        var blended = Assert.Single(
            matchingMaterials,
            static material => material.AlphaMode == ModelAlphaMode.Blend &&
                               material.Name.Contains("__st0", StringComparison.Ordinal));
        var cutout = Assert.Single(
            matchingMaterials,
            static material => material.AlphaMode == ModelAlphaMode.Mask &&
                               !material.Name.Contains("__st", StringComparison.Ordinal));

        using var blendedImage = Image.Load<Rgba32>(
            document.Textures[blended.TextureIndex!.Value].PngBytes!);
        using var cutoutImage = Image.Load<Rgba32>(
            document.Textures[cutout.TextureIndex!.Value].PngBytes!);
        var blendedPixels = new Rgba32[blendedImage.Width * blendedImage.Height];
        var cutoutPixels = new Rgba32[cutoutImage.Width * cutoutImage.Height];
        blendedImage.CopyPixelDataTo(blendedPixels);
        cutoutImage.CopyPixelDataTo(cutoutPixels);

        Assert.Equal(4096, blendedPixels.Length);
        Assert.Equal(1458, blendedPixels.Count(static pixel => pixel.A == 0));
        Assert.Equal(278, blendedPixels.Count(static pixel => pixel.A == 128));
        Assert.Equal(2360, blendedPixels.Count(static pixel => pixel.A == 255));
        Assert.DoesNotContain(blendedPixels, static pixel => pixel.A == 254);
        Assert.Equal(1458, cutoutPixels.Count(static pixel => pixel.A == 0));
        Assert.Equal(2638, cutoutPixels.Count(static pixel => pixel.A == 255));
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
        var mesh = file.Meshes[23];
        Assert.False(mesh.UsesDynamicLighting);
        var face = mesh.Faces[0];
        Assert.True(face.IsGouraud);
        Assert.Equal((byte)158, face.R);
        Assert.Equal((byte)188, face.B);
        var colors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, mesh, face, file.GouraudPalette);

        Assert.Equal(file.GouraudPalette![158], colors.C0);
        Assert.Equal(file.GouraudPalette[188], colors.C2);
        Assert.NotEqual(colors.C0.X, colors.C0.Y);
        Assert.NotEqual(colors.C2.Y, colors.C2.Z);

        // Flat v6 colors are direct 8-bit diffuse values. PS1-style /128
        // modulation would clamp this common yellow tuple to white.
        var flatMesh = file.Meshes[28];
        Assert.False(flatMesh.UsesDynamicLighting);
        var flatFace = flatMesh.Faces[0];
        Assert.False(flatFace.IsGouraud);
        Assert.True(flatFace.IsTextured);
        Assert.Equal((byte)255, flatFace.R);
        Assert.Equal((byte)253, flatFace.G);
        Assert.Equal((byte)175, flatFace.B);
        var flatColor = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, flatMesh, flatFace, file.GouraudPalette).C0;
        Assert.Equal(1f, flatColor.X);
        Assert.Equal(253f / 255f, flatColor.Y);
        Assert.Equal(175f / 255f, flatColor.Z);

        var document = ParseDocument(source, entry.Name);
        AssertDocumentContainsColor(
            document,
            PsxGeometryHelpers.DisplayRgbToLinear(flatColor));
    }

    [Fact]
    public void PcJameson_FromDataPkr_UsesRuntimeLightingInsteadOfRgbPalette()
    {
        var pkrPath = paths.FindSampleFile(PcBuildName, "data.pkr");
        Assert.SkipWhen(pkrPath == null, "Spider-Man PC data.pkr sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pkrPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = Assert.Single(
            backend.FindAllByName("jameson.psx"),
            static candidate => !candidate.FullName.Contains("lowres", StringComparison.OrdinalIgnoreCase));
        var source = new ArchiveAssetSource(backend, entry);
        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        Assert.Equal((ushort)0x06, file!.Version);
        Assert.True(file.IsSuperModel);
        Assert.Equal(16, file.Meshes.Count);
        Assert.Equal(213, file.GouraudPalette?.Length);
        Assert.All(file.Meshes, static mesh => Assert.True(mesh.UsesDynamicLighting));
        Assert.Equal(663, file.Meshes.Sum(static mesh => mesh.Faces.Count));
        Assert.All(
            file.Meshes.SelectMany(static mesh => mesh.Faces),
            static face => Assert.NotEqual(0, face.Flags & 0x0004));

        var coloredFace = file.Meshes
            .SelectMany(static mesh => mesh.Faces
                .Where(static face => face.IsGouraud)
                .Select(face => (Mesh: mesh, Face: face)))
            .First(item =>
            {
                var color = file.GouraudPalette![item.Face.R];
                return MathF.Abs(color.X - color.Y) > 0.01f ||
                       MathF.Abs(color.Y - color.Z) > 0.01f;
            });
        var mesh = coloredFace.Mesh;
        var face = coloredFace.Face;
        var serializedColors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, face, file.GouraudPalette);
        Assert.True(
            MathF.Abs(serializedColors.C0.X - serializedColors.C0.Y) > 0.01f ||
            MathF.Abs(serializedColors.C0.Y - serializedColors.C0.Z) > 0.01f);
        var runtimeColors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, mesh, face, file.GouraudPalette);
        Assert.Equal(Vector4.One, runtimeColors.C0);
        Assert.Equal(Vector4.One, runtimeColors.C1);
        Assert.Equal(Vector4.One, runtimeColors.C2);
        Assert.Equal(Vector4.One, runtimeColors.C3);

        var document = ParseDocument(source, entry.Name);
        var vertices = document.Meshes
            .SelectMany(static modelMesh => modelMesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .ToArray();
        Assert.NotEmpty(vertices);
        Assert.All(vertices, static vertex =>
        {
            Assert.Equal(1f, vertex.Color.X);
            Assert.Equal(1f, vertex.Color.Y);
            Assert.Equal(1f, vertex.Color.Z);
        });
    }

    [Fact]
    public void DreamcastJameson_UsesTheSameRuntimeLightingFaceFlag()
    {
        const string dreamcastBuild = "Spider-Man (2001-2-14, DC - Prototype)";
        var path = paths.FindSampleFile(dreamcastBuild, "jameson.psx");
        Assert.SkipWhen(path == null, "Spider-Man Dreamcast Jameson sample not available");

        var file = PsxMeshFile.Parse(path!);
        Assert.NotNull(file);
        Assert.Equal((ushort)0x06, file!.Version);
        Assert.Equal(16, file.Meshes.Count);
        Assert.Equal(213, file.GouraudPalette?.Length);
        Assert.All(file.Meshes, static mesh => Assert.True(mesh.UsesDynamicLighting));
        Assert.Equal(663, file.Meshes.Sum(static mesh => mesh.Faces.Count));

        var mesh = file.Meshes.First(static candidate =>
            candidate.Faces.Any(static face => face.IsGouraud));
        var face = mesh.Faces.First(static candidate => candidate.IsGouraud);
        var colors = PsxGeometryHelpers.ComputePsxFaceColors(
            file.Version, mesh, face, file.GouraudPalette);
        Assert.Equal(Vector4.One, colors.C0);
        Assert.Equal(Vector4.One, colors.C1);
        Assert.Equal(Vector4.One, colors.C2);
        Assert.Equal(Vector4.One, colors.C3);
    }

    [Fact]
    public void PcL2A1_FromDataPkr_PreservesWidenedBaseUvsAndNativeScrollScale()
    {
        var pkrPath = paths.FindSampleFile(PcBuildName, "data.pkr");
        Assert.SkipWhen(pkrPath == null, "Spider-Man PC data.pkr sample not available");

        var backend = ArchiveAssetBackend.TryOpen(pkrPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("L2A1_G.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry!);
        var file = PsxMeshFile.Parse(source.ReadBytes());
        Assert.NotNull(file);
        Assert.Equal((ushort)0x06, file!.Version);

        var wibbledFaces = file.Meshes
            .SelectMany(static mesh => mesh.Faces)
            .Where(static face => face.TextureWibble != null)
            .ToArray();
        Assert.Equal(28, wibbledFaces.Length);
        Assert.All(wibbledFaces, static face =>
        {
            Assert.True(face.TextureWibble!.UsesFaceTextureCoordinates);
            Assert.All(face.TextureWibble.Vertices, static vertex =>
            {
                // PC retains the amplitude/phase bytes but leaves the legacy
                // PS1 base-UV byte pair empty. Its renderer reads base UVs
                // from the widened face payload instead.
                Assert.Equal((byte)0, vertex.U);
                Assert.Equal((byte)0, vertex.V);
            });
        });
        Assert.Contains(wibbledFaces, static face =>
            face.GetTextureCoordinate(0) != default ||
            face.GetTextureCoordinate(1) != default ||
            face.GetTextureCoordinate(2) != default);

        var document = ParseDocument(source, entry.Name);
        var animatedVertices = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Where(static vertex => vertex.TextureWibble.HasValue)
            .ToArray();
        Assert.NotEmpty(animatedVertices);
        Assert.All(animatedVertices, static vertex =>
        {
            var wibble = vertex.TextureWibble!.Value;
            Assert.Equal(512, wibble.TextureWidth);
            Assert.Equal(512, wibble.TextureHeight);
        });
        Assert.Contains(animatedVertices, static vertex =>
            vertex.TextureWibble!.Value.UVelocity == -8192);

        // The regression collapsed every animated face onto its zero
        // placeholder, sampling one texel as a solid-colour plane.
        Assert.True(animatedVertices.Select(static vertex => vertex.TexCoord).Distinct().Count() > 4);
    }

    [Theory]
    [InlineData(BuildName, "CD.WAD", 0x04, 0x4AF40103u, 32)]
    [InlineData(PcBuildName, "data.pkr", 0x06, 0x224DF7AFu, 40)]
    public void L2a1Antenna_FromArchive_PreservesConnectedAuthoredTriangles(
        string buildName,
        string archiveName,
        int expectedVersion,
        uint expectedTextureHash,
        int expectedFaceLength)
    {
        var archivePath = paths.FindSampleFile(buildName, archiveName);
        Assert.SkipWhen(archivePath == null, $"{buildName} {archiveName} sample not available");

        var backend = ArchiveAssetBackend.TryOpen(archivePath!);
        Assert.NotNull(backend);
        using var fileSystem = backend!.FileSystem;
        var entry = backend.FindEntry("l2a1_g.psx");
        Assert.NotNull(entry);

        var source = new ArchiveAssetSource(backend, entry!);
        var bytes = source.ReadBytes();
        var file = PsxMeshFile.Parse(bytes);
        Assert.NotNull(file);
        Assert.Equal((ushort)expectedVersion, file!.Version);

        // Both the PS1 source and the v6 PC port author this rooftop piece as
        // the same five positions and three triangles. It is a connected fan,
        // not three detached records or an incomplete quad decode.
        var antenna = file.Meshes[196];
        Assert.Equal(5, antenna.Vertices.Count);
        Assert.Equal(3, antenna.Faces.Count);
        var expectedPositions = new (short X, short Y, short Z)[]
        {
            (137, -270, 1117),
            (-246, -270, 386),
            (-247, -270, 141),
            (-709, -270, 387),
            (-259, -272, 937)
        };
        Assert.Equal(
            expectedPositions,
            antenna.Vertices
                .Select(static vertex => (vertex.RawX, vertex.RawY, vertex.RawZ))
                .ToArray());
        Assert.Equal(new uint[] { 1, 0, 2 }, FaceVertexIndices(antenna.Faces[0]));
        Assert.Equal(new uint[] { 1, 3, 4 }, FaceVertexIndices(antenna.Faces[1]));
        Assert.Equal(new uint[] { 0, 1, 4 }, FaceVertexIndices(antenna.Faces[2]));
        Assert.All(antenna.Faces, face => Assert.Equal(expectedTextureHash, face.TextureHash));

        var sourceTriangles = antenna.Faces
            .Select(face => FaceVertexIndices(face).ToHashSet())
            .ToArray();
        for (var first = 0; first < sourceTriangles.Length; first++)
        {
            for (var second = first + 1; second < sourceTriangles.Length; second++)
                Assert.True(sourceTriangles[first].Overlaps(sourceTriangles[second]));
        }

        // Each source record has four zero bytes after the parsed fields in
        // both formats. They are padding, not a discarded fourth index: the
        // PS1 record is 32 bytes and the widened v6 record is 40 bytes.
        Assert.Equal(3, antenna.FaceReadInfos.Count);
        Assert.All(antenna.FaceReadInfos, read =>
        {
            Assert.True(read.IsAccepted);
            Assert.Equal(expectedFaceLength, read.Length);
            Assert.Equal(4, read.UnderreadBytes);
            var paddingOffset = checked((int)read.Offset + read.BytesConsumed);
            Assert.All(bytes.AsSpan(paddingOffset, 4).ToArray(),
                static value => Assert.Equal((byte)0, value));
        });

        var document = new ModelDocument { Name = "l2a1_g" };
        PsxGeometryWriter.PopulatePsx(document, file, textureProvider: null);
        var antennaNode = Assert.Single(document.Nodes,
            static node => node.Name == "object_196");
        Assert.NotNull(antennaNode.MeshIndex);
        var primitive = Assert.Single(document.Meshes[antennaNode.MeshIndex!.Value].Primitives);
        Assert.Equal(3, primitive.TriangleCount);
        Assert.Equal(5,
            primitive.Vertices.Select(static vertex => vertex.Position).Distinct().Count());

        var emittedTriangles = primitive.Indices
            .Chunk(3)
            .Select(indices => indices
                .Select(index => primitive.Vertices[index].Position)
                .ToHashSet())
            .ToArray();
        for (var first = 0; first < emittedTriangles.Length; first++)
        {
            for (var second = first + 1; second < emittedTriangles.Length; second++)
                Assert.True(emittedTriangles[first].Overlaps(emittedTriangles[second]));
        }

        // Duplicating vertices per face must not introduce a visible texture
        // seam: every repeated position retains one identical authored UV.
        Assert.All(
            primitive.Vertices.GroupBy(static vertex => vertex.Position),
            static group => Assert.Single(
                group.Select(static vertex => vertex.TexCoord).Distinct()));
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

    private static uint[] FaceVertexIndices(PsxFace face)
    {
        return Enumerable.Range(0, face.IsQuad ? 4 : 3)
            .Select(slot => PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot))
            .ToArray();
    }

    private static int FindFaceSlot(PsxFace face, uint vertexIndex)
    {
        return Enumerable.Range(0, face.IsQuad ? 4 : 3)
            .Single(slot => PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot) == vertexIndex);
    }

    private static byte FaceColorIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.R,
            1 => face.G,
            2 => face.B,
            3 => face.Mode,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }

    private static Vector4 FaceColor(
        (Vector4 C0, Vector4 C1, Vector4 C2, Vector4 C3) colors,
        int slot)
    {
        return slot switch
        {
            0 => colors.C0,
            1 => colors.C1,
            2 => colors.C2,
            3 => colors.C3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }
}
