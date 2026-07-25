namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Cross-sibling alias-bank scoring: picks the companion bank whose
///     cue table best matches a bankless .SFX file.
/// </summary>
internal static class SfxAliasResolver
{
    internal static bool TryFindCompanionBank(string inputPath, out string bankPath)
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

    internal static bool TryFindAliasBank(
        string inputPath,
        List<SfxCue> entries,
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

            if (!SfxPathResolver.TryParseEntries(siblingPath, out var siblingEntries, out _))
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
        if (best.Score > SfxExtractor.AliasScoreThreshold ||
            secondBestScore - best.Score < SfxExtractor.AliasMarginThreshold)
        {
            error = "Companion KAT/VAB soundbank not found and no high-confidence sibling SFX alias was found";
            return false;
        }

        bankPath = best.BankPath;
        error = "";
        return true;
    }

    private static int ScoreEntries(List<SfxCue> left, List<SfxCue> right)
    {
        var count = Math.Min(left.Count, right.Count);
        var score = Math.Abs(left.Count - right.Count) * 20;

        for (var i = 0; i < count; i++)
        {
            if (left[i].Program != right[i].Program)
                score += 6;
            if (left[i].Category != right[i].Category)
                score += 6;
            if (left[i].Alias != right[i].Alias)
                score += 6;
            if (left[i].Note != right[i].Note)
                score += 3;
            if (left[i].Loop != right[i].Loop)
                score += 3;
            if (left[i].Pitch != right[i].Pitch || left[i].Volume != right[i].Volume)
                score += 2;
        }

        return score;
    }

    internal static bool IsZeroedEntry(byte[] data, int offset)
    {
        for (var i = 0; i < SfxExtractor.EntrySize; i++)
        {
            if (data[offset + i] != 0)
                return false;
        }

        return true;
    }

    internal static uint ReadUInt32LittleEndian(byte[] data, int offset)
    {
        return data[offset] |
               ((uint)data[offset + 1] << 8) |
               ((uint)data[offset + 2] << 16) |
               ((uint)data[offset + 3] << 24);
    }
}
