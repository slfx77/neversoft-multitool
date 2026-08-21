using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Pins that a carved cart's own <c>tricks.bin</c> reaches its animation
///     slots. The cart ships the table as an unclassified payload with no
///     distinguishing name, so it is found by parsing rather than by path.
/// </summary>
public sealed class N64TrickTableLocatorTests(TestPaths paths)
{
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3Rom = "Tony Hawk's Pro Skater 3 (USA).z64";
    private const string SpiderBuild = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderRom = "Spider-Man (USA).z64";

    /// <summary>
    ///     THPS2 N64's skater bank holds exactly 218 slots and its table tops
    ///     out at 217, so the names attach. 110 of those slots are owned by
    ///     exactly one trick — the figure `TricksFileTests` pins for the same
    ///     table read straight out of the ROM.
    /// </summary>
    [CorpusFact]
    public void TheSkaterBankTakesItsNamesFromTheCartsOwnTable()
    {
        var backend = OpenRom(Thps2Build, Thps2Rom);
        var source = N64Bundles.OpenBundle(backend, "045");

        var names = N64TrickTableLocator.ForBundle(source, 218);
        Assert.Equal(110, names.Count);
        Assert.Equal("Christ Air", names[75]);
        Assert.Equal("Japan Air", names[31]);
    }

    /// <summary>
    ///     The gate is EXACT, not "every slot fits". Carts hold shells with as
    ///     many as 300 clips, and a loose bound would let any of them swallow
    ///     this table's names.
    /// </summary>
    [CorpusFact]
    public void ABankThatIsNotExactlyTheTablesBankIsRefused()
    {
        var backend = OpenRom(Thps2Build, Thps2Rom);
        var source = N64Bundles.OpenBundle(backend, "045");

        Assert.NotEmpty(N64TrickTableLocator.ForBundle(source, 218));
        Assert.Empty(N64TrickTableLocator.ForBundle(source, 217));
        Assert.Empty(N64TrickTableLocator.ForBundle(source, 219));
        Assert.Empty(N64TrickTableLocator.ForBundle(source, 300));
        Assert.Empty(N64TrickTableLocator.ForBundle(source, 0));
    }

    /// <summary>
    ///     Spider-Man ships no trick table, so every slot count names nothing —
    ///     rather than borrowing another cart's table.
    /// </summary>
    [CorpusFact]
    public void ACartWithoutATableNamesNothing()
    {
        var backend = OpenRom(SpiderBuild, SpiderRom);
        var bundle = backend.Entries.First(static e =>
            e.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase));
        var source = new ArchiveAssetSource(backend, bundle);

        foreach (var slots in new[] { 44, 218, 300 })
            Assert.Empty(N64TrickTableLocator.ForBundle(source, slots));
    }

    /// <summary>
    ///     A carve extracted to disk must resolve the same names as the same
    ///     carve read in place. The two take different branches — one walks
    ///     archive entries, the other walks up to the carve root — so parity is
    ///     not free.
    /// </summary>
    [CorpusFact]
    public void AnExtractedCarveResolvesTheSameNamesAsTheRom()
    {
        var romPath = paths.FindSampleFile(Thps2Build, Thps2Rom);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));

        var root = Path.Combine(Path.GetTempPath(), $"n64carve_{Guid.NewGuid():N}");
        try
        {
            foreach (var asset in assets)
            {
                var target = Path.Combine(root, asset.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, asset.Data);
            }

            var bundle = Directory
                .EnumerateFiles(Path.Combine(root, "models", "045"), "*.psx.n64")
                .Single();
            var source = new FileSystemAssetSource(bundle);

            Assert.Equal(root, NeversoftMultitool.Core.Formats.Mesh.N64.N64ModelCompanions
                .TryFindCarveRoot(source));
            var names = N64TrickTableLocator.ForBundle(source, 218);

            Assert.Equal(110, names.Count);
            Assert.Equal("Christ Air", names[75]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    ///     Across a whole cart, exactly the skater bank takes the names. This is
    ///     the fail-closed sweep: the exact-fit gate is what stops any of the
    ///     other animated shells — one carries 300 clips — from swallowing the
    ///     table, and only a per-cart census actually proves that.
    /// </summary>
    [CorpusTheory]
    // Both skating carts keep their skater bank at slot 045, and each bank is
    // exactly one slot longer than its table's highest reference (218/217,
    // 226/225) — the same equality the disc pairings show.
    [InlineData(Thps2Build, Thps2Rom, "045", 218, 110)]
    [InlineData(Thps3Build, Thps3Rom, "045", 226, 124)]
    [InlineData(SpiderBuild, SpiderRom, null, 0, 0)]
    public void ExactlyOneBundlePerCartTakesTheNames(
        string build, string rom, string? expectedSlot, int expectedBankSlots, int expectedNames)
    {
        var backend = OpenRom(build, rom);
        var named = new List<(string Slot, int Slots, int Names)>();

        foreach (var entry in backend.Entries)
        {
            if (!entry.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase))
                continue;

            var source = new ArchiveAssetSource(backend, entry);
            var slots = N64CompressedAnimationBank.TryParse(source.ReadBytes())?.Entries.Count ?? 0;
            if (slots == 0)
                continue;

            var names = N64TrickTableLocator.ForBundle(source, slots);
            if (names.Count > 0)
                named.Add((entry.Directory.Split('/')[^1], slots, names.Count));
        }

        var detail = string.Join(", ", named.Select(n => $"{n.Slot}:{n.Slots}slots/{n.Names}names"));
        if (expectedSlot == null)
        {
            Assert.True(named.Count == 0, $"expected no named bundle, got {detail}");
            return;
        }

        Assert.True(named.Count == 1, $"expected exactly one named bundle, got {detail}");
        var hit = named[0];
        Assert.Equal(expectedSlot, hit.Slot);
        Assert.Equal(expectedBankSlots, hit.Slots);
        Assert.Equal(expectedNames, hit.Names);
    }

    /// <summary>
    ///     The GUI's animation list is fed by discovery, which is a separate
    ///     path from the export — so it needs its own pin that the names arrive.
    /// </summary>
    [CorpusFact]
    public void DiscoveryLabelsTheNamedSlotsToo()
    {
        var backend = OpenRom(Thps2Build, Thps2Rom);
        var source = N64Bundles.OpenBundle(backend, "045");

        var probes = AnimationDiscovery.FindForCharacter(source, null, CancellationToken.None);
        Assert.Equal(218, probes.Count);
        Assert.EndsWith("::Christ Air", probes[75].DisplayName, StringComparison.Ordinal);
        Assert.Equal(110, probes.Count(p =>
            !AnimationExportName.IsUnnamedSlot(p.DisplayName.Split("::")[^1])));
    }

    private ArchiveAssetBackend OpenRom(string build, string rom)
    {
        var path = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(path == null, $"{build} ROM not available");
        var backend = ArchiveAssetBackend.TryOpen(path!);
        Assert.NotNull(backend);
        return backend!;
    }
}
