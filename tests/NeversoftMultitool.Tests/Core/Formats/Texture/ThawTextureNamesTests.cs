using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Texture;

public class ThawTextureNamesTests
{
    // Checksums come from the debug.log build transcripts inside THAW QTex .tex.zip
    // bundles and are opaque build-tool ids, NOT QbKeys. These vectors pin the
    // materialized checksum/name side map against known transcript pairs.
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
        Assert.NotEqual(0x5A11D8F1u, QbKey.Hash("cat_bg_new"));
        Assert.NotEqual(0x5A11D8F1u, QbKey.HashLower("cat_bg_new"));
    }
}
