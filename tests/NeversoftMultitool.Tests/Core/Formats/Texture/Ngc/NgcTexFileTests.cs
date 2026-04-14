using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ngc;

public sealed class NgcTexFileTests(TestPaths paths)
{
    private string RepresentativeSampleFile =>
        paths.SampleBuildsDir is null ? string.Empty : Path.Combine(
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
        var data = NgcTexTestBuilder.CreateDictionary(formatA: 0, formatB: 0);

        var result = NgcTexFile.Parse(data);

        Assert.False(result.Success);
        Assert.Contains("Unsupported NGC texture format", result.ErrorMessage);
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
}
