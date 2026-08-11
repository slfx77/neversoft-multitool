using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     Pins the two SFX cue record layouts (2026-08-07). Both are the
///     decomp-verified <c>SFX_ParseSFXFile</c> grammar; the N64 port re-encoded
///     it big-endian AND widened the alias field from u16 to u32, which is why
///     the layout is declared rather than derived from a byte order alone.
///     <para>
///         The widening is measured, not assumed: across all 55 carved
///         Spider-Man banks (1,929 records) bytes +8 and +9 are zero in every
///         record while +11 is populated in every one and +10 in a quarter of
///         them — a u32 carrying u16-range values. Bytes +12..15 are zero
///         throughout, so the pad is four bytes where the PS1 has six.
///     </para>
///     <para>
///         Parsing the cue TABLE is all this buys today: ABI1 stored-wave
///         decoding exists, but cue-to-BFX/PTR ownership and authoritative
///         rate, pitch, and loop scheduling remain unresolved.
///     </para>
/// </summary>
public sealed class SfxCueLayoutTests(TestPaths paths)
{
    private const string SpiderManN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string RomName = "Spider-Man (USA).z64";

    private List<byte[]> CarveCueBanks()
    {
        var romPath = paths.FindSampleFile(SpiderManN64Build, RomName);
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return assets
            .Where(static asset => asset.Path.EndsWith(".sfx.n64", StringComparison.Ordinal))
            .Select(static asset => asset.Data)
            .ToList();
    }

    /// <summary>
    ///     Every carved bank parses under the N64 layout, and the fields land in
    ///     ranges the grammar allows — MIDI notes are 7-bit, and a pitch read
    ///     with the wrong byte order lands far outside the engine's range.
    /// </summary>
    [CorpusFact]
    public void EveryCarvedCueBank_ParsesUnderTheN64Layout()
    {
        var banks = CarveCueBanks();
        Assert.NotEmpty(banks);

        var records = 0;
        foreach (var data in banks)
        {
            Assert.True(
                SfxCueResolver.TryParseCues(data, SfxCueLayout.N64, out var cues, out var error),
                $"carved cue bank failed to parse: {error}");
            Assert.NotEmpty(cues);

            foreach (var cue in cues)
            {
                records++;
                Assert.InRange(cue.Note, 0, 0x7F);
                Assert.InRange(cue.Program, 0, 0xFF);
                // Byte-swapped pitches land in the tens of thousands; real ones
                // cluster on 0x1000 (unity) and its neighbours.
                Assert.InRange(cue.Pitch, 1, 0x8000);
            }
        }

        Assert.True(records > 1000, $"expected the full cue corpus, got {records} records");
    }

    /// <summary>
    ///     The alias really is 32 bits wide. Read as the PS1's u16 at the same
    ///     offset it would come back ZERO for every record, because the value
    ///     lives in the high half — which is exactly the failure mode a
    ///     byte-order-only model would ship.
    /// </summary>
    [CorpusFact]
    public void N64Alias_IsWiderThanThePs1Field()
    {
        var banks = CarveCueBanks();
        var narrow = SfxCueLayout.N64 with { AliasWidth = 2 };

        var nonZeroWide = 0;
        var nonZeroNarrow = 0;
        foreach (var data in banks)
        {
            if (!SfxCueResolver.TryParseCues(data, SfxCueLayout.N64, out var wide, out _))
                continue;
            nonZeroWide += wide.Count(static cue => cue.Alias != 0);

            if (SfxCueResolver.TryParseCues(data, narrow, out var narrowCues, out _))
                nonZeroNarrow += narrowCues.Count(static cue => cue.Alias != 0);
        }

        Assert.True(nonZeroWide > 1000, $"expected populated aliases, got {nonZeroWide}");
        Assert.Equal(0, nonZeroNarrow);
    }

    /// <summary>
    ///     The PS1 layout is unchanged — the little-endian overload is still the
    ///     default and still reads the u16 alias at +8.
    /// </summary>
    [Fact]
    public void Ps1Layout_IsUnchanged()
    {
        Assert.False(SfxCueLayout.LittleEndian.BigEndian);
        Assert.Equal(8, SfxCueLayout.LittleEndian.AliasOffset);
        Assert.Equal(2, SfxCueLayout.LittleEndian.AliasWidth);
        Assert.Equal(10, SfxCueLayout.LittleEndian.PadOffset);

        Assert.True(SfxCueLayout.N64.BigEndian);
        Assert.Equal(4, SfxCueLayout.N64.AliasWidth);
        Assert.Equal(12, SfxCueLayout.N64.PadOffset);
    }
}
