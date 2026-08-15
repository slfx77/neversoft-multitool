using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class XbxTexFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";

    // ── IsTexFile ──

    [CorpusFact]
    public void IsTexFile_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(XbxTexFile.IsTexFile(data));
    }

    [Fact]
    public void IsTexFile_EmptyData_ReturnsFalse()
    {
        Assert.False(XbxTexFile.IsTexFile([]));
    }

    [Fact]
    public void IsTexFile_WrongVersion_ReturnsFalse()
    {
        Assert.False(XbxTexFile.IsTexFile(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void Parse_NonPositiveMipLevelCount_FailsWithoutTextures(uint rawMipLevelCount)
    {
        var result = XbxTexFile.Parse(CreateSingleEntryTexture(rawMipLevelCount));

        Assert.False(result.Success);
        Assert.Equal(
            $"Texture 0 has invalid mip level count {unchecked((int)rawMipLevelCount)}",
            result.ErrorMessage);
        Assert.Empty(result.Textures);
    }

    [Fact]
    public void Parse_DxtVersion2_DecodesAsDxt1()
    {
        var result = XbxTexFile.Parse(CreateSingleMipVersion2Texture());

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(4, texture.Width);
        Assert.Equal(4, texture.Height);
        Assert.Equal(new byte[4 * 4 * 4], texture.Pixels);
    }

    // ── Parse known files ──

    [CorpusFact]
    public void Parse_AP_Has195Textures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(195, result.Textures.Count);
    }

    [CorpusFact]
    public void Parse_AP_TexturesHaveValidDimensions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        foreach (var tex in result.Textures)
        {
            Assert.True(tex.Width > 0 && tex.Width <= 2048, $"Width {tex.Width} out of range");
            Assert.True(tex.Height > 0 && tex.Height <= 2048, $"Height {tex.Height} out of range");
            Assert.NotNull(tex.Pixels);
            Assert.Equal(tex.Width * tex.Height * 4, tex.Pixels!.Length);
        }
    }

    [CorpusFact]
    public void Parse_AP_TexturesHaveNonZeroChecksums()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        Assert.All(result.Textures, tex => Assert.True(tex.Checksum != 0, "Checksum should not be zero"));
    }

    // ── PNG output ──

    [CorpusFact]
    public void SaveAllAsPng_AP_ProducesPngFiles()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "AP.tex.xbx");
        Assert.SkipWhen(file is null, "AP.tex.xbx not found");

        var result = XbxTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        // Unique per run: a fixed shared path is deleted wholesale in the
        // finally below, so a concurrent or interrupted run leaves this one
        // writing into a directory something else is removing.
        var outputDir = Path.Combine(
            Path.GetTempPath(), "xbxtex_test_" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            var count = XbxTexFile.SaveAllAsPng(result, outputDir, "AP");
            Assert.Equal(195, count);

            var pngs = Directory.GetFiles(Path.Combine(outputDir, "AP"), "*.png");
            Assert.Equal(195, pngs.Length);
        }
        finally
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);
        }
    }

    // ── Batch parse all TEX files ──

    [CorpusFact]
    public void Parse_AllTexFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.tex.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No TEX files found");

        var failures = new List<string>();
        var totalTextures = 0;

        foreach (var file in files)
        {
            var result = XbxTexFile.Parse(file);
            if (!result.Success)
            {
                failures.Add($"{Path.GetFileName(file)}: {result.ErrorMessage}");
                continue;
            }

            totalTextures += result.Textures.Count;
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} files failed:\n" +
            string.Join("\n", failures.Take(20)));
        Assert.True(totalTextures > 0, "Should have extracted textures");
    }

    // ── IMG tests ──

    [CorpusFact]
    public void IsImgFile_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "black.img.xbx");
        Assert.SkipWhen(file is null, "black.img.xbx not found");

        var data = File.ReadAllBytes(file);
        Assert.True(XbxImgFile.IsImgFile(data));
    }

    [Fact]
    public void IsImgFile_EmptyData_ReturnsFalse()
    {
        Assert.False(XbxImgFile.IsImgFile([]));
    }

    [CorpusFact]
    public void Parse_BlackImg_Succeeds()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "black.img.xbx");
        Assert.SkipWhen(file is null, "black.img.xbx not found");

        var result = XbxImgFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Textures);

        var tex = result.Textures[0];
        Assert.True(tex.Width > 0, "Width should be positive");
        Assert.True(tex.Height > 0, "Height should be positive");
        Assert.NotNull(tex.Pixels);
    }

    // ── Batch parse all IMG files ──

    [CorpusFact]
    public void Parse_AllImgFiles_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.img.xbx").ToArray();
        Assert.SkipWhen(files.Length == 0, "No IMG files found");

        var failures = new List<string>();

        foreach (var file in files)
        {
            var result = XbxImgFile.Parse(file);
            if (!result.Success)
                failures.Add($"{Path.GetFileName(file)}: {result.ErrorMessage}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{files.Length} files failed:\n" +
            string.Join("\n", failures.Take(20)));
    }

    private static byte[] CreateSingleEntryTexture(uint rawMipLevelCount)
    {
        var data = new byte[40];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);

        var entry = data.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], rawMipLevelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], 32);
        return data;
    }

    private static byte[] CreateSingleMipVersion2Texture()
    {
        var data = new byte[52];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 1);

        var entry = data.AsSpan(8);
        BinaryPrimitives.WriteUInt32LittleEndian(entry, 0x12345678);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], 32);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[24..], 2);

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(44), 0x001F);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(46), 0xF800);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), uint.MaxValue);
        return data;
    }
}
