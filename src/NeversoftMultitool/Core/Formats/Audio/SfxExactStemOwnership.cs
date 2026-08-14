namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>One scanned asset participating in SFX ownership.</summary>
internal readonly record struct SfxScanCandidate(
    int Index,
    string EntryPath,
    byte[]? CueData = null);

/// <summary>
///     The selected bank for one SFX cue sheet. A null bank means the sheet is
///     unpaired or ambiguous. Alias matches are high-confidence cross-stem
///     matches; exact-stem and unowned results both leave <see cref="IsAlias" /> false.
/// </summary>
internal readonly record struct SfxCueOwnership(
    int CueIndex,
    int? BankIndex,
    bool IsAlias = false);

/// <summary>
///     Assigns SFX cue sheets to banks. Unique same-directory, same-stem
///     ownership is resolved first (KAT before VAB). Remaining sheets may use
///     the established high-confidence cue-layout score, but only through
///     same-directory sheets that already have exact ownership.
/// </summary>
internal static class SfxExactStemOwnership
{
    internal static IReadOnlyList<SfxCueOwnership> Plan(IReadOnlyList<SfxScanCandidate> assets)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var parsed = assets
            .Select(static candidate => Parse(candidate))
            .Where(static candidate => candidate.Kind != AssetKind.Other)
            .ToArray();
        var banksByKey = parsed
            .Where(static candidate => candidate.Kind is AssetKind.Kat or AssetKind.Vab)
            .GroupBy(static candidate => candidate.Key)
            .ToDictionary(static group => group.Key, static group => group.ToArray());
        var candidatesByIndex = parsed
            .GroupBy(static candidate => candidate.Index)
            .ToDictionary(static group => group.Key, static group => group.First());
        var exactAssignments = parsed
            .Where(static candidate => candidate.Kind == AssetKind.Sfx)
            .OrderBy(static candidate => candidate.CanonicalPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.CanonicalPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Index)
            .Select(candidate =>
            {
                var exactBanks = banksByKey.GetValueOrDefault(candidate.Key);
                return new ExactAssignment(
                    candidate,
                    SelectBank(exactBanks),
                    exactBanks is { Length: > 0 });
            })
            .ToArray();

        // Only valid, non-empty sheets with exact ownership can establish a
        // cross-stem bank identity. Newly aliased sheets never become anchors,
        // so a weak match cannot propagate transitively through a scan.
        var anchors = exactAssignments
            .Where(static assignment => assignment.BankIndex != null)
            .Select(assignment => TryCreateAnchor(assignment, candidatesByIndex))
            .Where(static anchor => anchor != null)
            .Select(static anchor => anchor!.Value)
            .ToArray();

