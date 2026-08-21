using NeversoftMultitool.Core.Formats.Audio.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Audio.Gba;

/// <summary>
///     Pins the GAX sample extractor against Tony Hawk's Pro Skater 2 (GBA,
///     GAX v1.99d): the engine fingerprint, the self-validating wave-set anchor
///     (101 contiguous samples), and that the samples decode to real PCM.
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

    // The GAX version evolves across the whole GBA line (v1.99 → 3.05A) and the
    // fingerprint token that follows "GAX Sound Engine " gains/loses a leading
    // 'v'. The wave-set contiguity anchor is version-agnostic, so all seven
    // detect and extract; the sample counts are pinned per build.
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", "v1.99d", 101)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", "2.11", 53)]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", "3.0", 52)]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", "3.03A", 55)]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", "3.05", 55)]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", "3.05A", 55)]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", "3.05", 39)]
    public void ExtractsAcrossTheWholeGbaLine(string build, string versionToken, int expectedSamples)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);

        Assert.True(GbaGaxAudio.IsGaxRom(rom));
        Assert.Contains(versionToken, GbaGaxAudio.GetVersionBanner(rom));
        Assert.True(GbaGaxAudio.TryFindWaveSet(rom, out _, out var samples));
        Assert.Equal(expectedSamples, samples.Count);
        for (var i = 0; i < samples.Count - 1; i++)
            Assert.Equal(samples[i].Address + (uint)samples[i].Size, samples[i + 1].Address);
    }
}
