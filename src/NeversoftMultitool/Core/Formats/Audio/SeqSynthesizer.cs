namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Renders a <see cref="SeqFile" /> through its VAB bank to PCM — the
///     SsSeqPlay playback path in miniature: program-change selects a VAB
///     program, a note starts every tone whose note range admits it, pitch is
///     SsPitchFromNote (44100 × 2^((note − centre − shift/128)/12), the same
///     formula the SFX cue resolver uses), sustained instruments hold via the
///     sample's SPU loop points, and levels follow the SPU ADSR envelope
///     stepped per output sample from the tone's ADSR1/ADSR2 registers.
/// </summary>
/// <remarks>
///     Deliberate approximations, documented rather than hidden: rendering is
///     a single pass (loop-marker controllers are ignored, playback stops at
///     End-of-Track plus the release tail); the envelope implements the
///     documented shift/step stepper with the exponential attack knee at
///     0x6000 and level-proportional exponential decay/release, but not the
///     SPU's exact stepped quantisation; pitch bend spans ±2 semitones
///     (channel default — per-tone bend ranges are not consulted); resampling
///     is linear. Notes, timing, pitch, program selection, velocity/volume
///     scaling and pan are engine-faithful.
/// </remarks>
public static class SeqSynthesizer
{
    public const int OutputSampleRate = 44100;
    private const float MaxLevel = 32768f;
    private const float BendRangeSemitones = 2f;
    private const double ReleaseTailSeconds = 2.0;

    public static short[]? Render(SeqFile seq, VabProgramSet vab)
    {
        if (seq.Events.Count == 0)
            return null;

        // Tick → seconds through the tempo map (tempo events are absolute).
        var eventTimes = BuildEventTimes(seq);
        var totalSeconds = eventTimes[^1] + ReleaseTailSeconds;
        var totalFrames = checked((int)(totalSeconds * OutputSampleRate) + 1);
        var mix = new float[totalFrames * 2];

        var channels = new ChannelState[16];
        for (var i = 0; i < channels.Length; i++)
            channels[i] = ChannelState.Default;

        var voices = new List<Voice>();
        for (var i = 0; i < seq.Events.Count; i++)
        {
            var seqEvent = seq.Events[i];
            var frame = (int)(eventTimes[i] * OutputSampleRate);
            ref var channel = ref channels[seqEvent.Channel];

            switch (seqEvent.Type)
            {
                case SeqEventType.ProgramChange:
                    channel.Program = seqEvent.Data;
                    break;

                case SeqEventType.Control:
                    switch (seqEvent.Data)
                    {
                        case 7: channel.Volume = seqEvent.Value; break;
                        case 10: channel.Pan = seqEvent.Value; break;
                        case 11: channel.Expression = seqEvent.Value; break;
                    }

                    break;

                case SeqEventType.PitchBend:
                    channel.BendSemitones = (seqEvent.Value - 8192) / 8192f * BendRangeSemitones;
                    break;

                case SeqEventType.NoteOn:
                    StartVoices(
                        voices, vab, seqEvent.Channel, channel, seqEvent.Data,
                        seqEvent.Value, frame);
                    break;

                case SeqEventType.NoteOff:
                    foreach (var voice in voices)
                    {
                        if (voice.Channel == seqEvent.Channel
                            && voice.Note == seqEvent.Data
                            && voice.ReleaseFrame < 0)
                        {
                            voice.ReleaseFrame = frame;
                        }
                    }

                    break;

                case SeqEventType.EndOfTrack:
                    // Anything still held releases at end of track.
                    foreach (var voice in voices)
                    {
                        if (voice.ReleaseFrame < 0)
                            voice.ReleaseFrame = frame;
                    }

                    break;
            }
        }

        foreach (var voice in voices)
            voice.RenderInto(mix, totalFrames);

        return Normalize(mix, totalFrames);
    }

