using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core;

public class QbLocalNamesTests
{
    // Inline CHECKSUM_NAME (token 0x2B) registrations harvested globally from the
    // THPS3/THPS4/THUG/THUG2 script corpus (QbKeyNames.QbLocalNames.txt). Each pair is
    // proven by re-hash, and aggregating them means a name defined in one script now
    // resolves everywhere — the largest single source of QbKey coverage in the project.
    [Theory]
    [InlineData("_180LateTurn", 0x1CB4E53Bu)]
    [InlineData("_360VarialHeelFlipLien_Idle", 0xB84B144Eu)]
    public void TryResolve_InlineChecksumNames_ResolveGlobally(string name, uint checksum)
    {
        Assert.Equal(checksum, QbKey.HashLower(name)); // proven-by-rehash (ship bar)
        Assert.Equal(name, QbKey.TryResolve(checksum)); // now resolves through the global dictionary
    }
}