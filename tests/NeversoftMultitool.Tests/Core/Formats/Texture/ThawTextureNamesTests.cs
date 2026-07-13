using NeversoftMultitool.Core.Formats.Texture;

namespace NeversoftMultitool.Tests.Core.Formats.Texture;

public class ThawTextureNamesTests
{
    // Checksums come from the debug.log build transcripts inside THAW QTex .tex.zip
    // bundles (harvested by tools/utilities/harvest_thaw_texture_names.py). They are
    // opaque build-tool ids, NOT QbKeys.
    [Theory]
    [InlineData(0x5A11D8F1u, "cat_bg_new")]
    [InlineData(0x560644FEu, "Acc_Elbowpads01")]
    public void TryResolve_KnownChecksum_ReturnsSourceArtName(uint checksum, string expected)
    {
        Assert.Equal(expected, ThawTextureNames.TryResolve(checksum));
    }

    [Fact]
    public void TryResolve_UnknownChecksum_ReturnsNull()
    {
        Assert.Null(ThawTextureNames.TryResolve(0xDEADBEEFu));
    }

    [Fact]
    public void TryResolve_IsNotAQbKey()
    {
        // The texture side map is deliberately distinct from the QbKey dictionaries:
        // the checksum is not the hash of the resolved name in either CRC variant.
        Assert.NotEqual(0x5A11D8F1u, NeversoftMultitool.Core.QbKey.QbKey.Hash("cat_bg_new"));
        Assert.NotEqual(0x5A11D8F1u, NeversoftMultitool.Core.QbKey.QbKey.HashLower("cat_bg_new"));
    }
}