    private static void StartVoices(
        List<Voice> voices,
        VabProgramSet vab,
        byte channelIndex,
        in ChannelState channel,
        byte note,
        int velocity,
        int frame)
    {
        if (channel.Program >= vab.Programs.Count)
            return;

        var program = vab.Programs[channel.Program];
        foreach (var tone in program.Tones)
        {
            if (note < tone.MinNote || note > tone.MaxNote)
                continue;
            var pcm = vab.GetPcm(tone.VagIndex);
            if (pcm == null)
                continue;

            // SsPitchFromNote, as pinned by SfxCueResolver.EstimateCueSampleRate.
            var semitones = note - tone.Centre - tone.Shift / 128f + channel.BendSemitones;
            var step = MathF.Pow(2f, semitones / 12f);

            var gain = velocity / 127f
                       * (tone.Volume / 127f)
                       * (program.MasterVolume / 127f)
                       * (channel.Volume / 127f)
                       * (channel.Expression / 127f);

            // Equal-power pan from the tone pan biased by the channel pan
            // (both 0..127, 64 = centre).
            var pan = Math.Clamp((tone.Pan - 64) + (channel.Pan - 64) + 64, 0, 127) / 127f;
            var panAngle = pan * MathF.PI / 2f;

            voices.Add(new Voice
            {
                Channel = channelIndex,
                Note = note,
                Pcm = pcm,
                Step = step,
                StartFrame = frame,
                GainLeft = gain * MathF.Cos(panAngle),
                GainRight = gain * MathF.Sin(panAngle),
                Envelope = new SpuEnvelope(tone.Adsr1, tone.Adsr2)
            });
        }
    }

    private static double[] BuildEventTimes(SeqFile seq)
    {
        var times = new double[seq.Events.Count];
        var secondsPerTick = seq.InitialTempoMicroseconds / 1_000_000.0 / seq.Resolution;
        long lastTick = 0;
        var lastTime = 0.0;

        for (var i = 0; i < seq.Events.Count; i++)
        {
            var seqEvent = seq.Events[i];
            lastTime += (seqEvent.Tick - lastTick) * secondsPerTick;
            lastTick = seqEvent.Tick;
            times[i] = lastTime;
            if (seqEvent.Type == SeqEventType.Tempo && seqEvent.Value > 0)
                secondsPerTick = seqEvent.Value / 1_000_000.0 / seq.Resolution;
        }

        return times;
    }

    private static short[] Normalize(float[] mix, int totalFrames)
    {
        // Headroom scaling: only rescale when the mix clips, so quiet songs
        // keep their authored dynamics.
        var peak = 0f;
        for (var i = 0; i < totalFrames * 2; i++)
            peak = MathF.Max(peak, MathF.Abs(mix[i]));

        var scale = peak > MaxLevel - 1 ? (MaxLevel - 1) / peak : 1f;
        var output = new short[totalFrames * 2];
        for (var i = 0; i < output.Length; i++)
            output[i] = (short)Math.Clamp(mix[i] * scale, short.MinValue, short.MaxValue);

        return output;
    }

    private sealed class Voice
    {
        public required byte Channel { get; init; }
        public required byte Note { get; init; }
        public required VabPcmSample Pcm { get; init; }
        public required float Step { get; init; }
        public required int StartFrame { get; init; }
        public required float GainLeft { get; init; }
        public required float GainRight { get; init; }
        public required SpuEnvelope Envelope { get; init; }
        public int ReleaseFrame { get; set; } = -1;

        public void RenderInto(float[] mix, int totalFrames)
        {
            var samples = Pcm.Samples;
            var position = 0.0;
            var envelope = Envelope;

            for (var frame = StartFrame; frame < totalFrames; frame++)
            {
                if (ReleaseFrame >= 0 && frame == ReleaseFrame)
                    envelope.Release();

                var level = envelope.Advance();
                if (level <= 0f && envelope.Finished)
                    return;

                var index = (int)position;
                if (index >= samples.Length - 1)
                {
                    if (Pcm.Loops && Pcm.LoopEnd > Pcm.LoopStart)
                    {
                        position -= Pcm.LoopEnd - Pcm.LoopStart;
                        index = (int)position;
                        if (index >= samples.Length - 1)
                            return; // Degenerate loop shorter than one step.
                    }
                    else
                    {
                        return; // One-shot sample exhausted.
                    }
                }

                var frac = (float)(position - index);
                var sample = samples[index] + (samples[index + 1] - samples[index]) * frac;
                var value = sample * level;
                mix[frame * 2] += value * GainLeft;
                mix[frame * 2 + 1] += value * GainRight;
                position += Step;
            }
        }
    }

