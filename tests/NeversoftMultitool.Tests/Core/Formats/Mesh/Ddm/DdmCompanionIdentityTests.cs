using NeversoftMultitool.Core.Formats.Mesh.Ddm;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ddm;

public class DdmCompanionIdentityTests
{
    [Fact]
    public void FindCompanionPsxPaths_SameStemInAnotherDirectory_RemainsStandalone()
    {
        var ddm = Full("root", "A", "level.ddm");
        var localPsx = Full("root", "A", "level.psx");
        var otherPsx = Full("root", "B", "level.psx");

        var companions = DdmCompanionIdentity.FindCompanionPsxPaths(
            [ddm], [localPsx, otherPsx], path => path == localPsx);

        Assert.Equal([localPsx], companions);
        Assert.DoesNotContain(otherPsx, companions);
    }

    [Fact]
    public void FindCompanionPsxPaths_CaseSensitiveMiss_DoesNotSuppressDifferentCase()
    {
        var ddm = Full("root", "LEVEL.ddm");
        var lowerPsx = Full("root", "level.psx");

        var companions = DdmCompanionIdentity.FindCompanionPsxPaths(
            [ddm], [lowerPsx], _ => false);

        Assert.Empty(companions);
    }

    [Fact]
    public void FindCompanionPsxPaths_CaseInsensitiveStore_UsesUniquePreservedCase()
    {
        var ddm = Full("root", "LEVEL.ddm");
        var lowerPsx = Full("root", "level.psx");

        var companions = DdmCompanionIdentity.FindCompanionPsxPaths(
            [ddm], [lowerPsx], _ => true);

        Assert.Equal([lowerPsx], companions);
    }

    [Fact]
    public void FindCompanionPsxPaths_CaseSensitiveStoreSuppressesOnlyExactVariant()
    {
        var ddm = Full("root", "LEVEL.ddm");
        var upperPsx = Full("root", "LEVEL.psx");
        var lowerPsx = Full("root", "level.psx");

        var companions = DdmCompanionIdentity.FindCompanionPsxPaths(
            [ddm], [lowerPsx, upperPsx], path => path == upperPsx);

        Assert.Equal([upperPsx], companions);
        Assert.DoesNotContain(lowerPsx, companions);
    }

    [Fact]
    public void FindCompanionPsxPaths_AmbiguousFoldedCandidates_SuppressesNeither()
    {
        var ddm = Full("root", "Level.ddm");
        var upperPsx = Full("root", "LEVEL.psx");
        var lowerPsx = Full("root", "level.psx");

        var companions = DdmCompanionIdentity.FindCompanionPsxPaths(
            [ddm], [lowerPsx, upperPsx], _ => true);

        Assert.Empty(companions);
    }

    private static string Full(params string[] parts) => Path.GetFullPath(Path.Combine(parts));
}
