using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public sealed class ArchiveNamingPureExtensionTests
{
    private static readonly byte[] ValidPre =
    [
        0x01, 0x00, 0x00, 0x00,
        0x61, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00,
        0x41
    ];

    [Theory]
    [InlineData(".pre", "file.pre")]
    [InlineData(".PRE", "file.PRE")]
    [InlineData(".prd", "file.prd")]
    [InlineData(".cut", "file.cut")]
    [InlineData(".zip", "file.zip")]
    [InlineData("level.pre", "level")]
    [InlineData(".first.pre", ".first")]
    [InlineData("level.prd", "level.prd")]
    [InlineData("...pre", "..")]
    public void GetExtractionStem_UsesSafePureExtensionFallback(string archiveName, string expected)
    {
        Assert.Equal(expected, ArchiveNaming.GetExtractionStem(archiveName));
    }

    [Fact]
    public void IsAlreadyExtracted_PureExtensionArchive_UsesFallbackSiblingDirectory()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(tempRoot, ".pre");
            File.WriteAllBytes(archivePath, ValidPre);

            Assert.False(RecursiveUnpacker.IsAlreadyExtracted(archivePath));

            var extractionDirectory = Path.Combine(tempRoot, "file.pre");
            Directory.CreateDirectory(extractionDirectory);
            File.WriteAllBytes(Path.Combine(extractionDirectory, "a"), [0x41]);

            Assert.True(RecursiveUnpacker.IsAlreadyExtracted(archivePath));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void ExtractFiles_PureExtensionPre_WritesUnderFallbackDirectory()
    {
        var tempRoot = CreateTempDirectory();
        try
        {
            var archivePath = Path.Combine(tempRoot, ".pre");
            var outputRoot = Path.Combine(tempRoot, "output");
            File.WriteAllBytes(archivePath, ValidPre);

            PreArchive.ExtractFiles(
                archivePath,
                outputRoot,
                token: TestContext.Current.CancellationToken);

            Assert.Equal(
                new byte[] { 0x41 },
                File.ReadAllBytes(Path.Combine(outputRoot, "file.pre", "a")));
            Assert.False(File.Exists(Path.Combine(outputRoot, "a")));
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_Test_ArchiveNaming_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
