using System.Collections.Concurrent;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

internal static class ThawSkeletonDiscovery
{
    private const string SkeletonExtensionPs2 = ".ske.ps2";
    private const string SkeletonExtensionCross = ".ske";

    private static readonly string[] DefaultCandidateStems = ["thps6_human", "thps5_human", "human", "test_skater_m"];
    private static readonly string[] HeadPrefixCandidates = ["pro_", "skater_", "sec_"];

    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, List<string>>> SkeletonIndexCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string? FindSkeletonPath(string skinFilePath, string stem, bool isThawSkin)
    {
        var dir = Path.GetDirectoryName(skinFilePath);
        if (dir is null)
            return null;

        var direct = CompanionSearch.FindCompanion(dir, stem, [SkeletonExtensionPs2, SkeletonExtensionCross],
            ["SKE", "Skeletons"]);
        if (direct != null)
            return direct;

        if (!isThawSkin)
            return null;

        var buildsRoot = FindBuildsRoot(skinFilePath);
        if (buildsRoot is null || !Directory.Exists(buildsRoot))
            return null;

        var skeletonIndex = SkeletonIndexCache.GetOrAdd(buildsRoot, BuildSkeletonIndex);
        foreach (var candidateStem in BuildCandidateStems(stem))
        {
            foreach (var extension in new[] { SkeletonExtensionPs2, SkeletonExtensionCross })
            {
                var fileName = candidateStem + extension;
                if (!skeletonIndex.TryGetValue(fileName, out var matches) || matches.Count == 0)
                    continue;

                return matches
                    .OrderByDescending(path => ScoreCandidate(path, candidateStem, extension))
                    .ThenBy(path => path.Length)
                    .First();
            }
        }

        return null;
    }

    /// <summary>
    ///     Scan an archive's entries for a skeleton matching this skin's stem (plus
    ///     the same humanoid-fallback rules as the filesystem variant). The archive
    ///     entry list is scored on <see cref="ArchiveEntry.FullName" /> so PAK
    ///     directory prefixes like <c>pre/skeletons/</c> still influence picks.
    /// </summary>
    public static Result? FindInArchive(
        IReadOnlyList<ArchiveEntry> archiveEntries,
        ArchiveAssetBackend backend,
        string stem,
        bool isThawSkin)
    {
        var direct = backend.FindEntry(stem + SkeletonExtensionPs2) ?? backend.FindEntry(stem + SkeletonExtensionCross);
        if (direct != null)
            return new Result(backend.ReadEntryBytes(direct), direct.Name);

        if (!isThawSkin)
            return null;

        var basenameToEntries = BuildArchiveIndex(archiveEntries);

        foreach (var candidateStem in BuildCandidateStems(stem))
        {
            foreach (var extension in new[] { SkeletonExtensionPs2, SkeletonExtensionCross })
            {
                var basename = candidateStem + extension;
                if (!basenameToEntries.TryGetValue(basename, out var matches) || matches.Count == 0)
                    continue;

                var winner = matches
                    .OrderByDescending(entry => ScoreCandidate(entry.FullName, candidateStem, extension))
                    .ThenBy(entry => entry.FullName.Length)
                    .First();
                return new Result(backend.ReadEntryBytes(winner), winner.Name);
            }
        }

        return null;
    }

    private static Dictionary<string, List<ArchiveEntry>> BuildArchiveIndex(IReadOnlyList<ArchiveEntry> entries)
    {
        var index = new Dictionary<string, List<ArchiveEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            if (!entry.Name.EndsWith(SkeletonExtensionCross, StringComparison.OrdinalIgnoreCase) &&
                !entry.Name.EndsWith(SkeletonExtensionPs2, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!index.TryGetValue(entry.Name, out var matches))
            {
                matches = [];
                index[entry.Name] = matches;
            }

            matches.Add(entry);
        }

        return index;
    }

    private static IReadOnlyDictionary<string, List<string>> BuildSkeletonIndex(string buildsRoot)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(buildsRoot, "*.ske*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(SkeletonExtensionCross, StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(SkeletonExtensionPs2, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!index.TryGetValue(name, out var matches))
            {
                matches = [];
                index[name] = matches;
            }

            matches.Add(file);
        }

        return index;
    }

    private static IEnumerable<string> BuildCandidateStems(string stem)
    {
        var yielded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Yield(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return false;
            return yielded.Add(candidate);
        }

        if (Yield(stem))
            yield return stem;

        if (stem.EndsWith("_head", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in HeadPrefixCandidates)
            {
                if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var trimmed = stem[prefix.Length..];
                if (Yield(trimmed))
                    yield return trimmed;
            }
        }

        foreach (var candidate in DefaultCandidateStems.Where(Yield))
            yield return candidate;
    }

    private static int ScoreCandidate(string path, string candidateStem, string extension)
    {
        var score = 0;
        if (extension.Equals(SkeletonExtensionPs2, StringComparison.OrdinalIgnoreCase))
            score += 1000;
        if (path.Contains("Tony Hawk's Underground 2", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("PS2", StringComparison.OrdinalIgnoreCase))
            score += 300;
        if (path.Contains("Tony Hawk's Underground", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("PS2", StringComparison.OrdinalIgnoreCase))
            score += 200;
        if (path.Contains("Tony Hawk's Pro Skater 4", StringComparison.OrdinalIgnoreCase) &&
            path.Contains("PS2", StringComparison.OrdinalIgnoreCase))
            score += 100;
        if (path.Contains($"{Path.DirectorySeparatorChar}Extracted{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (path.Contains($"{Path.DirectorySeparatorChar}pre{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{Path.DirectorySeparatorChar}Pre{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            score -= 25;
        if (candidateStem.EndsWith("_head", StringComparison.OrdinalIgnoreCase))
            score += 50;
        return score;
    }

    private static string? FindBuildsRoot(string path)
    {
        var current = Path.GetDirectoryName(path);
        while (current != null)
        {
            if (string.Equals(Path.GetFileName(current), "Builds", StringComparison.OrdinalIgnoreCase))
                return current;

            current = Path.GetDirectoryName(current);
        }

        return null;
    }

    /// <summary>Skeleton bytes plus the original entry name (used to decide which parser to use).</summary>
    public readonly record struct Result(byte[] Bytes, string EntryName);
}
