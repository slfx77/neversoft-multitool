namespace NeversoftMultitool.Core.Formats.Audio;

public static partial class SfxExtractor
{
    private static bool TryResolvePlan(string inputPath, out SfxExtractionPlan plan, out string error)
    {
        plan = new SfxExtractionPlan(new SfxBankSource("", "", []), []);

        if (!File.Exists(inputPath))
        {
            error = "SFX file not found";
            return false;
        }

        if (!TryParseEntries(inputPath, out var cues, out error))
            return false;

        if (!TryResolveBankSource(inputPath, cues, out var bankSource, out error))
            return false;

        var mappings = CreateCueMappings(cues, bankSource);
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
        IReadOnlyList<SfxCue> entries,
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

        bankSource = new SfxBankSource("", "", []);
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
                    bankSource = new SfxBankSource("", "", []);
                    error = "Companion KAT soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource(bankPath, "KAT", samples);
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
                    bankSource = new SfxBankSource("", "", []);
                    error = "Companion VAB soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource(bankPath, "VAB", samples);
                error = "";
                return true;
            }

            default:
                bankSource = new SfxBankSource("", "", []);
                error = $"Unsupported SFX companion bank type: {ext}";
                return false;
        }
    }

    private static bool TryParseEntries(string inputPath, out List<SfxCue> cues, out string error)
    {
        cues = [];

        if (!BinaryProbeReader.TryReadAllBytes(inputPath, out var data))
        {
            error = "Failed to read SFX file";
            return false;
        }

        return TryParseCues(data, out cues, out error);
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
