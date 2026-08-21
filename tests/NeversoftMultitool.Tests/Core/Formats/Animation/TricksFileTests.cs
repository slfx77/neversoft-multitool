using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Pins the <c>tricks.bin</c> grammar — the only place the PS1-era engine
///     names its otherwise anonymous animation slots.
///     <para>
///         The retail opcode table was read out of the jump table
///         <c>Trick_Skip</c> dispatches through, located by shape in all three
///         PS1 binaries: THPS2 <c>0x800AFA74</c>, THPS3 <c>0x800B19C8</c>,
///         THPS4 <c>0x800B4288</c>, byte-for-byte identical in content. The
///         prototype table is the matched decomp's own (PHYSICS.cpp:4268).
///     </para>
/// </summary>
public sealed class TricksFileTests(TestPaths paths)
{
    private const string Thps2Proto = "Tony Hawk's Pro Skater 2 (2000-3-29, PSX - Prototype)";
    private const string Thps2Final = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string Thps2Dc = "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)";
    private const string Thps2X = "Tony Hawk's Pro Skater 2X (2001-11-15, Xbox - Final)";
    private const string Thps3Ps1 = "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)";
    private const string Thps4Ps1 = "Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)";
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2N64Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3N64Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3N64Rom = "Tony Hawk's Pro Skater 3 (USA).z64";

    /// <summary>
    ///     Every trick reaching its terminator IS the verification: one wrong
    ///     record width desynchronises the walk within a few records, so a table
    ///     that consumes 1,000+ streams exactly across six builds cannot be
    ///     coincidence. Counts are derived independently, not from this parser.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps2Proto, TricksDialect.HalfwordOpcode, 121, 103, 4, 146, 66, 38)]
    [InlineData(Thps2Final, TricksDialect.ByteOpcode, 202, 181, 4, 217, 142, 111)]
    [InlineData(Thps2Dc, TricksDialect.ByteOpcode, 202, 181, 4, 217, 142, 111)]
    [InlineData(Thps2X, TricksDialect.ByteOpcode, 202, 181, 4, 217, 142, 111)]
    [InlineData(Thps3Ps1, TricksDialect.ByteOpcode, 230, 202, 4, 225, 159, 125)]
    [InlineData(Thps4Ps1, TricksDialect.ByteOpcode, 229, 201, 4, 234, 159, 125)]
    public void EveryTrickStreamConsumesExactlyToItsTerminator(
        string build, TricksDialect dialect, int tricks, int withIds,
        int minId, int maxId, int distinctIds, int uniquelyOwned)
    {
        var file = Load(build);
        Assert.Equal(dialect, file.Dialect);
        Assert.Equal(tricks, file.Tricks.Count);
        Assert.All(file.Tricks, trick => Assert.True(
            trick.Terminated, $"'{trick.Name}' ran on — the opcode table desynchronised"));

        var carrying = file.Tricks.Where(t => t.AnimationIds.Count > 0).ToArray();
        Assert.Equal(withIds, carrying.Length);

        var ids = carrying.SelectMany(t => t.AnimationIds).ToArray();
        Assert.Equal(minId, ids.Min());
        Assert.Equal(maxId, ids.Max());
        Assert.Equal(distinctIds, ids.Distinct().Count());
        Assert.Equal(uniquelyOwned, TrickAnimationNames.Build(file).Count);
    }

    /// <summary>
    ///     The operands index the skater bank directly: in every build the
    ///     highest slot any trick references is the bank's LAST index. Four
    ///     independent builds landing exactly on <c>slots - 1</c> is what proves
    ///     these are plain indices rather than ids needing a mapping.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps2Proto, 147)]
    [InlineData(Thps2Final, 218)]
    [InlineData(Thps3Ps1, 226)]
    [InlineData(Thps4Ps1, 235)]
    public void TheHighestReferencedSlotIsTheAnimationBanksLastIndex(
        string build, int bankSlots)
    {
        var file = Load(build);
        var highest = file.Tricks.SelectMany(t => t.AnimationIds).Max();
        Assert.Equal(bankSlots - 1, highest);

        // And the fit gate admits it, while a bank one slot too small does not.
        Assert.NotEmpty(TrickAnimationNames.BuildForBank(file, bankSlots));
        Assert.Empty(TrickAnimationNames.BuildForBank(file, highest));
    }

    /// <summary>
    ///     The prototype and retail files use different encodings, different
    ///     opcode tables and were parsed by different code paths, so agreeing on
    ///     a slot's name is independent corroboration that both readings are
    ///     right rather than merely self-consistent.
    /// </summary>
    [CorpusFact]
    public void BothDialectsAgreeOnTheSlotsTheyBothName()
    {
        var proto = TrickAnimationNames.Build(Load(Thps2Proto));
        var retail = TrickAnimationNames.Build(Load(Thps2Final));

        var shared = proto.Keys.Intersect(retail.Keys).ToArray();
        Assert.NotEmpty(shared);
        var agree = shared.Count(slot => string.Equals(
            proto[slot], retail[slot], StringComparison.OrdinalIgnoreCase));

        // Retail re-authored part of the trick set, so not every shared slot has
        // to match; the point is that agreement dominates.
        Assert.True(agree * 2 > shared.Length,
            $"only {agree}/{shared.Length} shared slots agree across dialects");
        foreach (var (slot, name) in new[]
                 {
                     (18, "Method"), (19, "Indy Nosebone"),
                     (28, "Kickflip to Indy"), (29, "Stalefish"),
                 })
        {
            Assert.Equal(name, proto[slot]);
            Assert.Equal(name, retail[slot]);
        }
    }

