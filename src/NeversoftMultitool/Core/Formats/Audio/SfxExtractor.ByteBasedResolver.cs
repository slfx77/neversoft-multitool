namespace NeversoftMultitool.Core.Formats.Audio;

public static partial class SfxExtractor
{
    private static bool TryResolvePlanFromBytes(
        byte[] sfxData, SfxBankBytes? bankBytes, out SfxExtractionPlan plan, out string error)
    {
        plan = new SfxExtractionPlan(new SfxBankSource("", "", []), []);

        if (!TryParseCues(sfxData, out var cues, out error))
            return false;

        if (bankBytes is not { } bb)
        {
            error = "Companion KAT/VAB soundbank not found (archive source)";
            return false;
        }

        if (!TryCreateBankSourceFromBytes(bb, out var bankSource, out error))
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

    private static bool TryCreateBankSourceFromBytes(
        SfxBankBytes bankBytes, out SfxBankSource bankSource, out string error)
    {
        switch (bankBytes.Format)
        {
            case "KAT":
            {
                var samples = KatExtractor.EnumerateSamples(bankBytes.Data)
                    .Select(static sample => new SfxBankSample(
                        sample.Index, sample.DataSize, sample.SampleRate, sample.Channels, sample.Encoding))
                    .ToList();

                if (samples.Count == 0)
                {
                    bankSource = new SfxBankSource("", "", []);
                    error = "Companion KAT soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource("", "KAT", samples, bankBytes.Data);
                error = "";
                return true;
            }

            case "VAB":
            {
                var samples = VabExtractor.EnumerateSamples(bankBytes.Data)
                    .Select(static sample => new SfxBankSample(
                        sample.Index, sample.DataSize, sample.SampleRate, 1, "SPU-ADPCM"))
                    .ToList();

                if (samples.Count == 0)
                {
                    bankSource = new SfxBankSource("", "", []);
                    error = "Companion VAB soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource("", "VAB", samples, bankBytes.Data);
                error = "";
                return true;
            }

            default:
                bankSource = new SfxBankSource("", "", []);
                error = $"Unsupported SFX companion bank type: {bankBytes.Format}";
                return false;
        }
    }
}
