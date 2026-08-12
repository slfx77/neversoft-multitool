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

    [Fact]
    public void LeadingZeroRuns_RemainTransitiveWhenOneNameEndsAtTheRun()
    {
        const string first = "anim_001";
        const string second = "anim_1aa";
        const string third = "anim_1b";
        var comparer = NaturalStringComparer.OrdinalIgnoreCase;

        Assert.True(comparer.Compare(first, second) < 0);
        Assert.True(comparer.Compare(second, third) < 0);
        Assert.True(comparer.Compare(first, third) < 0);

        string[] names = [third, first, second];
        Assert.Equal([first, second, third], names.OrderBy(static name => name, comparer));
    }

    [Fact]
    public void NonAsciiDigits_UseOrdinalTextOrderingAndRemainTransitive()
    {
        const string first = "0:0١";
        const string second = "0:A";
        const string third = "0:١";
        var comparer = NaturalStringComparer.OrdinalIgnoreCase;

        Assert.True(comparer.Compare(first, second) < 0);
        Assert.True(comparer.Compare(second, third) < 0);
        Assert.True(comparer.Compare(first, third) < 0);
    }
}
