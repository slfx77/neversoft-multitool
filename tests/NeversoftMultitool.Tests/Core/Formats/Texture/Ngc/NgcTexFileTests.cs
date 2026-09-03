using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ngc;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexFileTests(TestPaths paths)
{
    private const string ThawGameCubeBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";
    private const string DownhillJamWiiBuild =
        "Tony Hawk's Downhill Jam (2006-11-19, Wii - Final)";
    private const string ProvingGroundWiiBuild =
        "Tony Hawk's Proving Ground (2007-10-16, Wii - Final)";

    private string RepresentativeSampleFile =>
        paths.SampleBuildsDir is null
            ? string.Empty
            : Path.Combine(
                paths.SampleBuildsDir,
                "Tony Hawk's American Wasteland (2005-8-22, GC - Final)",
                "models",
                "Animals",
                "Anl_Pigeon",
                "anl_pigeon.tex.ngc");

    [Fact]
    public void TryReadHeader_ReadsBigEndianFields()
    {
        var data = NgcTexTestBuilder.CreateDictionary();

        var success = NgcTexFile.TryReadHeader(data, out var header, out var error);

        Assert.True(success, error);
        Assert.Equal((ushort)1, header.TextureCount);
        Assert.Equal((uint)8, header.MetadataOffset);
    }

    [Fact]
    public void TryReadEntry_ReadsBigEndianFields()
    {
        var data = NgcTexTestBuilder.CreateDictionary(widthLog2: 3, heightLog2: 2, checksum: 0x89ABCDEF);
        Assert.True(NgcTexFile.TryReadHeader(data, out var header, out var error), error);

        var success = NgcTexFile.TryReadEntry(data, header, 0, out var entry, out error);

        Assert.True(success, error);
        Assert.Equal(0x04205211u, entry.Magic);
        Assert.Equal(0x89ABCDEFu, entry.Checksum);
        Assert.Equal(8, entry.Width);
        Assert.Equal(4, entry.Height);
        Assert.Equal((byte)0, entry.WidthPadding);
        Assert.Equal((byte)0, entry.HeightPadding);
        Assert.Equal((byte)14, entry.FormatA);
        Assert.Equal((byte)12, entry.FormatB);
        Assert.Equal(32, entry.DataSize);
        Assert.Equal(40, entry.DataOffset);
    }

    [Fact]
    public void Parse_SupportedDictionary_DecodesTexture()
    {
        var data = NgcTexTestBuilder.CreateDictionary();

        var result = NgcTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.NotNull(texture.Pixels);
        Assert.Equal(4 * 4 * 4, texture.Pixels!.Length);

        for (var i = 0; i < texture.Pixels.Length; i += 4)
        {
            Assert.Equal((byte)0xFF, texture.Pixels[i]);
            Assert.Equal((byte)0x00, texture.Pixels[i + 1]);
            Assert.Equal((byte)0x00, texture.Pixels[i + 2]);
            Assert.Equal((byte)0xFF, texture.Pixels[i + 3]);
        }
    }

    [Fact]
    public void Parse_UnsupportedFormatPair_FailsExplicitly()
    {
        var data = NgcTexTestBuilder.CreateDictionary(0, 0);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains("Unsupported NGC texture format", result.ErrorMessage);
    }

    [Fact]
    public void Parse_WidthExponent32_FailsExplicitly()
    {
        var data = NgcTexTestBuilder.CreateDictionary(widthLog2: 32);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("NGC TEX entry 0 has invalid width exponent 32.", result.ErrorMessage);
    }

    [Fact]
    public void Parse_HeightExponent32_FailsExplicitly()
    {
        var data = NgcTexTestBuilder.CreateDictionary(heightLog2: 32);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Equal("NGC TEX entry 0 has invalid height exponent 32.", result.ErrorMessage);
    }

    [Fact]
    public void Parse_DictionaryExponent30_ReturnsFailureWithoutThrowing()
    {
        var data = NgcTexTestBuilder.CreateDictionary(widthLog2: 30, heightLog2: 30);
        Ps2TexResult? result = null;

        var exception = Record.Exception(() => result = NgcTexFile.Parse(data));

        Assert.Null(exception);
        Assert.NotNull(result);
        Assert.False(result.Success);
        Assert.Equal("NGC TEX entry 0 has invalid width exponent 30.", result.ErrorMessage);
    }

    [Theory]
    [InlineData(0, 5, "invalid record version 5")]
    [InlineData(1, 16, "invalid record depth 16")]
    [InlineData(28, 1, "invalid reserved trailer 0x01000000")]
    public void Parse_InvalidRecordSignatureFields_FailClosed(
        int fieldOffset,
        byte value,
        string expectedError)
    {
        var data = NgcTexTestBuilder.CreateDictionary();
        data[8 + fieldOffset] = value;

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.ErrorMessage);
    }

    [Fact]
    public void Parse_UnknownFormatCompanionConstant_FailsClosed()
    {
        var data = NgcTexTestBuilder.CreateDictionary(formatA: 14, formatB: 99);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains("Unsupported NGC texture format (14,99)", result.ErrorMessage);
    }

    [Fact]
    public void DecodeToRgba_CropsPaddedCmprImageToOriginalDimensions()
    {
        var pixels = NgcTexCmprDecoder.DecodeToRgba(NgcTexTestBuilder.CreateSolidRedCmprTextureData(), 4, 4);

        Assert.Equal(4 * 4 * 4, pixels.Length);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal((byte)0xFF, pixels[i]);
            Assert.Equal((byte)0x00, pixels[i + 1]);
            Assert.Equal((byte)0x00, pixels[i + 2]);
            Assert.Equal((byte)0xFF, pixels[i + 3]);
        }
    }

    [Fact]
    public void Parse_WiiRgba8_UnwrapsThePaddingBytesByPayloadSize()
    {
        // 0x80 is only the low byte of the true 384-pixel width pad:
        // 1024 - (128 + 256) = 640. This shipped class used to decode as
        // 1024x280 and visibly scrambled every tile row.
        var data = NgcTexTestBuilder.CreateBareRecord(
            formatA: 6,
            widthLog2: 10,
            heightLog2: 9,
            widthPadding: 128,
            heightPadding: 64,
            dataSize: 640 * 448 * 4);

        var result = NgcTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((640, 448), (texture.Width, texture.Height));
        Assert.Equal(640 * 448 * 4, texture.Pixels!.Length);
    }

    [Fact]
    public void Parse_WiiC8_UsesBothPaddingDimensions()
    {
        var data = NgcTexTestBuilder.CreateBareRecord(
            formatA: 6,
            widthLog2: 8,
            heightLog2: 6,
            widthPadding: 64,
            heightPadding: 0,
            dataSize: 192 * 64 + NgcTexC8Decoder.PaletteBytes);

        var result = NgcTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((192, 64), (texture.Width, texture.Height));
        Assert.Equal(192 * 64 * 4, texture.Pixels!.Length);
    }

    [Fact]
    public void Parse_FullPotCmpr_DecodesTheStorageSurfaceThenCrops()
    {
        var data = NgcTexTestBuilder.CreateBareRecord(
            formatA: 14,
            widthLog2: 4,
            heightLog2: 4,
            widthPadding: 8,
            heightPadding: 8,
            dataSize: 128); // 16x16 CMPR storage; displayed as 8x8.

        var result = NgcTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((8, 8), (texture.Width, texture.Height));
        Assert.Equal(8 * 8 * 4, texture.Pixels!.Length);
    }

    [Fact]
    public void Parse_RepresentativeSample_Succeeds()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(!File.Exists(RepresentativeSampleFile), "Representative .tex.ngc sample not found");

        var result = NgcTexFile.Parse(RepresentativeSampleFile);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Textures);
        Assert.All(result.Textures, texture =>
        {
            Assert.True(texture.Width > 0);
            Assert.True(texture.Height > 0);
            Assert.NotNull(texture.Pixels);
        });
    }

    [CorpusFact]
    public void Parse_AllThawGameCubeStExFiles_DecodeEveryTexture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(ThawGameCubeBuild, "*.stex.ngc").ToArray();
        Assert.Equal(2846, files.Length);

        var textureCount = 0;
        foreach (var file in files)
        {
            var result = NgcTexFile.Parse(file);
            Assert.True(result.Success, $"{file}: {result.ErrorMessage}");
            Assert.All(result.Textures, texture => Assert.NotNull(texture.Pixels));
            textureCount += result.Textures.Count;
        }

        Assert.Equal(6570, textureCount);
    }

    [CorpusFact]
    public void Parse_AllGameCubeAndWiiImgFiles_ResolvesEveryPaddingIdentity()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var builds = new[]
        {
            (ThawGameCubeBuild, 9440),
            (DownhillJamWiiBuild, 1559),
            (ProvingGroundWiiBuild, 1128)
        };
        var unwrappedWiiFormat6Widths = 0;
        var croppedFullPotCmpr = 0;
        var total = 0;

        foreach (var (build, expectedCount) in builds)
        {
            var files = paths.FindSampleFiles(build, "*.img.ngc").ToArray();
            Assert.Equal(expectedCount, files.Length);
            total += files.Length;

            foreach (var file in files)
            {
                var data = File.ReadAllBytes(file);
                var result = NgcTexFile.Parse(data);
                Assert.True(result.Success, $"{file}: {result.ErrorMessage}");
                var texture = Assert.Single(result.Textures);
                Assert.NotNull(texture.Pixels);
                Assert.Equal(texture.Width * texture.Height * 4, texture.Pixels!.Length);

                var paddedWidth = 1 << data[10];
                var paddedHeight = 1 << data[11];
                if (build != ThawGameCubeBuild && data[13] == 6 && texture.Width != paddedWidth)
                    unwrappedWiiFormat6Widths++;

                var dataSize = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
                var fullPotCmprBytes = (long)((paddedWidth + 7) / 8)
                                       * ((paddedHeight + 7) / 8) * 32;
                var hasLogicalCmprSizeMatch = false;
                for (var widthPad = (int)data[8]; widthPad < paddedWidth; widthPad += 256)
                {
                    for (var heightPad = (int)data[9]; heightPad < paddedHeight; heightPad += 256)
                    {
                        var logicalWidth = paddedWidth - widthPad;
                        var logicalHeight = paddedHeight - heightPad;
                        var logicalBytes = (long)((logicalWidth + 7) / 8)
                                           * ((logicalHeight + 7) / 8) * 32;
                        hasLogicalCmprSizeMatch |= logicalBytes == dataSize;
                    }
                }

                if (data[13] == 14
                    && dataSize == fullPotCmprBytes
                    && !hasLogicalCmprSizeMatch
                    && (data[8] != 0 || data[9] != 0))
                {
                    croppedFullPotCmpr++;
                    Assert.Equal((paddedWidth - data[8], paddedHeight - data[9]),
                        (texture.Width, texture.Height));
                }
            }
        }

        Assert.Equal(12127, total);
        // 284 Wii format-6 records require modulo-256 width unwrapping. The
        // older size heuristic mishandled 286 format-6 records in the full
        // corpus; the other two are THAW GameCube C8 images.
        Assert.Equal(284, unwrappedWiiFormat6Widths);
        Assert.Equal(210, croppedFullPotCmpr);
    }
}

