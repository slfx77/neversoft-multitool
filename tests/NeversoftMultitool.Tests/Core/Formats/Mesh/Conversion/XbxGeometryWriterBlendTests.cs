using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Synthetic-fixture coverage for the THUG2/THAW Xbox/PC/GC blend baking:
///     pass-0 framebuffer ADD/SUBTRACT bakes (<see cref="XbxPassCompositor" />),
///     pass-k overlay compositing, synthetic-checksum registration (so shared
///     source textures cannot be cache-poisoned by a baked variant), and the
///     THAW PC reader's ColorW → FixedAlpha decode.
/// </summary>
public sealed class XbxGeometryWriterBlendTests
{
    private const uint MaterialChecksum = 0x11223344;
    private const uint BaseTextureChecksum = 0x55667788;
    private const uint OverlayTextureChecksum = 0x66778899;

    [Fact]
    public void SkinVertexCodec_ReadsU16BoneIndicesTimesThree()
    {
        // The skinned vertex record stores bone indices as 4×u16 pre-multiplied
        // by 3 (nxtools fmt_thscene_import.py; ped_boone_full's sMesh stride 48
        // only fits this layout). The codec must consume exactly 12 bytes so the
        // packed normal / colour / UV fields that follow stay aligned — the old
        // 4×u8 read shifted them all by 4 (rainbow vertex colours, cardinal
        // normals, smeared UVs on every weight-flagged THUG2/THAW PC skin).
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write(0x7FFu);            // packed weights: weight0 = 1.0
            w.Write((ushort)(5 * 3));   // bone 5
            w.Write((ushort)(9 * 3));   // bone 9
            w.Write((ushort)(12 * 3));  // bone 12
            w.Write((ushort)(40 * 3));  // bone 40
        }

        ms.Position = 0;
        using var r = new BinaryReader(ms);
        var vertex = new XbxVertex();
        XbxSkinVertexCodec.ReadSkinningData(r, ref vertex);

