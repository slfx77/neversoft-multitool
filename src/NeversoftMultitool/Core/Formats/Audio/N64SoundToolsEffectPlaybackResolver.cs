namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Resolves the statically knowable initial playback state of one Sound
///     Tools BFX effect against its PTR descriptor and a proven ROM mixer rate.
///     Dynamic handle frequency offsets, pitch bend, portamento, envelopes,
///     and subsequent bytecode are deliberately outside this result.
/// </summary>
public static class N64SoundToolsEffectPlaybackResolver
{
    public const string ResolutionBasis =
        "soundTools314InitialNotePtrDetuneAndAlAdpcmLoop";
    public const string NoStoredLoopMode = "none";
    public const string InfiniteStoredLoopMode = "infiniteUntilVoiceStop";
    public const string FiniteStoredLoopMode = "finiteRepeatCount";

    /// <summary>
    ///     Resolves an effect number. Sound Tools defines the first
    ///     <c>number_of_effects</c> component entries as directly startable
    ///     effects, so the effect and component indices are identical here.
    /// </summary>
    public static N64SoundToolsEffectPlayback Resolve(
        N64SoundToolsFxBank fxBank,
        N64SoundToolsPointerBank pointerBank,
        int effectIndex,
        uint mixerOutputRateHz)
    {
        ArgumentNullException.ThrowIfNull(fxBank);
        ArgumentNullException.ThrowIfNull(pointerBank);
        ArgumentOutOfRangeException.ThrowIfZero(mixerOutputRateHz);
        if ((uint)effectIndex >= (uint)fxBank.EffectCount)
        {
            throw new InvalidDataException(
                $"Sound Tools effect index {effectIndex} is outside 0..{fxBank.EffectCount - 1}");
        }

        var component = fxBank.Components[effectIndex];
        var binding = N64SoundToolsFxInitialWaveResolver.Resolve(
            fxBank, pointerBank, component) ?? throw new InvalidDataException(
            $"Sound Tools effect {effectIndex} has no proven initial-wave binding");
        var initialEvent = N64SoundToolsFxInitialEventResolver.Resolve(
            fxBank, pointerBank, component) ?? throw new InvalidDataException(
            $"Sound Tools effect {effectIndex} has no proven initial note event");
        var continuation = N64SoundToolsFxContinuationResolver.Resolve(
            fxBank, pointerBank, component) ?? throw new InvalidDataException(
            $"Sound Tools effect {effectIndex} has an unresolved continuation");
        if (initialEvent.NoteKind != N64SoundToolsFxInitialEventResolver.NoteKind)
        {
            throw new InvalidDataException(
                $"Sound Tools effect {effectIndex} begins with a rest, not a playable note");
        }

        var pointerWaveIndex = binding.PointerWaveIndex;
        if (pointerWaveIndex >= pointerBank.Waves.Count ||
            pointerWaveIndex >= pointerBank.BaseNotes.Count ||
            pointerWaveIndex >= pointerBank.FineTuneCells.Count)
        {
            throw new InvalidDataException(
                $"Sound Tools effect {effectIndex} resolved an incomplete PTR descriptor tuple");
        }

        var wave = pointerBank.Waves[pointerWaveIndex];
        var baseNote = pointerBank.BaseNotes[pointerWaveIndex];
        var fineTune = pointerBank.FineTuneCells[pointerWaveIndex];
        var staticPitchSemitones = CalculateStaticPitchSemitones(
            initialEvent.NoteValueRaw,
            baseNote.RuntimeBasePitchOffsetSemitones,
            fineTune.FineTuneCents);
        var calculatedPitchRatio = CalculatePitchRatio(staticPitchSemitones);
        var velocitySilencedByPitchLimit = calculatedPitchRatio > 2.0f;
        var runtimePitchRatio = velocitySilencedByPitchLimit ? 2.0f : calculatedPitchRatio;
        var effectiveStoredPcmRateHz = (double)mixerOutputRateHz * runtimePitchRatio;
        var nearestWavRateHz = checked((int)Math.Floor(effectiveStoredPcmRateHz + 0.5d));
        if (nearestWavRateHz <= 0)
            throw new InvalidDataException("Sound Tools static pitch resolved a nonpositive WAV rate");

        var loopMode = wave.Loop switch
        {
            null => NoStoredLoopMode,
            { CountRaw: uint.MaxValue } => InfiniteStoredLoopMode,
            _ => FiniteStoredLoopMode
        };

        return new N64SoundToolsEffectPlayback(
            effectIndex,
            component.Index,
            ResolutionBasis,
            mixerOutputRateHz,
            binding.LocalWaveIndex,
            pointerWaveIndex,
            wave,
            initialEvent,
            continuation,
            baseNote.Raw,
            baseNote.RuntimeBasePitchOffsetSemitones,
            fineTune.FineTuneCents,
            staticPitchSemitones,
            calculatedPitchRatio,
            runtimePitchRatio,
            velocitySilencedByPitchLimit,
            effectiveStoredPcmRateHz,
            nearestWavRateHz,
            nearestWavRateHz - effectiveStoredPcmRateHz,
            loopMode);
    }

