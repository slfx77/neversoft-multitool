using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using QbKeyHasher = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Discovers named PSX level geometry controlled by Spider-Man's
///     SetVisibilityByName TRG command. Every source-authored range is retained
///     as a selectable group. A single restart supplies its authored state;
///     multi-restart levels use a static-preview union while retaining the
///     AUTOEXEC state for small alternate props.
/// </summary>
internal static class PsxTriggerVisibilityResolver
{
    private const int MaxSuffixRange = 1024;

    internal static IReadOnlyList<PsxVisibilityGroupDefinition> FindVisibilityGroups(
        AssetSource source,
        string fileName,
        PsxMeshFile file)
    {
        if (file.IsSuperModel || !TryGetLevelStem(fileName, out var levelStem))
            return [];

        byte[]? trgBytes = null;
        foreach (var companionName in GetCompanionNames(levelStem))
        {
            trgBytes = source.TryReadCompanion(companionName);
            if (trgBytes != null)
                break;
        }

        if (trgBytes == null)
            return [];

        try
        {
            using var stream = new MemoryStream(trgBytes, writable: false);
            using var reader = new BinaryReader(stream);
            var trg = TrgFile.Parse(reader, levelStem + "_t.trg");
            return FindVisibilityGroups(trg, file);
        }
        catch (Exception ex)
        {
            // A malformed or unrelated companion must never prevent the mesh
            // itself from opening; fall back to displaying authored geometry.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to parse optional PSX trigger companion: {ex.Message}");
            return [];
        }
    }

    internal static IReadOnlyList<PsxVisibilityGroupDefinition> FindVisibilityGroups(
        TrgFile trg,
        PsxMeshFile file)
    {
        if (!trg.IsSpiderMan)
            return [];

        var ranges = FindDistinctRanges(trg);
        if (ranges.Count == 0)
            return [];

        var objectIndicesByHash = BuildObjectIndicesByHash(file);
        var initialVisibility = BuildInitialVisibility(trg);
        var selectedRestartVisibility = BuildSelectedRestartVisibility(trg);
        var authoredTriangleCount = file.Objects
            .Where(obj => obj.MeshIndex < file.Meshes.Count)
            .Sum(obj => CountTriangles(file.Meshes[obj.MeshIndex]));
        var groups = new List<PsxVisibilityGroupDefinition>();
        var assetHash = QbKeyHasher.Hash(GetAssetKey(trg.FileName));

        foreach (var range in ranges)
        {
            var matchedSuffixes = new List<MatchedSuffix>();
            for (var suffix = range.FirstSuffix; suffix <= range.LastSuffix; suffix++)
            {
                var hash = HashRangeMember(range.Prefix, suffix);
                if (!objectIndicesByHash.TryGetValue(hash, out var objectIndices))
                    continue;

                var visible = !initialVisibility.TryGetValue(hash, out var initial) || initial;
                matchedSuffixes.Add(new MatchedSuffix(suffix, visible, objectIndices));
            }

            if (matchedSuffixes.Count == 0)
                continue;

            // A single-object range occupying at most one percent of the level
            // is normally an alternate prop rather than room streaming.
            // Preserve the AUTOEXEC-selected state for those small toggles
            // (L1A1's What-If sign), while substantial room geometry uses the
            // restart union so the static viewer does not discard most of a
            // level.
            var distinctObjects = matchedSuffixes
                .SelectMany(static match => match.ObjectIndices)
                .Distinct()
                .Take(2)
                .ToArray();
            if (selectedRestartVisibility != null
                && distinctObjects.Length == 1
                && authoredTriangleCount > 0
                && CountObjectTriangles(file, distinctObjects[0]) * 100 <= authoredTriangleCount)
            {
                for (var i = 0; i < matchedSuffixes.Count; i++)
                {
                    var match = matchedSuffixes[i];
                    var hash = HashRangeMember(range.Prefix, match.Suffix);
                    if (selectedRestartVisibility.TryGetValue(hash, out var selected))
                        matchedSuffixes[i] = match with { Visible = selected };
                }
            }

            // Overlapping restart commands can leave one authored range with
            // mixed initial states. Split only at those state boundaries so a
            // Boolean descriptor always reports and recreates its true default.
            var segmentStart = 0;
            while (segmentStart < matchedSuffixes.Count)
            {
                var segmentEnd = segmentStart + 1;
                while (segmentEnd < matchedSuffixes.Count
                       && matchedSuffixes[segmentEnd].Visible ==
                       matchedSuffixes[segmentStart].Visible)
                {
                    segmentEnd++;
                }

                AddRangeSegment(
                    groups,
                    range,
                    matchedSuffixes,
                    segmentStart,
                    segmentEnd,
                    assetHash,
                    split: segmentStart != 0 || segmentEnd != matchedSuffixes.Count);
                segmentStart = segmentEnd;
            }
        }

        return groups;
    }