        Assert.Equal(12, ms.Position);
        Assert.Equal(5, vertex.BoneIndex0);
        Assert.Equal(9, vertex.BoneIndex1);
        Assert.Equal(12, vertex.BoneIndex2);
        Assert.Equal(40, vertex.BoneIndex3);
        Assert.True(vertex.HasSkinData);
        Assert.Equal(1f, vertex.BoneWeight0, 2);
    }

    [Fact]
    public void Mode1_AdditiveBake()
    {
        var raw = CreatePngBytes(new Rgba32(64, 32, 16, 255));
        var material = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 1 });

        var document = Convert(CreateScene(material), CreateResolver((BaseTextureChecksum, raw)));

        var texture = Assert.Single(document.Textures);
        Assert.NotNull(texture.PngBytes);
        Assert.False(texture.PngBytes.AsSpan().SequenceEqual(raw));

        // Additive-blend convention: alpha = max source channel, hue preserved
        // at full brightness (ConvertAdditiveBlendTexture).
        var pixel = DecodePixels(texture.PngBytes)[0];
        Assert.Equal(64, pixel.A);
        Assert.Equal(255, pixel.R);

        Assert.NotNull(texture.NativeChecksum);
        Assert.NotEqual(0u, texture.NativeChecksum.Value & 0x80000000u);
        Assert.EndsWith("__add", texture.Name);
        Assert.Equal(ModelAlphaMode.Blend, Assert.Single(document.Materials).AlphaMode);
    }

    [Fact]
    public void Mode2_FixedAlphaHalves()
    {
        var raw = CreatePngBytes(new Rgba32(64, 32, 16, 255));

        var mode1Document = Convert(
            CreateScene(CreateMaterial(
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 1 })),
            CreateResolver((BaseTextureChecksum, raw)));
        var mode2Document = Convert(
            CreateScene(CreateMaterial(
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 2, FixedAlpha = 64 })),
            CreateResolver((BaseTextureChecksum, raw)));

        var mode1Alpha = DecodePixels(Assert.Single(mode1Document.Textures).PngBytes!)[0].A;
        var mode2Alpha = DecodePixels(Assert.Single(mode2Document.Textures).PngBytes!)[0].A;

        // ADD_FIXED with fix = 64 scales the additive alpha by 64/128 = 0.5.
        Assert.Equal(64, mode1Alpha);
        Assert.InRange(mode2Alpha, mode1Alpha / 2 - 1, mode1Alpha / 2 + 1);
    }

    [Fact]
    public void Mode3_SubtractiveBake()
    {
        var raw = CreatePngBytes(new Rgba32(64, 32, 16, 255));
        var material = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 3 });

        var document = Convert(CreateScene(material), CreateResolver((BaseTextureChecksum, raw)));

        // Subtractive: RGB black with alpha = brightness (max channel = 64)
        // scaled by the 0.30 subtractive strength.
        var pixel = DecodePixels(Assert.Single(document.Textures).PngBytes!)[0];
        Assert.Equal(0, pixel.R);
        Assert.Equal(0, pixel.G);
        Assert.Equal(0, pixel.B);
        Assert.Equal((byte)MathF.Round(64 * 0.30f), pixel.A);

        Assert.EndsWith("__sub", Assert.Single(document.Textures).Name);
        Assert.Equal(ModelAlphaMode.Blend, Assert.Single(document.Materials).AlphaMode);
    }

    [Fact]
    public void Mode5_ByteIdentityPin()
    {
        // Mode 5 (vBLEND_MODE_BLEND) is not a bake mode: the texture must ship
        // byte-identical and only the framebuffer AlphaMode changes.
        var graduated = CreatePngBytes(new Rgba32(64, 32, 16, 128), new Rgba32(200, 180, 160, 96));
        var blendDocument = Convert(
            CreateScene(CreateMaterial(
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 5 })),
            CreateResolver((BaseTextureChecksum, graduated)));

        var blendTexture = Assert.Single(blendDocument.Textures);
        Assert.NotNull(blendTexture.PngBytes);
        Assert.True(blendTexture.PngBytes.AsSpan().SequenceEqual(graduated));
        Assert.Equal(BaseTextureChecksum, blendTexture.NativeChecksum);
        Assert.Equal(ModelAlphaMode.Blend, Assert.Single(blendDocument.Materials).AlphaMode);

        // An opaque texture in mode 5 has nothing to blend — the material must
        // keep the default alpha mode (no framebuffer blend, no bake).
        var opaque = CreatePngBytes(new Rgba32(64, 32, 16, 255));
        var opaqueDocument = Convert(
            CreateScene(CreateMaterial(
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 5 })),
            CreateResolver((BaseTextureChecksum, opaque)));

        Assert.True(Assert.Single(opaqueDocument.Textures).PngBytes.AsSpan().SequenceEqual(opaque));
        Assert.Equal(ModelAlphaMode.Opaque, Assert.Single(opaqueDocument.Materials).AlphaMode);
    }

    [Fact]
    public void SharedTexture_CachePoisoningGuard()
    {
        // Two materials share one source texture: a plain mode-0 material and
        // an additive mode-1 material. The bake must register a SEPARATE
        // synthetic texture so the plain material keeps the pristine copy.
        var raw = CreatePngBytes(new Rgba32(64, 32, 16, 255));
        var scene = CreateScene(
            CreateMaterial(0x1000_0001,
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 0 }),
            CreateMaterial(0x1000_0002,
                new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 1 }));

        var document = Convert(scene, CreateResolver((BaseTextureChecksum, raw)));

        Assert.Equal(2, document.Textures.Count);
        var pristine = Assert.Single(document.Textures, t => t.NativeChecksum == BaseTextureChecksum);
        var baked = Assert.Single(document.Textures, t => t.NativeChecksum != BaseTextureChecksum);

        Assert.True(pristine.PngBytes.AsSpan().SequenceEqual(raw));
        Assert.NotNull(baked.NativeChecksum);
        Assert.NotEqual(0u, baked.NativeChecksum.Value & 0x80000000u);
        Assert.NotEqual(document.Materials[0].TextureIndex, document.Materials[1].TextureIndex);
    }

    [Fact]
    public void MultiPass_OverlayComposites()
    {
        var basePixel = new Rgba32(40, 80, 120, 255);
        var basePng = CreatePngBytes(basePixel, basePixel, basePixel);
        var overlayPng = CreatePngBytes(
            new Rgba32(200, 60, 20, 255), // fully opaque overlay texel
            new Rgba32(0, 0, 0, 0), // alpha hole — base must show through
            new Rgba32(200, 60, 20, 128)); // half-alpha lerp

        var material = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 0 },
            new XbxPass { TextureChecksum = OverlayTextureChecksum, BlendMode = 5 });
        var document = Convert(
            CreateScene(material),
            CreateResolver((BaseTextureChecksum, basePng), (OverlayTextureChecksum, overlayPng)));

        var texture = Assert.Single(document.Textures);
        Assert.NotNull(texture.PngBytes);
        Assert.False(texture.PngBytes.AsSpan().SequenceEqual(basePng));
        Assert.EndsWith("__mp", texture.Name);

        var pixels = DecodePixels(texture.PngBytes);
        Assert.Equal(new Rgba32(200, 60, 20, 255), pixels[0]);
        Assert.Equal(basePixel, pixels[1]);

        // Half-alpha texel: RGB lerped by the overlay alpha, base alpha kept
        // (the engine takes framebuffer alpha from pass 0 alone).
        var expected = new Rgba32(
            LerpChannel(basePixel.R, 200, 128),
            LerpChannel(basePixel.G, 60, 128),
            LerpChannel(basePixel.B, 20, 128),
            255);
        Assert.Equal(expected, pixels[2]);

        // Compositing alone never forces a framebuffer blend — pass 0 is mode 0.
        Assert.Equal(ModelAlphaMode.Opaque, Assert.Single(document.Materials).AlphaMode);
    }

    [Fact]
    public void OverlaySkipFlags()
    {
        // Environment passes use camera-generated UVs — they cannot bake, so
        // the base texture must pass through untouched under its own checksum.
        var basePng = CreatePngBytes(new Rgba32(40, 80, 120, 255));
        var overlayPng = CreatePngBytes(new Rgba32(200, 60, 20, 255));
        var material = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 0 },
            new XbxPass
            {
                TextureChecksum = OverlayTextureChecksum,
                BlendMode = 5,
                Flags = XbxMaterialFlags.Environment
            });

        var document = Convert(
            CreateScene(material),
            CreateResolver((BaseTextureChecksum, basePng), (OverlayTextureChecksum, overlayPng)));

        var texture = Assert.Single(document.Textures);
        Assert.True(texture.PngBytes.AsSpan().SequenceEqual(basePng));
        Assert.Equal(BaseTextureChecksum, texture.NativeChecksum);
    }

    [Fact]
    public void OverlayColorModulation()
    {
        // Pass colour modulates the overlay with 0.5 = neutral (engine
        // multiplies then doubles), so (1.0, 0.5, 0.5) doubles red only.
        var basePng = CreatePngBytes(new Rgba32(0, 0, 0, 255), new Rgba32(0, 0, 0, 255));
        var overlayPng = CreatePngBytes(new Rgba32(100, 60, 200, 255), new Rgba32(200, 60, 200, 255));
        var material = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 0 },
            new XbxPass
            {
                TextureChecksum = OverlayTextureChecksum,
                BlendMode = 5,
                HasColor = true,
                Color = new Vector3(1.0f, 0.5f, 0.5f)
            });

        var document = Convert(
            CreateScene(material),
            CreateResolver((BaseTextureChecksum, basePng), (OverlayTextureChecksum, overlayPng)));

        var pixels = DecodePixels(Assert.Single(document.Textures).PngBytes!);
        Assert.Equal(new Rgba32(200, 60, 200, 255), pixels[0]); // red 100×2, G/B neutral
        Assert.Equal(new Rgba32(255, 60, 200, 255), pixels[1]); // red 200×2 clamps at 255
    }

    [Fact]
    public void SyntheticChecksum_Deterministic()
    {
        // The same material baked into two separate documents must land on the
        // same synthetic checksum (deterministic FNV recipe, not a counter).
        var raw = CreatePngBytes(new Rgba32(64, 32, 16, 255));
        uint ChecksumOfConversion()
        {
            var document = Convert(
                CreateScene(CreateMaterial(
                    new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 2, FixedAlpha = 64 })),
                CreateResolver((BaseTextureChecksum, raw)));
            var checksum = Assert.Single(document.Textures).NativeChecksum;
            Assert.NotNull(checksum);
            return checksum.Value;
        }

        var first = ChecksumOfConversion();
        var second = ChecksumOfConversion();
        Assert.Equal(first, second);
        Assert.NotEqual(0u, first & 0x80000000u);

        // A different blend ingredient (FixedAlpha) must produce a different
        // synthetic checksum — the bake result differs.
        var fix64 = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 2, FixedAlpha = 64 });
        var fix32 = CreateMaterial(
            new XbxPass { TextureChecksum = BaseTextureChecksum, BlendMode = 2, FixedAlpha = 32 });
        Assert.NotEqual(
            XbxPassCompositor.CreateSyntheticTextureChecksum(fix64),
            XbxPassCompositor.CreateSyntheticTextureChecksum(fix32));
    }

    [Fact]
    public void ThawReaderB_FixedAlphaFromColorW()
    {
        // THAW PC serializes the engine's m_color[pass] with W = fixed_alpha/128
        // (material.cpp:671): ColorW = 0.5 must decode to FixedAlpha 64.
        using var stream = new MemoryStream(CreateThawMaterialRecord(colorW: 0.5f, blendMode: 2));
        using var reader = new BinaryReader(stream);

        var material = ThawSceneMeshSupport.ReadMaterial(reader);

        var pass = Assert.Single(material.Passes);
        Assert.Equal(2u, pass.BlendMode);
        Assert.Equal(0.5f, pass.ColorW);
        Assert.Equal(64u, pass.FixedAlpha);
    }

    private static byte[] CreateThawMaterialRecord(float colorW, ushort blendMode)
    {
        const int maxPasses = 4;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(0xAABB0001u); // checksum
        writer.Write(0xAABB0002u); // name checksum
        writer.Write(1); // numPasses
        writer.Write(new byte[4]); // unknown byte, doubleSided, u16
        writer.Write(new byte[4]); // opacity cutoff byte, pad, useCutoff u16
        writer.Write(new byte[24]); // skipped block
        writer.Write(0f); // drawOrder

        for (var i = 0; i < maxPasses; i++)
            writer.Write(0u); // pass flags
        writer.Write(0x12345678u); // pass-0 texture checksum
        for (var i = 1; i < maxPasses; i++)
            writer.Write(0u);

        // Pass colours: RGB + the W component under test.
        writer.Write(0.5f);
        writer.Write(0.5f);
        writer.Write(0.5f);
        writer.Write(colorW);
        for (var i = 1; i < maxPasses; i++)
        {
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);
            writer.Write(0f);
        }

        writer.Write(blendMode); // pass-0 u16 blend mode
        writer.Write((short)0); // pass-0 i16 extra
        for (var i = 1; i < maxPasses; i++)
        {
            writer.Write((ushort)0);
            writer.Write((short)0);
        }

        for (var i = 0; i < maxPasses; i++)
            writer.Write(0u); // addressing
        writer.Write(new byte[maxPasses * 8]); // skipped per-pass block
        writer.Write(new byte[maxPasses * 4]); // skipped per-pass block
        writer.Write(new byte[maxPasses * 4]); // skipped per-pass block
        writer.Write(new byte[16]); // skipped block
        for (var i = 0; i < 4; i++)
            writer.Write(0); // trailing i32 quad
        writer.Write(new byte[16]); // final skipped block

        return stream.ToArray();
    }

    private static ModelDocument Convert(ParsedXbxScene scene, MeshChecksumTextureResolver resolver)
    {
        var document = new ModelDocument { Name = "xbx_blend", SourceKind = ModelSourceKind.XbxScene };
        foreach (var material in scene.Materials)
            document.Materials.Add(new RenderMaterial { Name = $"mat_{material.Checksum:X8}" });

        XbxGeometryWriter.PopulateXbxScene(document, scene, resolver);
        return document;
    }

    private static ParsedXbxScene CreateScene(params XbxMaterial[] materials)
    {
        return new ParsedXbxScene
        {
            Materials = materials,
            Sectors = [],
            Links = []
        };
    }

    private static XbxMaterial CreateMaterial(params XbxPass[] passes)
    {
        return CreateMaterial(MaterialChecksum, passes);
    }

    private static XbxMaterial CreateMaterial(uint checksum, params XbxPass[] passes)
    {
        return new XbxMaterial
        {
            Checksum = checksum,
            NumPasses = passes.Length,
            Passes = passes
        };
    }

    private static MeshChecksumTextureResolver CreateResolver(params (uint Checksum, byte[] Png)[] textures)
    {
        var map = textures.ToDictionary(static t => t.Checksum, static t => t.Png);
        return checksum => map.TryGetValue(checksum, out var png) ? png : null;
    }

    private static byte[] CreatePngBytes(params Rgba32[] pixels)
    {
        using var image = new Image<Rgba32>(pixels.Length, 1);
        for (var x = 0; x < pixels.Length; x++)
            image[x, 0] = pixels[x];

        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static Rgba32[] DecodePixels(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        var pixels = new Rgba32[image.Width * image.Height];
        image.CopyPixelDataTo(pixels);
        return pixels;
    }

    private static byte LerpChannel(byte baseValue, byte overlayValue, byte overlayAlpha)
    {
        var a = overlayAlpha / 255f;
        return (byte)Math.Clamp((int)MathF.Round(baseValue + (overlayValue - baseValue) * a), 0, 255);
    }
}
