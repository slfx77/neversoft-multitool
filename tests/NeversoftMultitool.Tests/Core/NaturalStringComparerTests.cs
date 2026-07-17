using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class NaturalStringComparerTests
{
    [Fact]
    public void SortsGeneratedAnimationNamesByNumericSuffix()
    {
        string[] names = ["anim_11", "anim_2", "anim_10", "anim_3", "anim_1"];

        var sorted = names.OrderBy(
            static name => name,
            NaturalStringComparer.OrdinalIgnoreCase);

        Assert.Equal(["anim_1", "anim_2", "anim_3", "anim_10", "anim_11"], sorted);
    }

    [Fact]
    public void UsesCaseInsensitiveOrdinalOrderingForTextSegments()
    {
        string[] names = ["Bruce_anim_10", "bruce_ANIM_2", "alpha_anim_20"];

        var sorted = names.OrderBy(
            static name => name,
            NaturalStringComparer.OrdinalIgnoreCase);

        Assert.Equal(["alpha_anim_20", "bruce_ANIM_2", "Bruce_anim_10"], sorted);
    }

    [Fact]
    public void DistinguishesEqualLengthNamesWithRedistributedLeadingZeroes()
    {
        var comparer = NaturalStringComparer.OrdinalIgnoreCase;

        Assert.True(comparer.Compare("a01b0", "a1b00") < 0);
        Assert.True(comparer.Compare("a1b00", "a01b0") > 0);
    }
}
