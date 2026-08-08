using System.Text;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

/// <summary>
///     Pins the .z64 sub-file table walker against the real THPS2 N64 ROM
///     (2026-08-05). The boot package table at ROM 0x13B74 (15 blocks) is the
///     known-good anchor: its first block decodes to the skater-definition
///     data beginning "sk2def" — the same output the emulated ROM decompressor
///     produces.
/// </summary>
public sealed class N64RomArchiveTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    [Fact]
    public void Thps2Rom_TableWalkFindsTheBootPackage()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        Assert.True(N64RomArchive.IsN64Rom(romPath!));

        var rom = File.ReadAllBytes(romPath!);
        var tables = N64RomArchive.FindTables(rom);

        Assert.NotEmpty(tables);
        var boot = Assert.Single(tables, static table => table.Offset == 0x13B74);
        Assert.Equal(15, boot.Blocks.Count);

        var data = N64RomArchive.ExtractTable(rom, boot);
        // 14 full 64 KB blocks + a short final block (the boot loop writes
        // block N at dst + N*0x10000; the file ends mid-block).
        Assert.Equal(949_776, data.Length);
        Assert.Equal("sk2def", Encoding.ASCII.GetString(data, 0, 6));
    }

    /// <summary>
    ///     Only the boot package is table-shaped; the asset corpus is
    ///     standalone back-to-back blocks. THPS2's assets are ERZ v1 (only its
    ///     boot package is v2); the enumeration must still surface all of them.
    /// </summary>
    [Fact]
    public void Thps2Rom_StandaloneScanSurfacesTheAssetCorpus()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        var tables = N64RomArchive.FindTables(rom);
        var standalone = N64RomArchive.FindStandaloneBlocks(rom, tables);

        // ROM-wide census: 15 v2 (boot table) + 1,143 v1 (assets).
        Assert.True(standalone.Count > 1_100,
            $"standalone scan surfaced only {standalone.Count} of ~1,143 asset blocks");
    }

    /// <summary>
    ///     Master-directory walk (RE'd 2026-08-05): the u32 before the boot
    ///     table points at a root of per-stream block directories covering the
    ///     whole asset corpus — including the raw audio groups and the
    ///     odd-offset blocks the aligned magic scan missed.
    /// </summary>
    [Theory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 7, 1_097)]
    [InlineData(Thps2N64Build, RomName, 9, 1_151)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 9, 1_114)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 8, 1_789)]
    public void MasterDirectory_CoversTheWholeCorpus(
        string buildName,
        string romName,
        int expectedGroups,
        int expectedLeaves)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        Assert.True(N64RomArchive.TryReadMasterDirectory(rom, out _, out var groups, out var bootTable));

        Assert.Equal(expectedGroups, groups.Count);
        Assert.NotEmpty(bootTable.Blocks);
        Assert.Contains(groups, static group => !group.IsCompressed); // raw audio ships in every ROM
        Assert.Equal(expectedLeaves, groups.Sum(static group => group.Leaves.Count(static leaf => leaf.Length > 0)));
    }

    /// <summary>
    ///     Carved extraction (2026-08-05, replacing the flat offset-named
    ///     blocks): 16 KB stream blocks reassemble into logical streams, whose
    ///     payload tables carve into typed assets. Counts pinned from the
    ///     five-agent taxonomy pass validated against the PS1 corpora (models
    ///     = 4-slot bundles, 141 for THPS2; 14 level TRGs; 2,905 texture
    ///     records; 14 SFX cue banks; the sk2anim shell at models/045 is the
    ///     587,000-byte trick-animation bank).
    /// </summary>
    [Fact]
    public void CarvedListing_MatchesTheTaxonomy()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var entries = N64RomArchive.GetFileList(romPath!);
        Assert.Equal(3_962, entries.Count);

        int CountUnder(string dir) => entries.Count(entry => entry.Name.StartsWith(dir + "/", StringComparison.Ordinal));
        Assert.Equal(564, CountUnder("models")); // 141 bundles x 4 slots
        Assert.Equal(14, CountUnder("triggers"));
        Assert.Equal(2_905, CountUnder("textures"));
        Assert.Equal(99, CountUnder("images"));
        Assert.Equal(14, CountUnder("sfx"));
        Assert.Equal(194, CountUnder("misc"));
        Assert.Equal(163, CountUnder("group2")); // N64-native render bank, format still open
        Assert.Equal(8, CountUnder("audio"));    // raw wavetable + instrument banks
        Assert.Contains(entries, static entry => entry.Name == "boot.bin");
        // Slot 045 is a shared-rig character: 433 of 450 bundles match a PS1
        // file, but nine unrelated skaters share this content key, so it keeps
        // the bare slot rather than claiming one of their names.
        Assert.Contains(entries, static entry =>
            entry.Name == "models/045/045.psx.n64" && entry.Size == 587_000);
        // ...and one bundle content DOES name, which is the whole point of
        // N64BundleNames: the slot stays as a prefix, the name identifies it.
        Assert.Contains(entries, static entry => entry.Name == "models/008/008_c_kart.psx.n64");
        Assert.Contains(entries, static entry => entry.Name == "textures/0000_abutton.tex.n64");
        Assert.Contains(entries, static entry => entry.Name == "misc/anims_fe.psh");
    }

    /// <summary>
    ///     Cross-ROM carve pins for the classifier edge cases THPS2 can't
    ///     exercise: THPS1's package layout, THPS3's 4,445-slot texture
    ///     dictionary (payload tables exceed the ROM-directory 4,095 cap), and
    ///     Spider-Man's render bank, whose single chance 24,064-byte entry
    ///     must NOT drag the group into "misc" via the size-only .rec test.
    /// </summary>
    [Theory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 2_176, "textures", 1_488)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 3_313, "textures", 2_484)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64",
        4_286, "group2", 280)]
    public void CarvedListing_PinsTheOtherRoms(
        string buildName,
        string romName,
        int expectedEntries,
        string pinnedDir,
        int pinnedCount)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");

        var entries = N64RomArchive.GetFileList(romPath!);
        Assert.Equal(expectedEntries, entries.Count);
        Assert.Equal(pinnedCount, entries.Count(entry =>
            entry.Name.StartsWith(pinnedDir + "/", StringComparison.Ordinal)));
    }

    /// <summary>
    ///     Regression pin (user-reported 2026-08-05): before the v1 core landed,
    ///     <see cref="N64RomArchive.ExtractFiles" /> kept a "copy v1 blocks raw"
    ///     fallback — and it survived the v1 commit, so extraction shipped
    ///     still-compressed files (0x00A7D62.bin opened with an ERZ header).
    ///     Extraction output must never carry the ERZ magic, and on-disk sizes
    ///     must match the listing's sizes.
    /// </summary>
    [Fact]
    public void ExtractFiles_WritesFullyDecodedOutput()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        var outputDir = Directory.CreateTempSubdirectory("n64-extract-test").FullName;
        try
        {
            N64RomArchive.ExtractFiles(romPath!, outputDir, token: TestContext.Current.CancellationToken);

            var listing = N64RomArchive.GetFileList(romPath!)
                .ToDictionary(static entry => entry.Name, static entry => entry.Size);
            var files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            Assert.Equal(listing.Count, files.Length);

            foreach (var file in files)
            {
                var head = new byte[ErzDecoder.HeaderSize];
                using (var stream = File.OpenRead(file))
                {
                    stream.ReadExactly(head.AsSpan(0, (int)Math.Min(head.Length, stream.Length)));
                }

                var relative = Path.GetRelativePath(outputDir, file).Replace('\\', '/');
                Assert.False(ErzDecoder.IsErz(head), $"{relative} still starts with an ERZ header");
                Assert.Equal(listing[relative], new FileInfo(file).Length);
            }
        }
        finally
        {
            Directory.Delete(outputDir, recursive: true);
        }
    }

    /// <summary>
    ///     Full-corpus decode sweeps: every standalone asset block in every ROM
    ///     must decode with the transcribed cores (Spider-Man is all-v2; the
    ///     three THPS ROMs are v1 asset corpora). Any stream either core
    ///     mishandles throws and fails the sweep.
    /// </summary>
    [Theory]
    // Per-ROM packing varies: Spider-Man/THPS2/THPS3 keep their asset corpora
    // as standalone back-to-back blocks; THPS1 packages EVERYTHING into
    // multi-block tables (7 asset packages + boot). The sweep decodes every
    // ERZ block from BOTH sources.
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64", 1_500, 20_000_000)]
    [InlineData(Thps2N64Build, RomName, 1_100, 15_000_000)]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 1_000, 10_000_000)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 1_000, 15_000_000)]
    public void EveryAssetBlockDecodes(
        string buildName,
        string romName,
        int minBlocks,
        long minDecodedBytes)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        var tables = N64RomArchive.FindTables(rom);
        var standalone = N64RomArchive.FindStandaloneBlocks(rom, tables);
        var blocks = tables.SelectMany(static table => table.Blocks)
            .Concat(standalone)
            .Where(block => block.Length >= ErzDecoder.HeaderSize
                            && ErzDecoder.IsErz(rom.AsSpan(block.Offset, ErzDecoder.HeaderSize)))
            .ToList();
        Assert.True(blocks.Count > minBlocks,
            $"only {blocks.Count} ERZ blocks surfaced across tables + standalone");

        long decodedBytes = 0;
        foreach (var (offset, length) in blocks)
        {
            var block = rom[offset..(offset + length)];
            var data = ErzDecoder.Decode(block);
            Assert.Equal(ErzDecoder.GetDecompressedSize(block), data.Length);
            decodedBytes += data.Length;
        }

        Assert.True(decodedBytes > minDecodedBytes,
            $"decoded only {decodedBytes} bytes across the corpus");
    }
}
