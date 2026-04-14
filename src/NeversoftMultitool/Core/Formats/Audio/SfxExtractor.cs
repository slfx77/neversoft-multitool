namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts Dreamcast SFX cue tables by resolving them against companion KAT/VAB soundbanks.
///     Spider-Man-style banks expose direct sample references in the SFX entries. When the cue
///     indirection cannot be decoded confidently, extraction falls back to the resolved companion
///     bank so the asset still converts instead of failing outright.
/// </summary>
public static class SfxExtractor
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

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir)
    {
        if (!TryResolvePlan(inputPath, out var plan, out var error))
            return new AudioConvertResult { ErrorMessage = error };

        var stem = Path.GetFileNameWithoutExtension(inputPath);
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
                    mapping.BankSample.SampleRate);
                if (tempPath == null || !File.Exists(tempPath))
                    continue;

                Directory.CreateDirectory(outDir);
                var finalPath = Path.Combine(outDir, $"{mapping.CueIndex:D3}.wav");
                File.Move(tempPath, finalPath, overwrite: true);
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
                Directory.Delete(tempDir, recursive: true);
        }
    }

    public static bool CanExtract(string inputPath, out string error)
    {
        return TryResolvePlan(inputPath, out _, out error);
    }

    private static bool TryResolvePlan(string inputPath, out SfxExtractionPlan plan, out string error)
    {
        plan = new SfxExtractionPlan(new SfxBankSource("", "", 0, []), []);

        if (!File.Exists(inputPath))
        {
            error = "SFX file not found";
            return false;
        }

        if (!TryParseEntries(inputPath, out var entries, out error))
            return false;

        if (!TryResolveBankSource(inputPath, entries, out var bankSource, out error))
            return false;

        List<SfxCueMapping> mappings;
        if (TryCreateDirectReferenceMappings(entries, bankSource, out mappings))
        {
            plan = new SfxExtractionPlan(bankSource, mappings);
            error = "";
            return true;
        }

        mappings = CreateFullBankMappings(bankSource);
        if (mappings.Count == 0)
        {
            error = $"Companion {bankSource.BankFormat} soundbank could not be parsed";
            return false;
        }

        plan = new SfxExtractionPlan(bankSource, mappings);
        error = "";
        return true;
    }

    private static bool TryResolveBankSource(
        string inputPath,
        IReadOnlyList<SfxEntry> entries,
        out SfxBankSource bankSource,
        out string error)
    {
        if (TryFindCompanionBank(inputPath, out var bankPath) &&
            TryCreateBankSource(bankPath, out bankSource, out error))
        {
            return true;
        }

        if (TryFindAliasBank(inputPath, entries, out bankPath, out error) &&
            TryCreateBankSource(bankPath, out bankSource, out error))
        {
            return true;
        }

        bankSource = new SfxBankSource("", "", 0, []);
        error = string.IsNullOrWhiteSpace(error) ? "Companion KAT/VAB soundbank not found" : error;
        return false;
    }

    private static bool TryCreateBankSource(string bankPath, out SfxBankSource bankSource, out string error)
    {
        var ext = Path.GetExtension(bankPath).ToLowerInvariant();
        switch (ext)
        {
            case ".kat":
            {
                var samples = KatExtractor.EnumerateSamples(bankPath)
                    .Select(static sample => new SfxBankSample(
                        sample.Index,
                        sample.DataSize,
                        sample.SampleRate,
                        sample.Channels,
                        sample.Encoding))
                    .ToList();

                if (samples.Count == 0)
                {
                    bankSource = new SfxBankSource("", "", 0, []);
                    error = "Companion KAT soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource(bankPath, "KAT", 0, samples);
                error = "";
                return true;
            }

            case ".vab":
            {
                var samples = VabExtractor.EnumerateSamples(bankPath)
                    .Select(static sample => new SfxBankSample(
                        sample.Index,
                        sample.DataSize,
                        sample.SampleRate,
                        1,
                        "SPU-ADPCM"))
                    .ToList();

                if (samples.Count == 0)
                {
                    bankSource = new SfxBankSource("", "", 0, []);
                    error = "Companion VAB soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource(bankPath, "VAB", 1, samples);
                error = "";
                return true;
            }

            default:
                bankSource = new SfxBankSource("", "", 0, []);
                error = $"Unsupported SFX companion bank type: {ext}";
                return false;
        }
    }

    private static bool TryParseEntries(string inputPath, out List<SfxEntry> entries, out string error)
    {
        entries = [];

        if (!BinaryProbeReader.TryReadAllBytes(inputPath, out var data))
        {
            error = "Failed to read SFX file";
            return false;
        }

        if (data.Length < HeaderSize + EntrySize)
        {
            error = "Invalid SFX file layout";
            return false;
        }

        var headerValue = ReadUInt32BigEndian(data, 0);
        if ((headerValue & 0x000000FF) != ExpectedHeaderSuffix)
        {
            error = $"Unsupported SFX header value 0x{headerValue:X8}";
            return false;
        }

        entries = new List<SfxEntry>();
        for (var offset = HeaderSize; offset + EntrySize <= data.Length; offset += EntrySize)
        {
            if (IsZeroedEntry(data, offset))
                break;

            var entry = new SfxEntry(
                ReadUInt32LittleEndian(data, offset),
                ReadUInt32LittleEndian(data, offset + 4),
                ReadUInt32LittleEndian(data, offset + 8),
                ReadUInt32LittleEndian(data, offset + 12));

            if (entry.PackedId == TerminatorPackedId)
                break;

            entries.Add(entry);
        }

        if (entries.Count == 0)
        {
            error = "SFX file contains no cue entries";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryCreateDirectReferenceMappings(
        IReadOnlyList<SfxEntry> entries,
        SfxBankSource bankSource,
        out List<SfxCueMapping> mappings)
    {
        mappings = [];

        // Spider-Man-style SFX banks use PackedSampleNumber as a direct 1-based bank sample
        // reference. THPS2-style SFX uses non-zero PackedVariant values for extra indirection,
        // so reject those here and let them fall back to full companion-bank extraction.
        if (entries.Any(static entry => entry.PackedVariant != 0 || entry.PackedSampleNumber <= 0))
            return false;

        var samplesByIndex = bankSource.Samples.ToDictionary(static sample => sample.ExternalIndex);
        mappings = new List<SfxCueMapping>(entries.Count);

        for (var i = 0; i < entries.Count; i++)
        {
            var bankSampleIndex = bankSource.IndexBase + entries[i].PackedSampleNumber - 1;
            if (!samplesByIndex.TryGetValue(bankSampleIndex, out var bankSample))
            {
                mappings = [];
                return false;
            }

            mappings.Add(new SfxCueMapping(i, entries[i], bankSample, bankSource.BankFormat));
        }

        return true;
    }

    private static List<SfxCueMapping> CreateFullBankMappings(SfxBankSource bankSource)
    {
        return bankSource.Samples
            .Select(sample => new SfxCueMapping(sample.ExternalIndex, null, sample, bankSource.BankFormat))
            .ToList();
    }

    private static string? ExtractBankSampleToWav(
        SfxBankSource bankSource,
        int sampleIndex,
        string outputDir,
        int sampleRate)
    {
        return bankSource.BankFormat switch
        {
            "KAT" => KatExtractor.ExtractSingleToWav(bankSource.BankPath, sampleIndex, outputDir),
            "VAB" => VabExtractor.ExtractSingleToWav(bankSource.BankPath, sampleIndex, outputDir, sampleRate),
            _ => null
        };
    }

    private static bool TryFindCompanionBank(string inputPath, out string bankPath)
    {
        foreach (var extension in new[] { ".kat", ".KAT", ".vab", ".VAB" })
        {
            var candidate = Path.ChangeExtension(inputPath, extension);
            if (File.Exists(candidate))
            {
                bankPath = candidate;
                return true;
            }
        }

        bankPath = "";
        return false;
    }

    private static bool TryFindAliasBank(
        string inputPath,
        IReadOnlyList<SfxEntry> entries,
        out string bankPath,
        out string error)
    {
        bankPath = "";
        error = "Companion KAT/VAB soundbank not found";

        var directory = Path.GetDirectoryName(inputPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return false;

        var sfxFiles = Directory.EnumerateFiles(directory)
            .Where(static path => Path.GetExtension(path).Equals(".sfx", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Equals(inputPath, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var candidates = new List<SfxAliasCandidate>();
        foreach (var siblingPath in sfxFiles)
        {
            if (!TryFindCompanionBank(siblingPath, out var siblingBankPath))
                continue;

            if (!TryParseEntries(siblingPath, out var siblingEntries, out _))
                continue;

            if (siblingEntries.Count == 0)
                continue;

            var score = ScoreEntries(entries, siblingEntries);
            candidates.Add(new SfxAliasCandidate(siblingPath, siblingBankPath, score));
        }

        if (candidates.Count == 0)
            return false;

        var ordered = candidates
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.BankPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = ordered[0];
        var secondBestScore = ordered.Count > 1 ? ordered[1].Score : int.MaxValue;
        if (best.Score > AliasScoreThreshold || secondBestScore - best.Score < AliasMarginThreshold)
        {
            error = "Companion KAT/VAB soundbank not found and no high-confidence sibling SFX alias was found";
            return false;
        }

        bankPath = best.BankPath;
        error = "";
        return true;
    }

    private static int ScoreEntries(IReadOnlyList<SfxEntry> left, IReadOnlyList<SfxEntry> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var score = Math.Abs(left.Count - right.Count) * 20;

        for (var i = 0; i < count; i++)
        {
            if (left[i].Flags != right[i].Flags)
                score += 5;
            if (left[i].CueValue != right[i].CueValue)
                score += 2;
            if (left[i].PackedFlags != right[i].PackedFlags)
                score += 3;
            if (left[i].PackedSampleNumber != right[i].PackedSampleNumber)
                score += 6;
            if (left[i].PackedVariant != right[i].PackedVariant)
                score += 6;
            if (left[i].PackedMarker != right[i].PackedMarker)
                score += 3;
        }

        return score;
    }

    private static bool IsZeroedEntry(byte[] data, int offset)
    {
        for (var i = 0; i < EntrySize; i++)
        {
            if (data[offset + i] != 0)
                return false;
        }

        return true;
    }

    private static uint ReadUInt32BigEndian(byte[] data, int offset)
    {
        return ((uint)data[offset] << 24) |
               ((uint)data[offset + 1] << 16) |
               ((uint)data[offset + 2] << 8) |
               data[offset + 3];
    }

    private static uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return data[offset] |
               ((uint)data[offset + 1] << 8) |
               ((uint)data[offset + 2] << 16) |
               ((uint)data[offset + 3] << 24);
    }

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

    private sealed record SfxBankSource(
        string BankPath,
        string BankFormat,
        int IndexBase,
        IReadOnlyList<SfxBankSample> Samples);

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
