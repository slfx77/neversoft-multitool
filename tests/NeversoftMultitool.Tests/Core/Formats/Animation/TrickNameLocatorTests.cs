using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Pins that trick names actually reach a bank's slots — the parser being
///     right is not the same as the naming being wired.
/// </summary>
public sealed class TrickNameLocatorTests(TestPaths paths)
{
    private const string Thps2Final = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string SpiderMan = "Spider-Man (2000-9-1, PSX - Final)";

    [CorpusFact]
    public void TheSkaterBankTakesItsNamesFromTheSiblingTrickTable()
    {
        var bankPath = paths.FindSampleFile(Thps2Final, "sk2anim.psx");
        Assert.SkipWhen(bankPath == null, "sk2anim.psx not available");

        var names = TrickNameLocator.ForBank(new FileSystemAssetSource(bankPath!), 218);
        Assert.Equal(111, names.Count);
        Assert.Equal("Christ Air", names[75]);
        Assert.Equal("360 Flip", names[60]);
        Assert.Equal("Japan Air", names[31]);

        // Slots several tricks share stay synthetic rather than taking an
        // arbitrary owner's name.
        Assert.DoesNotContain(14, names.Keys);
    }

    /// <summary>
    ///     A build that ships no tricks.bin must name nothing rather than
    ///     borrowing another build's table.
    /// </summary>
    [CorpusFact]
    public void ABuildWithoutATrickTableNamesNothing()
    {
        var bankPath = paths.FindSampleFile(SpiderMan, "spidey.psx")
                       ?? paths.FindSampleFile(SpiderMan, "l1a1_g.psx");
        Assert.SkipWhen(bankPath == null, "Spider-Man sample not available");
        Assert.False(File.Exists(Path.Combine(
            Path.GetDirectoryName(bankPath!)!, "tricks.bin")));

        Assert.Empty(TrickNameLocator.ForBank(new FileSystemAssetSource(bankPath!), 64));
    }

    /// <summary>
    ///     A bank too small for the table's slot references is refused whole. The
    ///     pairing is positional — nothing in either file names the other — so
    ///     this fit check is what keeps a mismatched pair from mislabelling
    ///     rather than declining.
    /// </summary>
    [CorpusFact]
    public void AnIncompatibleBankIsRefusedRatherThanPartiallyNamed()
    {
        var bankPath = paths.FindSampleFile(Thps2Final, "sk2anim.psx");
        Assert.SkipWhen(bankPath == null, "sk2anim.psx not available");
        var source = new FileSystemAssetSource(bankPath!);

        Assert.NotEmpty(TrickNameLocator.ForBank(source, 218));
        Assert.Empty(TrickNameLocator.ForBank(source, 217));
        Assert.Empty(TrickNameLocator.ForBank(source, 0));
    }

    [Fact]
    public void TheNameFactoryFallsBackToTheSyntheticSlotLabel()
    {
        var factory = TrickNameLocator.NameFactory(
            new Dictionary<int, string> { [7] = "Christ Air" });

        Assert.Equal("Christ Air", factory(7));
        Assert.Equal("anim_8", factory(8));
    }
}
