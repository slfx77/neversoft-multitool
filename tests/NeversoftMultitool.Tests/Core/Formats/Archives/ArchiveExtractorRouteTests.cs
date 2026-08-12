using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Tests.Core.Formats.Archives;

public sealed class ArchiveExtractorRouteTests
{
    [Fact]
    public void PickerExtensions_ContainTheCanonicalArchiveCatalog()
    {
        foreach (var extension in ArchiveTypeDetector.ArchiveExtensions)
        {
            Assert.Contains(
                extension,
                ArchiveExtractorRoute.PickerExtensions,
                StringComparer.OrdinalIgnoreCase);
        }

        Assert.Contains(".prg", ArchiveExtractorRoute.PickerExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".apk", ArchiveExtractorRoute.PickerExtensions, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void PickerExtensions_HaveNoCaseInsensitiveDuplicates()
    {
        Assert.Equal(
            ArchiveExtractorRoute.PickerExtensions.Count,
            ArchiveExtractorRoute.PickerExtensions
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count());
    }

    [Theory]
    [InlineData("data.pak.xbx", ".pak")]
    [InlineData("textures.zip.wpc", ".zip")]
    public void ResolveExtension_UsesTheCanonicalCompoundArchiveExtension(
        string fileName,
        string expectedExtension)
    {
        Assert.Equal(expectedExtension, ArchiveExtractorRoute.ResolveExtension(fileName));
    }
}