    private struct ChannelState
    {
        public int Program;
        public int Volume;
        public int Pan;
        public int Expression;
        public float BendSemitones;

        public static ChannelState Default => new()
        {
            Program = 0,
            Volume = 127,
            Pan = 64,
            Expression = 127,
            BendSemitones = 0f
        };
    }
}

/// <summary>
///     The SPU ADSR envelope stepped at the output rate, from the two ADSR
///     register words. Field decode per the established SPU documentation:
///     ADSR1 = attack mode(15) | attack shift(14-10) | attack step(9-8) |
///     decay shift(7-4) | sustain level(3-0); ADSR2 = sustain mode(15) |
///     sustain direction(14) | sustain shift(12-8) | sustain step(7-6) |
///     release mode(5) | release shift(4-0).
/// </summary>
internal struct SpuEnvelope(ushort adsr1, ushort adsr2)
{
    private enum Phase
    {
        Attack,
        Decay,
        Sustain,
        Release,
        Done
    }

    private Phase _phase = Phase.Attack;
    private float _level;

    public readonly bool Finished => _phase == Phase.Done;

    public void Release()
    {
        if (_phase != Phase.Done)
            _phase = Phase.Release;
    }

    /// <summary>Advance one output sample; returns the level in 0..1.</summary>
    public float Advance()
    {
        switch (_phase)
        {
            case Phase.Attack:
            {
                var shift = (adsr1 >> 10) & 0x1F;
                var stepValue = 7 - ((adsr1 >> 8) & 3);
                var rate = RatePerSample(shift, stepValue);
                if ((adsr1 & 0x8000) != 0 && _level > 0x6000 / 32768f)
                    rate *= 0.25f; // Exponential attack knee above 0x6000.
                _level += rate;
                if (_level >= 1f)
                {
                    _level = 1f;
                    _phase = Phase.Decay;
                }

                break;
            }

            case Phase.Decay:
            {
                var shift = (adsr1 >> 4) & 0x0F;
                var sustain = ((adsr1 & 0x0F) + 1) * 0x800 / 32768f;
                _level -= RatePerSample(shift, 8) * MathF.Max(_level, 0.001f);
                if (_level <= sustain)
                {
                    _level = sustain;
                    _phase = Phase.Sustain;
                }

                break;
            }

            case Phase.Sustain:
            {
                var shift = (adsr2 >> 8) & 0x1F;
                var stepValue = 7 - ((adsr2 >> 6) & 3);
                var rate = RatePerSample(shift, stepValue);
                if ((adsr2 & 0x4000) != 0)
                {
                    // Decreasing sustain; exponential mode scales by level.
                    if ((adsr2 & 0x8000) != 0)
                        rate *= MathF.Max(_level, 0.001f);
                    _level -= rate;
                    if (_level <= 0f)
                    {
                        _level = 0f;
                        _phase = Phase.Done;
                    }
                }
                else
                {
                    _level = MathF.Min(1f, _level + rate);
                }

                break;
            }

            case Phase.Release:
            {
                var shift = adsr2 & 0x1F;
                var rate = RatePerSample(shift, 8);
                if ((adsr2 & 0x20) != 0)
                    rate *= MathF.Max(_level, 0.001f); // Exponential release.
                _level -= rate;
                if (_level <= 0f)
                {
                    _level = 0f;
                    _phase = Phase.Done;
                }

                break;
            }
        }

        return _level;
    }

    /// <summary>
    ///     Envelope change per 44100 Hz sample for a shift/step pair: the SPU
    ///     applies <c>step &lt;&lt; max(0, 11 − shift)</c> every
    ///     <c>1 &lt;&lt; max(0, shift − 11)</c> ticks out of 0x8000.
    /// </summary>
    private static float RatePerSample(int shift, int stepValue)
    {
        var step = stepValue << Math.Max(0, 11 - shift);
        var interval = 1 << Math.Max(0, shift - 11);
        return step / (float)interval / 32768f;
    }
}
