using NeversoftMultitool.Core.Formats.Qb;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

/// <summary>
///     Reads the single-player THPS3 PS2 level/sky ownership authored in
///     <c>SKATE3/Scripts/levels.qb</c>. The relationship is deliberately not
///     inferred from BSP basenames: the Tutorials script, for example, loads
///     <c>Tut.bsp</c> with <c>Sk3Ed_Bch_Sky.bsp</c>, while Foundry and Warehouse
///     explicitly load no sky.
/// </summary>
internal static class Thps3Ps2LevelManifest
{
    private static readonly uint MasterLevelListKey = QbChecksum.HashLower("master_level_list");
    private static readonly uint LevelNameKey = QbChecksum.HashLower("level_name");
    private static readonly uint LoadScriptKey = QbChecksum.HashLower("load_script");
    private static readonly uint DebugLevelKey = QbChecksum.HashLower("debug_level");
    private static readonly uint NotPs2LevelKey = QbChecksum.HashLower("notPS2_level");
    private static readonly uint LoadLevelGeometryKey = QbChecksum.HashLower("loadlevelgeometry");
    private static readonly uint LevelKey = QbChecksum.HashLower("level");
    private static readonly uint SkyKey = QbChecksum.HashLower("sky");
    private static readonly uint PreSetKey = QbChecksum.HashLower("Pre_set");
    private static readonly uint SetBackgroundColorKey = QbChecksum.HashLower("SetBackgroundColor");
    private static readonly uint InMultiPlayerGameKey = QbChecksum.HashLower("InMultiPlayerGame");
    private static readonly uint InNetGameKey = QbChecksum.HashLower("InNetGame");
    private static readonly uint RedKey = QbChecksum.HashLower("r");
    private static readonly uint GreenKey = QbChecksum.HashLower("g");
    private static readonly uint BlueKey = QbChecksum.HashLower("b");

    internal sealed record Entry(
        string DisplayName,
        string LoadScriptName,
        string LevelAssetPath,
        string? SkyAssetPath,
        string? PreSet,
        uint? BackgroundColor);

    internal sealed record Resolved(
        Entry ManifestEntry,
        string ManifestPath,
        string LevelBspPath,
        string? SkyBspPath);

    private sealed record MasterEntry(
        string DisplayName,
        uint LoadScriptChecksum,
        string LoadScriptName);

    private sealed record BranchArm(int Frame, int Arm, bool IsNonSinglePlayerCondition);

    private sealed record GeometryCall(
        int TokenIndex,
        string LevelAssetPath,
        string? SkyAssetPath,
        string? PreSet,
        IReadOnlyList<BranchArm> BranchPath);

    private sealed record BackgroundCall(
        int TokenIndex,
        uint Color,
        IReadOnlyList<BranchArm> BranchPath);

    /// <summary>
    ///     Resolves only packaged runtime BSPs beneath one build's
    ///     <c>SKATE3/pre</c> tree. Authored paths must have one exact suffix
    ///     match there. Loose editor/source copies, ambiguous matches, malformed
    ///     manifests, and sky assets selected directly all decline composition.
    /// </summary>
    internal static bool TryResolve(string bspPath, out Resolved? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(bspPath) || !File.Exists(bspPath))
            return false;

        var fullBspPath = Path.GetFullPath(bspPath);
        if (!TryFindRuntimeRoot(fullBspPath, out _, out var preRoot, out var manifestPath)
            || !IsDescendantOf(fullBspPath, preRoot))
        {
            return false;
        }

