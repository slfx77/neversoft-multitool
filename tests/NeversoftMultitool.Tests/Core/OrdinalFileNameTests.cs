using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class OrdinalFileNameTests
{
    private static readonly string[] CompoundSuffixes = [".tex.ps2", ".img.ps2", ".skin.xbx"];

    [Fact]
    public void HasAnySuffix_MatchesCompoundSuffixIgnoringCase()
    {
        Assert.True(OrdinalFileName.HasAnySuffix("BANNER.TEX.PS2", CompoundSuffixes));
        Assert.False(OrdinalFileName.HasAnySuffix("banner.tex", CompoundSuffixes));
    }

    [Fact]
    public void HasExtension_MatchesCaseInsensitiveExtension()
    {
        var sfdPath = Path.Combine("temp", "INTRO.SFD");
        var strPath = Path.Combine("temp", "INTRO.STR");
        Assert.True(OrdinalFileName.HasExtension(sfdPath, ".sfd"));
        Assert.False(OrdinalFileName.HasExtension(strPath, ".sfd"));
    }

    [Fact]
    public void StripCompoundSuffix_RemovesMatchingSuffixBeforeExtensionFallback()
    {
        Assert.Equal("banner", OrdinalFileName.StripCompoundSuffix("banner.tex.ps2", CompoundSuffixes));
        Assert.Equal("banner", OrdinalFileName.StripCompoundSuffix("banner.ps2", CompoundSuffixes));
    }
}