internal static class NgcTexTestBuilder
{
    public static byte[] CreateDictionary(
        byte formatA = 14,
        byte formatB = 12,
        byte widthLog2 = 2,
        byte heightLog2 = 2,
        uint checksum = 0x12345678)
    {
        var data = new byte[72];
        data[0] = 0x01;
        data[1] = 0x08;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), 8);

        var entry = data.AsSpan(8, 32);
        BinaryPrimitives.WriteUInt32BigEndian(entry, 0x04205211u);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], checksum);
        entry[8] = 0;
        entry[9] = 0;
        entry[10] = widthLog2;
        entry[11] = heightLog2;
        entry[12] = 1;
        entry[13] = formatA;
        entry[14] = formatB;
        entry[15] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(entry[16..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(entry[20..], 40);
        BinaryPrimitives.WriteInt32BigEndian(entry[24..], -1);
        BinaryPrimitives.WriteUInt32BigEndian(entry[28..], 0);

        CreateSolidRedCmprTextureData().CopyTo(data, 40);
        return data;
    }

    public static byte[] CreateSolidRedCmprTextureData()
    {
        var data = new byte[32];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(0), 0xF800);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0x001F);
        return data;
    }

    public static byte[] CreateBareRecord(
        byte formatA,
        byte widthLog2,
        byte heightLog2,
        byte widthPadding,
        byte heightPadding,
        int dataSize)
    {
        var data = new byte[checked(32 + dataSize)];
        var entry = data.AsSpan(0, 32);
        entry[0] = 4;
        entry[1] = 32;
        entry[8] = widthPadding;
        entry[9] = heightPadding;
        entry[10] = widthLog2;
        entry[11] = heightLog2;
        entry[12] = 1;
        entry[13] = formatA;
        entry[14] = formatA == 14 ? (byte)12 : (byte)4;
        entry[15] = 4;
        BinaryPrimitives.WriteUInt32BigEndian(entry[16..], (uint)dataSize);
        BinaryPrimitives.WriteUInt32BigEndian(entry[20..], 32);
        BinaryPrimitives.WriteUInt32BigEndian(entry[24..], uint.MaxValue);
        return data;
    }
}
