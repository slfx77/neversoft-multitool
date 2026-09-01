using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Audio.Gba;

namespace NeversoftMultitool.Tests.Core.Formats.Audio.Gba;

/// <summary>
///     Pins the GAX sequenced-music decoder + renderer against Tony Hawk's Pro
///     Skater 2 (GBA, GAX v1.99d): the 11 songs, the order-list stride (pats×4,
///     confirmed by disassembly of the engine's per-channel order pointer), and that
///     the decoded notes form coherent diatonic music that renders to real audio.
/// </summary>
public sealed class GbaGaxMusicTests(TestPaths paths)
{
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)";
    private const string Thps2Rom = "Tony Hawk's Pro Skater 2 (USA, Europe).gba";

    private byte[]? LoadThps2()
    {
        var path = paths.FindSampleFile(Thps2Build, Thps2Rom);
        return path == null ? null : File.ReadAllBytes(path);
    }

    private static (int Played, int DistinctPitchClasses, int TopNote) Analyze(
        byte[] rom, GbaGaxMusic.GaxSongHeader header)
    {
        var notes = new List<int>();
        for (var ch = 0; ch < header.ChannelCount; ch++)
        foreach (var e in GbaGaxMusic.DecodeChannel(rom, header, ch, out _))
            if (e.Note >= 2)
                notes.Add(e.Note);
        var distinctPc = notes.Select(n => (n - 2) % 12).Distinct().Count();
        var top = notes.GroupBy(n => n).OrderByDescending(g => g.Count()).First().Key;
        return (notes.Count, distinctPc, top);
    }

    [Fact]
    public void FindsElevenSongs_WithPinnedHeaders()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");

        var headers = GbaGaxMusic.FindSongHeaders(rom);
        Assert.Equal(11, headers.Count);

        // (address, channelCount, orderLength) per song, in address order.
        (uint Addr, int Ch, int Order)[] expected =
        [
            (0x087F2FE8, 6, 14), (0x087F383C, 5, 34), (0x087F3D8C, 5, 11), (0x087F435C, 5, 20),
            (0x087F4B04, 5, 16), (0x087F5258, 5, 15), (0x087F5860, 5, 18), (0x087F62C8, 5, 30),
            (0x087F6A34, 5, 15), (0x087F711C, 5, 21), (0x087F7A3C, 5, 22)
        ];
        Assert.Equal(
            expected,
            headers.Select(h => (h.Address, h.ChannelCount, h.OrderLength)).ToArray());
        Assert.All(headers, h => Assert.Equal(32, h.RowsPerPattern));
    }

    [Fact]
    public void DecodesFaithfulDiatonicMusic_UnderPatsTimes4Stride()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var headers = GbaGaxMusic.FindSongHeaders(rom);

        // Song 0 is a 6-channel piece whose lead is a sustained D4 (note 62). The
        // played-note count is the pats×4 decode; a wrong stride would change it.
        var song0 = Analyze(rom, headers[0]);
        Assert.Equal(615, song0.Played);
        Assert.Equal(3, song0.DistinctPitchClasses);
        Assert.Equal(62, song0.TopNote); // D4

        // Song 1 (34 patterns) is the sharpest stride regression guard: pats×4
        // decodes 1356 played notes; the old fixed-0x38 stride mis-read channels
        // 1-4 and gave 1198. Seven pitch classes = a diatonic scale.
        var song1 = Analyze(rom, headers[1]);
        Assert.Equal(1356, song1.Played);
        Assert.Equal(7, song1.DistinctPitchClasses);
    }

    [Fact]
    public void RendersRealTimbreAudio()
    {
        var rom = LoadThps2();
        Assert.SkipWhen(rom == null, "THPS2 GBA ROM sample not available");
        var headers = GbaGaxMusic.FindSongHeaders(rom);

        // The title song plays at the game's 18158 Hz call-site request; the output
        // rate is the true hardware rate from the ROM's own config (16777216/924).
        var options = new GaxRenderer.Options { RequestedRateHz = GaxRenderer.TitleRateHz };
        var pcm = GaxRenderer.RenderSong(rom, headers[0], options, out var sampleRate);

        Assert.Equal(18157, sampleRate);
        // Byte-exact against the validated reference implementation, which itself
        // matched 3,871 live mixer voice-frame events exactly and scored 0.927
        // log-spectrogram correlation against the emulator's real audio output.
        Assert.Equal(563312, pcm.Length);
        var bytes = new byte[pcm.Length * 2];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
        Assert.Equal(
            "1e524cab4e0580db6ab3f3ca97193dccc2268b635a104e1c9472c2fc580f28e5",
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    // The common pattern grammar survives all three engine generations. These
    // exact counts and first-song note totals pin each generation's distinct
    // header/order indirection without relying on game-specific addresses.
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)", 11,
        GbaGaxMusic.GaxSongLayout.Version1, 615)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)", 14,
        GbaGaxMusic.GaxSongLayout.Version2, 621)]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)", 10,
        GbaGaxMusic.GaxSongLayout.Version3, 1034)]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)", 7,
        GbaGaxMusic.GaxSongLayout.Version3, 1039)]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)", 6,
        GbaGaxMusic.GaxSongLayout.Version3, 17)]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)", 9,
        GbaGaxMusic.GaxSongLayout.Version3, 1488)]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)", 11,
        GbaGaxMusic.GaxSongLayout.Version3, 487)]
    public void DecodesSongsAcrossTheGbaLine(
        string build,
        int expectedSongCount,
        GbaGaxMusic.GaxSongLayout expectedLayout,
        int expectedFirstSongNotes)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);
        var headers = GbaGaxMusic.FindSongHeaders(rom);

        Assert.Equal(expectedSongCount, headers.Count);
        Assert.All(headers, header =>
        {
            Assert.Equal(expectedLayout, header.Layout);
            Assert.Equal(header.ChannelCount, header.ChannelOrderAddresses.Count);
            Assert.True(header.SampleAddress >= 0x08000000);
            if (expectedLayout == GbaGaxMusic.GaxSongLayout.Version1)
                Assert.Equal(0, header.MixingRate);
            else
                Assert.InRange(header.MixingRate, 5735, 42049);
        });
        Assert.Equal(expectedFirstSongNotes, Analyze(rom, headers[0]).Played);
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-6-11, GBA - Final)")]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-3-15, GBA - Final)")]
    [InlineData("Tony Hawk's Pro Skater 4 (2002-10-23, GBA - Final)")]
    [InlineData("Tony Hawk's Underground (2003-10-27, GBA - Final)")]
    [InlineData("Tony Hawk's Underground 2 (2004-10-4, GBA - Final)")]
    [InlineData("Tony Hawk's American Sk8land (2005-10-18, GBA - Final)")]
    [InlineData("Tony Hawk's Downhill Jam (2006-11-7, GBA - Final)")]
    public void FirstSongRendererMakesProgressAcrossTheGbaLine(string build)
    {
        var path = paths.FindSampleFiles(build, "*.gba").FirstOrDefault();
        Assert.SkipWhen(path == null, $"{build} ROM sample not available");
        var rom = File.ReadAllBytes(path!);
        var header = Assert.Single(GbaGaxMusic.FindSongHeaders(rom).Take(1));
        var requestedRate = header.MixingRate > 0 ? header.MixingRate : GaxRenderer.TitleRateHz;

        var pcm = GaxRenderer.RenderSong(
            rom,
            header,
            new GaxRenderer.Options { RequestedRateHz = requestedRate, MaxSeconds = 1 },
            out var sampleRate);

        Assert.InRange(sampleRate, 5735, 42049);
        Assert.InRange(pcm.Length, 1, sampleRate);
        Assert.Contains(pcm, sample => sample != 0);
    }
}