        QbFile qb;
        try
        {
            qb = QbFile.Parse(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException or OverflowException)
        {
            return false;
        }

        if (!TryParse(qb, out var entries))
            return false;

        string[] runtimeBspFiles;
        try
        {
            runtimeBspFiles = Directory.EnumerateFiles(preRoot, "*", SearchOption.AllDirectories)
                .Where(static path => Path.GetExtension(path).Equals(".bsp", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        Entry? selected = null;
        string? selectedMain = null;
        foreach (var entry in entries)
        {
            var main = FindUniqueRuntimeAsset(runtimeBspFiles, preRoot, entry.LevelAssetPath);
            if (main == null || !PathsEqual(main, fullBspPath))
                continue;
            if (selected != null)
                return false;
            selected = entry;
            selectedMain = main;
        }

        if (selected == null || selectedMain == null)
            return false;

        var sky = selected.SkyAssetPath == null
            ? null
            : FindUniqueRuntimeAsset(runtimeBspFiles, preRoot, selected.SkyAssetPath);
        if (selected.SkyAssetPath != null && sky == null)
            return false;

        resolved = new Resolved(selected, manifestPath, selectedMain, sky);
        return true;
    }

    /// <summary>
    ///     Extracts the non-debug, PS2 entries from <c>master_level_list</c>,
    ///     joins each exact <c>load_script</c>, and selects its authored
    ///     single-player <c>loadlevelgeometry</c> arm. An empty <c>Sky</c>
    ///     remains an explicit no-sky result.
    /// </summary>
    internal static bool TryParse(QbFile qb, out IReadOnlyList<Entry> entries)
    {
        entries = [];
        if (!TryReadMasterEntries(qb, out var masterEntries)
            || !TryIndexScripts(qb, out var scripts))
        {
            return false;
        }

        var parsed = new List<Entry>(masterEntries.Count);
        var levelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var master in masterEntries)
        {
            if (!scripts.TryGetValue(master.LoadScriptChecksum, out var range)
                || !TryReadLoadScript(qb, range.Start, range.End, out var geometry, out var background))
            {
                return false;
            }

            if (!levelPaths.Add(geometry.LevelAssetPath))
                return false;

            parsed.Add(new Entry(
                master.DisplayName,
                master.LoadScriptName,
                geometry.LevelAssetPath,
                geometry.SkyAssetPath,
                geometry.PreSet,
                background));
        }

        entries = parsed;
        return parsed.Count > 0;
    }

    private static bool TryReadMasterEntries(QbFile qb, out List<MasterEntry> entries)
    {
        entries = [];
        var tokens = qb.Tokens;
        var matches = new List<(int Start, int End)>();
        for (var i = 0; i + 2 < tokens.Count; i++)
        {
            if (tokens[i].Type != QbTokenType.Name
                || tokens[i].NameChecksum != MasterLevelListKey
                || tokens[i + 1].Type != QbTokenType.Equals
                || tokens[i + 2].Type != QbTokenType.StartArray
                || !TryFindContainerEnd(tokens, i + 2, out var end))
            {
                continue;
            }

            matches.Add((i + 2, end));
        }

        if (matches.Count != 1)
            return false;

        var (start, closedAt) = matches[0];
        var depth = 1;
        for (var i = start + 1; i < closedAt; i++)
        {
            if (tokens[i].Type == QbTokenType.StartArray)
            {
                depth++;
                continue;
            }

            if (tokens[i].Type == QbTokenType.EndArray)
            {
                depth--;
                continue;
            }

            if (tokens[i].Type != QbTokenType.StartStruct)
                continue;
            if (depth != 1 || !TryFindContainerEnd(tokens, i, out var structEnd)
                           || structEnd > closedAt)
            {
                return false;
            }

            if (!TryReadMasterStruct(qb, i, structEnd, out var entry, out var include))
                return false;
            if (include)
                entries.Add(entry!);
            i = structEnd;
        }

        if (entries.Count == 0)
            return false;

        var scripts = new HashSet<uint>();
        return entries.All(entry => scripts.Add(entry.LoadScriptChecksum));
    }

    private static bool TryReadMasterStruct(
        QbFile qb,
        int start,
        int end,
        out MasterEntry? entry,
        out bool include)
    {
        entry = null;
        include = false;
        string? displayName = null;
        uint? loadScript = null;
        var debug = false;
        var notPs2 = false;
        var depth = 1;
        for (var i = start + 1; i < end; i++)
        {
            var token = qb.Tokens[i];
            if (token.Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                depth++;
                continue;
            }

            if (token.Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                depth--;
                if (depth < 1)
                    return false;
                continue;
            }

            if (depth != 1 || token.Type != QbTokenType.Name)
                continue;

            if (token.NameChecksum == DebugLevelKey)
                debug = true;
            else if (token.NameChecksum == NotPs2LevelKey)
                notPs2 = true;

            if (i + 2 >= end || qb.Tokens[i + 1].Type != QbTokenType.Equals)
                continue;

            var value = qb.Tokens[i + 2];
            if (token.NameChecksum == LevelNameKey)
            {
                if (displayName != null || value.Type is not (QbTokenType.String or QbTokenType.LocalString)
                                        || string.IsNullOrWhiteSpace(value.StringValue))
                {
                    return false;
                }

                displayName = value.StringValue;
            }
            else if (token.NameChecksum == LoadScriptKey)
            {
                if (loadScript.HasValue || value.Type is not (QbTokenType.Name or QbTokenType.Enum))
                    return false;
                loadScript = value.NameChecksum;
            }
        }

        if (displayName == null || !loadScript.HasValue)
            return false;

        include = !debug && !notPs2;
        if (!include)
            return true;

        var scriptName = qb.ResolveName(loadScript.Value);
        if (!IsIdentifier(scriptName))
            return false;
        entry = new MasterEntry(displayName, loadScript.Value, scriptName);
        return true;
    }

    private static bool TryIndexScripts(
        QbFile qb,
        out IReadOnlyDictionary<uint, (int Start, int End)> scripts)
    {
        var parsed = new Dictionary<uint, (int Start, int End)>();
        var tokens = qb.Tokens;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != QbTokenType.KeywordScript)
                continue;
            if (i + 1 >= tokens.Count || tokens[i + 1].Type != QbTokenType.Name)
            {
                scripts = new Dictionary<uint, (int, int)>();
                return false;
            }

            var end = i + 2;
            while (end < tokens.Count && tokens[end].Type != QbTokenType.KeywordEndScript)
                end++;
            if (end >= tokens.Count || !parsed.TryAdd(tokens[i + 1].NameChecksum, (i + 2, end)))
            {
                scripts = new Dictionary<uint, (int, int)>();
                return false;
            }

            i = end;
        }

        scripts = parsed;
        return parsed.Count > 0;
    }

