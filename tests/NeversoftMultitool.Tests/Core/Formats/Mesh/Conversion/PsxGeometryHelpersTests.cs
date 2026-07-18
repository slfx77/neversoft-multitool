using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.Ps1;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxGeometryHelpersTests
{
    [Fact]
    public void ComputePsxFaceColors_V6GouraudWithoutPalette_UsesDirectVertexIntensities()
    {
        var face = new PsxFace
        {
            IsGouraud = true,
            IsQuad = true,
            R = 255,
            G = 128,
            B = 64,
            Mode = 0
        };

        var (c0, c1, c2, c3) = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, null);

        Assert.Equal(1f, c0.X);
        Assert.Equal(128f / 255f, c1.X);
        Assert.Equal(64f / 255f, c2.X);
        Assert.Equal(0f, c3.X);
        Assert.Equal(c1.X, c1.Y);
        Assert.Equal(c1.X, c1.Z);
    }

    [Fact]
    public void ComputePsxFaceColors_V6GouraudWithPalette_PreservesColoredLighting()
    {
        var face = new PsxFace
        {
            IsGouraud = true,
            IsQuad = true,
            R = 1,
            G = 2,
            B = 3,
            Mode = 4
        };
        var palette = new Vector4[5];
        palette[1] = new Vector4(128f / 255f, 64f / 255f, 32f / 255f, 1f);
        palette[2] = new Vector4(16f / 255f, 96f / 255f, 48f / 255f, 1f);
        palette[3] = new Vector4(8f / 255f, 24f / 255f, 112f / 255f, 1f);
        palette[4] = new Vector4(104f / 255f, 40f / 255f, 20f / 255f, 1f);

        var colors = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, palette);

        Assert.Equal(palette[1], colors.C0);
        Assert.Equal(palette[2], colors.C1);
        Assert.Equal(palette[3], colors.C2);
        Assert.Equal(palette[4], colors.C3);
        Assert.Equal(128f / 255f, colors.C0.X);
        Assert.NotEqual(1f, colors.C0.X);
    }

    [Fact]
    public void ComputePsxFaceColors_V6RuntimeLitMesh_UsesNeutralModulation()
    {
        var face = new PsxFace
        {
            Flags = 0x0080,
            IsGouraud = true,
            R = 1,
            G = 1,
            B = 1
        };
        var palette = new[]
        {
            Vector4.One,
            new Vector4(1f, 0.25f, 0.5f, 1f)
        };
        var rawFaceInfo = new PsxFaceReadInfo
        {
            RawFaceIndex = 0,
            Offset = 0,
            Flags = 0x0004,
            Length = 16,
            BytesConsumed = 16,
            UnderreadBytes = 0,
            OverreadBytes = 0,
            IsLengthAligned = true,
            IsAccepted = false,
            RejectionReason = "synthetic rejected face"
        };
        var mesh = new PsxMesh
        {
            Vertices = [],
            Normals = [new PsxNormal()],
            Faces = [face],
            FaceReadInfos = [rawFaceInfo]
        };

        Assert.True(mesh.UsesDynamicLighting);
        var serialized = PsxGeometryHelpers.ComputePsxFaceColors(0x06, face, palette);
        Assert.Equal(palette[1], serialized.C0);
        var runtime = PsxGeometryHelpers.ComputePsxFaceColors(0x06, mesh, face, palette);
        Assert.Equal(Vector4.One, runtime.C0);
        Assert.Equal(Vector4.One, runtime.C1);
        Assert.Equal(Vector4.One, runtime.C2);
        Assert.Equal(Vector4.One, runtime.C3);

        var headerFlagMesh = new PsxMesh
        {
            Flags = 0x0004,
            Vertices = [],
            Normals = [new PsxNormal()],
            Faces = [face]
        };
        Assert.True(headerFlagMesh.UsesDynamicLighting);

        var syntheticFaceMesh = new PsxMesh
        {
            Vertices = [],
            Normals = [new PsxNormal()],
            Faces = [new PsxFace { Flags = 0x0004 }]
        };
        Assert.True(syntheticFaceMesh.UsesDynamicLighting);

        var missingNormalsMesh = new PsxMesh
        {
            Vertices = [],
            Normals = [],
            Faces = [new PsxFace { Flags = 0x0004 }]
        };
        Assert.False(missingNormalsMesh.UsesDynamicLighting);
    }

    [Fact]
    public void ComputePsxFaceColors_Ps1PaletteUsesFaceAwareGpuScaling()
    {
        var palette = new Vector4[3];
        palette[1] = new Vector4(128f / 255f, 64f / 255f, 251f / 255f, 1f);
        palette[2] = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f);
        var textured = new PsxFace
        {
            IsGouraud = true,
            IsTextured = true,
            IsQuad = false,
            R = 1,
            G = 2,
            B = 1
        };
        var untextured = new PsxFace
        {
            IsGouraud = true,
            IsTextured = false,
            IsQuad = false,
            R = 1,
            G = 2,
            B = 1
        };

        var textureColors = PsxGeometryHelpers.ComputePsxFaceColors(0x04, textured, palette);
        var displayColors = PsxGeometryHelpers.ComputePsxFaceColors(0x04, untextured, palette);

        Assert.Equal(new Vector4(1f, 0.5f, 251f / 128f, 1f), textureColors.C0);
        Assert.Equal(new Vector4(144f / 128f, 119f / 128f, 223f / 128f, 1f), textureColors.C1);
        Assert.Equal(palette[1], displayColors.C0);
        Assert.Equal(palette[2], displayColors.C1);
    }

    [Fact]
    public void ComputePsxFaceColors_UsesModulationOnlyForPs1TexturedFaces()
    {
        var textured = new PsxFace { IsTextured = true, R = 128, G = 64, B = 255 };
        var untextured = new PsxFace { IsTextured = false, R = 128, G = 64, B = 255 };
        var pc = new PsxFace { IsTextured = true, R = 128, G = 64, B = 255 };

        var ps1TextureColor = PsxGeometryHelpers.ComputePsxFaceColors(0x04, textured, null).C0;
        var ps1FlatColor = PsxGeometryHelpers.ComputePsxFaceColors(0x04, untextured, null).C0;
        var pcColor = PsxGeometryHelpers.ComputePsxFaceColors(0x06, pc, null).C0;

        Assert.Equal(1f, ps1TextureColor.X);
        Assert.Equal(0.5f, ps1TextureColor.Y);
        Assert.Equal(255f / 128f, ps1TextureColor.Z);
        Assert.Equal(128f / 255f, ps1FlatColor.X);
        Assert.Equal(64f / 255f, ps1FlatColor.Y);
        Assert.Equal(128f / 255f, pcColor.X);
        Assert.Equal(64f / 255f, pcColor.Y);
    }

    [Fact]
    public void UntexturedSemiTransparentFace_RetainsAbrMaterialState()
    {
        var face = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var key = PsxGeometryHelpers.GetPsxMaterialKey(face);

        Assert.Equal(0u, key.Hash);
        Assert.True(key.SemiTransparent);
        Assert.Equal(1, key.BlendRate);
    }

    [Fact]
    public void UntexturedAdditiveFace_ConvertsVertexIntensityToAlpha()
    {
        var face = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var converted = PsxGeometryHelpers.ApplyPsxUntexturedBlend(
            face,
            new Vector4(0.25f, 0.5f, 0.125f, 1f));

        Assert.Equal(Vector3.One, new Vector3(converted.X, converted.Y, converted.Z));
        Assert.Equal(0.5f, converted.W);
    }

    [Fact]
    public void DisplayRgbToLinear_AppliesSrgbEotfAndPreservesAlpha()
    {
        var converted = PsxGeometryHelpers.DisplayRgbToLinear(
            new Vector4(0.04045f, 0.5f, 1f, 0.37f));

        Assert.InRange(converted.X, 0.0031307f, 0.0031309f);
        Assert.InRange(converted.Y, 0.2140409f, 0.2140414f);
        Assert.Equal(1f, converted.Z);
        Assert.Equal(0.37f, converted.W);
    }

    [Fact]
    public void DisplayRgbToLinear_Ps1TexturedModulationPreservesNeutralGreyEdgeAndOverbrightRange()
    {
        var paletteDisplay = new Vector4(144f / 255f, 119f / 255f, 223f / 255f, 1f);
        var packetModulation = new Vector4(144f / 128f, 119f / 128f, 223f / 128f, 1f);

        var untexturedLinear = PsxGeometryHelpers.DisplayRgbToLinear(paletteDisplay);
        var texturedLinear = PsxGeometryHelpers.DisplayRgbToLinear(
            packetModulation,
            isPs1TexturedModulation: true);
        var decodedNeutralLinear = PsxGeometryHelpers.DisplayRgbToLinear(
            new Vector4(131f / 255f, 131f / 255f, 131f / 255f, 1f));
        var modulatedEdge = texturedLinear * decodedNeutralLinear;

        Assert.InRange(MathF.Abs(modulatedEdge.X - untexturedLinear.X), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(modulatedEdge.Y - untexturedLinear.Y), 0f, 1e-6f);
        Assert.InRange(MathF.Abs(modulatedEdge.Z - untexturedLinear.Z), 0f, 1e-6f);
        Assert.True(texturedLinear.X > 1f);
        Assert.True(texturedLinear.Z > 1f);
    }

    [Fact]
    public void DisplayRgbToLinear_RunsAfterNativeBlendProxyAlpha()
    {
        var additive = new PsxFace
        {
            Flags = 0x00C0,
            IsTextured = false,
            IsSemiTransparent = true
        };

        var nativeBlend = PsxGeometryHelpers.ApplyPsxUntexturedBlend(
            additive,
            new Vector4(0.25f, 0.5f, 0.125f, 1f));
        var converted = PsxGeometryHelpers.DisplayRgbToLinear(nativeBlend);

        Assert.Equal(Vector3.One, new Vector3(converted.X, converted.Y, converted.Z));
        Assert.Equal(0.5f, converted.W);
    }

    [Fact]
    public void ComputePsxTextureUv_Ps1AddressesTexelCentresWithoutDisablingRepeat()
    {
        var face = new PsxFace { IsTextured = true };

        var first = PsxGeometryHelpers.ComputePsxTextureUv(0x04, face, 0, 0, 64, 32);
        var nextTile = PsxGeometryHelpers.ComputePsxTextureUv(0x04, face, 64, 32, 64, 32);

        Assert.Equal(0.5f / 64f, first.X);
        Assert.Equal(0.5f / 32f, first.Y);
        Assert.Equal(1f + 0.5f / 64f, nextTile.X);
        Assert.Equal(1f + 0.5f / 32f, nextTile.Y);
    }

    [Fact]
    public void ComputePsxTextureUv_V6RetainsFixedPortCoordinateSpace()
    {
        var face = new PsxFace { IsTextured = true };

        var uv = PsxGeometryHelpers.ComputePsxTextureUv(0x06, face, 128, 256, 64, 32);

        Assert.Equal(0.25f, uv.X);
        Assert.Equal(0.5f, uv.Y);
    }

    [Fact]
    public void TextureWibble_V6KeepsWidenedBaseUvsAndUsesNativePortScrollScale()
    {
        var face = new PsxFace
        {
            IsTextured = true,
            TextureCoordinates =
            [
                new PsxTextureCoordinate(128, 256),
                new PsxTextureCoordinate(192, 256),
                new PsxTextureCoordinate(128, 320),
                default
            ]
        };
        face.ApplyTextureWibble(new PsxTextureWibble
        {
            UVelocity = -4096,
            VVelocity = 2048,
            Frequency = 595,
            ZeroUAmplitudes = false,
            ZeroVAmplitudes = false,
            UsesFaceTextureCoordinates = true,
            Vertices =
            [
                new PsxTextureWibbleVertex(0, 0, 0x31, 0x42),
                new PsxTextureWibbleVertex(0, 0, 0x53, 0x64),
                new PsxTextureWibbleVertex(0, 0, 0x75, 0x86),
                new PsxTextureWibbleVertex(0, 0, 0, 0)
            ]
        });

        Assert.Equal(new PsxTextureCoordinate(128, 256), face.GetTextureCoordinate(0));
        var motion = ModelTextureWibble.FromFace(0x06, face, 0, (64, 32));
        Assert.True(motion.HasValue);
        var value = motion.GetValueOrDefault();
        Assert.Equal(-8192, value.UVelocity);
        Assert.Equal(4096, value.VVelocity);
        Assert.Equal(512, value.TextureWidth);
        Assert.Equal(512, value.TextureHeight);
        Assert.Equal((byte)3, value.UAmplitude);
        Assert.Equal((byte)1, value.UPhase);
        Assert.Equal((byte)4, value.VAmplitude);
        Assert.Equal((byte)2, value.VPhase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TexturedAbrMaterials_KeepDistinctProcessedTexturesRegardlessOfCreationOrder(
        bool additiveFirst)
    {
        const uint hash = 0x06A567C0u;
        var sourcePng = CreatePng(new Rgba32(64, 128, 192, 255));
        var document = new ModelDocument { Name = "psx-abr-variant" };
        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache =
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();

        var states = new[]
        {
            (SemiTransparent: false, DoubleSided: false, BlendRate: 0),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 0),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 1),
            (SemiTransparent: true, DoubleSided: true, BlendRate: 1),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 2),
            (SemiTransparent: true, DoubleSided: false, BlendRate: 3)
        };
        if (additiveFirst)
            Array.Reverse(states);
        foreach (var state in states)
        {
            PsxGeometryHelpers.GetOrCreatePsxMaterial(
                document,
                hash,
                state.SemiTransparent,
                state.DoubleSided,
                state.BlendRate,
                _ => sourcePng,
                textureDims,
                materialCache);
        }

        // Opaque plus four genuinely different ABR transforms. The one- and
        // two-sided ABR1 materials have byte-identical images and must share.
        Assert.Equal(5, document.Textures.Count);
        Assert.All(document.Textures, texture => Assert.Equal(hash, texture.NativeChecksum));

        var opaqueMaterial = Assert.Single(document.Materials, material => material.Name == "tex_06A567C0");
        var additiveMaterial = Assert.Single(document.Materials, material => material.Name == "tex_06A567C0__st1");
        var additiveTwoSided = Assert.Single(
            document.Materials,
            material => material.Name == "tex_06A567C0__st1__2sided");
        Assert.NotEqual(opaqueMaterial.TextureIndex, additiveMaterial.TextureIndex);
        Assert.Equal(additiveMaterial.TextureIndex, additiveTwoSided.TextureIndex);
        Assert.Equal(ModelAlphaMode.Opaque, opaqueMaterial.AlphaMode);
        Assert.Equal(ModelAlphaMode.Blend, additiveMaterial.AlphaMode);

        using var opaque = Image.Load<Rgba32>(document.Textures[opaqueMaterial.TextureIndex!.Value].PngBytes!);
        using var additive = Image.Load<Rgba32>(document.Textures[additiveMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(64, 128, 192, 255), opaque[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255, 115), additive[0, 0]);
    }

    [Fact]
    public void TexturedAbrMaterial_BlendsOnlyRuntimeStpTexels()
    {
        const uint hash = 0x44DB120Eu;
        using var source = new Image<Rgba32>(3, 1);
        source[0, 0] = new Rgba32(80, 96, 112, 253); // runtime-opaque CLUT entry
        source[1, 0] = new Rgba32(80, 96, 112, 254); // mesh-only STP sentinel
        source[2, 0] = new Rgba32(0, 0, 0, 0);       // transparent key
        using var sourceStream = new MemoryStream();
        source.SaveAsPng(sourceStream);
        var sourcePng = sourceStream.ToArray();
        var document = new ModelDocument { Name = "psx-stp" };
        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache =
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();

        var opaqueIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document, hash, false, false, 0, _ => sourcePng, textureDims, materialCache);
        var blendedIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document, hash, true, false, 0, _ => sourcePng, textureDims, materialCache);

        var opaqueMaterial = document.Materials[opaqueIndex];
        var blendedMaterial = document.Materials[blendedIndex];
        using var opaque = Image.Load<Rgba32>(document.Textures[opaqueMaterial.TextureIndex!.Value].PngBytes!);
        using var blended = Image.Load<Rgba32>(document.Textures[blendedMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new byte[] { 255, 255, 0 }, Enumerable.Range(0, 3).Select(x => opaque[x, 0].A).ToArray());
        Assert.Equal(new byte[] { 255, 128, 0 }, Enumerable.Range(0, 3).Select(x => blended[x, 0].A).ToArray());
        Assert.Equal(ModelAlphaMode.Mask, opaqueMaterial.AlphaMode);
        Assert.Equal(ModelAlphaMode.Blend, blendedMaterial.AlphaMode);
    }

    [Fact]
    public void MixedRuntimePalette_Abr1UsesConditionalAlphaMaterialInsteadOfWholeMaterialAdditive()
    {
        const uint mixedHash = 0x1234ABCDu;
        using var mixedSource = new Image<Rgba32>(3, 1);
        mixedSource[0, 0] = new Rgba32(64, 80, 96, 253);
        mixedSource[1, 0] = new Rgba32(64, 80, 96, 254);
        mixedSource[2, 0] = new Rgba32(0, 0, 0, 0);
        using var mixedStream = new MemoryStream();
        mixedSource.SaveAsPng(mixedStream);
        var document = new ModelDocument { Name = "psx-mixed-additive" };
        var materialIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document,
            mixedHash,
            semiTransparent: true,
            doubleSided: false,
            blendRate: 1,
            _ => mixedStream.ToArray(),
            new Dictionary<uint, (int Width, int Height)>(),
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>());

        var material = document.Materials[materialIndex];
        Assert.EndsWith("__st1__conditional", material.Name, StringComparison.Ordinal);
        using var processed = Image.Load<Rgba32>(document.Textures[material.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(64, 80, 96, 255), processed[0, 0]);
        Assert.Equal(new Rgba32(255, 255, 255, 77), processed[1, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), processed[2, 0]);

        const uint allStpHash = 0x1234ABCEu;
        using var allStpSource = new Image<Rgba32>(1, 1, new Rgba32(64, 80, 96, 254));
        using var allStpStream = new MemoryStream();
        allStpSource.SaveAsPng(allStpStream);
        var allStpIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document,
            allStpHash,
            semiTransparent: true,
            doubleSided: false,
            blendRate: 1,
            _ => allStpStream.ToArray(),
            new Dictionary<uint, (int Width, int Height)>(),
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>());
        var allStpMaterial = document.Materials[allStpIndex];
        Assert.EndsWith("__st1", allStpMaterial.Name, StringComparison.Ordinal);
        using var allStpProcessed = Image.Load<Rgba32>(
            document.Textures[allStpMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(64, 80, 96, 255), allStpProcessed[0, 0]);

        const uint allStpQuarterHash = 0x1234ABCFu;
        var allStpQuarterIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document,
            allStpQuarterHash,
            semiTransparent: true,
            doubleSided: false,
            blendRate: 3,
            _ => allStpStream.ToArray(),
            new Dictionary<uint, (int Width, int Height)>(),
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>());
        var allStpQuarterMaterial = document.Materials[allStpQuarterIndex];
        Assert.EndsWith("__st3", allStpQuarterMaterial.Name, StringComparison.Ordinal);
        using var allStpQuarterProcessed = Image.Load<Rgba32>(
            document.Textures[allStpQuarterMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(64, 80, 96, 64), allStpQuarterProcessed[0, 0]);
    }

    [Fact]
    public void DecodedRuntimePalette_DecodesEveryExactMagentaKeySlot()
    {
        const uint hash = 0x44DB120Eu;
        var header = new PsxTextureHeader
        {
            PalSize = 256,
            TexId = hash,
            Width = 4,
            Height = 1
        };
        var palette = new PsxPalette
        {
            TexId = hash,
            ColorData = [0, 0x7C1F, 0xFC1E, 0x7C1F, 0xFC1F]
        };
        // The decoder's native row order maps these to palette indices
        // 1, 2, 3, 4 in the returned pixel array.
        using var input = new MemoryStream([1, 4, 3, 2], writable: false);
        using var reader = new BinaryReader(input);
        var pixels = Ps1TextureDecoder.Extract8BitTexture(
            reader,
            header,
            [palette],
            preserveRuntimeSemiTransparency: true);

        Assert.NotNull(pixels);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 246, 0, 255, 254 }, pixels.AsSpan(4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan(8, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan(12, 4).ToArray());

        using var decoded = Image.LoadPixelData<Rgba32>(pixels, header.Width, header.Height);
        using var decodedStream = new MemoryStream();
        decoded.SaveAsPng(decodedStream);
        var decodedPng = decodedStream.ToArray();
        var document = new ModelDocument { Name = "decoded-psx-stp" };
        var textureDims = new Dictionary<uint, (int Width, int Height)>();
        var materialCache =
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>();

        var opaqueIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document, hash, false, false, 0, _ => decodedPng, textureDims, materialCache);
        var blendedIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document, hash, true, false, 0, _ => decodedPng, textureDims, materialCache);

        var opaqueMaterial = document.Materials[opaqueIndex];
        var blendedMaterial = document.Materials[blendedIndex];
        using var opaque = Image.Load<Rgba32>(document.Textures[opaqueMaterial.TextureIndex!.Value].PngBytes!);
        using var blended = Image.Load<Rgba32>(document.Textures[blendedMaterial.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(0, 0, 0, 0), opaque[0, 0]);
        Assert.Equal(new Rgba32(246, 0, 255, 255), opaque[1, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), opaque[2, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), opaque[3, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), blended[0, 0]);
        Assert.Equal(new Rgba32(246, 0, 255, 128), blended[1, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), blended[2, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), blended[3, 0]);
    }

    [Fact]
    public void FourBitRuntimePalette_PreservesPreKeyStpAndMakesNoKeyPaletteAllStp()
    {
        const uint hash = 0x10203040u;
        var header = new PsxTextureHeader
        {
            PalSize = 16,
            TexId = hash,
            Width = 8,
            Height = 1
        };
        var keyedPalette = new PsxPalette
        {
            TexId = hash,
            ColorData =
            [
                0x001F, // ordinary red before the key
                0x0000, // raw black is forced STP-visible before the key
                0x801F, // authored bit-15 red before the key
                0x7C1F, // first magenta key
                0x03E0, // ordinary entries after the key are forced STP
                0x0000,
                0xFC00,
                0x7C1F, // repeated artist key slot remains transparent
                0x001F,
                0x03E0,
                0x7C00,
                0x7FFF,
                0x001F,
                0x03E0,
                0x7C00,
                0x7FFF
            ]
        };

        using var keyedInput = new MemoryStream([0x10, 0x32, 0x54, 0x76], writable: false);
        using var keyedReader = new BinaryReader(keyedInput);
        var keyedPixels = Ps1TextureDecoder.Extract4BitTexture(
            keyedReader,
            header,
            [keyedPalette],
            preserveRuntimeSemiTransparency: true);

        Assert.NotNull(keyedPixels);
        Assert.Equal(
            new byte[] { 253, 0, 254, 254, 254, 0, 254, 254 },
            Enumerable.Range(0, 8).Select(index => keyedPixels[index * 4 + 3]).ToArray());

        var noKeyPalette = new PsxPalette
        {
            TexId = hash,
            ColorData =
            [
                0x001F, 0x03E0, 0x7C00, 0x7FFF,
                0x001F, 0x03E0, 0x7C00, 0x7FFF,
                0x001F, 0x03E0, 0x7C00, 0x7FFF,
                0x001F, 0x03E0, 0x7C00, 0x7FFF
            ]
        };
        using var noKeyInput = new MemoryStream([0x10, 0x32, 0x54, 0x76], writable: false);
        using var noKeyReader = new BinaryReader(noKeyInput);
        var noKeyPixels = Ps1TextureDecoder.Extract4BitTexture(
            noKeyReader,
            header,
            [noKeyPalette],
            preserveRuntimeSemiTransparency: true);

        Assert.NotNull(noKeyPixels);
        Assert.All(
            Enumerable.Range(0, 8),
            index => Assert.Equal(Ps1TextureDecoder.RuntimeSemiTransparencyAlpha,
                noKeyPixels[index * 4 + 3]));
    }

    [Fact]
    public void DecodedRuntimePaletteWithoutStp_KeepsOrdinaryTexelsOpaqueOnAbeFace()
    {
        const uint hash = 0x91ABCDEFu;
        var header = new PsxTextureHeader
        {
            PalSize = 256,
            TexId = hash,
            Width = 2,
            Height = 1
        };
        var palette = new PsxPalette
        {
            TexId = hash,
            ColorData = [0x001F, 0x7C1F]
        };
        using var input = new MemoryStream([0, 1, 0, 0], writable: false);
        using var reader = new BinaryReader(input);
        var pixels = Ps1TextureDecoder.Extract8BitTexture(
            reader,
            header,
            [palette],
            preserveRuntimeSemiTransparency: true);

        Assert.NotNull(pixels);
        Assert.Equal(new byte[] { 255, 0, 0, 253 }, pixels.AsSpan(0, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, pixels.AsSpan(4, 4).ToArray());

        using var decoded = Image.LoadPixelData<Rgba32>(pixels, header.Width, header.Height);
        using var decodedStream = new MemoryStream();
        decoded.SaveAsPng(decodedStream);
        var decodedPng = decodedStream.ToArray();
        var document = new ModelDocument { Name = "decoded-psx-no-stp" };
        var materialIndex = PsxGeometryHelpers.GetOrCreatePsxMaterial(
            document,
            hash,
            semiTransparent: true,
            doubleSided: false,
            blendRate: 0,
            _ => decodedPng,
            new Dictionary<uint, (int Width, int Height)>(),
            new Dictionary<(uint Hash, bool SemiTransparent, bool DoubleSided, int BlendRate), int>());

        var material = document.Materials[materialIndex];
        using var processed = Image.Load<Rgba32>(document.Textures[material.TextureIndex!.Value].PngBytes!);
        Assert.Equal(new Rgba32(255, 0, 0, 255), processed[0, 0]);
        Assert.Equal(new Rgba32(0, 0, 0, 0), processed[1, 0]);
        Assert.Equal(ModelAlphaMode.Blend, material.AlphaMode);
    }

    private static byte[] CreatePng(Rgba32 pixel)
    {
        using var image = new Image<Rgba32>(1, 1, pixel);
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }
}
