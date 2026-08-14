using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.N64;

/// <summary>
///     Pins the N64 texture decode (2026-08-05) against fixtures whose
///     appearance was verified externally: 'abutton' renders the N64
///     controller's blue A button (RGBA16 + a full-resolution 4bpp
///     alpha/coverage plane), 's2ddi02t' reproduces
///     the THPS2 SWEET HEART deck art layout (CI4, authored recolor variant
///     of the PS1 original), 'psxtxt_bfd7c623' matches the PS1 skven_l.psx
///     porta-potty prop via the texture-id join — the fixture that
///     established 64-bit row padding (32-bit padding scrambles it) — and
///     'biglight' (I4 score digit) is the user-reported striping exemplar
///     that pinned the 0x3F payload start: decoding from 0x40 misphases the
///     odd-row half-swap grid into 2-texel sliver swaps at every 32-bit
///     seam.
/// </summary>
public sealed class N64TexFileTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    private static Dictionary<string, byte[]> CarveThps2(TestPaths paths)
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets.ToDictionary(static asset => asset.Path, static asset => asset.Data);
    }

    [Fact]
    public void DictionaryRecord_DecodesEachCompleteStoredMip()
    {
        // 4x4 RGBA16 (32 bytes) followed by a 2x2 level (16 bytes).
        // Fixed source words deliberately exercise the odd-row half swap in
        // both levels; the exact output arrays make a skipped or misframed
        // level visible without relying on the external ROM corpus.
        var data = N64TexTestBuilder.CreateDictionaryWithCompleteStoredMipChain();

        var texture = N64TexFile.Decode(data);

        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal("RGBA16", texture.Format);
        Assert.Equal(0, texture.UndecodedPayloadByteCount);
        Assert.Collection(
            texture.MipLevels,
            top =>
            {
                Assert.Equal(0, top.Level);
                Assert.Equal(4, top.Width);
                Assert.Equal(4, top.Height);
                Assert.Same(texture.Rgba, top.Rgba);
                Assert.Equal(
                    [
                        255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255,
                        255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 0, 255, 0, 0, 0, 0,
                        0, 0, 255, 255, 255, 255, 255, 255, 255, 0, 0, 255, 0, 255, 0, 255,
                        255, 255, 255, 255, 0, 0, 0, 255, 255, 0, 0, 255, 0, 0, 255, 255
                    ],
                    top.Rgba);
            },
            mip =>
            {
                Assert.Equal(1, mip.Level);
                Assert.Equal(2, mip.Width);
                Assert.Equal(2, mip.Height);
                Assert.Equal(
                    [
                        0, 255, 0, 255, 0, 0, 255, 255,
                        255, 0, 0, 255, 255, 255, 255, 255
                    ],
                    mip.Rgba);
            });
    }

    [Fact]
    public void DictionaryRecord_DoesNotPublishAPartialMipChain()
    {
        var data = new byte[0x3F + 36];
        Encoding.ASCII.GetBytes("partial").CopyTo(data, 0);
        data[0x21] = 4;
        data[0x23] = 4;
        data[0x27] = 0x10;
        data[0x2B] = 36;

        var texture = N64TexFile.Decode(data);

        var top = Assert.Single(texture.MipLevels);
        Assert.Equal((0, 4, 4, 4 * 4 * 4),
            (top.Level, top.Width, top.Height, top.Rgba.Length));
        Assert.Equal(4, texture.UndecodedPayloadByteCount);
        Assert.False(texture.HasAuxiliaryPlane);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void IntensityFormats_ReplicateIntensityIntoEveryRgbaChannel(int bitsPerTexel)
    {
        var data = N64TexTestBuilder.CreateIntensityRecord(bitsPerTexel, renderFlags: 1);

        var texture = N64TexFile.Decode(data);

        Assert.Equal(bitsPerTexel == 4 ? "I4" : "I8", texture.Format);
        Assert.Equal(N64TexFile.N64TextureRenderClass.TextureCoverage, texture.RenderClass);
        var intensity = bitsPerTexel == 4 ? (byte)255 : (byte)128;
        Assert.Equal([0, 0, 0, 0, intensity, intensity, intensity, intensity], texture.Rgba);
    }

    [Theory]
    [InlineData(0u, N64TexFile.N64TextureRenderClass.Opaque)]
    [InlineData(1u, N64TexFile.N64TextureRenderClass.TextureCoverage)]
    [InlineData(2u, N64TexFile.N64TextureRenderClass.Opaque)]
    [InlineData(3u, N64TexFile.N64TextureRenderClass.Translucent)]
    public void DictionaryRecord_ExposesTheAuthoredRdpRenderClass(
        uint renderFlags,
        N64TexFile.N64TextureRenderClass expected)
    {
        var texture = N64TexFile.Decode(N64TexTestBuilder.CreateIntensityRecord(8, renderFlags));

        Assert.Equal(expected, texture.RenderClass);
    }

    [Fact]
    public void DictionaryRecord_CustomAlphaThreshold_DoesNotClaimALowBitRenderClass()
    {
        var data = N64TexTestBuilder.CreateIntensityRecord(8, renderFlags: 1);
        data[0x2E] = 0x80;

        var texture = N64TexFile.Decode(data);

        Assert.Equal(N64TexFile.N64TextureRenderClass.Unspecified, texture.RenderClass);
    }

    [Fact]
    public void Abutton_DoesNotMisclassifyItsFullResolutionAuxiliaryPlaneAsAMip()
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets["textures/0000_abutton.tex.n64"]);

        var top = Assert.Single(texture.MipLevels);
        Assert.Equal((0, 16, 16, 16 * 16 * 4),
            (top.Level, top.Width, top.Height, top.Rgba.Length));
        Assert.Equal(
            "fc2deb993434f46f273e63aad83ee6e8ec01fb3510fb5f884adc2bbe8f143e1a",
            Convert.ToHexStringLower(SHA256.HashData(top.Rgba)));
        Assert.True(texture.HasAuxiliaryPlane);
        Assert.Equal(16 * 16 / 2, texture.UndecodedPayloadByteCount);
    }

    [Fact]
    public void Ia8Record_ExposesItsCompleteShaPinnedMipChain()
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets["textures/1566_psxtxt_221c0004.tex.n64"]);
        (int Level, int Width, int Height, int Bytes, string Sha256)[] expected =
        [
            (0, 16, 16, 1024, "4e34f3b3b6a6d366dbd8019818ac958d96b0c01faeed6e7818e642ae87c29234"),
            (1, 8, 8, 256, "3e4a41b3fb2e0b83f3251c3f3fbff4fc4aa9c877ae6d08e68f9b9c00f713cb0d"),
            (2, 4, 4, 64, "66d917d7460474bf2938fc8126fa2b6253f7824422270cd96c48994a4cb5f5c5"),
            (3, 2, 2, 16, "26b8eb78f05268d12d78dff2d8114aadab989387eb56c17dec0701441e641136"),
            (4, 1, 1, 4, "eea30439cc7cf92c547ef04ae8845d1cce1fbd165e83651bb6536064b0fb20c1")
        ];

        Assert.Equal("IA8", texture.Format);
        Assert.Equal(expected.Length, texture.MipLevels.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            var actual = texture.MipLevels[i];
            Assert.Equal(
                (expected[i].Level, expected[i].Width, expected[i].Height, expected[i].Bytes),
                (actual.Level, actual.Width, actual.Height, actual.Rgba.Length));
            Assert.Equal(expected[i].Sha256,
                Convert.ToHexStringLower(SHA256.HashData(actual.Rgba)));
        }

        Assert.False(texture.HasAuxiliaryPlane);
        Assert.Equal(0, texture.UndecodedPayloadByteCount);
    }

    [Theory]
    [InlineData("textures/0000_abutton.tex.n64", "abutton", 16, 16, "RGBA16",
        "fc2deb993434f46f273e63aad83ee6e8ec01fb3510fb5f884adc2bbe8f143e1a")]
    [InlineData("textures/0100_s2ddi02t.tex.n64", "s2ddi02t", 32, 128, "CI4",
        "4d9c153e0754b3a4a49c5617163a5f8269e78c6aa296f3371a54be7bbffc814b")]
    [InlineData("textures/2816_psxtxt_bfd7c623.tex.n64", "psxtxt_bfd7c623", 24, 48, "CI4",
        "8b0928b223dcede20ce74c2715b0d9a502fe617f57d838223f646851dd945d29")]
    [InlineData("textures/0438_biglight.tex.n64", "biglight", 64, 64, "I4",
        "e9f217989ed17f7370b4d03a82d92c1bb1fdf401a4a7185072b1d4432343d2ef")]
    public void DictionaryRecords_DecodeTheVerifiedFixtures(
        string assetPath,
        string expectedName,
        int expectedWidth,
        int expectedHeight,
        string expectedFormat,
        string expectedRgbaSha256)
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets[assetPath]);

        Assert.Equal(expectedName, texture.Name);
        Assert.Equal(expectedWidth, texture.Width);
        Assert.Equal(expectedHeight, texture.Height);
        Assert.Equal(expectedFormat, texture.Format);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(texture.Rgba));
        Assert.True(expectedRgbaSha256 == actualSha, $"RGBA sha mismatch: actual {actualSha}");
    }

    [Fact]
    public void ImageRecord_DecodesTheMenuBackground()
    {
        var assets = CarveThps2(paths);
        var texture = N64TexFile.Decode(assets["images/003.img.n64"]);

        Assert.Equal(512, texture.Width);
        Assert.Equal(240, texture.Height);
        Assert.Equal("CI8", texture.Format);
        var actualSha = Convert.ToHexStringLower(SHA256.HashData(texture.Rgba));
        Assert.True(
            actualSha == "5e745cf04c7d6abf3a76bed7ba77a867e67b3e47367f5c84ba53d28d8912b155",
            $"RGBA sha mismatch: actual {actualSha}");
    }

    /// <summary>
    ///     Full-corpus sweeps: every carved texture and image record in every
    ///     ROM must decode without error.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 1_488, 228, 7, 6)]
    [InlineData(Thps2N64Build, RomName, 2_905, 99, 9, 35)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 2_484, 109, 12, 12)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)",
        "Spider-Man (USA).z64", 2_582, 46, 8, 16)]
    public void EveryCarvedTextureDecodes(
        string buildName,
        string romName,
        int expectedTextures,
        int expectedImages,
        int expectedTexturesWithMips,
        int expectedTexturesWithAuxiliaryPlanes)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        var textures = 0;
        var images = 0;
        var texturesWithMips = 0;
        var texturesWithAuxiliaryPlanes = 0;
        foreach (var asset in assets)
        {
            if (asset.Path.EndsWith(".tex.n64", StringComparison.Ordinal))
            {
                var texture = N64TexFile.Decode(asset.Data);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
                Assert.All(texture.MipLevels, static mip =>
                    Assert.Equal(mip.Width * mip.Height * 4, mip.Rgba.Length));
                if (texture.MipLevels.Count > 1)
                    texturesWithMips++;
                if (texture.HasAuxiliaryPlane)
                {
                    var auxiliaryStride = ((texture.Width * 4 + 7) / 8 + 7) & ~7;
                    Assert.Equal(auxiliaryStride * texture.Height,
                        texture.UndecodedPayloadByteCount);
                    Assert.Single(texture.MipLevels);
                    texturesWithAuxiliaryPlanes++;
                }
                else
                {
                    Assert.Equal(0, texture.UndecodedPayloadByteCount);
                }
                textures++;
            }
            else if (asset.Path.EndsWith(".img.n64", StringComparison.Ordinal))
            {
                var texture = N64TexFile.Decode(asset.Data);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Rgba.Length);
                images++;
            }
        }

        Assert.Equal(expectedTextures, textures);
        Assert.Equal(expectedImages, images);
        Assert.Equal(expectedTexturesWithMips, texturesWithMips);
        Assert.Equal(expectedTexturesWithAuxiliaryPlanes, texturesWithAuxiliaryPlanes);
    }
}

