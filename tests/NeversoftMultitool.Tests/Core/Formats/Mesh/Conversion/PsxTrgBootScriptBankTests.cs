using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     Pins the TRG-driven object-bank selection for THPS two-player and HORSE
///     regions (2026-08-04), which replaced a filename guess.
///
///     The engine picks the bank in <c>Trig_InitialParseTRGFile</c>: with two
///     players it runs AUTOEXEC2 (node type 15) INSTEAD of AUTOEXEC (type 4)
///     when any AUTOEXEC2 exists, and the bank is the last <c>0x8E SetObjFile</c>
///     that script executes. Four levels ship an AUTOEXEC2 that deliberately
///     names none, so their two-player regions have NO bank even though an
///     unreferenced <c>o2</c> bank sits beside them on the disc — the reported
///     over-placement. HORSE counts as two-player (<c>GGame == 7</c> launches
///     with <c>GNumberOfPlayers == 2</c>), which corrected this codebase's
///     previous claim that HORSE always ran the one-player bank.
/// </summary>
public sealed class PsxTrgBootScriptBankTests(TestPaths paths)
{
    private const string Thps1Final = "Tony Hawk's Pro Skater (1999-9-29, PSX - Final)";
    private const string Thps2Final = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string Apocalypse = "Apocalypse (1998-11-17, PSX - Final)";

    [CorpusTheory]
    // AUTOEXEC2 present but with no SetObjFile: no bank, in both games.
    [InlineData(Thps1Final, "skdown_2.psx", "")]
    [InlineData(Thps2Final, "skdown_2.psx", "")]
    [InlineData(Thps2Final, "skbul_2.psx", "")]
    [InlineData(Thps2Final, "skmar_2.psx", "")]
    [InlineData(Thps2Final, "skven_2.psx", "")]
    // No AUTOEXEC2 at all: the one-player AUTOEXEC bank applies.
    [InlineData(Thps1Final, "skschl_2.psx", "SkSchl_o.psx")]
    [InlineData(Thps2Final, "skware_2.psx", "SkWare_O.psx")]
    // AUTOEXEC2 names a reduced bank; the TRG also spells it exactly, which is
    // why the old _o2-vs-o2 spelling table is gone.
    [InlineData(Thps2Final, "sksf_2.psx", "SkSF_O2.psx")]
    [InlineData(Thps1Final, "skmall_2.psx", "SkMallo2.psx")]
    // HORSE runs AUTOEXEC2 too: skros_h takes the two-player bank (the filename
    // rule gave it SkRos_O), and skdown_h takes none.
    [InlineData(Thps1Final, "skros_h.psx", "SkRosO2.psx")]
    [InlineData(Thps2Final, "skros_h.psx", "SkRosO2.psx")]
    [InlineData(Thps2Final, "skdown_h.psx", "")]
    public void VariantRegion_TakesTheBankItsBootScriptNames(
        string build,
        string fileName,
        string expectedBank)
    {
        var path = paths.FindSampleFile(build, fileName);
        Assert.SkipWhen(path == null, $"{build} {fileName} sample not available");

        var source = new FileSystemAssetSource(path!);
        Assert.True(
            MeshCompanionResolver.TryResolvePsxLevelCompanions(source, fileName, out var companions),
            "the region should still resolve as a level even when it has no bank");
        Assert.Equal(expectedBank, companions.BankCompanionName);
    }

    /// <summary>
    ///     A bankless two-player region is still a LEVEL: callers use this flag
    ///     to pick fly mode and the walk eye height, and the region still gets
    ///     its TRG sky and pickup layers.
    /// </summary>
    [CorpusFact]
    public void BanklessVariant_StillCountsAsALevel()
    {
        var path = paths.FindSampleFile(Thps2Final, "skdown_2.psx");
        Assert.SkipWhen(path == null, "THPS2 skdown_2.psx sample not available");

        Assert.True(MeshCompanionResolver.HasSupportedLevelObjectCompanion(
            new FileSystemAssetSource(path!), "skdown_2.psx"));
    }

    /// <summary>
    ///     Apocalypse geometry chunks are spelled like two-player variants and
    ///     share a <c>&lt;base&gt;_t.trg</c> whose boot script DOES name a bank.
    ///     Letting the variant rule claim them would attach the level's shared
    ///     bank to every chunk — the per-chunk duplication the Apocalypse
    ///     resolver exists to prevent by attaching it to one primary.
    /// </summary>
    [CorpusFact]
    public void ApocalypseChunk_IsNotTreatedAsATwoPlayerVariant()
    {
        var path = paths.FindSampleFile(Apocalypse, "city_2.psx");
        Assert.SkipWhen(path == null, "Apocalypse city_2.psx sample not available");

        var resolved = MeshCompanionResolver.TryResolvePsxLevelCompanions(
            new FileSystemAssetSource(path!), "city_2.psx", out var companions);
        Assert.False(
            resolved && companions.BankCompanionName.Length > 0,
            "a chunk must not pick up the level's shared object bank");
    }
}
