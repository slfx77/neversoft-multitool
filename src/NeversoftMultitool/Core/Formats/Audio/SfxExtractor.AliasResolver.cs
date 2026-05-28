namespace NeversoftMultitool.Core.Formats.Audio;

public static partial class SfxExtractor
{
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

    /// <summary>
    ///     Explicit companion-bank bytes. Used by archive-sourced SFX extraction
    ///     where the path-based cross-sibling alias fallback isn't available.
}
