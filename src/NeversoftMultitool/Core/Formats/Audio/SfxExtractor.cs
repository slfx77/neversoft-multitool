namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts SFX cue tables by resolving them against companion KAT/VAB soundbanks.
///     The on-disk format (decomp-verified against the THPS2 PSX prototype's SFX_ParseSFXFile /
///     playSFX, 2026-07-09) is a flat array of 16-byte cue records terminated by a
///     0xFFFFFFFF word: marker(u8, 0xFE = loop), VAB program(u8), category(u8, selects tone
///     1&lt;&lt;category), MIDI note(u8), pitch(u16), volume(u16), alias(u16 — the cue's lookup key).
///     VAB companions resolve exactly via the program tone table (tone +0x16 = VAG index);
///     Dreamcast/PC KAT companions use the Spider-Man-style direct program→sample rule, and
///     THPS2 DC banks (whose tone tables shipped only in the PSX VAB) fall back to full
///     companion-bank extraction so the asset still converts.
/// </summary>
public static partial class SfxExtractor
{
    private const int EntrySize = 16;
    private const uint CueTerminator = 0xFFFFFFFF;
    private const byte LoopMarker = 0xFE;
    private const int AliasScoreThreshold = 24;
    private const int AliasMarginThreshold = 8;

    public static List<SfxSampleInfo> EnumerateSamples(string inputPath)
    {
        return TryResolvePlan(inputPath, out var plan, out _)
            ? plan.Mappings.Select(static mapping => mapping.ToSampleInfo()).ToList()
            : [];
    }

    /// <summary>
    ///     In-memory variant: caller supplies SFX bytes plus optional companion KAT/VAB bytes.
    ///     Cross-sibling alias fallback is skipped (only the explicit companion is tried).
    /// </summary>
    public static List<SfxSampleInfo> EnumerateSamples(byte[] sfxData, SfxBankBytes? bankBytes)
    {
        return TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out _)
            ? plan.Mappings.Select(static mapping => mapping.ToSampleInfo()).ToList()
            : [];
    }

    public static string? ExtractSingleToWav(string inputPath, int cueIndex, string outputDir)
    {
        if (!TryResolvePlan(inputPath, out var plan, out _))
            return null;

        var mapping = plan.Mappings.FirstOrDefault(candidate => candidate.CueIndex == cueIndex);
        if (mapping == null)
            return null;

        return ExtractBankSampleToWav(
            plan.BankSource,
            mapping.BankSample.ExternalIndex,
            outputDir,
            mapping.EffectiveSampleRate);
    }

    /// <summary>In-memory variant of <see cref="ExtractSingleToWav(string, int, string)" />.</summary>
    public static string? ExtractSingleToWav(
        byte[] sfxData, string stem, int cueIndex, SfxBankBytes? bankBytes, string outputDir)
    {
        if (!TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out _))
            return null;

        var mapping = plan.Mappings.FirstOrDefault(candidate => candidate.CueIndex == cueIndex);
        if (mapping == null) return null;

        return ExtractBankSampleToWav(
            plan.BankSource,
            mapping.BankSample.ExternalIndex,
            outputDir,
            mapping.EffectiveSampleRate,
            stem);
    }

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir)
    {
        if (!TryResolvePlan(inputPath, out var plan, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return ExtractToWavCore(plan, stem, outputDir);
    }

    /// <summary>In-memory variant of <see cref="ExtractToWav(string, string)" />.</summary>
    public static AudioConvertResult ExtractToWav(
        byte[] sfxData, string stem, SfxBankBytes? bankBytes, string outputDir)
    {
        if (!TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        return ExtractToWavCore(plan, stem, outputDir);
    }

    private static AudioConvertResult ExtractToWavCore(SfxExtractionPlan plan, string stem, string outputDir)
    {
        var outDir = Path.Combine(outputDir, stem);
        var tempDir = Path.Combine(outDir, "__sfx_tmp");
        Directory.CreateDirectory(tempDir);

        try
        {
            var filesWritten = 0;

            foreach (var mapping in plan.Mappings)
            {
                var tempPath = ExtractBankSampleToWav(
                    plan.BankSource,
                    mapping.BankSample.ExternalIndex,
                    tempDir,
                    mapping.EffectiveSampleRate,
                    stem);
                if (tempPath == null || !File.Exists(tempPath))
                    continue;

                Directory.CreateDirectory(outDir);
                var finalPath = Path.Combine(outDir, $"{mapping.CueIndex:D3}.wav");
                File.Move(tempPath, finalPath, true);
                filesWritten++;
            }

            return new AudioConvertResult
            {
                Success = filesWritten > 0,
                SamplesWritten = filesWritten,
                ErrorMessage = filesWritten > 0 ? null : "No WAV files could be extracted from the resolved SFX bank"
            };
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    public static bool CanExtract(string inputPath, out string error)
    {
        return TryResolvePlan(inputPath, out _, out error);
    }

    public readonly record struct SfxBankBytes(byte[] Data, string Format); // Format = "KAT" | "VAB"

    public sealed record SfxSampleInfo(
        int CueIndex,
        int BankSampleIndex,
        int DataSize,
        int SampleRate,
        int Channels,
        string Encoding,
        string BankFormat,
        int Alias = -1,
        bool Loop = false)
    {
        public int KatSampleIndex => BankSampleIndex;
    }

    /// <summary>One 16-byte cue record from the .SFX table (fields per SFX_ParseSFXFile).</summary>
    private sealed record SfxCue(
        int CueIndex, bool Loop, int Program, int Category, int Note, int Pitch, int Volume, int Alias);

    private sealed record SfxBankSample(int ExternalIndex, int DataSize, int SampleRate, int Channels, string Encoding);

    /// <summary>
    ///     Where a resolved companion bank lives. <c>BankPath</c> is the real on-disk
    ///     path when the SFX was loaded from the filesystem; <c>BankData</c> is the
    ///     companion bytes when loaded from an archive. Exactly one is non-empty.
    /// </summary>
    private sealed record SfxBankSource(
        string BankPath,
        string BankFormat,
        IReadOnlyList<SfxBankSample> Samples,
        byte[]? BankData = null);

    private sealed record SfxCueMapping(
        int CueIndex, SfxCue? Cue, SfxBankSample BankSample, string BankFormat, int? SampleRateOverride = null)
    {
        /// <summary>Cue-note-adjusted rate from the VAB tone walk, else the bank's estimate.</summary>
        public int EffectiveSampleRate => SampleRateOverride ?? BankSample.SampleRate;

        public SfxSampleInfo ToSampleInfo()
        {
            return new SfxSampleInfo(
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

    private sealed record SfxExtractionPlan(SfxBankSource BankSource, IReadOnlyList<SfxCueMapping> Mappings);

    private sealed record SfxAliasCandidate(string SiblingPath, string BankPath, int Score);
}
