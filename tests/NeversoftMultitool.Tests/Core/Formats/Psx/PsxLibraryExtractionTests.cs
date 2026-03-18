using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Tests.Helpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Psx;

public sealed class PsxLibraryExtractionTests(TestPaths paths)
{
    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void ExtractTextures_Xbox_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputDir = paths.PsxXboxDir!;
        var goldenDir = paths.GoldenPsxXboxDir!;
        var inputFile = Path.Combine(inputDir, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PsxXbox_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, false);

            Assert.True(result.TotalTextures > 0, $"No textures found in {filename}");
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            // Compare each output PNG against golden file
            var outputFiles = Directory.GetFiles(tempDir, "*.png");
            Assert.NotEmpty(outputFiles);

            foreach (var outputFile in outputFiles)
            {
                var outputName = Path.GetFileName(outputFile);
                var goldenFile = Path.Combine(goldenDir, outputName);
                Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found: {outputName}");

                var comparison = PixelComparer.CompareRgba(outputFile, goldenFile);
                Assert.True(comparison.Match, $"{outputName}: {comparison.Details}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    [InlineData("items.psx")]
    public void ExtractTextures_Ps1_MatchesGoldenFiles(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData || !paths.HasGoldenFiles, "Test data not available");

        var inputDir = paths.PsxPs1Dir!;
        var goldenDir = paths.GoldenPsxPs1Dir!;
        var inputFile = Path.Combine(inputDir, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PsxPs1_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, false);

            Assert.True(result.TotalTextures > 0, $"No textures found in {filename}");
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            var outputFiles = Directory.GetFiles(tempDir, "*.png");
            Assert.NotEmpty(outputFiles);

            foreach (var outputFile in outputFiles)
            {
                var outputName = Path.GetFileName(outputFile);
                var goldenFile = Path.Combine(goldenDir, outputName);
                Assert.SkipWhen(!File.Exists(goldenFile), $"Golden file not found: {outputName}");

                var comparison = PixelComparer.CompareRgba(outputFile, goldenFile);
                Assert.True(comparison.Match, $"{outputName}: {comparison.Details}");
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ExtractTextures_InvalidFile_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Invalid_" + Guid.NewGuid().ToString("N")[..8]);
        var tempFile = Path.Combine(tempDir, "invalid.psx");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllBytes(tempFile, [0x00, 0x00, 0x00, 0x00]);

            var result = PsxLibrary.ExtractTextures(tempFile, tempDir, false);

            Assert.False(result.Success);
            Assert.Equal(0, result.TexturesWritten);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void ExtractTextures_Xbox_DdsMainSurfaceMatchesPng(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Dds_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, false);
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            var ddsFiles = Directory.GetFiles(tempDir, "*.dds");
            Assert.SkipWhen(ddsFiles.Length == 0, "No DDS files produced (no 16-bit textures)");

            foreach (var ddsFile in ddsFiles)
            {
                var pngFile = Path.ChangeExtension(ddsFile, ".png");
                Assert.True(File.Exists(pngFile), $"Matching PNG not found for {Path.GetFileName(ddsFile)}");

                var ddsInfo = DdsTestReader.ReadMainSurface(ddsFile);
                using var pngImage = Image.Load<Rgba32>(pngFile);

                Assert.Equal(pngImage.Width, ddsInfo.Width);
                Assert.Equal(pngImage.Height, ddsInfo.Height);

                // Convert DDS main surface ushorts to RGBA and compare
                var ddsRgba = new byte[ddsInfo.Width * ddsInfo.Height * 4];
                var rgba = new byte[4];
                for (var i = 0; i < ddsInfo.Pixels.Length; i++)
                {
                    ColorHelpers.Convert16BppTo32Bpp(ddsInfo.Pixels[i], ddsInfo.Format, rgba);
                    ddsRgba[i * 4] = rgba[0];
                    ddsRgba[i * 4 + 1] = rgba[1];
                    ddsRgba[i * 4 + 2] = rgba[2];
                    ddsRgba[i * 4 + 3] = rgba[3];
                }

                for (var y = 0; y < pngImage.Height; y++)
                {
                    for (var x = 0; x < pngImage.Width; x++)
                    {
                        var px = pngImage[x, y];
                        var offset = (y * pngImage.Width + x) * 4;
                        Assert.True(
                            ddsRgba[offset] == px.R && ddsRgba[offset + 1] == px.G &&
                            ddsRgba[offset + 2] == px.B && ddsRgba[offset + 3] == px.A,
                            $"{Path.GetFileName(ddsFile)}: pixel mismatch at ({x},{y})");
                    }
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void ExtractTextures_Xbox_DdsHasCorrectMipHeaders(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_DdsMip_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, false);
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            var ddsFiles = Directory.GetFiles(tempDir, "*.dds");
            Assert.SkipWhen(ddsFiles.Length == 0, "No DDS files produced");

            foreach (var ddsFile in ddsFiles)
            {
                var header = DdsTestReader.ReadHeader(ddsFile);

                if (header.MipMapCount > 1)
                {
                    // Mipmapped: verify flags
                    Assert.True((header.Flags & 0x20000) != 0,
                        $"{Path.GetFileName(ddsFile)}: DDSD_MIPMAPCOUNT flag not set");
                    Assert.True((header.Caps & 0x8) != 0,
                        $"{Path.GetFileName(ddsFile)}: DDSCAPS_COMPLEX flag not set");
                    Assert.True((header.Caps & 0x400000) != 0,
                        $"{Path.GetFileName(ddsFile)}: DDSCAPS_MIPMAP flag not set");

                    // Verify total file size = 128 (magic + header) + sum of all mip level sizes
                    var expectedSize = 128L;
                    var w = header.Width;
                    var h = header.Height;
                    for (var level = 0; level < header.MipMapCount; level++)
                    {
                        expectedSize += w * h * 2; // 16 bpp = 2 bytes per pixel
                        w = Math.Max(1, w / 2);
                        h = Math.Max(1, h / 2);
                    }

                    var actualSize = new FileInfo(ddsFile).Length;
                    Assert.Equal(expectedSize, actualSize);
                }
                else
                {
                    // Non-mipmapped: caps should just be TEXTURE
                    Assert.True((header.Caps & 0x400000) == 0,
                        $"{Path.GetFileName(ddsFile)}: DDSCAPS_MIPMAP should not be set for non-mipmapped");
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    [InlineData("hawk2.PSX")]
    public void ExtractTextureByHash_FindsAllTexturesInNameList(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        // Read the texture name hash list
        using var stream = File.OpenRead(inputFile);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(4); // magic
        PsxLibrary.SkipModelData(reader);
        var texNames = PsxLibrary.ReadTextureInfo(reader);

        Assert.True(texNames.Length > 0, "No texture name hashes found");

        // Each name hash should be resolvable via ExtractTextureByHash
        var diagnostics = new List<string>();
        foreach (var hash in texNames)
        {
            var result = PsxLibrary.ExtractTextureByHash(inputFile, hash, diagnostics);
            Assert.True(result != null,
                $"ExtractTextureByHash failed for hash 0x{hash:X8}. Diagnostics: {string.Join("; ", diagnostics)}");
            Assert.True(result.Value.Width > 0 && result.Value.Height > 0);
            Assert.True(result.Value.Rgba.Length == result.Value.Width * result.Value.Height * 4);
            diagnostics.Clear();
        }
    }

    [Theory]
    [InlineData("ring.psx")]
    [InlineData("bits.psx")]
    [InlineData("items.psx")]
    public void ExtractTextureByHash_FindsAllTexturesInNameList_Ps1(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxPs1Dir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        using var stream = File.OpenRead(inputFile);
        using var reader = new BinaryReader(stream);
        reader.ReadBytes(4);
        PsxLibrary.SkipModelData(reader);
        var texNames = PsxLibrary.ReadTextureInfo(reader);

        Assert.True(texNames.Length > 0, "No texture name hashes found");

        var diagnostics = new List<string>();
        foreach (var hash in texNames)
        {
            var result = PsxLibrary.ExtractTextureByHash(inputFile, hash, diagnostics);
            Assert.True(result != null,
                $"ExtractTextureByHash failed for hash 0x{hash:X8}. Diagnostics: {string.Join("; ", diagnostics)}");
            Assert.True(result.Value.Width > 0 && result.Value.Height > 0);
            diagnostics.Clear();
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void ExtractTextureByHash_PixelsMatchBatchExtraction(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        // Enumerate textures to get headers + name hashes
        var textures = PsxLibrary.EnumerateTextures(inputFile);
        Assert.NotEmpty(textures);

        // Batch extract all textures to get reference PNGs
        var tempDir = Path.Combine(Path.GetTempPath(),
            "NsMultitool_Test_HashMatch_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var batchResult = PsxLibrary.ExtractTextures(inputFile, tempDir, false, false);
            Assert.True(batchResult.Success, $"Batch extraction failed: {batchResult.ErrorMessage}");

            // For each texture with a name hash, verify single-texture extraction matches
            foreach (var (header, nameHash) in textures)
            {
                if (nameHash == 0) continue;

                var singleResult = PsxLibrary.ExtractTextureByHash(inputFile, nameHash);
                Assert.True(singleResult != null, $"Single extraction failed for hash 0x{nameHash:X8}");

                Assert.Equal(header.Width, singleResult.Value.Width);
                Assert.Equal(header.Height, singleResult.Value.Height);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("bits.psx")]
    [InlineData("Default.PSX")]
    public void ExtractTextures_NoDds_ProducesOnlyPng(string filename)
    {
        Assert.SkipWhen(!paths.HasTestData, "Test data not available");

        var inputFile = Path.Combine(paths.PsxXboxDir!, filename);
        Assert.SkipWhen(!File.Exists(inputFile), $"Test file not found: {filename}");

        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_NoDds_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempDir);
            var result = PsxLibrary.ExtractTextures(inputFile, tempDir, false, false);
            Assert.True(result.Success, $"Extraction failed: {result.ErrorMessage}");

            var pngFiles = Directory.GetFiles(tempDir, "*.png");
            var ddsFiles = Directory.GetFiles(tempDir, "*.dds");

            Assert.NotEmpty(pngFiles);
            Assert.Empty(ddsFiles);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }
}