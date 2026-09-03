using NeversoftMultitool.Core.Formats.Qb;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Reads THPS4 Windows' authored level/sky/shell ownership from
///     <c>data/scripts/Levels.qb</c>. The scene files carry no relationship
///     field of their own: notably Motox deliberately loads Hof's sky, while
///     the otherwise plausible Can_Sky and Pink_Sky scenes are unused residue.
/// </summary>
internal static class Thps4PcLevelManifest
{
    private const string SceneSuffix = "scn.dat";
    private static readonly uint LevelKey = QbChecksum.HashLower("level");
    private static readonly uint SkyKey = QbChecksum.HashLower("sky");
    private static readonly uint OuterShellKey = QbChecksum.HashLower("outer_shell");

    internal sealed record Entry(
        string StructureName,
        string LevelName,
        string? SkyName,
        string? OuterShellName);

    internal sealed record Resolved(
        Entry ManifestEntry,
        string ManifestPath,
        string LevelScenePath,
        string? SkyScenePath,
        string? OuterShellScenePath);

    /// <summary>
    ///     Resolves a main loose level scene and every scene that the shipping
    ///     <c>load_level</c> script loads with it. Non-level assets, residue
    ///     skies, missing/ambiguous companions, and malformed manifests decline
    ///     the whole join instead of guessing from directory names.
    /// </summary>
    internal static bool TryResolve(string scenePath, out Resolved? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(scenePath) || !File.Exists(scenePath))
            return false;

        var fullScenePath = Path.GetFullPath(scenePath);
        var sceneName = Path.GetFileName(fullScenePath);
        if (!TryStripDelimiterFreeSuffix(sceneName, out var sceneStem))
            return false;

