using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public sealed class PsxLibraryFormatTests(TestPaths paths)
{
    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void EnumerateTextures_ReturnsCorrectCount(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var textures = PsxLibrary.EnumerateTextures(inputFile);
        Assert.NotEmpty(textures);

        // Each texture should have valid dimensions
        foreach (var (header, _) in textures)
        {
            Assert.True(header.Width > 0, $"Invalid width at offset 0x{header.Offset:X}");
            Assert.True(header.Height > 0, $"Invalid height at offset 0x{header.Offset:X}");
        }
    }

    // ── GUI Round-Trip Tests ────────────────────────────────────────────
    // These verify that every texture returned by EnumerateTextures (which
    // the GUI displays) can be resolved back via ExtractTextureByHash
    // (which the GUI calls for preview). This is the exact flow that must
    // work end-to-end for texture previews to function.

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void EnumerateTextures_AllHashesResolvableByExtractTextureByHash(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var textures = PsxLibrary.EnumerateTextures(inputFile);
        Assert.NotEmpty(textures);

        var diagnostics = new List<string>();
        foreach (var (header, nameHash) in textures)
        {
            if (nameHash == 0) continue;

            var result = PsxLibrary.ExtractTextureByHash(inputFile, nameHash, diagnostics);
            Assert.True(result != null,
                $"GUI preview would fail for hash 0x{nameHash:X8} at offset 0x{header.Offset:X}. " +
                $"Diagnostics: {string.Join("; ", diagnostics)}");
            Assert.Equal(header.Width, result.Value.Width);
            Assert.Equal(header.Height, result.Value.Height);
            diagnostics.Clear();
        }
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    [InlineData("items.psx")]
    public void EnumerateTextures_AllHashesResolvableByExtractTextureByHash_Ps1(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxPs1Dir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var textures = PsxLibrary.EnumerateTextures(inputFile);
        Assert.NotEmpty(textures);

        var diagnostics = new List<string>();
        foreach (var (header, nameHash) in textures)
        {
            if (nameHash == 0) continue;

            var result = PsxLibrary.ExtractTextureByHash(inputFile, nameHash, diagnostics);
            Assert.True(result != null,
                $"GUI preview would fail for hash 0x{nameHash:X8} at offset 0x{header.Offset:X}. " +
                $"Diagnostics: {string.Join("; ", diagnostics)}");
            Assert.Equal(header.Width, result.Value.Width);
            Assert.Equal(header.Height, result.Value.Height);
            diagnostics.Clear();
        }
    }
}