    /// <summary>
    ///     Mirrors <c>__MusIntRemapPtrBank</c> followed by the initial-note
    ///     assignment in Nintendo 64 Sound Tools 3.14. The intermediate
    ///     stores are intentionally single precision.
    /// </summary>
    public static float CalculateStaticPitchSemitones(
        byte noteValueRaw,
        sbyte basePitchOffsetSemitones,
        sbyte fineTuneCents)
    {
        var fineSemitones = (float)((double)(float)fineTuneCents / 100.0d);
        var detune = (float)(fineSemitones + (float)basePitchOffsetSemitones);
        return (float)((float)noteValueRaw + detune);
    }

    /// <summary>
    ///     Mirrors Sound Tools 3.14 <c>__MusIntPowerOf2</c>, including its
    ///     float monomial intermediates, double constants/summation, and final
    ///     float conversion. This is intentionally not <see cref="Math.Pow(double,double)"/>.
    /// </summary>
    public static float CalculatePitchRatio(float semitones)
    {
        var x = (float)((double)semitones * (1.0d / 12.0d));
        if (x == 0.0f)
            return 1.0f;

        var negative = x < 0.0f;
        if (negative)
            x = -x;

        var x2 = (float)(x * x);
        var x3 = (float)(x2 * x);
        var x4 = (float)(x2 * x2);
        var x5 = (float)(x4 * x);
        var x6 = (float)(x4 * x2);
        var approximation =
            1.0d +
            (double)x * 0.693147180559945d +
            (double)x2 * 0.240226506959101d +
            (double)x3 * 5.55041086648216E-02d +
            (double)x4 * 9.61812910762848E-03d +
            (double)x5 * 1.33335581464284E-03d +
            (double)x6 * 1.54035303933816E-04d;
        return (float)(negative ? 1.0d / approximation : approximation);
    }
}

public sealed record N64SoundToolsEffectPlayback(
    int EffectIndex,
    int ComponentIndex,
    string Basis,
    uint MixerOutputRateHz,
    int LocalWaveIndex,
    ushort PointerWaveIndex,
    N64SoundToolsWaveDescriptor PointerWave,
    N64SoundToolsFxInitialEvent InitialEvent,
    N64SoundToolsFxContinuation Continuation,
    byte PointerBaseNoteRaw,
    sbyte PointerBasePitchOffsetSemitones,
    sbyte PointerFineTuneCents,
    float StaticPitchSemitones,
    float CalculatedPitchRatio,
    float RuntimePitchRatio,
    bool VelocitySilencedByPitchLimit,
    double EffectiveStoredPcmRateHz,
    int NearestWavRateHz,
    double WavRateRepresentationErrorHz,
    string StoredLoopMode);