internal static class N64TexTestBuilder
{
    public static byte[] CreateIntensityRecord(int bitsPerTexel, uint renderFlags)
    {
        if (bitsPerTexel is not (4 or 8))
            throw new ArgumentOutOfRangeException(nameof(bitsPerTexel));

        const int dataSize = 8; // One 64-bit TMEM row.
        var data = new byte[0x3F + dataSize];
        Encoding.ASCII.GetBytes($"i{bitsPerTexel}").CopyTo(data, 0);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x20), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x22), 1);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x26), (ushort)(0x0400 | bitsPerTexel));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0x2A), dataSize);
        data[0x2E] = 0xFF;
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x2F), renderFlags);
        data[0x3F] = bitsPerTexel == 4 ? (byte)0x0F : (byte)0x00;
        if (bitsPerTexel == 8)
            data[0x40] = 0x80;
        return data;
    }

    public static byte[] CreateDictionaryWithCompleteStoredMipChain()
    {
        var data = new byte[0x3F + 48];
        Encoding.ASCII.GetBytes("mips").CopyTo(data, 0);
        data[0x21] = 4;
        data[0x23] = 4;
        data[0x27] = 0x10;
        data[0x2B] = 48;

        byte[] stored =
        [
            // 4x4 top level: odd rows decode word order 2,3,0,1.
            0xF8, 0x01, 0x07, 0xC1, 0x00, 0x3F, 0xFF, 0xFF,
            0x00, 0x01, 0x00, 0x00, 0xF8, 0x01, 0x07, 0xC1,
            0x00, 0x3F, 0xFF, 0xFF, 0xF8, 0x01, 0x07, 0xC1,
            0xF8, 0x01, 0x00, 0x3F, 0xFF, 0xFF, 0x00, 0x01,
            // 2x2 mip: its odd row lives in the second 32-bit half.
            0x07, 0xC1, 0x00, 0x3F, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0xF8, 0x01, 0xFF, 0xFF
        ];
        stored.CopyTo(data, 0x3F);
        return data;
    }

    public static byte[] CreateImageRecord()
    {
        var data = new byte[51];
        BinaryPrimitives.WriteUInt32BigEndian(data, 3);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), 20);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), 48);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), 50);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(16), 51);

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0x00080410);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(24), 3);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(36), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(44), 0);

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 0xF801);
        data[50] = 0;
        return data;
    }
}
