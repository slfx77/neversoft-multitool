using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.XbxScene;

public sealed class ThawTexFileTests(TestPaths paths)
{
    private const string BuildName = "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";

    // ── IsThawTex ──

    [Fact]
    public void IsThawTex_EmptyData_ReturnsFalse()
    {
        Assert.False(ThawTexFile.IsThawTex([]));
    }

    [Fact]
    public void IsThawTex_WrongMagic_ReturnsFalse()
    {
        Assert.False(ThawTexFile.IsThawTex(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void IsThawTex_ValidFile_ReturnsTrue()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.wpc not found");

        var data = File.ReadAllBytes(file);
        Assert.True(ThawTexFile.IsThawTex(data));
    }

    [Fact]
    public void IsThawTex_XbxTexFile_ReturnsFalse()
    {
        // XbxTexFile has version=1 as u32, not 0xABADD00D magic
        Assert.False(ThawTexFile.IsThawTex(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 }));
    }

    [Fact]
    public void TryFindEmbeddedDictionaryOffset_PrefixedDictionary_ReturnsOffset()
    {
        var data = BuildSyntheticBgraDictionary(16, false);

        Assert.True(ThawTexFile.TryFindEmbeddedDictionaryOffset(data, out var offset));
        Assert.Equal(16, offset);
    }

    // ── Parse known files ──

    [Fact]
    public void Parse_AccBackpack01_ExtractsTexture()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.wpc not found");

        var result = ThawTexFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotEmpty(result.Textures);

        var tex = result.Textures[0];
        Assert.Equal(0xA85AD56Bu, tex.Checksum);
        Assert.Equal(64, tex.Width);
        Assert.Equal(64, tex.Height);
        Assert.NotNull(tex.Pixels);
        Assert.Equal(64 * 64 * 4, tex.Pixels!.Length);
    }

    [Fact]
    public void Parse_TexturesHaveValidDimensions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = paths.FindSampleFile(BuildName, "acc_backpack01.tex.wpc");
        Assert.SkipWhen(file is null, "acc_backpack01.tex.wpc not found");

        var result = ThawTexFile.Parse(file);
        Assert.True(result.Success, result.ErrorMessage);

        foreach (var tex in result.Textures)
        {
            Assert.True(tex.Width > 0 && tex.Width <= 2048, $"Width {tex.Width} out of range");
            Assert.True(tex.Height > 0 && tex.Height <= 2048, $"Height {tex.Height} out of range");
            Assert.NotNull(tex.Pixels);
            Assert.Equal(tex.Width * tex.Height * 4, tex.Pixels!.Length);
        }
    }

    [Fact]
    public void Parse_PrefixedDictionaryWithTruncatedTail_ReturnsPartialTextures()
    {
        var data = BuildSyntheticBgraDictionary(12, true);

        var result = ThawTexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var tex = Assert.Single(result.Textures);
        Assert.Equal(0x11223344u, tex.Checksum);
        Assert.Equal(2, tex.Width);
        Assert.Equal(2, tex.Height);
        Assert.Equal(
        [
            0xFF, 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0xFF,
            0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
        ], tex.Pixels);
    }

    // ── Batch parse all TEX files ──

    [Fact]
    public void BatchParse_AllThawTexWpc_ZeroFailures()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = paths.FindSampleFiles(BuildName, "*.tex.wpc").ToArray();
        Assert.SkipWhen(files.Length == 0, "No TEX files found");

        var failures = new List<string>();
        var totalTextures = 0;

        foreach (var file in files)
        {
            var result = ThawTexFile.Parse(file);
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

    private static byte[] BuildSyntheticBgraDictionary(int prefixBytes, bool truncateTail)
    {
        var bytes = new List<byte>(prefixBytes + 64);

        for (var i = 0; i < prefixBytes; i++)
            bytes.Add(0xCC);

        // File header: magic + version:u8 + flag:u8 + textureCount:u16
        bytes.AddRange(BitConverter.GetBytes(0xABADD00Du));
        bytes.Add(1);
        bytes.Add(0);
        bytes.AddRange(BitConverter.GetBytes((ushort)(truncateTail ? 2 : 1)));

        // Texture 0 header
        bytes.AddRange(BitConverter.GetBytes(0xABADD00Du));
        bytes.AddRange(BitConverter.GetBytes(0u));
        bytes.AddRange(BitConverter.GetBytes(0x11223344u));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        bytes.AddRange(BitConverter.GetBytes((ushort)2));
        bytes.Add(1); // mipCount
        bytes.Add(32); // texelDepth
        bytes.Add(0); // compression
        bytes.Add(0); // paletteDepth
        bytes.AddRange(BitConverter.GetBytes((ushort)8)); // bytesPerLine
        bytes.AddRange(BitConverter.GetBytes((ushort)2)); // numLines

        // Bottom-up BGRA rows so DecodeBgra32 + FlipVertical restores top-down RGBA output.
        bytes.AddRange(
        [
            0xFF, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
            0x00, 0x00, 0xFF, 0xFF, 0x00, 0xFF, 0x00, 0xFF
        ]);

        if (truncateTail)
        {
            bytes.AddRange(BitConverter.GetBytes(0xABADD00Du));
            bytes.AddRange(BitConverter.GetBytes(0u));
            bytes.AddRange(BitConverter.GetBytes(0x55667788u));
        }

        return bytes.ToArray();
    }
}