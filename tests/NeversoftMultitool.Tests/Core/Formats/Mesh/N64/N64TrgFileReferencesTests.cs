using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins how a trigger's file references are read (2026-08-08).
///     <para>
///         A TRG spells the files it loads as literal strings, which is what
///         lets carved slots be named from the ROM itself rather than from a
///         harvested dictionary.
///     </para>
/// </summary>
public sealed class N64TrgFileReferencesTests
{
    private static TrgFile Build(params (int Opcode, string Arg)[] commands)
    {
        return new TrgFile
        {
            VersionMajor = 2,
            NodeCount = 1,
            Nodes =
            [
                new TrgNode
                {
                    Index = 0,
                    Type = "AUTOEXEC",
                    Commands = commands
                        .Select(static c => new TrgCommand
                        {
                            Opcode = c.Opcode,
                            Args = [c.Arg]
                        })
                        .ToList()
                }
            ]
        };
    }

    [Fact]
    public void Collect_TakesTheBankFromSetObjFile()
    {
        var set = N64TrgFileReferences.Collect(Build(
            (0x7E, "SkVans_L"),
            (0x7E, "SkVans_O"),
            (0x8E, "SkVans_O"),
            (0x80, "SkVans")));

        Assert.Equal("SkVans_O", set.BankName);
        Assert.Equal(["SkVans", "SkVans_L", "SkVans_O"], set.Family);
        Assert.Empty(set.Outsiders);
    }

    /// <summary>
    ///     A trigger names more than its own level, and the extras live
    ///     elsewhere in the slot space. Feeding them to the run aligner breaks
    ///     contiguity — it took Spider-Man from 55 aligned triggers to 0 — so
    ///     the split is load-bearing rather than cosmetic.
    /// </summary>
    [Fact]
    public void Collect_SplitsOutsidersFromTheLevelFamily()
    {
        var set = N64TrgFileReferences.Collect(Build(
            (0x7E, "L1A1_L"),
            (0x7E, "henchman"),
            (0x7E, "blackcat"),
            (0x8E, "L1A1_O"),
            (0x80, "L1A1_G")));

        Assert.Equal("L1A1_O", set.BankName);
        Assert.Equal(["L1A1_G", "L1A1_L", "L1A1_O"], set.Family);
        Assert.Equal(["blackcat", "henchman"], set.Outsiders);
    }

    /// <summary>
    ///     THE SORT ORDER, which is the whole alignment.
    ///     <para>
    ///         Names order LOWERCASED then ordinal, so <c>_</c> (0x5F) precedes
    ///         <c>l</c> (0x6C) and <c>skdown_2</c> comes before <c>skdownl2</c>.
    ///         Under <see cref="StringComparer.OrdinalIgnoreCase" /> the compare
    ///         happens UPPERCASED, where <c>L</c> (0x4C) precedes <c>_</c>
    ///         (0x5F) and the pair swaps — which silently mis-aligned the tail
    ///         of every family carrying both an <c>_l</c> and an <c>l2</c>
    ///         library, costing four THPS1 slots.
    ///     </para>
    /// </summary>
    [Fact]
    public void Collect_OrdersLowercaseOrdinal_NotOrdinalIgnoreCase()
    {
        var set = N64TrgFileReferences.Collect(Build(
            (0x8E, "skdown_o"),
            (0x80, "skdown"),
            (0x80, "skdown_2"),
            (0x80, "skdown_h"),
            (0x7E, "skdown_l"),
            (0x7E, "skdownl2")));

        Assert.Equal(
            ["skdown", "skdown_2", "skdown_h", "skdown_l", "skdown_o", "skdownl2"],
            set.Family);

        // The order the wrong comparer would have produced, spelled out so the
        // difference is visible rather than implied.
        Assert.NotEqual(
            set.Family,
            set.Family.OrderBy(static n => n, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    [Theory]
    [InlineData("l1a1_o", "l1a1")]
    [InlineData("SkVans_O", "SkVans")]
    [InlineData("skjamo2", "skjam")]      // both spellings ship in one game
    [InlineData("sksf_o2", "sksf")]
    [InlineData("noSuffix", "noSuffix")]
    public void LevelStem_StripsEitherBankSpelling(string bank, string expected)
    {
        Assert.Equal(expected, N64TrgFileReferences.LevelStem(bank));
    }

    /// <summary>
    ///     A script naming no bank is a faithful answer, not a failure: THPS3's
    ///     four sked park-editor triggers name none, exactly as the PS1 ships no
    ///     sked&lt;N&gt;_o.
    /// </summary>
    [Fact]
    public void Collect_ReportsNoFamilyWhenNoBankIsNamed()
    {
        var set = N64TrgFileReferences.Collect(Build((0x80, "sked1"), (0x7E, "sked1_l")));

        Assert.Equal("", set.BankName);
        Assert.False(set.HasFamily);
        Assert.Equal(["sked1", "sked1_l"], set.Outsiders);
    }

    /// <summary>
    ///     GapPolyHit and BackgroundCreate also carry string args, but theirs
    ///     are checksums. Only the three file opcodes contribute.
    /// </summary>
    [Fact]
    public void Collect_IgnoresChecksumStringsAndOtherOpcodes()
    {
        var set = N64TrgFileReferences.Collect(Build(
            (0x8E, "SkVans_O"),
            (0x80, "0xED4E56EE"),   // a checksum spelled as text
            (0xC9, "SkVans_X"),     // GapPolyHit: not a file opcode
            (0x80, "")));           // empty operand

        Assert.Equal(["SkVans_O"], set.Family);
        Assert.Empty(set.Outsiders);
    }
}
