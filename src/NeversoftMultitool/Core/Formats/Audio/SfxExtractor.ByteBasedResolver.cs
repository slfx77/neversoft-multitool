namespace NeversoftMultitool.Core.Formats.Audio;

public static partial class SfxExtractor
{
    private static bool TryResolvePlanFromBytes(
        byte[] sfxData, SfxBankBytes? bankBytes, out SfxExtractionPlan plan, out string error)
    {
        plan = new SfxExtractionPlan(new SfxBankSource("", "", 0, []), []);

        if (!TryParseEntriesFromData(sfxData, out var entries, out error))
            return false;

        if (bankBytes is not { } bb)
        {
            error = "Companion KAT/VAB soundbank not found (archive source)";
            return false;
        }

        if (!TryCreateBankSourceFromBytes(bb, out var bankSource, out error))
            return false;

        if (TryCreateDirectReferenceMappings(entries, bankSource, out var mappings))
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
                    bankSource = new SfxBankSource("", "", 0, []);
                    error = "Companion KAT soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource("", "KAT", 0, samples, bankBytes.Data);
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
                    bankSource = new SfxBankSource("", "", 0, []);
                    error = "Companion VAB soundbank could not be parsed";
                    return false;
                }

                bankSource = new SfxBankSource("", "VAB", 1, samples, bankBytes.Data);
                error = "";
                return true;
            }

            default:
                bankSource = new SfxBankSource("", "", 0, []);
                error = $"Unsupported SFX companion bank type: {bankBytes.Format}";
                return false;
        }
    }

    private static bool TryParseEntriesFromData(byte[] data, out List<SfxEntry> entries, out string error)
    {
        entries = [];

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
}
