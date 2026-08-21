namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Renders a decoded GAX song (<see cref="GbaGaxMusic" />) to interleaved
///     stereo 16-bit PCM. The <b>note sequence is faithful</b> — pitches, rhythm and
///     song order are exactly what the ROM plays — but two things are deliberate
///     approximations, because the engine does not carry them where a decoder can
///     read them:
///     <list type="bullet">
///         <item>
///             <b>Tempo.</b> v1.99 has no per-song tempo field; the row rate is the
///             engine's global tick rate divided by the current speed effect. The
///             default here (10 rows/s ≈ 60 Hz ÷ speed 6) is a policy, tunable via
///             <see cref="Options.RowsPerSecond" />, not a value read from the ROM.
///         </item>
///         <item>
///             <b>Timbre.</b> Each note is synthesised as a mellow tone rather than
///             played through its instrument's PCM sample, because the per-instrument
///             sample binding (an audio-rate DMA mixer, not the note interpreter) is
///             not yet reverse-engineered. The result is a chiptune rendition of the
///             real melody/harmony, not the game's exact sound.
///         </item>
///     </list>
///     Pitch mapping is validated: song 0's decoded D drone renders at 293.66 Hz (D4).
/// </summary>
public static class GaxRenderer
{
    public sealed record Options
    {
        public int SampleRate { get; init; } = 22050;

        /// <summary>Pattern rows per second (tempo policy; see the class remarks).</summary>
        public double RowsPerSecond { get; init; } = 10.0;

        /// <summary>Fraction of a row a note sounds before release.</summary>
        public double Gate { get; init; } = 0.9;

        /// <summary>Global semitone transpose applied on top of per-pattern transpose.</summary>
        public int Transpose { get; init; }

        public double Volume { get; init; } = 0.8;
    }

    /// <summary>Renders one song to interleaved stereo PCM16 ([L,R,L,R,…]).</summary>
    public static short[] RenderSong(
        ReadOnlySpan<byte> rom, GbaGaxMusic.GaxSongHeader header, Options options, out int sampleRate)
    {
        sampleRate = options.SampleRate;

        var channels = new List<GbaGaxMusic.GaxNoteEvent>[header.ChannelCount];
        var totalRows = 0;
        for (var ch = 0; ch < header.ChannelCount; ch++)
        {
            channels[ch] = GbaGaxMusic.DecodeChannel(rom, header, ch, out var rows);
            totalRows = Math.Max(totalRows, rows);
        }

        var rowDur = 1.0 / options.RowsPerSecond;
        var frames = (int)((totalRows * rowDur + 1.0) * sampleRate);
        if (frames <= 0)
            return [];

        var left = new float[frames];
        var right = new float[frames];
        var noteFrames = (int)(rowDur * options.Gate * sampleRate);

        var attack = Math.Min((int)(0.004 * sampleRate), noteFrames / 4 + 1);
        var release = Math.Min((int)(0.03 * sampleRate), noteFrames / 2 + 1);

        for (var ch = 0; ch < channels.Length; ch++)
        {
            // A gentle static spread so the channels are distinguishable.
            var pan = 0.5 + 0.35 * Math.Sin(ch);
            var gainLeft = options.Volume * (1.0 - pan);
            var gainRight = options.Volume * pan;
            foreach (var e in channels[ch])
            {
                if (e.Note < 2) // 0 = continue, 1 = note-off
                    continue;
                var start = (int)(e.Row * rowDur * sampleRate);
                var len = Math.Min(noteFrames, frames - start);
                if (start < 0 || len <= 0)
                    continue;
                // The engine's note-on maps the raw note directly (validated: note 62
                // -> D4, 293.66 Hz). The order-list transpose byte is decoded but NOT
                // applied — disassembly of THPS2 v1.99's note-on (0x080364CC) shows it
                // uses only (note-2)*32 and never reads order[idx]+2. Only the user's
                // global transpose is applied here.
                var freq = 440.0 * Math.Pow(2.0, (e.Note + options.Transpose - 69) / 12.0);
                var voice = new Voice(2.0 * Math.PI * freq / sampleRate, gainLeft, gainRight, attack, release);
                RenderTone(left, right, start, len, voice);
            }
        }

        return Interleave(left, right);
    }

    private readonly record struct Voice(double AngularFreq, double GainLeft, double GainRight, int Attack, int Release);

    private static void RenderTone(float[] left, float[] right, int start, int len, Voice voice)
    {
        for (var i = 0; i < len; i++)
        {
            var phase = voice.AngularFreq * i;
            // fundamental + a little square/octave character
            var s = 0.6 * Math.Sin(phase)
                    + 0.2 * Math.Sign(Math.Sin(phase))
                    + 0.15 * Math.Sin(2.0 * phase);
            var env = 1.0;
            if (i < voice.Attack)
                env = (double)i / voice.Attack;
            else if (i >= len - voice.Release)
                env = (double)(len - i) / voice.Release;
            var v = s * 0.5 * env;
            left[start + i] += (float)(v * voice.GainLeft);
            right[start + i] += (float)(v * voice.GainRight);
        }
    }

    private static short[] Interleave(float[] left, float[] right)
    {
        var peak = 1e-9f;
        for (var i = 0; i < left.Length; i++)
        {
            peak = Math.Max(peak, Math.Abs(left[i]));
            peak = Math.Max(peak, Math.Abs(right[i]));
        }

        var scale = peak > 0.95f ? 0.95f / peak : 1.0f;
        var stereo = new short[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            stereo[i * 2] = ToPcm16(left[i] * scale);
            stereo[i * 2 + 1] = ToPcm16(right[i] * scale);
        }

        return stereo;
    }

    private static short ToPcm16(float v) => (short)(Math.Clamp(v, -1.0f, 1.0f) * 32767);
}