    private static int CountObjectTriangles(PsxMeshFile file, int objectIndex)
    {
        if (objectIndex < 0 || objectIndex >= file.Objects.Count)
            return 0;

        var meshIndex = file.Objects[objectIndex].MeshIndex;
        return meshIndex < file.Meshes.Count
            ? CountTriangles(file.Meshes[meshIndex])
            : 0;
    }

    private static int CountTriangles(PsxMesh mesh)
    {
        return mesh.Faces.Sum(static face => face.IsQuad ? 2 : 1);
    }

    /// <summary>
    ///     Compatibility helper for format diagnostics which still reason in
    ///     mesh indices. Production generation uses object indices so repeated
    ///     placements of the same named mesh remain selectable as a unit.
    /// </summary>
    internal static IReadOnlySet<int> FindInitiallyHiddenMeshes(
        AssetSource source,
        string fileName,
        PsxMeshFile file)
    {
        return ToInitiallyHiddenMeshIndices(
            FindVisibilityGroups(source, fileName, file), file);
    }

    internal static IReadOnlySet<int> FindInitiallyHiddenMeshes(
        TrgFile trg,
        PsxMeshFile file)
    {
        return ToInitiallyHiddenMeshIndices(FindVisibilityGroups(trg, file), file);
    }

    private static HashSet<int> ToInitiallyHiddenMeshIndices(
        IReadOnlyList<PsxVisibilityGroupDefinition> groups,
        PsxMeshFile file)
    {
        var hiddenMeshes = new HashSet<int>();
        foreach (var group in groups.Where(static group => !group.DefaultEnabled))
        {
            foreach (var objectIndex in group.VisibleWhenEnabledObjectIndices)
            {
                if (objectIndex >= 0 && objectIndex < file.Objects.Count)
                    hiddenMeshes.Add(file.Objects[objectIndex].MeshIndex);
            }
        }

        return hiddenMeshes;
    }

    private static List<VisibilityRange> FindDistinctRanges(TrgFile trg)
    {
        var ranges = new List<VisibilityRange>();
        var seen = new HashSet<VisibilityRangeKey>();
        foreach (var command in trg.Nodes
                     .Where(static node => node.Commands != null)
                     .SelectMany(static node => node.Commands!))
        {
            if (!TryGetVisibilityRange(command, out var range))
                continue;

            var key = new VisibilityRangeKey(
                range.Prefix,
                range.FirstSuffix,
                range.LastSuffix);
            if (seen.Add(key))
                ranges.Add(range);
        }

        return ranges;
    }

    private static Dictionary<uint, List<int>> BuildObjectIndicesByHash(PsxMeshFile file)
    {
        var result = new Dictionary<uint, List<int>>();
        for (var objectIndex = 0; objectIndex < file.Objects.Count; objectIndex++)
        {
            var meshIndex = file.Objects[objectIndex].MeshIndex;
            if (meshIndex >= file.MeshNameHashes.Length)
                continue;

            var hash = file.MeshNameHashes[meshIndex];
            if (!result.TryGetValue(hash, out var objectIndices))
            {
                objectIndices = [];
                result.Add(hash, objectIndices);
            }

            objectIndices.Add(objectIndex);
        }

        return result;
    }