        var ownerDirectory = Path.GetDirectoryName(fullScenePath);
        var levelsDirectory = ownerDirectory == null ? null : Path.GetDirectoryName(ownerDirectory);
        var dataDirectory = levelsDirectory == null ? null : Path.GetDirectoryName(levelsDirectory);
        if (ownerDirectory == null || levelsDirectory == null || dataDirectory == null
            || !Path.GetFileName(levelsDirectory).Equals("levels", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var manifestPath = FindUniqueChildFile(
            Path.Combine(dataDirectory, "scripts"), "Levels.qb");
        if (manifestPath == null)
            return false;

        QbFile manifest;
        try
        {
            manifest = QbFile.Parse(manifestPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or OverflowException)
        {
            return false;
        }

        if (!TryParse(manifest, out var entries)
            || !entries.TryGetValue(sceneStem, out var entry))
        {
            return false;
        }

        var authoredLevelPath = FindUniqueScene(levelsDirectory, entry.LevelName);
        if (authoredLevelPath == null
            || !Path.GetFullPath(authoredLevelPath).Equals(fullScenePath, PathComparison))
        {
            return false;
        }

        var skyPath = entry.SkyName == null
            ? null
            : FindUniqueScene(levelsDirectory, entry.SkyName);
        if (entry.SkyName != null && skyPath == null)
            return false;

        var shellPath = entry.OuterShellName == null
            ? null
            : FindUniqueScene(levelsDirectory, entry.OuterShellName);
        if (entry.OuterShellName != null && shellPath == null)
            return false;

        resolved = new Resolved(
            entry,
            manifestPath,
            authoredLevelPath,
            skyPath,
            shellPath);
        return true;
    }

    /// <summary>
    ///     Extracts only complete top-level structure assignments with an
    ///     authored <c>level</c> member. Duplicate level names or duplicate
    ///     ownership members invalidate the manifest rather than choosing an
    ///     arbitrary definition.
    /// </summary>
    internal static bool TryParse(
        QbFile qb,
        out IReadOnlyDictionary<string, Entry> entries)
    {
        var parsed = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var tokens = qb.Tokens;
        var inScript = false;
        var containerDepth = 0;
        for (var index = 0; index + 2 < tokens.Count; index++)
        {
            if (tokens[index].Type == QbTokenType.KeywordScript)
            {
                inScript = true;
                continue;
            }

            if (tokens[index].Type == QbTokenType.KeywordEndScript)
            {
                inScript = false;
                continue;
            }

            if (inScript)
                continue;
            if (tokens[index].Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                containerDepth++;
                continue;
            }

            if (tokens[index].Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                if (--containerDepth < 0)
                {
                    entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }

                continue;
            }

            // Classic THPS4 PC QB uses END-OF-LINE-NUMBER tokens. QbFile's
            // broad legacy item index intentionally treats only the unnumbered
            // terminator as a global boundary, so scan the token stream here
            // using the actual top-level NAME = { ... } grammar instead.
            if (containerDepth != 0
                || tokens[index].Type != QbTokenType.Name
                || tokens[index + 1].Type != QbTokenType.Equals
                || tokens[index + 2].Type != QbTokenType.StartStruct
                || !TryFindStructEnd(tokens, index + 2, out var structEnd))
            {
                continue;
            }

            var structureChecksum = tokens[index].NameChecksum;
            if (!TryReadEntry(qb, structureChecksum, index + 2, structEnd, out var entry))
            {
                index = structEnd;
                continue;
            }

            if (!parsed.TryAdd(entry.LevelName, entry))
            {
                entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                return false;
            }

            index = structEnd;
        }

        if (inScript || containerDepth != 0)
        {
            entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            return false;
        }

        entries = parsed;
        return parsed.Count > 0;
    }

    private static bool TryReadEntry(
        QbFile qb,
        uint structureChecksum,
        int start,
        int closedAt,
        out Entry entry)
    {
        entry = null!;
        if (start < 0 || closedAt >= qb.Tokens.Count || closedAt - start < 1)
        {
            return false;
        }

        var tokens = qb.Tokens;
        if (tokens[start].Type != QbTokenType.StartStruct
            || tokens[closedAt].Type != QbTokenType.EndStruct)
        {
            return false;
        }

        string? level = null;
        string? sky = null;
        string? shell = null;
        var depth = 1;
        for (var index = start + 1; index < closedAt; index++)
        {
            var token = tokens[index];
            if (token.Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                depth++;
                continue;
            }

            if (token.Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                depth--;
                if (depth < 0)
                    return false;
                if (depth == 0)
                    break;

                continue;
            }

            if (depth != 1
                || token.Type != QbTokenType.Name
                || index + 2 >= closedAt
                || tokens[index + 1].Type != QbTokenType.Equals)
            {
                continue;
            }

            var target = token.NameChecksum switch
            {
                var key when key == LevelKey => 1,
                var key when key == SkyKey => 2,
                var key when key == OuterShellKey => 3,
                _ => 0
            };
            if (target == 0)
                continue;

            if (!TryReadAssetName(qb, tokens[index + 2], out var value))
                return false;
            switch (target)
            {
                case 1 when level == null:
                    level = value;
                    break;
                case 2 when sky == null:
                    sky = value;
                    break;
                case 3 when shell == null:
                    shell = value;
                    break;
                default:
                    return false;
            }
        }

        if (depth != 1 || level == null)
            return false;

        var structureName = qb.ResolveName(structureChecksum);
        if (!IsAssetName(structureName))
            return false;

        entry = new Entry(structureName, level, sky, shell);
        return true;
    }

    private static bool TryFindStructEnd(
        IReadOnlyList<QbToken> tokens,
        int start,
        out int end)
    {
        end = -1;
        var depth = 0;
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index].Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                depth++;
            }
            else if (tokens[index].Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                depth--;
                if (depth == 0)
                {
                    end = index;
                    return true;
                }

                if (depth < 0)
                    return false;
            }
            else if (tokens[index].Type == QbTokenType.EndOfFile)
            {
                return false;
            }
        }

        return false;
    }

    private static bool TryReadAssetName(QbFile qb, QbToken token, out string value)
    {
        value = token.Type switch
        {
            QbTokenType.String or QbTokenType.LocalString => token.StringValue ?? string.Empty,
            QbTokenType.Name or QbTokenType.Enum => qb.ResolveName(token.NameChecksum),
            _ => string.Empty
        };
        return IsAssetName(value);
    }

    private static bool IsAssetName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static bool TryStripDelimiterFreeSuffix(string fileName, out string stem)
    {
        stem = string.Empty;
        if (fileName.Length <= SceneSuffix.Length
            || !fileName.EndsWith(SceneSuffix, StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith('.' + SceneSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        stem = fileName[..^SceneSuffix.Length];
        return IsAssetName(stem);
    }

    private static string? FindUniqueScene(string levelsDirectory, string assetName)
    {
        string[] directories;
        try
        {
            directories = Directory.EnumerateDirectories(levelsDirectory)
                .Where(path => Path.GetFileName(path)
                    .Equals(assetName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return directories.Length == 1
            ? FindUniqueChildFile(directories[0], assetName + SceneSuffix)
            : null;
    }

    private static string? FindUniqueChildFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return null;

        try
        {
            var matches = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path)
                    .Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
