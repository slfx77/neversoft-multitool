namespace NeversoftMultitool.Core.Formats.Audio;

public static partial class SfxExtractor
{
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
        int sampleRate,
        string? stemOverride = null)
    {
        if (bankSource.BankData != null)
        {
            var stem = stemOverride ?? "sfx";
            return bankSource.BankFormat switch
            {
                "KAT" => KatExtractor.ExtractSingleToWav(bankSource.BankData, stem, sampleIndex, outputDir),
                "VAB" => VabExtractor.ExtractSingleToWav(bankSource.BankData, stem, sampleIndex, outputDir, sampleRate),
                _ => null
            };
        }

        return bankSource.BankFormat switch
        {
            "KAT" => KatExtractor.ExtractSingleToWav(bankSource.BankPath, sampleIndex, outputDir),
            "VAB" => VabExtractor.ExtractSingleToWav(bankSource.BankPath, sampleIndex, outputDir, sampleRate),
            _ => null
        };
    }
}
