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
public static class SfxExtractor
{
    internal const int EntrySize = 16;
    internal const uint CueTerminator = 0xFFFFFFFF;
    internal const byte LoopMarker = 0xFE;
    internal const int AliasScoreThreshold = 24;
    internal const int AliasMarginThreshold = 8;

    public static List<SfxSampleInfo> EnumerateSamples(string inputPath)
    {
        return SfxPathResolver.TryResolvePlan(inputPath, out var plan, out _)
            ? plan.Mappings.Select(static mapping => mapping.ToSampleInfo()).ToList()
            : [];
    }

    /// <summary>
    ///     In-memory variant: caller supplies SFX bytes plus optional companion KAT/VAB bytes.
    ///     Cross-sibling alias fallback is skipped (only the explicit companion is tried).
    /// </summary>
    public static List<SfxSampleInfo> EnumerateSamples(byte[] sfxData, SfxBankBytes? bankBytes)
    {
        return SfxByteBankResolver.TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out _)
            ? plan.Mappings.Select(static mapping => mapping.ToSampleInfo()).ToList()
            : [];
    }

    public static string? ExtractSingleToWav(string inputPath, int cueIndex, string outputDir)
    {
        if (!SfxPathResolver.TryResolvePlan(inputPath, out var plan, out _))
            return null;

        var mapping = plan.Mappings.FirstOrDefault(candidate => candidate.CueIndex == cueIndex);
        if (mapping == null)
            return null;

        return SfxPathResolver.ExtractBankSampleToWav(
            plan.BankSource,
            mapping.BankSample.ExternalIndex,
            outputDir,
            mapping.EffectiveSampleRate);
    }

    /// <summary>In-memory variant of <see cref="ExtractSingleToWav(string, int, string)" />.</summary>
    public static string? ExtractSingleToWav(
        byte[] sfxData, string stem, int cueIndex, SfxBankBytes? bankBytes, string outputDir)
    {
        if (!SfxByteBankResolver.TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out _))
            return null;

        var mapping = plan.Mappings.FirstOrDefault(candidate => candidate.CueIndex == cueIndex);
        if (mapping == null) return null;

        return SfxPathResolver.ExtractBankSampleToWav(
            plan.BankSource,
            mapping.BankSample.ExternalIndex,
            outputDir,
            mapping.EffectiveSampleRate,
            stem);
    }

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir)
    {
        if (!SfxPathResolver.TryResolvePlan(inputPath, out var plan, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        var stem = Path.GetFileNameWithoutExtension(inputPath);
        return ExtractToWavCore(plan, stem, outputDir);
    }

    /// <summary>In-memory variant of <see cref="ExtractToWav(string, string)" />.</summary>
    public static AudioConvertResult ExtractToWav(
        byte[] sfxData, string stem, SfxBankBytes? bankBytes, string outputDir)
    {
        if (!SfxByteBankResolver.TryResolvePlanFromBytes(sfxData, bankBytes, out var plan, out var error))
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
                var tempPath = SfxPathResolver.ExtractBankSampleToWav(
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
        return SfxPathResolver.TryResolvePlan(inputPath, out _, out error);
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
}
