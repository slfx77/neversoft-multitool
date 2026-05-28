namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts Dreamcast SFX cue tables by resolving them against companion KAT/VAB soundbanks.
///     Spider-Man-style banks expose direct sample references in the SFX entries. When the cue
///     indirection cannot be decoded confidently, extraction falls back to the resolved companion
///     bank so the asset still converts instead of failing outright.
/// </summary>
public static partial class SfxExtractor
{
    private const int HeaderSize = 4;
    private const int EntrySize = 16;
    private const uint ExpectedHeaderSuffix = 0x0000003C;
    private const uint TerminatorPackedId = 0xFFFFFFFF;
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
            mapping.BankSample.SampleRate);
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
            mapping.BankSample.SampleRate,
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
                    mapping.BankSample.SampleRate,
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
        string BankFormat)
    {
        public int KatSampleIndex => BankSampleIndex;
    }

    private sealed record SfxEntry(uint Flags, uint CueValue, uint Unknown, uint PackedId)
    {
        public byte PackedFlags => (byte)(PackedId & 0xFF);
        public int PackedSampleNumber => (int)((PackedId >> 8) & 0xFF);
        public int PackedVariant => (int)((PackedId >> 16) & 0xFF);
        public byte PackedMarker => (byte)((PackedId >> 24) & 0xFF);
    }

    private sealed record SfxBankSample(int ExternalIndex, int DataSize, int SampleRate, int Channels, string Encoding);

    /// <summary>
    ///     Where a resolved companion bank lives. <c>BankPath</c> is the real on-disk
    ///     path when the SFX was loaded from the filesystem; <c>BankData</c> is the
    ///     companion bytes when loaded from an archive. Exactly one is non-empty.
    /// </summary>
    private sealed record SfxBankSource(
        string BankPath,
        string BankFormat,
        int IndexBase,
        IReadOnlyList<SfxBankSample> Samples,
        byte[]? BankData = null);

    private sealed record SfxCueMapping(int CueIndex, SfxEntry? Entry, SfxBankSample BankSample, string BankFormat)
    {
        public SfxSampleInfo ToSampleInfo()
        {
            return new SfxSampleInfo(
                CueIndex,
                BankSample.ExternalIndex,
                BankSample.DataSize,
                BankSample.SampleRate,
                BankSample.Channels,
                BankSample.Encoding,
                BankFormat);
        }
    }

    private sealed record SfxExtractionPlan(SfxBankSource BankSource, IReadOnlyList<SfxCueMapping> Mappings);

    private sealed record SfxAliasCandidate(string SiblingPath, string BankPath, int Score);
}
