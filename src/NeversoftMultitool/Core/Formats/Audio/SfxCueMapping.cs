namespace NeversoftMultitool.Core.Formats.Audio;

internal sealed record SfxCueMapping(
    int CueIndex,
    SfxCue? Cue,
    SfxBankSample BankSample,
    string BankFormat,
    int? SampleRateOverride = null)
{
    /// <summary>Cue-note-adjusted rate from the VAB tone walk, else the bank's estimate.</summary>
    public int EffectiveSampleRate => SampleRateOverride ?? BankSample.SampleRate;

    public SfxExtractor.SfxSampleInfo ToSampleInfo()
    {
        return new SfxExtractor.SfxSampleInfo(
            CueIndex,
            BankSample.ExternalIndex,
            BankSample.DataSize,
            EffectiveSampleRate,
            BankSample.Channels,
            BankSample.Encoding,
            BankFormat,
            Cue?.Alias ?? -1,
            Cue?.Loop ?? false);
    }
}
