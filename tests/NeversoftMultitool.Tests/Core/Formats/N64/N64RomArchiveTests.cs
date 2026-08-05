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
    ///     Spider-Man's entire corpus is ERZ v2, so every asset decodes with
    ///     the transcribed core TODAY. This sweeps all of them — any stream the
    ///     transcription mishandles throws and fails the test.
    /// </summary>
    [Fact]
    public void SpiderManRom_EveryAssetBlockDecodes()
    {
        var romPath = paths.FindSampleFile(
            "Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64");
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");

        var rom = File.ReadAllBytes(romPath!);
        var tables = N64RomArchive.FindTables(rom);
        var standalone = N64RomArchive.FindStandaloneBlocks(rom, tables);
        Assert.True(standalone.Count > 1_500,
            $"standalone scan surfaced only {standalone.Count} of ~1,568 asset blocks");

        long decodedBytes = 0;
        foreach (var (offset, length) in standalone)
        {
            var block = rom[offset..(offset + length)];
            Assert.Equal(2, ErzDecoder.GetVersion(block));
            var data = ErzDecoder.Decode(block);
            Assert.Equal(ErzDecoder.GetDecompressedSize(block), data.Length);
            decodedBytes += data.Length;
        }

        Assert.True(decodedBytes > 20_000_000,
            $"decoded only {decodedBytes} bytes across the corpus");
    }
}
