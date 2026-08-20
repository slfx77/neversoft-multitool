using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

/// <summary>
///     Pins the overlay roster the ports DMA from a hardcoded cart address —
///     the region nothing in the master directory points at, so the carve could
///     not see it at all before 2026-08-20.
/// </summary>
public sealed class N64OverlayManifestTests(TestPaths paths)
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
    ///     Located by shape, not by a tabled offset: exactly one run of
    ///     stride-0x1C records qualifies in each boot image, which is what makes
    ///     scanning safe. THPS1 ships no roster at all.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps1Build, Thps1Rom, 0)]
    [InlineData(Thps2Build, Thps2Rom, 16)]
    [InlineData(Thps3Build, Thps3Rom, 16)]
    [InlineData(SpiderBuild, SpiderRom, 30)]
    public void TheRosterIsTheSoleQualifyingRunInTheImage(
        string buildName, string romName, int expectedEntries)
    {
        var assets = Carve(buildName, romName);
        var boot = N64BootImage.TryOpen(
            Assert.Single(assets, static a => a.Path == "boot.bin").Data);

        var entries = N64OverlayManifest.ReadEntries(boot);
        Assert.Equal(expectedEntries, entries.Count);
        Assert.All(entries, static entry =>
        {
            Assert.NotEmpty(entry.Name);
            Assert.True(entry.CodeSize > 0 && entry.RelocSize > 0);
        });
    }

    /// <summary>
    ///     The whole reading rests on this. The roster's addresses are NOT ROM
    ///     offsets — masking the cart domain off lands past the stored data and
    ///     most payloads decode as pure zeros — so the fixup is derived from
    ///     where directory coverage ends. The proof that the derivation is right
    ///     is that the payloads then TILE the region in order, accounting for
    ///     every byte, and each one lands on dense code rather than fill.
    /// </summary>
    [CorpusTheory]
    [InlineData(Thps2Build, Thps2Rom, 16, 312_940)]
    [InlineData(Thps3Build, Thps3Rom, 16, 324_904)]
    [InlineData(SpiderBuild, SpiderRom, 30, 631_884)]
    public void PayloadsTileTheRegionAndLandOnRealCode(
        string buildName, string romName, int expectedEntries, int expectedClaimedBytes)
    {
        var rom = ReadRom(buildName, romName);
        Assert.True(N64RomArchive.TryReadMasterDirectory(rom, out _, out var groups, out var bootTable));
        var start = N64OverlayManifest.FindDirectoryEnd(bootTable, groups);
        var end = N64OverlayManifest.FindRegionEnd(rom, start);

        var assets = Carve(buildName, romName);
        var boot = N64BootImage.TryOpen(
            Assert.Single(assets, static a => a.Path == "boot.bin").Data);
        var entries = N64OverlayManifest.ReadEntries(boot);
        var payloads = N64OverlayManifest.Slice(entries, start, end, rom.Length);

        Assert.Equal(expectedEntries * 2, payloads.Count);
        Assert.Equal(expectedClaimedBytes, payloads.Sum(static p => p.Length));

        // In order, with no gap and no overlap, beginning exactly where the
        // master directory stops.
        var cursor = start;
        foreach (var payload in payloads.OrderBy(static p => p.Offset))
        {
            Assert.Equal(cursor, payload.Offset);
            cursor += payload.Length;
        }

        // The footer the roster does not claim is small and bounded.
        Assert.InRange(end - cursor, 0, N64OverlayManifest.MaxTrailerBytes);

        // Code payloads must be dense MIPS, not fill: a mis-derived fixup would
        // slide them into the zero region past the stored data.
        foreach (var payload in payloads.Where(static p => p.Extension == ".bin"))
        {
            var slice = rom.AsSpan(payload.Offset, Math.Min(payload.Length, 512));
            var nonZero = 0;
            foreach (var b in slice)
            {
                if (b != 0)
                    nonZero++;
            }

            Assert.True(nonZero > slice.Length / 8,
                $"{payload.Name}.bin looks like fill, not code ({nonZero}/{slice.Length} non-zero)");
        }
    }

    [CorpusFact]
    public void SpiderManRosterCarriesThePs1OverlayNames()
    {
        var overlays = Carve(SpiderBuild, SpiderRom)
            .Where(static a => a.Path.StartsWith("overlays/", StringComparison.Ordinal))
            .Select(static a => a.Path)
            .ToHashSet(StringComparer.Ordinal);

        // 30 code + 30 reloc + the unclaimed footer.
        Assert.Equal(61, overlays.Count);
        foreach (var name in new[] { "blackcat", "carnage", "chopper", "cop", "docock", "venom" })
        {
            Assert.Contains($"overlays/{name}.bin", overlays);
            Assert.Contains($"overlays/{name}.rel", overlays);
        }

        Assert.Contains("overlays/trailer.bin", overlays);
    }

    /// <summary>
    ///     Every emitted code payload starts inside a real function: a mis-sliced
    ///     boundary would put the stream out of instruction alignment, and the
    ///     two commonest MIPS idioms would vanish.
    /// </summary>
    [CorpusFact]
    public void EmittedOverlayCodeDecodesAsMips()
    {
        var overlays = Carve(SpiderBuild, SpiderRom)
            .Where(static a => a.Path.EndsWith(".bin", StringComparison.Ordinal)
                               && a.Path.StartsWith("overlays/", StringComparison.Ordinal)
                               && a.Path != "overlays/trailer.bin")
            .ToArray();

        Assert.Equal(30, overlays.Length);
        var withPrologues = 0;
        foreach (var overlay in overlays)
        {
            var returns = 0;
            var prologues = 0;
            for (var offset = 0; offset + 4 <= overlay.Data.Length; offset += 4)
            {
                var word = BinaryPrimitives.ReadUInt32BigEndian(overlay.Data.AsSpan(offset));
                if (word == 0x03E0_0008)
                    returns++;                                   // jr ra
                else if (word >> 16 == 0x27BD && (word & 0x8000) != 0)
                    prologues++;                                 // addiu sp, sp, -N
            }

            // Every overlay must contain at least one of the two idioms. Not
            // both: sm_epanelinfo is a data-heavy overlay with returns but no
            // stack frames, which is a real shape rather than a mis-slice.
            Assert.True(returns > 0 || prologues > 0,
                $"{overlay.Path}: no MIPS idioms at all — the slice is not aligned code");
            if (prologues > 0)
                withPrologues++;
        }

        // Misaligning the stream by a byte would erase both idioms everywhere,
        // so requiring most overlays to carry stack frames is the real check.
        Assert.True(withPrologues >= 25,
            $"only {withPrologues}/30 overlays carry stack prologues");
    }

    [CorpusFact]
    public void Thps1HasNoOverlayRegionAndEmitsNothing()
    {
        var rom = ReadRom(Thps1Build, Thps1Rom);
        Assert.True(N64RomArchive.TryReadMasterDirectory(rom, out _, out var groups, out var bootTable));
        var start = N64OverlayManifest.FindDirectoryEnd(bootTable, groups);

        Assert.Equal(start, N64OverlayManifest.FindRegionEnd(rom, start));
        Assert.DoesNotContain(Carve(Thps1Build, Thps1Rom), static a =>
            a.Path.StartsWith("overlays/", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutARosterTheRegionIsNotSliced()
    {
        // The single-blob fallback exists so an unprovable roster cannot cause
        // a silent mis-slice; withholding the entries must produce no payloads.
        Assert.Empty(N64OverlayManifest.Slice([], 0x100, 0x200, 0x1000));
        Assert.Empty(N64OverlayManifest.ReadEntries(null));
    }

    private byte[] ReadRom(string buildName, string romName)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        return File.ReadAllBytes(romPath!);
    }

    private IReadOnlyList<N64AssetCarver.CarvedAsset> Carve(string buildName, string romName)
    {
        Assert.True(N64AssetCarver.TryCarve(ReadRom(buildName, romName), out var assets));
        return assets;
    }
}
