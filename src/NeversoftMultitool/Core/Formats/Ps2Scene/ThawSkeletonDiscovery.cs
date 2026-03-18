using System.Collections.Concurrent;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawSkeletonDiscovery
{
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, List<string>>> SkeletonIndexCache =
        new(StringComparer.OrdinalIgnoreCase);

    public static string? FindSkeletonPath(string skinFilePath, string stem, bool isThawSkin)
    {
        var dir = Path.GetDirectoryName(skinFilePath);
        if (dir is null)
            return null;

        var direct = CompanionSearch.FindCompanion(dir, stem, [".ske.ps2", ".ske"], ["SKE", "Skeletons"]);
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
            foreach (var extension in new[] { ".ske.ps2", ".ske" })
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

    private static IReadOnlyDictionary<string, List<string>> BuildSkeletonIndex(string buildsRoot)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(buildsRoot, "*.ske*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(".ske", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase))
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
            foreach (var prefix in new[] { "pro_", "skater_", "sec_" })
            {
                if (!stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var trimmed = stem[prefix.Length..];
                if (Yield(trimmed))
                    yield return trimmed;
            }
        }

        foreach (var candidate in new[] { "thps6_human", "thps5_human", "human", "test_skater_m" })
        {
            if (Yield(candidate))
                yield return candidate;
        }
    }

    private static int ScoreCandidate(string path, string candidateStem, string extension)
    {
        var score = 0;
        if (extension.Equals(".ske.ps2", StringComparison.OrdinalIgnoreCase))
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
}