    private static Dictionary<uint, bool> BuildInitialVisibility(TrgFile trg)
    {
        var baseline = new Dictionary<uint, bool>();
        foreach (var commands in FindInitialAutoexecNodes(trg)
                     .Select(static node => node.Commands))
        {
            ApplyVisibilityCommands(commands, baseline);
        }

        var restarts = trg.Nodes
            .Where(static node => node.TypeId == TrgNodeMetadata.TypeRestart)
            .ToArray();
        if (restarts.Length == 0)
            return baseline;

        if (restarts.Length == 1)
        {
            ApplyVisibilityCommands(restarts[0].Commands, baseline);
            return baseline;
        }

        // Multiple restarts are mutually exclusive room/checkpoint states.
        // A static viewer has no gameplay position with which to select one,
        // so use their visibility union: geometry hidden in every authored
        // start remains hidden, while room geometry visible from any restart
        // remains available. Selecting the one AUTOEXEC restart instead made
        // most of L1A4 disappear.
        var restartStates = new Dictionary<uint, bool>[restarts.Length];
        var allHashes = new HashSet<uint>(baseline.Keys);
        for (var i = 0; i < restarts.Length; i++)
        {
            var state = new Dictionary<uint, bool>(baseline);
            ApplyVisibilityCommands(restarts[i].Commands, state);
            restartStates[i] = state;
            allHashes.UnionWith(state.Keys);
        }

        var initialVisibility = new Dictionary<uint, bool>();
        foreach (var hash in allHashes)
        {
            // An omitted value is unknown rather than hidden, so preserve it.
            // Only a unanimous, explicit hidden state is safe to apply.
            initialVisibility[hash] = !restartStates.All(state =>
                state.TryGetValue(hash, out var visible) && !visible);
        }

        return initialVisibility;
    }

    private static TrgNode[] FindInitialAutoexecNodes(TrgFile trg)
    {
        var singlePlayerNodes = trg.Nodes
            .Where(static node => node.TypeId == TrgNodeMetadata.TypeAutoexec)
            .ToArray();
        return singlePlayerNodes.Length > 0
            ? singlePlayerNodes
            : trg.Nodes
                .Where(static node => node.TypeId == TrgNodeMetadata.TypeAutoexec2)
                .ToArray();
    }

    private static Dictionary<uint, bool>? BuildSelectedRestartVisibility(TrgFile trg)
    {
        string? restartName = null;
        var selected = new Dictionary<uint, bool>();
        foreach (var commands in FindInitialAutoexecNodes(trg)
                     .Select(static node => node.Commands))
        {
            ApplyVisibilityCommands(commands, selected);
            if (commands == null)
                continue;

            foreach (var command in commands)
            {
                if (command.Opcode == 0x8C
                    && command.Args is { Count: > 0 }
                    && command.Args[0] is string candidate
                    && candidate.Length > 0)
                {
                    restartName = candidate;
                }
            }
        }

        if (restartName == null)
            return null;

        var restart = trg.Nodes.FirstOrDefault(node =>
            node.TypeId == TrgNodeMetadata.TypeRestart
            && string.Equals(node.Name, restartName, StringComparison.OrdinalIgnoreCase));
        if (restart == null)
            return null;

        ApplyVisibilityCommands(restart.Commands, selected);
        return selected;
    }

    private static void ApplyVisibilityCommands(
        IReadOnlyList<TrgCommand>? commands,
        Dictionary<uint, bool> visibility)
    {
        if (commands == null)
            return;

        foreach (var command in commands)
        {
            if (!TryGetVisibilityRange(command, out var range))
                continue;

            for (var suffix = range.FirstSuffix; suffix <= range.LastSuffix; suffix++)
                visibility[HashRangeMember(range.Prefix, suffix)] = range.Visible;
        }
    }

