using NeversoftMultitool.Core.Formats.Audio.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Audio.Gba;

/// <summary>
///     Pins both GAX sample-table paths: the legacy longest-contiguous-run helper
///     used by the v1.99 music locator, and complete bank discovery across every
///     Vicarious Visions Tony Hawk GBA ROM.
/// </summary>
public sealed class GbaGaxAudioTests(TestPaths paths)
{
    private const string Build = "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)";
    private const string Rom = "Tony Hawk's Pro Skater 2 (USA, Europe).gba";

    private byte[]? LoadRom()
    {
        var path = paths.FindSampleFile(Build, Rom);
        return path == null ? null : File.ReadAllBytes(path);
    }

    [Fact]
    public void DetectsEngineAndBanner()
    {
        var rom = LoadRom();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");

        Assert.True(GbaGaxAudio.IsGaxRom(rom));
        var banner = GbaGaxAudio.GetVersionBanner(rom);
        Assert.NotNull(banner);
        Assert.Contains("GAX Sound Engine v1.99d", banner);
    }

    [Fact]
    public void FindsWaveSet_ContiguousSamples_ThatDecodeToRealPcm()
    {
        var rom = LoadRom();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");

        Assert.True(GbaGaxAudio.TryFindWaveSet(rom, out var tableOffset, out var samples));
        Assert.Equal(101, samples.Count);
        Assert.True(tableOffset > 0);

        // The bank is packed back-to-back — the anchor's defining invariant.
        for (var i = 0; i < samples.Count - 1; i++)
            Assert.Equal(samples[i].Address + (uint)samples[i].Size, samples[i + 1].Address);

        // Every record points inside the ROM at a plausible sample size.
        foreach (var s in samples)
        {
            Assert.InRange(s.Size, 1, 0x20000);
            Assert.True(s.Address >= 0x08000000 && s.Address + s.Size <= 0x08000000 + (uint)rom!.Length);
        }

        // A representative sample decodes to real audio: not silence, and the
        // sample-to-sample delta is far below the random-noise floor (~85 for
        // signed-8), i.e. a smooth waveform.
        var mid = samples[samples.Count / 2];
        var raw = GbaGaxAudio.GetSampleBytes(rom, mid);
        var pcm = GbaGaxAudio.DecodeToPcm16(raw);
        Assert.Equal(raw.Length, pcm.Length);
        Assert.Contains(pcm, v => v != 0);

        long deltaSum = 0;
        for (var i = 1; i < raw.Length; i++)
            deltaSum += Math.Abs((sbyte)raw[i] - (sbyte)raw[i - 1]);
        var avgDelta = (double)deltaSum / Math.Max(1, raw.Length - 1);
        Assert.True(avgDelta < 60, $"sample looks like noise (avg delta {avgDelta:F1})");
    }

    [Fact]
    public void DecodesBothGaxPcmConventions()
    {
        byte[] raw = [0x00, 0x7F, 0x80, 0xFF];

        Assert.Equal(
            [0, 32512, -32768, -256],
            GbaGaxAudio.DecodeToPcm16(raw, GbaGaxAudio.GaxPcmEncoding.Signed8));
        Assert.Equal(
            [-32768, -256, 0, 32512],
            GbaGaxAudio.DecodeToPcm16(raw, GbaGaxAudio.GaxPcmEncoding.Unsigned8));
    }

    // Each cart has two sparse sample tables. The old longest-run heuristic only
    // returned part of one table (and even selected the smaller bank in DHJ);
    // complete discovery preserves empty slot indices used by instruments.
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "v1.99d", 1,
        GbaGaxAudio.GaxPcmEncoding.Signed8, 34, 35, 126, 132)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", "2.11", 2,
        GbaGaxAudio.GaxPcmEncoding.Signed8, 53, 54, 48, 52)]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "3.0", 3,
        GbaGaxAudio.GaxPcmEncoding.Unsigned8, 52, 54, 61, 62)]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", "3.03A", 3,
        GbaGaxAudio.GaxPcmEncoding.Unsigned8, 55, 57, 24, 24)]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "3.05", 3,
        GbaGaxAudio.GaxPcmEncoding.Unsigned8, 55, 57, 26, 26)]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "3.05A", 3,
        GbaGaxAudio.GaxPcmEncoding.Unsigned8, 55, 57, 54, 59)]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "3.05", 3,
        GbaGaxAudio.GaxPcmEncoding.Unsigned8, 57, 57, 98, 100)]
    public void ExtractsCompleteBanksAcrossTheWholeGbaLine(
        string build,
        string versionToken,
        int expectedMajorVersion,
        GbaGaxAudio.GaxPcmEncoding expectedEncoding,
        int bank0Samples,
        int bank0Slots,
        int bank1Samples,
        int bank1Slots)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);

        Assert.True(GbaGaxAudio.IsGaxRom(rom));
        Assert.Contains(versionToken, GbaGaxAudio.GetVersionBanner(rom));
        Assert.Equal(expectedMajorVersion, GbaGaxAudio.GetEngineMajorVersion(rom));

        var banks = GbaGaxAudio.FindWaveSets(rom);
        Assert.Equal(2, banks.Count);
        Assert.Equal(
            [(bank0Samples, bank0Slots), (bank1Samples, bank1Slots)],
            banks.Select(bank => (bank.Samples.Count, bank.SlotCount)).ToArray());
        Assert.All(banks, bank => Assert.Equal(expectedEncoding, bank.Encoding));
        Assert.All(banks, bank => Assert.Equal(bank.Samples.Count, bank.Samples.Select(s => s.Index).Distinct().Count()));
        Assert.All(banks, bank => Assert.All(bank.Samples, sample =>
        {
            Assert.InRange(sample.Index, 1, bank.SlotCount);
            Assert.InRange(sample.Size, 1, rom.Length);
            Assert.InRange(sample.Address, 0x08000000u, 0x08000000u + (uint)rom.Length - 1);
        }));
    }
}