        return exactAssignments
            .Select(assignment =>
            {
                if (assignment.BankIndex is { } exactBankIndex)
                    return new SfxCueOwnership(assignment.Cue.Index, exactBankIndex);

                // A same-stem bank set that is ambiguous is intentionally
                // fail-closed. Do not let a cross-stem score silently redirect
                // that sheet to an unrelated bank.
                if (assignment.HasExactBankCandidates)
                    return new SfxCueOwnership(assignment.Cue.Index, null);

                var aliasBankIndex = SelectAliasBank(assignment.Cue, anchors, candidatesByIndex);
                return new SfxCueOwnership(
                    assignment.Cue.Index,
                    aliasBankIndex,
                    aliasBankIndex != null);
            })
            .ToArray();
    }

    private static AliasAnchor? TryCreateAnchor(
        ExactAssignment assignment,
        IReadOnlyDictionary<int, ParsedCandidate> candidatesByIndex)
    {
        if (assignment.BankIndex is not { } bankIndex ||
            !candidatesByIndex.ContainsKey(bankIndex) ||
            !TryParseCues(assignment.Cue.CueData, out var cues))
        {
            return null;
        }

        return new AliasAnchor(bankIndex, assignment.Cue.Directory, cues);
    }

    private static int? SelectAliasBank(
        ParsedCandidate target,
        IReadOnlyList<AliasAnchor> anchors,
        IReadOnlyDictionary<int, ParsedCandidate> candidatesByIndex)
    {
        if (!TryParseCues(target.CueData, out var targetCues))
            return null;

        // Multiple anchored sheets may describe one bank. Collapse them to a
        // distinct bank identity and retain that bank's strongest sheet score;
        // otherwise duplicate sheets would create a false runner-up tie.
        var ordered = anchors
            .Where(anchor => anchor.Directory == target.Directory)
            .GroupBy(static anchor => anchor.BankIndex)
            .Select(group => new AliasBankScore(
                group.Key,
                group.Min(anchor => SfxAliasResolver.ScoreEntries(targetCues, anchor.Cues)),
                candidatesByIndex.TryGetValue(group.Key, out var bank)
                    ? bank.CanonicalPath
                    : string.Empty))
            .OrderBy(static candidate => candidate.Score)
            .ThenBy(static candidate => candidate.BankPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.BankPath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.BankIndex)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var best = ordered[0];
        var secondBestScore = ordered.Length > 1 ? ordered[1].Score : int.MaxValue;
        return SfxAliasResolver.IsHighConfidenceMatch(best.Score, secondBestScore)
            ? best.BankIndex
            : null;
    }

    private static bool TryParseCues(byte[]? data, out IReadOnlyList<SfxCue> cues)
    {
        if (data != null &&
            SfxCueResolver.TryParseCues(data, out var parsed, out _) &&
            parsed.Count > 0)
        {
            cues = parsed;
            return true;
        }

        cues = [];
        return false;
    }

    private static int? SelectBank(IReadOnlyList<ParsedCandidate>? candidates)
    {
        if (candidates == null)
            return null;

        var kat = candidates.Where(static candidate => candidate.Kind == AssetKind.Kat).ToArray();
        if (kat.Length == 1)
            return kat[0].Index;
        if (kat.Length > 1)
            return null;

        var vab = candidates.Where(static candidate => candidate.Kind == AssetKind.Vab).ToArray();
        return vab.Length == 1 ? vab[0].Index : null;
    }

    private static ParsedCandidate Parse(SfxScanCandidate candidate)
    {
        var canonical = candidate.EntryPath.Replace('\\', '/').Trim('/');
        var separator = canonical.LastIndexOf('/');
        var directory = separator < 0 ? string.Empty : canonical[..separator];
        var leaf = separator < 0 ? canonical : canonical[(separator + 1)..];
        var dot = leaf.LastIndexOf('.');
        if (dot <= 0 || dot == leaf.Length - 1)
        {
            return new ParsedCandidate(
                candidate.Index,
                canonical,
                directory.ToLowerInvariant(),
                default,
                AssetKind.Other,
                candidate.CueData);
        }

        var extension = leaf[dot..];
        var kind = extension.ToLowerInvariant() switch
        {
            ".sfx" => AssetKind.Sfx,
            ".kat" => AssetKind.Kat,
            ".vab" => AssetKind.Vab,
            _ => AssetKind.Other
        };
        var normalizedDirectory = directory.ToLowerInvariant();
        var key = new AssetKey(normalizedDirectory, leaf[..dot].ToLowerInvariant());
        return new ParsedCandidate(
            candidate.Index,
            canonical,
            normalizedDirectory,
            key,
            kind,
            candidate.CueData);
    }

    private enum AssetKind
    {
        Other,
        Sfx,
        Kat,
        Vab
    }

    private readonly record struct AssetKey(string Directory, string Stem);

    private readonly record struct ParsedCandidate(
        int Index,
        string CanonicalPath,
        string Directory,
        AssetKey Key,
        AssetKind Kind,
        byte[]? CueData);

    private readonly record struct ExactAssignment(
        ParsedCandidate Cue,
        int? BankIndex,
        bool HasExactBankCandidates);

    private readonly record struct AliasAnchor(
        int BankIndex,
        string Directory,
        IReadOnlyList<SfxCue> Cues);

    private readonly record struct AliasBankScore(
        int BankIndex,
        int Score,
        string BankPath);
}
