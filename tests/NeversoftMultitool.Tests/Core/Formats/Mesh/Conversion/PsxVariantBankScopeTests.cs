using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

/// <summary>
///     A two-player region shares its one-player region's <c>_t.trg</c>, so the
///     SAME PLATFORM/MANIPOB nodes run for both. What separates them is the
///     bank the boot script binds: the engine resolves a node's model checksum
///     against <c>pCurrentObjFile</c> alone, and a node whose model is not in
///     the bound bank instances nothing.
///     <para>
///         There is no SPATIAL region filter to add.
///         <c>Trig_InitialParseTRGFile</c> (TRIG.cpp:3090, PERFECT 130/130)
///         only chooses AUTOEXEC2 over AUTOEXEC when two players are active —
///         which <c>PsxTrgBootScript</c> already implements — and
///         <c>Trig_ParseTRGFile</c> walks every node regardless of region.
///     </para>
///     <para>
///         These pin that contract on the reported case. <c>skny_2</c> was
///         reported as leaking one-player instances because it carries the same
///         eight <c>obj_barrier01</c> placements as <c>skny</c> while
///         <c>SkNY_O2</c> holds only two objects. It does — and it is correct,
///         because <c>obj_barrier01</c> is ONE OF THOSE TWO objects. The
///         resolver is structurally incapable of the reported leak: its result
///         is keyed by bank object index, so every placement it returns belongs
///         to the bound bank by construction.
///     </para>
/// </summary>
public sealed class PsxVariantBankScopeTests(TestPaths paths)
{
    private const string Thps2Final = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";

    [CorpusTheory]
    // The one-player bank carries four objects; its extra props are the ones
    // the two-player region must NOT show.
    [InlineData("skny_o.psx", 5, 8, new[] { "obj_ny_banks_backboard", "obj_token01" })]
    // The reduced two-player bank carries two, one of which is the barrier.
    [InlineData("skny_o2.psx", 2, 8, new string[0])]
    public void SharedTriggerInstancesOnlyWhatTheBoundBankContains(
        string bankName, int expectedObjects, int expectedBarriers, string[] expectedExtras)
    {
        var bankPath = paths.FindSampleFile(Thps2Final, bankName);
        Assert.SkipWhen(bankPath == null, $"{bankName} not available");
        var bank = PsxMeshFile.Parse(File.ReadAllBytes(bankPath!));
        Assert.NotNull(bank);
        Assert.Equal(expectedObjects, bank!.Objects.Count);

        var trg = PsxLevelObjectPlacementResolver.TryLoadTriggerCompanion(
            new FileSystemAssetSource(bankPath!), "skny");
        Assert.NotNull(trg);

        var placements = PsxLevelObjectPlacementResolver.Resolve(trg, bank);

        // Every key is a bank object index — the leak the report described
        // cannot be expressed by this return type.
        Assert.All(placements.Keys, index => Assert.InRange(index, 0, bank.Objects.Count - 1));

        var byMesh = placements.ToDictionary(
            pair => MeshNameOf(bank, pair.Key),
            static pair => pair.Value.Count);

        Assert.Equal(expectedBarriers, byMesh.GetValueOrDefault("obj_barrier01"));
        foreach (var extra in expectedExtras)
            Assert.True(byMesh.GetValueOrDefault(extra) > 0, $"{extra} should be placed");

        // Nothing outside the bound bank can appear.
        Assert.All(byMesh.Keys, name => Assert.Contains(
            name, Enumerable.Range(0, bank.Objects.Count).Select(i => MeshNameOf(bank, i))));
    }

    /// <summary>
    ///     The one-player level's own geometry contains a mesh NAMED
    ///     <c>dt_park_rail03</c>, and so does the two-player BANK. Same name,
    ///     unrelated sources — which is exactly the coincidence that makes a
    ///     name-only comparison of two converted levels look like a leak.
    /// </summary>
    [CorpusFact]
    public void TheSharedRailNameComesFromDifferentSourcesInEachRegion()
    {
        var levelPath = paths.FindSampleFile(Thps2Final, "skny.psx");
        var bankPath = paths.FindSampleFile(Thps2Final, "skny_o2.psx");
        Assert.SkipWhen(levelPath == null || bankPath == null, "skny samples not available");

        var level = PsxMeshFile.Parse(File.ReadAllBytes(levelPath!));
        var twoPlayerBank = PsxMeshFile.Parse(File.ReadAllBytes(bankPath!));
        Assert.NotNull(level);
        Assert.NotNull(twoPlayerBank);

        // Resolved by NAME rather than a pasted checksum, so the assertion
        // cannot quietly pass against a stale literal.
        Assert.Contains("dt_park_rail03", ResolvedMeshNames(level!));
        Assert.Contains("dt_park_rail03", ResolvedMeshNames(twoPlayerBank!));

        // The one-player level does NOT carry the barrier in its own geometry —
        // its eight placements come from the bank plus the shared trigger.
        Assert.DoesNotContain("obj_barrier01", ResolvedMeshNames(level!));
    }

    private static HashSet<string> ResolvedMeshNames(PsxMeshFile file)
    {
        return file.MeshNameHashes
            .Select(static hash => QbKey.TryResolve(hash) ?? $"0x{hash:X8}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string MeshNameOf(PsxMeshFile bank, int objectIndex)
    {
        var meshIndex = bank.Objects[objectIndex].MeshIndex;
        return meshIndex < bank.MeshNameHashes.Length
            ? QbKey.TryResolve(bank.MeshNameHashes[meshIndex]) ?? $"0x{bank.MeshNameHashes[meshIndex]:X8}"
            : "<none>";
    }
}