    private static void AddRangeSegment(
        List<PsxVisibilityGroupDefinition> groups,
        VisibilityRange range,
        List<MatchedSuffix> matchedSuffixes,
        int start,
        int end,
        uint assetHash,
        bool split)
    {
        var firstSuffix = matchedSuffixes[start].Suffix;
        var lastSuffix = matchedSuffixes[end - 1].Suffix;
        var targetObjects = matchedSuffixes
            .Skip(start)
            .Take(end - start)
            .SelectMany(static match => match.ObjectIndices)
            .ToHashSet();
        var idHash = QbKeyHasher.Hash(
            $"{range.Prefix}|{range.FirstSuffix:D4}|{range.LastSuffix:D4}");
        var id = $"psx.trg.{assetHash:X8}.{idHash:X8}." +
                 $"{range.FirstSuffix:D4}-{range.LastSuffix:D4}";
        if (split)
            id += $".{firstSuffix:D4}-{lastSuffix:D4}";

        groups.Add(new PsxVisibilityGroupDefinition(
            id,
            FormatRangeLabel(range.Prefix, firstSuffix, lastSuffix),
            matchedSuffixes[start].Visible,
            ModelVisibilityGroupSource.TriggerRange,
            $"SetVisibilityByName(\"{range.Prefix}\", " +
            $"{firstSuffix}, {lastSuffix})",
            ExclusiveSetId: null,
            targetObjects,
            new HashSet<int>()));
    }

    private static string FormatRangeLabel(string prefix, int firstSuffix, int lastSuffix)
    {
        var firstName = prefix + firstSuffix.ToString("D2", CultureInfo.InvariantCulture);
        if (firstSuffix == lastSuffix)
            return firstName;

        var lastName = prefix + lastSuffix.ToString("D2", CultureInfo.InvariantCulture);
        return $"{firstName} – {lastName}";
    }

    private static string GetAssetKey(string trgFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(trgFileName);
        if (stem.EndsWith("_t", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^2];
        return stem.ToUpperInvariant();
    }

    private static bool TryGetVisibilityRange(
        TrgCommand command,
        out VisibilityRange range)
    {
        range = default;
        if (command.Opcode != 0xBF
            || command.Args is not { Count: >= 4 } args
            || args[0] is not string prefix
            || prefix.Length == 0
            || !TryGetUInt16(args[1], out var firstSuffix)
            || !TryGetUInt16(args[2], out var lastSuffix)
            || !TryGetUInt16(args[3], out var visible)
            || lastSuffix < firstSuffix
            || lastSuffix - firstSuffix > MaxSuffixRange
            || visible > 1)
        {
            return false;
        }

        range = new VisibilityRange(prefix, firstSuffix, lastSuffix, visible != 0);
        return true;
    }

    private static uint HashRangeMember(string prefix, int suffix)
    {
        return QbKeyHasher.Hash(
            prefix + suffix.ToString("D2", CultureInfo.InvariantCulture));
    }

    private static bool TryGetLevelStem(string fileName, out string levelStem)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
        {
            levelStem = string.Empty;
            return false;
        }

        levelStem = stem[..^2];
        return levelStem.Length > 0;
    }

    private static IEnumerable<string> GetCompanionNames(string levelStem)
    {
        yield return levelStem + "_t.trg";
        yield return levelStem + "_T.trg";
        yield return levelStem + "_t.TRG";
        yield return levelStem + "_T.TRG";
        yield return levelStem.ToLowerInvariant() + "_t.trg";
        yield return levelStem.ToUpperInvariant() + "_T.TRG";
    }

    private static bool TryGetUInt16(object value, out int result)
    {
        switch (value)
        {
            case ushort ushortValue:
                result = ushortValue;
                return true;
            case int intValue when intValue is >= ushort.MinValue and <= ushort.MaxValue:
                result = intValue;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private readonly record struct VisibilityRange(
        string Prefix,
        int FirstSuffix,
        int LastSuffix,
        bool Visible);

    private readonly record struct VisibilityRangeKey(
        string Prefix,
        int FirstSuffix,
        int LastSuffix);

    private readonly record struct MatchedSuffix(
        int Suffix,
        bool Visible,
        IReadOnlyList<int> ObjectIndices);
}