    private static bool TryReadLoadScript(
        QbFile qb,
        int start,
        int end,
        out GeometryCall selected,
        out uint? background)
    {
        selected = null!;
        background = null;
        var geometryCalls = new List<GeometryCall>();
        var backgrounds = new List<BackgroundCall>();
        var path = new List<BranchArm>();
        var nextFrame = 0;
        for (var i = start; i < end; i++)
        {
            var token = qb.Tokens[i];
            switch (token.Type)
            {
                case QbTokenType.KeywordIf:
                    path.Add(new BranchArm(
                        nextFrame++,
                        0,
                        IsExactNonSinglePlayerCondition(qb.Tokens, i + 1, end)));
                    continue;
                case QbTokenType.KeywordElseIf:
                    // An elseif arm is not the false arm of the recognized
                    // multiplayer predicate. Its runtime meaning would require
                    // evaluating another condition, so composition fails closed.
                    return false;
                case QbTokenType.KeywordElse:
                    if (path.Count == 0)
                        return false;
                    path[^1] = path[^1] with { Arm = path[^1].Arm + 1 };
                    continue;
                case QbTokenType.KeywordEndIf:
                    if (path.Count == 0)
                        return false;
                    path.RemoveAt(path.Count - 1);
                    continue;
            }

            if (token.Type != QbTokenType.Name)
                continue;
            var statementEnd = FindStatementEnd(qb.Tokens, i + 1, end);
            if (token.NameChecksum == LoadLevelGeometryKey)
            {
                if (!TryReadGeometryCall(qb, i, statementEnd, path, out var geometry))
                    return false;
                geometryCalls.Add(geometry);
            }
            else if (token.NameChecksum == SetBackgroundColorKey
                     && TryReadBackgroundCall(qb, i, statementEnd, path, out var color))
            {
                backgrounds.Add(color);
            }
        }

        if (path.Count != 0 || geometryCalls.Count == 0)
            return false;

        var mainPaths = geometryCalls
            .Select(static call => call.LevelAssetPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (mainPaths.Length != 1)
            return false;

        if (!TrySelectSinglePlayerGeometry(geometryCalls, out selected))
            return false;

        var preSets = geometryCalls
            .Select(static call => call.PreSet)
            .Where(static value => value != null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (preSets.Length > 1)
            return false;
        selected = selected with { PreSet = preSets.FirstOrDefault() };

        // A background call applies to the selected flow only when its lexical
        // conditional scope is an ancestor of (or exactly) the selected geometry
        // arm. This excludes Airport's multiplayer-only colour while retaining
        // Canada's unconditional post-endif colour and the other levels' exact
        // single-player branch colours. The last applicable call is the runtime
        // value when a script intentionally overwrites an earlier one.
        var selectedBranchPath = selected.BranchPath;
        background = backgrounds
            .Where(call => IsPrefix(call.BranchPath, selectedBranchPath))
            .OrderBy(static call => call.TokenIndex)
            .Select(static call => (uint?)call.Color)
            .LastOrDefault();
        return true;
    }

    /// <summary>
    ///     The shipping PS2 scripts express alternate geometry as exactly
    ///     <c>if InMultiPlayerGame ... else ... endif</c>, except Tokyo which
    ///     uses <c>InNetGame</c>. The false/else arm is therefore the authored
    ///     single-player load. Do not infer that relationship from which arm
    ///     happens to have a non-empty sky: unrelated conditions and more
    ///     elaborate control flow fail closed instead.
    /// </summary>
    private static bool TrySelectSinglePlayerGeometry(
        IReadOnlyList<GeometryCall> geometryCalls,
        out GeometryCall selected)
    {
        selected = null!;
        if (geometryCalls.Count == 1)
        {
            if (geometryCalls[0].BranchPath.Count != 0)
                return false;
            selected = geometryCalls[0];
            return true;
        }

        if (geometryCalls.Count != 2
            || geometryCalls.Any(static call => call.BranchPath.Count != 1))
        {
            return false;
        }

        var first = geometryCalls[0].BranchPath[0];
        var second = geometryCalls[1].BranchPath[0];
        if (!first.IsNonSinglePlayerCondition
            || !second.IsNonSinglePlayerCondition
            || first.Frame != second.Frame
            || first.Arm == second.Arm
            || (first.Arm is not 0 and not 1)
            || (second.Arm is not 0 and not 1))
        {
            return false;
        }

        selected = first.Arm == 1 ? geometryCalls[0] : geometryCalls[1];
        return true;
    }

    private static bool IsExactNonSinglePlayerCondition(
        IReadOnlyList<QbToken> tokens,
        int start,
        int limit)
    {
        var end = FindStatementEnd(tokens, start, limit);
        if (start >= end || end - start != 1 || tokens[start].Type != QbTokenType.Name)
            return false;
        return tokens[start].NameChecksum is var checksum
               && (checksum == InMultiPlayerGameKey || checksum == InNetGameKey);
    }

    private static bool TryReadGeometryCall(
        QbFile qb,
        int callIndex,
        int statementEnd,
        IReadOnlyList<BranchArm> branchPath,
        out GeometryCall call)
    {
        call = null!;
        string? level = null;
        string? sky = null;
        string? preSet = null;
        var sawSky = false;
        for (var i = callIndex + 1; i + 2 < statementEnd; i++)
        {
            var token = qb.Tokens[i];
            if (token.Type != QbTokenType.Name || qb.Tokens[i + 1].Type != QbTokenType.Equals)
                continue;

            var value = qb.Tokens[i + 2];
            if (token.NameChecksum == LevelKey)
            {
                if (level != null || !TryReadPathString(value, allowEmpty: false, out level))
                    return false;
            }
            else if (token.NameChecksum == SkyKey)
            {
                if (sawSky || !TryReadPathString(value, allowEmpty: true, out var parsedSky))
                    return false;
                sawSky = true;
                sky = parsedSky.Length == 0 ? null : parsedSky;
            }
            else if (token.NameChecksum == PreSetKey)
            {
                if (preSet != null || value.Type is not (QbTokenType.String or QbTokenType.LocalString)
                                   || !IsIdentifier(value.StringValue ?? string.Empty))
                {
                    return false;
                }

                preSet = value.StringValue;
            }
        }

        if (level == null || !sawSky)
            return false;
        call = new GeometryCall(callIndex, level, sky, preSet, branchPath.ToArray());
        return true;
    }

    private static bool TryReadBackgroundCall(
        QbFile qb,
        int callIndex,
        int statementEnd,
        IReadOnlyList<BranchArm> branchPath,
        out BackgroundCall call)
    {
        call = null!;
        var structStart = -1;
        for (var i = callIndex + 1; i < statementEnd; i++)
        {
            if (qb.Tokens[i].Type == QbTokenType.StartStruct)
            {
                structStart = i;
                break;
            }
        }

        if (structStart < 0 || !TryFindContainerEnd(qb.Tokens, structStart, out var structEnd)
                            || structEnd >= statementEnd)
        {
            return false;
        }

        int? red = null;
        int? green = null;
        int? blue = null;
        var depth = 1;
        for (var i = structStart + 1; i < structEnd; i++)
        {
            var token = qb.Tokens[i];
            if (token.Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                depth++;
                continue;
            }
            if (token.Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                depth--;
                continue;
            }
            if (depth != 1 || token.Type != QbTokenType.Name || i + 2 >= structEnd
                || qb.Tokens[i + 1].Type != QbTokenType.Equals
                || qb.Tokens[i + 2].Type != QbTokenType.Integer)
            {
                continue;
            }

            var value = qb.Tokens[i + 2].IntValue;
            if (value is < 0 or > 255)
                return false;
            if (token.NameChecksum == RedKey)
            {
                if (red.HasValue) return false;
                red = value;
            }
            else if (token.NameChecksum == GreenKey)
            {
                if (green.HasValue) return false;
                green = value;
            }
            else if (token.NameChecksum == BlueKey)
            {
                if (blue.HasValue) return false;
                blue = value;
            }
        }

        if (!red.HasValue || !green.HasValue || !blue.HasValue)
            return false;
        var packed = (uint)((red.Value << 16) | (green.Value << 8) | blue.Value);
        call = new BackgroundCall(callIndex, packed, branchPath.ToArray());
        return true;
    }

    private static int FindStatementEnd(IReadOnlyList<QbToken> tokens, int start, int limit)
    {
        for (var i = start; i < limit; i++)
        {
            if (tokens[i].Type is QbTokenType.EndOfLine or QbTokenType.EndOfLineNumber)
                return i;
        }
        return limit;
    }

    private static bool IsPrefix(IReadOnlyList<BranchArm> candidate, IReadOnlyList<BranchArm> selected)
    {
        if (candidate.Count > selected.Count)
            return false;
        for (var i = 0; i < candidate.Count; i++)
        {
            if (candidate[i] != selected[i])
                return false;
        }
        return true;
    }

    private static bool TryReadPathString(QbToken token, bool allowEmpty, out string normalized)
    {
        normalized = string.Empty;
        if (token.Type is not (QbTokenType.String or QbTokenType.LocalString))
            return false;
        var value = token.StringValue ?? string.Empty;
        if (value.Length == 0)
            return allowEmpty;
        return TryNormalizeAssetPath(value, out normalized);
    }

    private static bool TryNormalizeAssetPath(string value, out string normalized)
    {
        normalized = string.Empty;
        if (value.Length > 260 || Path.IsPathRooted(value))
            return false;
        var parts = value.Replace('\\', '/').Split('/', StringSplitOptions.None);
        if (parts.Length != 3
            || !parts[0].Equals("Levels", StringComparison.OrdinalIgnoreCase)
            || parts.Any(static part => !IsIdentifier(Path.GetFileNameWithoutExtension(part))))
        {
            return false;
        }
        if (!Path.GetExtension(parts[2]).Equals(".bsp", StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileNameWithoutExtension(parts[2]).Equals(parts[1], StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        normalized = string.Join('/', parts);
        return true;
    }

    private static bool TryFindContainerEnd(
        IReadOnlyList<QbToken> tokens,
        int start,
        out int end)
    {
        end = -1;
        if (start < 0 || start >= tokens.Count
            || tokens[start].Type is not (QbTokenType.StartStruct or QbTokenType.StartArray))
        {
            return false;
        }
        var depth = 0;
        for (var i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Type is QbTokenType.StartStruct or QbTokenType.StartArray)
                depth++;
            else if (tokens[i].Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                depth--;
                if (depth == 0)
                {
                    end = i;
                    return true;
                }
                if (depth < 0)
                    return false;
            }
            else if (tokens[i].Type == QbTokenType.EndOfFile)
                return false;
        }
        return false;
    }

    private static bool TryFindRuntimeRoot(
        string bspPath,
        out string skate3Root,
        out string preRoot,
        out string manifestPath)
    {
        skate3Root = string.Empty;
        preRoot = string.Empty;
        manifestPath = string.Empty;
        var directory = Path.GetDirectoryName(bspPath);
        while (directory != null)
        {
            if (Path.GetFileName(directory).Equals("SKATE3", StringComparison.OrdinalIgnoreCase))
            {
                var scripts = FindUniqueChildDirectory(directory, "Scripts");
                var pre = FindUniqueChildDirectory(directory, "pre");
                var manifest = scripts == null ? null : FindUniqueChildFile(scripts, "levels.qb");
                if (pre != null && manifest != null)
                {
                    skate3Root = directory;
                    preRoot = pre;
                    manifestPath = manifest;
                    return true;
                }
            }
            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    private static string? FindUniqueRuntimeAsset(
        IReadOnlyList<string> runtimeBspFiles,
        string preRoot,
        string assetPath)
    {
        var normalized = assetPath.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var matches = runtimeBspFiles
            .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
            .Where(path => ("/" + Path.GetRelativePath(preRoot, path).Replace('\\', '/'))
                .EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static string? FindUniqueChildDirectory(string directory, string name)
    {
        try
        {
            var matches = Directory.EnumerateDirectories(directory)
                .Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindUniqueChildFile(string directory, string name)
    {
        try
        {
            var matches = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsDescendantOf(string path, string directory)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative)
               && relative != ".."
               && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), PathComparison);

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128)
            return false;
        return value.All(static c => char.IsAsciiLetterOrDigit(c) || c == '_');
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
