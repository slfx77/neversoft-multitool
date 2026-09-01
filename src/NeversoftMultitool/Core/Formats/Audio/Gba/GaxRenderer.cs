namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Renders a GAX song (<see cref="GbaGaxMusic" />) to mono PCM16 with the
///     engine's <b>real timbre</b>: every note plays its instrument's PCM wave with
///     the driver's own envelopes, vibrato, portamento, loop modes and tempo,
///     executed by the frame-faithful <see cref="GaxSynth" />. The earlier tone-synth
///     placeholder (and its rows-per-second tempo policy) is gone — tempo now comes
///     from the song's own speed effects at the driver's 59.7275 fps tick.
///
///     <para>The one remaining GAX 1.x policy is the <b>requested mix rate</b>,
///     which is call-site state rather than song data: THPS2 plays its title song
///     at 18158 Hz and everything else at 15769 Hz. GAX 2/3 store that request in
///     each song header. The output is always the corresponding hardware rate.</para>
/// </summary>
public static class GaxRenderer
{
    /// <summary>The game's request for every non-title song (THPS2 call-site).</summary>
    public const int DefaultRateHz = 15769;

    /// <summary>The game's request for the boot/title song (address-order index 0).</summary>
    public const int TitleRateHz = 18158;

    public sealed record Options
    {
        /// <summary>Requested mix rate; the engine picks its first config entry ≥ this.</summary>
        public int RequestedRateHz { get; init; } = DefaultRateHz;

        /// <summary>Hard cap; a song normally ends itself (one order pass + ring-out).</summary>
        public double MaxSeconds { get; init; } = 360;
    }

    /// <summary>Renders one song to mono PCM16; <paramref name="sampleRate" /> is the true hardware rate.</summary>
    public static short[] RenderSong(
        byte[] rom, GbaGaxMusic.GaxSongHeader header, Options options, out int sampleRate)
    {
        var synth = new GaxSynth(rom, header, options.RequestedRateHz);
        sampleRate = synth.OutputRateHz;
        return synth.Render(options.MaxSeconds);
    }
}
