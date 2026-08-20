using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the model-slot name array boot.bin carries — the only source that
///     STATES a slot's name rather than inferring it.
/// </summary>
public sealed class N64BundleNameTableTests(TestPaths paths)
{
    private const string Thps1Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string Thps1Rom = "Tony Hawk's Pro Skater (USA).z64";
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3Rom = "Tony Hawk's Pro Skater 3 (USA).z64";
    private const string SpiderBuild = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderRom = "Spider-Man (USA).z64";

    /// <summary>
    ///     The array is found by shape, not by a tabled offset. What makes that
    ///     safe is that each ROM contains EXACTLY ONE qualifying run — the
    ///     reader is not choosing the longest of several candidates — and the
    ///     one it finds sits where independent disassembly said it would.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps1Build, Thps1Rom, 80)]
    [InlineData(Thps2Build, Thps2Rom, 135)]
    [InlineData(Thps3Build, Thps3Rom, 104)]
    [InlineData(SpiderBuild, SpiderRom, 298)]
    public void TheArrayIsTheSoleQualifyingRunInTheImage(
        string buildName, string romName, int expectedEntries)
    {
        var boot = OpenBoot(buildName, romName);
        var entries = N64BundleNameTable.ReadEntries(boot);

        Assert.Equal(expectedEntries, entries.Count);
        Assert.All(entries, static entry =>
            Assert.EndsWith(".psx", entry.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Coverage against the REAL carved slot set, which is sparse — THPS3
    ///     numbers to 196 for 112 bundles — so the table's slots are intersected
    ///     with the directories that exist rather than compared against a count.
    ///     Every entry must land on a slot that is actually carved; a dangling
    ///     entry would mean the slot space is not the one we think it is.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps1Build, Thps1Rom, 80, 80, 0)]
    [InlineData(Thps2Build, Thps2Rom, 141, 126, 4)]
    [InlineData(Thps3Build, Thps3Rom, 112, 103, 0)]
    [InlineData(SpiderBuild, SpiderRom, 261, 258, 0)]
    public void ResolvedSlots_CoverTheCarvedSlotsAndDeclineAmbiguousOnes(
        string buildName,
        string romName,
        int expectedCarvedSlots,
        int expectedUnambiguous,
        int expectedDeclined)
    {
        var assets = Carve(buildName, romName);
        var boot = N64BootImage.TryOpen(
            Assert.Single(assets, static a => a.Path == "boot.bin").Data);
        var carvedSlots = CarvedSlots(assets);
        Assert.Equal(expectedCarvedSlots, carvedSlots.Count);

        var resolved = N64BundleNameTable.Resolve(boot);
        Assert.Equal(expectedUnambiguous, resolved.Keys.Count(carvedSlots.Contains));

        // Slots the array mentions but the carve does not produce would mean a
        // mismatch between the two slot spaces.
        Assert.Empty(resolved.Keys.Where(slot => !carvedSlots.Contains(slot)));

        // A slot the array spells two ways is declined rather than guessed:
        // THPS2's one- and two-player bank pairs share a slot, so there is no
        // single correct name for it.
        var mentioned = N64BundleNameTable.ReadEntries(boot)
            .Select(static e => e.Slot)
            .Where(carvedSlots.Contains)
            .ToHashSet();
        Assert.Equal(expectedDeclined, mentioned.Count(slot => !resolved.ContainsKey(slot)));
    }

    [CorpusFact]
    public void DeclinedThps2Slots_AreTheTwoPlayerBankPairs()
    {
        var assets = Carve(Thps2Build, Thps2Rom);
        var boot = N64BootImage.TryOpen(
            Assert.Single(assets, static a => a.Path == "boot.bin").Data);
        var resolved = N64BundleNameTable.Resolve(boot);

        var declined = N64BundleNameTable.ReadEntries(boot)
            .GroupBy(static e => e.Slot)
            .Where(g => !resolved.ContainsKey(g.Key))
            .ToDictionary(
                static g => g.Key,
                static g => g.Select(static e => e.Name.ToLowerInvariant())
                    .Distinct()
                    .OrderBy(static n => n, StringComparer.Ordinal)
                    .ToArray());

        Assert.Equal([108, 138, 139, 140], declined.Keys.Order());
        Assert.Equal(["skny_o.psx", "skny_o2.psx"], declined[108]);
        Assert.Equal(["bkfact.psx", "bkfact_2.psx"], declined[138]);
    }

    /// <summary>
    ///     The reason the table exists: content identity cannot separate slots
    ///     built on a shared rig, so these collapsed onto one name before.
    /// </summary>
    [CorpusFact]
    public void SharedRigSlots_GetDistinctNames()
    {
        var thps1 = N64BundleNameTable.Resolve(OpenBoot(Thps1Build, Thps1Rom));
        Assert.Equal("glif", thps1[33]);
        Assert.Equal("glif_FE", thps1[34]);

        var spider = N64BundleNameTable.Resolve(OpenBoot(SpiderBuild, SpiderRom));
        Assert.Equal(
            ["lizman", "lizman2", "lizman2t", "lizmant"],
            new[] { spider[106], spider[107], spider[221], spider[222] });
    }

    /// <summary>
    ///     THE ACCEPTANCE GATE. Where the stated table and the inferred sources
    ///     both name a slot, the table is allowed to REFINE the answer but never
    ///     to contradict it.
    ///     <para>
    ///         Refinement is what a disagreement looks like here, and it has one
    ///         exact shape: the inferred name is a PREFIX of the stated one.
    ///         Content identity deliberately falls back to "a shared prefix that
    ///         is itself a candidate" when several files share a rig — <c>glif</c>
    ///         for <c>glif|glif2|glif2b|glif_fe</c> — so it answers <c>lizman</c>
    ///         for all four lizman slots and <c>glif</c> for both glif slots.
    ///         The table supplies the suffix that content could never see.
    ///     </para>
    ///     <para>
    ///         An overlap that is NOT a prefix would mean the two sources
    ///         genuinely disagree about which file a slot holds, and priority
    ///         order would be silently picking a winner. That must fail.
    ///     </para>
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps1Build, Thps1Rom, 9, 0)]
    [InlineData(Thps2Build, Thps2Rom, 12, 0)]
    [InlineData(Thps3Build, Thps3Rom, 9, 4)]
    [InlineData(SpiderBuild, SpiderRom, 4, 0)]
    public void StatedNames_OnlyRefineOrCorrectAnInferredName(
        string buildName, string romName, int expectedRefinements, int expectedCorrections)
    {
        var assets = Carve(buildName, romName);
        var (bundles, triggers, boot) = SplitForResolver(assets);

        var inferred = N64BundleNameResolver.Resolve(bundles, triggers);
        var stated = N64BundleNameResolver.Resolve(bundles, triggers, boot);

        // A name the inferred sources put on more than one slot is one they
        // contradicted themselves about, so they cannot be authoritative for it.
        var inferredCollisions = inferred
            .GroupBy(static kv => kv.Value, StringComparer.OrdinalIgnoreCase)
            .Where(static g => g.Count() > 1)
            .Select(static g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overlaps = inferred
            .Where(kv => stated.TryGetValue(kv.Key, out var name)
                         && !string.Equals(name, kv.Value, StringComparison.OrdinalIgnoreCase))
            .Select(kv => (Slot: kv.Key, Inferred: kv.Value, Stated: stated[kv.Key]))
            .OrderBy(static x => x.Slot, StringComparer.Ordinal)
            .ToArray();

        var refinements = overlaps
            .Where(static x => x.Stated.StartsWith(x.Inferred, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var corrections = overlaps.Except(refinements).ToArray();

        // Anything that is neither a refinement of a collapsed name nor a
        // correction of a self-contradicting one would be a real disagreement
        // about which file a slot holds, and priority order would be silently
        // picking a winner.
        var unexplained = corrections
            .Where(x => !inferredCollisions.Contains(x.Inferred))
            .Select(static x => $"slot {x.Slot}: inferred '{x.Inferred}' vs stated '{x.Stated}'")
            .ToArray();
        Assert.Empty(unexplained);

        Assert.Equal(expectedRefinements, refinements.Length);
        Assert.Equal(expectedCorrections, corrections.Length);

        // The table must never LOSE a name the inferred sources already had.
        Assert.Empty(inferred.Keys.Where(slot => !stated.ContainsKey(slot)));
    }

    /// <summary>
    ///     The four THPS3 corrections above are one pre-existing defect the
    ///     table exposed: the TRG family alignment mis-places the port's
    ///     two-player object banks (<c>aa&lt;stem&gt;2o</c>), putting
    ///     <c>aadnhl_o</c> — really slot 166 — onto three slots belonging to
    ///     entirely different levels. Pinned by name so the defect is recorded
    ///     rather than merely absorbed, and so a fix to the alignment shows up
    ///     here as this test going quiet.
    /// </summary>
    [CorpusFact]
    public void Thps3TwoPlayerObjectBanks_AreMisalignedByTheTriggerRouteAndCorrected()
    {
        var (bundles, triggers, boot) = SplitForResolver(Carve(Thps3Build, Thps3Rom));
        var inferred = N64BundleNameResolver.Resolve(bundles, triggers);
        var stated = N64BundleNameResolver.Resolve(bundles, triggers, boot);

        foreach (var (slot, wrong, right) in new[]
                 {
                     ("143", "aaair_o", "aaair2o"),
                     ("153", "aadnhl_o", "aaburb2o"),
                     ("177", "aadnhl_o", "aajap2o"),
                     ("182", "aadnhl_o", "aala2o")
                 })
        {
            Assert.Equal(wrong, inferred[slot]);
            Assert.Equal(right, stated[slot]);
        }

        // The name the alignment over-used really belongs to exactly one slot.
        Assert.Equal("aadnhl_o", stated["166"]);
        Assert.Single(stated.Where(static kv => kv.Value == "aadnhl_o"));
    }

    [CorpusTheory]
    [InlineData(Thps1Build, Thps1Rom, 73, 80)]
    [InlineData(Thps2Build, Thps2Rom, 98, 134)]
    [InlineData(Thps3Build, Thps3Rom, 66, 109)]
    [InlineData(SpiderBuild, SpiderRom, 179, 259)]
    public void AdoptingTheTableRaisesNamedSlotCoverage(
        string buildName, string romName, int expectedBefore, int expectedAfter)
    {
        var assets = Carve(buildName, romName);
        var (bundles, triggers, boot) = SplitForResolver(assets);

        Assert.Equal(expectedBefore, N64BundleNameResolver.Resolve(bundles, triggers).Count);
        Assert.Equal(expectedAfter, N64BundleNameResolver.Resolve(bundles, triggers, boot).Count);
    }

    [Fact]
    public void NoBootImage_YieldsNoTableRatherThanAGuess()
    {
        Assert.Empty(N64BundleNameTable.Resolve(null));
        Assert.Empty(N64BundleNameTable.ReadEntries(null));
    }

    private static (List<N64BundleNameResolver.Bundle> Bundles, List<byte[]> Triggers,
        N64BootImage? Boot) SplitForResolver(IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
    {
        var bundles = new List<N64BundleNameResolver.Bundle>();
        var triggers = new List<byte[]>();
        N64BootImage? boot = null;

        foreach (var asset in assets)
        {
            if (asset.Path == "boot.bin")
            {
                boot = N64BootImage.TryOpen(asset.Data);
            }
            else if (asset.Path.StartsWith("triggers/", StringComparison.Ordinal)
                     && asset.Path.EndsWith(".trg.n64", StringComparison.Ordinal))
            {
                triggers.Add(asset.Data);
            }
            else if (asset.Path.StartsWith("models/", StringComparison.Ordinal)
                     && asset.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            {
                bundles.Add(new N64BundleNameResolver.Bundle(SlotOf(asset.Path), asset.Data));
            }
        }

        return (bundles, triggers, boot);
    }

    private static HashSet<int> CarvedSlots(IReadOnlyList<N64AssetCarver.CarvedAsset> assets)
    {
        return assets
            .Where(static a => a.Path.StartsWith("models/", StringComparison.Ordinal)
                               && a.Path.EndsWith(".psx.n64", StringComparison.Ordinal))
            .Select(static a => int.Parse(SlotOf(a.Path)))
            .ToHashSet();
    }

    private static string SlotOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return path[(path.LastIndexOf('/', slash - 1) + 1)..slash];
    }

    private IReadOnlyList<N64AssetCarver.CarvedAsset> Carve(string buildName, string romName)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets;
    }

    private N64BootImage? OpenBoot(string buildName, string romName)
    {
        return N64BootImage.TryOpen(
            Assert.Single(Carve(buildName, romName), static a => a.Path == "boot.bin").Data);
    }
}