    /// <summary>
    ///     A slot several tricks share keeps its synthetic name. Naming it after
    ///     whichever trick happened to be seen first would attach an arbitrary
    ///     label — prototype slot 14 leads "Kissed the Rail" but 28 tricks
    ///     reference it.
    /// </summary>
    [CorpusFact]
    public void SharedSlotsAreLeftUnnamed()
    {
        var file = Load(Thps2Proto);
        var owners = file.Tricks
            .Where(t => t.AnimationIds.Contains(14))
            .Select(t => t.Name)
            .Distinct()
            .ToArray();

        Assert.True(owners.Length > 1, "slot 14 should be shared");
        Assert.DoesNotContain(14, TrickAnimationNames.Build(file).Keys);
    }

    /// <summary>Retail wraps special trick names in braces; the slot name drops them.</summary>
    [CorpusFact]
    public void SpecialTrickNamesLoseTheirBraces()
    {
        var names = TrickAnimationNames.Build(Load(Thps2Final));
        Assert.Equal("Christ Air", names[75]);
        Assert.All(names.Values, n => Assert.DoesNotContain('{', n));
    }

    /// <summary>
    ///     The N64 ports keep the byte opcode and the whole record grammar but
    ///     store their 16-bit operands big-endian. Read the PS1 way, the slot
    ///     indices come back spread across the entire s16 range — so byte order
    ///     is decided by scoring real parses, not assumed from the platform.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps2N64Build, Thps2N64Rom, 202, 217, 110)]
    [InlineData(Thps3N64Build, Thps3N64Rom, 230, 225, 124)]
    public void TheN64PortsShipTheirOwnTableWithBigEndianOperands(
        string build, string rom, int tricks, int highestSlot, int uniquelyOwned)
    {
        var file = LoadFromRom(build, rom);
        Assert.Equal(TricksDialect.ByteOpcode, file.Dialect);
        Assert.True(file.BigEndianOperands, "operands should read big-endian");
        Assert.Equal(tricks, file.Tricks.Count);
        Assert.All(file.Tricks, trick => Assert.True(trick.Terminated, trick.Name));

        var names = TrickAnimationNames.Build(file);
        Assert.Equal(uniquelyOwned, names.Count);
        Assert.Equal(highestSlot, names.Keys.Concat(
            file.Tricks.SelectMany(t => t.AnimationIds).Where(static id => id >= 0)).Max());
    }

    /// <summary>
    ///     The N64 THPS2 table is NOT its PS1 sibling's bytes — 21,318 of 33,168
    ///     differ — yet the two agree on the slots they both name. Two files,
    ///     two byte orders, one answer.
    /// </summary>
    [CorpusFact]
    public void ThePs1AndN64TablesAgreeOnTheSlotsTheyBothName()
    {
        var ps1 = TrickAnimationNames.Build(Load(Thps2Final));
        var n64 = TrickAnimationNames.Build(LoadFromRom(Thps2N64Build, Thps2N64Rom));

        var shared = ps1.Keys.Intersect(n64.Keys).ToArray();
        Assert.True(shared.Length > 80, $"only {shared.Length} shared slots");
        var disagree = shared
            .Where(slot => !string.Equals(ps1[slot], n64[slot], StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.True(disagree.Length == 0,
            $"{disagree.Length} slots disagree, e.g. {string.Join(", ", disagree.Take(4)
                .Select(s => $"{s}: {ps1[s]} vs {n64[s]}"))}");
    }

    [Fact]
    public void GarbageIsRejectedRatherThanParsedIntoNames()
    {
        Assert.Null(TricksFile.Parse(new byte[8]));
        Assert.Null(TricksFile.Parse(Enumerable.Repeat((byte)0xEE, 512).ToArray()));
    }

    private TricksFile Load(string build)
    {
        var path = paths.FindSampleFile(build, "tricks.bin");
        Assert.SkipWhen(path == null, $"{build} tricks.bin not available");
        var file = TricksFile.Parse(File.ReadAllBytes(path!));
        Assert.NotNull(file);
        return file!;
    }

    /// <summary>
    ///     The carts carry the table as an ordinary carved payload with no
    ///     distinguishing name, so it is found by parsing rather than by path —
    ///     which also keeps the test from breaking when carve ordinals shift.
    /// </summary>
    private TricksFile LoadFromRom(string build, string rom)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        // Exactly one carved asset in a cart is a credible trick table. Before
        // Parse required every trick to terminate, a 430 KB render bank also
        // qualified here, which is what surfaced that gap.
        var candidates = assets
            .Select(static asset => TricksFile.Parse(asset.Data))
            .Where(static file => file != null)
            .ToArray();

        Assert.Single(candidates);
        return candidates[0]!;
    }
}
