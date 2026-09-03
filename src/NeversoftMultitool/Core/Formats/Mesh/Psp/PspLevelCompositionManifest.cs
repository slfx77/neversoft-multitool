using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Qb;
using QbChecksum = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Psp;

/// <summary>
///     Resolves the PSP level scenes that the shipping runtime explicitly owns
///     through its same-build <c>levels.qb</c>. This deliberately does not pair
///     files from <c>_sky</c> suffixes or neighbouring directory names.
/// </summary>
internal static class PspLevelCompositionManifest
{
    private static readonly uint StructureNameKey = QbChecksum.HashLower("structure_name");
    private static readonly uint LoadScriptKey = QbChecksum.HashLower("load_script");
    private static readonly uint LevelKey = QbChecksum.HashLower("level");
    private static readonly uint SkyKey = QbChecksum.HashLower("sky");
    private static readonly uint OuterShellKey = QbChecksum.HashLower("outer_shell");
    private static readonly uint IsStreamingLevelKey = QbChecksum.HashLower("is_streaming_level");
    private static readonly uint LoadLevelKey = QbChecksum.HashLower("load_level");
    private static readonly uint LoadSceneKey = QbChecksum.HashLower("loadscene");
    private static readonly uint SceneKey = QbChecksum.HashLower("scene");
    private static readonly uint IsNetKey = QbChecksum.HashLower("is_net");
    private static readonly uint IsDictionaryKey = QbChecksum.HashLower("is_dictionary");
    private static readonly uint NoSuperSectorsKey = QbChecksum.HashLower("no_supersectors");
    private static readonly uint IsPspKey = QbChecksum.HashLower("ispsp");
    private static readonly uint ParkEditorKey = QbChecksum.HashLower("park_editor");

    internal enum Game
    {
        Thug2Remix,
        Project8
    }

    internal sealed record Entry(
        uint StructureChecksum,
        string StructureName,
        uint? LoadScriptChecksum,
        string? LoadScriptName,
        string? LevelName,
        string? SkyName,
        string? OuterShellName,
        bool? IsStreamingLevel,
        bool IsEditorAlternative);

    internal sealed record Resolved(
        Game Game,
        Entry ManifestEntry,
        string ManifestPath,
        string LevelScenePath,
        string? SkyScenePath,
        string? OuterShellScenePath,
        bool IsNetworkVariant);

    private sealed record LoadSceneCall(int TokenIndex, uint ValueChecksum, IReadOnlySet<uint> Flags);

    private enum EntryReadResult
    {
        NotLevelDefinition,
        Valid,
        Malformed
    }

    /// <summary>
    ///     Resolves only exact packaged main scenes. A missing or malformed
    ///     manifest, duplicate ownership, an invalid load wrapper, or a missing
    ///     optional scene declines the complete join.
    /// </summary>
    internal static bool TryResolve(string levelScenePath, out Resolved? resolved)
    {
        resolved = null;
        if (string.IsNullOrWhiteSpace(levelScenePath) || !File.Exists(levelScenePath))
            return false;

        string fullLevelPath;
        try
        {
            fullLevelPath = Path.GetFullPath(levelScenePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return false;
        }

        if (!TryLocateBuild(fullLevelPath, out var game, out var datapRoot, out var assetRoot))
            return false;

        var manifestPath = game == Game.Thug2Remix
            ? FindUniqueRelativeFile(datapRoot, ["scripts", "game"], "levels.qb")
            : FindUniqueRelativeFile(datapRoot, ["pak", "qb.pak", "scripts", "game"], "levels.qb.psp");
        if (manifestPath == null)
            return false;

        QbFile qb;
        try
        {
            var bytes = File.ReadAllBytes(manifestPath);
            if (!HasExactManifestEnvelope(bytes, game))
                return false;
            qb = game == Game.Thug2Remix
                ? QbFile.ParseLegacyFastBranches(bytes, Path.GetFileName(manifestPath))
                : QbFile.Parse(bytes, Path.GetFileName(manifestPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException
                                   or OverflowException or IndexOutOfRangeException)
        {
            return false;
        }

        if (!TryParse(qb, game, out var entries))
            return false;

        var fileName = Path.GetFileName(fullLevelPath);
        if (!fileName.EndsWith(PspLevelFile.Suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var fileStem = fileName[..^PspLevelFile.Suffix.Length];
        var ownerName = Path.GetFileName(Path.GetDirectoryName(fullLevelPath));

        var matches = entries
            .Where(entry => entry.LevelName != null
                            && string.Equals(ownerName, entry.LevelName, StringComparison.OrdinalIgnoreCase)
                            && (fileStem.Equals(entry.LevelName, StringComparison.OrdinalIgnoreCase)
                                || fileStem.Equals(entry.LevelName + "_net", StringComparison.OrdinalIgnoreCase)))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
            return false;

        var selected = matches[0];
        if (selected.LoadScriptChecksum == null
            || !TryValidateEntryLoader(qb, selected))
        {
            return false;
        }

        // Project 8's world-zone structures mark this explicitly. Requiring
        // the authored flag prevents unrelated non-streaming test scenes from
        // acquiring world-zone semantics merely because a directory moved.
        if (game == Game.Project8 && selected.IsStreamingLevel != true)
            return false;

        var isNetwork = fileStem.EndsWith("_net", StringComparison.OrdinalIgnoreCase);
        var expectedMain = FindUniqueScene(assetRoot, selected.LevelName!, isNetwork);
        if (expectedMain == null || !PathsEqual(expectedMain, fullLevelPath))
            return false;

        var skyRoot = game == Game.Thug2Remix
            ? assetRoot
            : FindUniqueChildDirectory(datapRoot, "skies");
        var skyPath = selected.SkyName == null || skyRoot == null
            ? null
            : FindUniqueScene(skyRoot, selected.SkyName, false);
        if (selected.SkyName != null && skyPath == null)
            return false;

        var shellPath = selected.OuterShellName == null
            ? null
            : FindUniqueScene(assetRoot, selected.OuterShellName, false);
        if (selected.OuterShellName != null && shellPath == null)
            return false;

        resolved = new Resolved(
            game,
            selected,
            manifestPath,
            expectedMain,
            skyPath,
            shellPath,
            isNetwork);
        return true;
    }

    /// <summary>
    ///     Parses level structures and validates the generic loader contract.
    ///     The returned list intentionally retains duplicate level names so the
    ///     filesystem resolver can reject only the ambiguous selected asset.
    /// </summary>
    internal static bool TryParse(QbFile qb, Game game, out IReadOnlyList<Entry> entries)
    {
        entries = [];
        ArgumentNullException.ThrowIfNull(qb);
        if (qb.Tokens.Count == 0
            || qb.Tokens[^1].Type != QbTokenType.EndOfFile
            || qb.Tokens.Take(qb.Tokens.Count - 1).Any(static token => token.Type == QbTokenType.EndOfFile)
            || !TryValidateGenericLoader(qb, game))
        {
            return false;
        }

        var parsed = new List<Entry>();
        var tokens = qb.Tokens;
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Type == QbTokenType.KeywordScript)
            {
                if (!TryFindEndScript(tokens, index, out var scriptEnd))
                    return false;
                index = scriptEnd;
                continue;
            }

            if (index + 2 >= tokens.Count
                || tokens[index].Type != QbTokenType.Name
                || tokens[index + 1].Type != QbTokenType.Equals
                || tokens[index + 2].Type is not (QbTokenType.StartStruct or QbTokenType.StartArray))
            {
                if (tokens[index].Type is QbTokenType.StartStruct or QbTokenType.EndStruct
                    or QbTokenType.StartArray or QbTokenType.EndArray
                    or QbTokenType.KeywordEndScript)
                {
                    return false;
                }

                continue;
            }

            if (!TryFindContainerEnd(tokens, index + 2, out var containerEnd))
                return false;
            if (tokens[index + 2].Type == QbTokenType.StartStruct)
            {
                var result = TryReadEntry(qb, game, tokens[index].NameChecksum,
                    index + 2, containerEnd, out var entry);
                if (result == EntryReadResult.Malformed)
                    return false;
                if (result == EntryReadResult.Valid)
                    parsed.Add(entry!);
            }

            index = containerEnd;
        }

        entries = parsed;
        return parsed.Count > 0;
    }

    private static EntryReadResult TryReadEntry(
        QbFile qb,
        Game game,
        uint assignmentChecksum,
        int start,
        int end,
        out Entry? entry)
    {
        entry = null;
        uint? structureChecksum = null;
        uint? loadScriptChecksum = null;
        string? level = null;
        string? sky = null;
        string? shell = null;
        bool? isStreamingLevel = null;
        var stack = new Stack<QbTokenType>();
        stack.Push(QbTokenType.EndStruct);

        for (var index = start + 1; index < end; index++)
        {
            var token = qb.Tokens[index];
            if (token.Type == QbTokenType.KeywordScript)
            {
                if (!TryFindEndScript(qb.Tokens, index, out var scriptEnd) || scriptEnd >= end)
                    return EntryReadResult.Malformed;
                index = scriptEnd;
                continue;
            }

            if (token.Type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                stack.Push(token.Type == QbTokenType.StartStruct
                    ? QbTokenType.EndStruct
                    : QbTokenType.EndArray);
                continue;
            }

            if (token.Type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                if (stack.Count == 0 || stack.Pop() != token.Type)
                    return EntryReadResult.Malformed;
                continue;
            }

            if (stack.Count != 1 || token.Type != QbTokenType.Name)
                continue;

            var target = token.NameChecksum switch
            {
                var key when key == StructureNameKey => 1,
                var key when key == LoadScriptKey => 2,
                var key when key == LevelKey => 3,
                var key when key == SkyKey => 4,
                var key when key == OuterShellKey => 5,
                var key when key == IsStreamingLevelKey => 6,
                _ => 0
            };
            if (target == 0 || index + 2 >= end || qb.Tokens[index + 1].Type != QbTokenType.Equals)
                continue;

            var value = qb.Tokens[index + 2];
            switch (target)
            {
                case 1:
                    if (structureChecksum != null || value.Type is not (QbTokenType.Name or QbTokenType.Enum))
                        return EntryReadResult.Malformed;
                    structureChecksum = value.NameChecksum;
                    break;
                case 2:
                    if (loadScriptChecksum != null || value.Type is not (QbTokenType.Name or QbTokenType.Enum))
                        return EntryReadResult.Malformed;
                    loadScriptChecksum = value.NameChecksum;
                    break;
                case 3:
                    if (level != null || !TryReadAssetName(qb, value, out level))
                        return EntryReadResult.Malformed;
                    break;
                case 4:
                    if (sky != null || !TryReadAssetName(qb, value, out sky))
                        return EntryReadResult.Malformed;
                    break;
                case 5:
                    if (shell != null || !TryReadAssetName(qb, value, out shell))
                        return EntryReadResult.Malformed;
                    break;
                case 6:
                    if (isStreamingLevel != null || value.Type != QbTokenType.Integer
                                                 || value.IntValue is not (0 or 1))
                    {
                        return EntryReadResult.Malformed;
                    }

                    isStreamingLevel = value.IntValue == 1;
                    break;
            }
        }

        if (structureChecksum == null)
            return EntryReadResult.NotLevelDefinition;
        if (structureChecksum != assignmentChecksum)
            return EntryReadResult.Malformed;

        var structureName = DisplayChecksumName(qb, structureChecksum.Value);
        var loadScriptName = loadScriptChecksum.HasValue
            ? DisplayChecksumName(qb, loadScriptChecksum.Value)
            : null;

        var isEditorAlternative = game == Game.Thug2Remix
                                  && level == null
                                  && sky != null
                                  && shell != null;
        if (level == null && !isEditorAlternative)
            return EntryReadResult.NotLevelDefinition;

        entry = new Entry(
            structureChecksum.Value,
            structureName,
            loadScriptChecksum,
            loadScriptName,
            level,
            sky,
            shell,
            isStreamingLevel,
            isEditorAlternative);
        return EntryReadResult.Valid;
    }

    private static bool TryValidateGenericLoader(QbFile qb, Game game)
    {
        var loaders = qb.Items
            .Where(item => item.Kind == QbItemKind.Script && item.NameChecksum == LoadLevelKey)
            .Take(2)
            .ToArray();
        if (loaders.Length != 1
            || !TryGetScriptRange(qb.Tokens, loaders[0], out var start, out var end))
        {
            return false;
        }

        var calls = ReadLoadSceneCalls(qb.Tokens, start, end);
        if (game == Game.Thug2Remix)
        {
            var sky = FindUniqueCall(calls, SkyKey);
            var editorLevel = FindUniqueCall(calls, LevelKey, IsDictionaryKey);
            var shell = FindUniqueCall(calls, OuterShellKey, NoSuperSectorsKey);
            var networkLevel = FindUniqueCall(calls, LevelKey, IsNetKey);
            var ordinaryLevel = FindUniqueCall(calls, LevelKey);
            return sky != null && editorLevel != null && shell != null
                   && networkLevel != null && ordinaryLevel != null
                   && sky.TokenIndex < editorLevel.TokenIndex
                   && editorLevel.TokenIndex < shell.TokenIndex
                   && shell.TokenIndex < networkLevel.TokenIndex
                   && networkLevel.TokenIndex < ordinaryLevel.TokenIndex
                   && ContainsName(qb.Tokens, start, editorLevel.TokenIndex, ParkEditorKey);
        }

        var p8Sky = FindUniqueCall(calls, SkyKey);
        var p8NetworkLevel = FindUniqueCall(calls, LevelKey, IsNetKey);
        var p8OrdinaryLevel = FindUniqueCall(calls, LevelKey);
        return p8Sky != null && p8NetworkLevel != null && p8OrdinaryLevel != null
               && p8Sky.TokenIndex < p8NetworkLevel.TokenIndex
               && p8NetworkLevel.TokenIndex < p8OrdinaryLevel.TokenIndex
               && HasPspStreamingGuard(qb.Tokens, start, p8Sky.TokenIndex);
    }

    private static bool TryValidateEntryLoader(QbFile qb, Entry entry)
    {
        var scripts = qb.Items
            .Where(item => item.Kind == QbItemKind.Script
                           && item.NameChecksum == entry.LoadScriptChecksum)
            .Take(2)
            .ToArray();
        if (scripts.Length != 1
            || !TryGetScriptRange(qb.Tokens, scripts[0], out var start, out var end))
        {
            return false;
        }

        var matches = 0;
        for (var index = start; index <= end; index++)
        {
            if (qb.Tokens[index].Type != QbTokenType.Name
                || qb.Tokens[index].NameChecksum != LoadLevelKey)
            {
                continue;
            }

            var lineEnd = FindLineEnd(qb.Tokens, index + 1, end);
            if (Enumerable.Range(index + 1, lineEnd - index - 1)
                .Any(candidate => qb.Tokens[candidate].Type == QbTokenType.Name
                                  && qb.Tokens[candidate].NameChecksum == entry.StructureChecksum))
            {
                matches++;
            }
        }

        return matches == 1;
    }

    private static IReadOnlyList<LoadSceneCall> ReadLoadSceneCalls(
        IReadOnlyList<QbToken> tokens,
        int start,
        int end)
    {
        var result = new List<LoadSceneCall>();
        for (var index = start; index + 4 <= end; index++)
        {
            if (tokens[index].Type != QbTokenType.Name || tokens[index].NameChecksum != LoadSceneKey
                || tokens[index + 1].Type != QbTokenType.Name || tokens[index + 1].NameChecksum != SceneKey
                || tokens[index + 2].Type != QbTokenType.Equals
                || tokens[index + 3].Type != QbTokenType.Arg
                || tokens[index + 4].Type != QbTokenType.Name)
            {
                continue;
            }

            var lineEnd = FindLineEnd(tokens, index + 5, end);
            var flags = new HashSet<uint>();
            for (var candidate = index + 5; candidate < lineEnd; candidate++)
            {
                if (tokens[candidate].Type == QbTokenType.Name)
                    flags.Add(tokens[candidate].NameChecksum);
            }

            result.Add(new LoadSceneCall(index, tokens[index + 4].NameChecksum, flags));
            index = lineEnd;
        }

        return result;
    }

    private static LoadSceneCall? FindUniqueCall(
        IReadOnlyList<LoadSceneCall> calls,
        uint valueChecksum,
        params uint[] exactFlags)
    {
        var matches = calls
            .Where(call => call.ValueChecksum == valueChecksum && call.Flags.SetEquals(exactFlags))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool HasPspStreamingGuard(
        IReadOnlyList<QbToken> tokens,
        int start,
        int skyCall)
    {
        for (var index = start; index < skyCall; index++)
        {
            if (tokens[index].Type != QbTokenType.Name || tokens[index].NameChecksum != IsPspKey)
                continue;

            var limit = Math.Min(skyCall, index + 16);
            var sawOr = false;
            for (var candidate = index + 1; candidate + 3 < limit; candidate++)
            {
                if (tokens[candidate].Type == QbTokenType.Or)
                    sawOr = true;
                if (sawOr
                    && tokens[candidate].Type == QbTokenType.Arg
                    && tokens[candidate + 1].Type == QbTokenType.Name
                    && tokens[candidate + 1].NameChecksum == IsStreamingLevelKey
                    && tokens[candidate + 2].Type == QbTokenType.Equals
                    && tokens[candidate + 3].Type == QbTokenType.Integer
                    && tokens[candidate + 3].IntValue == 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ContainsName(
        IReadOnlyList<QbToken> tokens,
        int start,
        int end,
        uint checksum)
    {
        for (var index = start; index < end; index++)
        {
            if (tokens[index].Type == QbTokenType.Name && tokens[index].NameChecksum == checksum)
                return true;
        }

        return false;
    }

    private static bool TryGetScriptRange(
        IReadOnlyList<QbToken> tokens,
        QbItem item,
        out int start,
        out int end)
    {
        start = item.StartTokenIndex;
        end = -1;
        if (start < 0 || start >= tokens.Count || tokens[start].Type != QbTokenType.KeywordScript)
            return false;
        return TryFindEndScript(tokens, start, out end);
    }

    private static bool TryFindEndScript(
        IReadOnlyList<QbToken> tokens,
        int start,
        out int end)
    {
        end = -1;
        for (var index = start + 1; index < tokens.Count; index++)
        {
            if (tokens[index].Type == QbTokenType.KeywordScript)
                return false;
            if (tokens[index].Type == QbTokenType.KeywordEndScript)
            {
                end = index;
                return true;
            }
            if (tokens[index].Type == QbTokenType.EndOfFile)
                return false;
        }

        return false;
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

        var stack = new Stack<QbTokenType>();
        stack.Push(tokens[start].Type == QbTokenType.StartStruct
            ? QbTokenType.EndStruct
            : QbTokenType.EndArray);
        for (var index = start + 1; index < tokens.Count; index++)
        {
            var type = tokens[index].Type;
            if (type == QbTokenType.KeywordScript)
            {
                if (!TryFindEndScript(tokens, index, out var scriptEnd))
                    return false;
                index = scriptEnd;
                continue;
            }

            if (type is QbTokenType.StartStruct or QbTokenType.StartArray)
            {
                stack.Push(type == QbTokenType.StartStruct
                    ? QbTokenType.EndStruct
                    : QbTokenType.EndArray);
            }
            else if (type is QbTokenType.EndStruct or QbTokenType.EndArray)
            {
                if (stack.Count == 0 || stack.Pop() != type)
                    return false;
                if (stack.Count == 0)
                {
                    end = index;
                    return true;
                }
            }
            else if (type is QbTokenType.EndOfFile or QbTokenType.KeywordEndScript)
            {
                return false;
            }
        }

        return false;
    }

    private static int FindLineEnd(IReadOnlyList<QbToken> tokens, int start, int limit)
    {
        for (var index = start; index <= limit; index++)
        {
            if (tokens[index].Type is QbTokenType.EndOfLine or QbTokenType.EndOfLineNumber
                or QbTokenType.KeywordEndScript or QbTokenType.EndOfFile)
            {
                return index;
            }
        }

        return limit;
    }

    private static bool TryReadAssetName(QbFile qb, QbToken token, out string? value)
    {
        value = token.Type switch
        {
            QbTokenType.String or QbTokenType.LocalString => token.StringValue,
            QbTokenType.Name or QbTokenType.Enum => qb.ResolveName(token.NameChecksum),
            _ => null
        };
        return value != null && IsIdentifier(value);
    }

    private static bool IsIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128)
            return false;
        return value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    private static string DisplayChecksumName(QbFile qb, uint checksum)
    {
        var resolved = qb.ResolveName(checksum);
        return IsIdentifier(resolved) ? resolved : $"0x{checksum:X8}";
    }

    private static bool HasExactManifestEnvelope(byte[] bytes, Game game)
    {
        if (game == Game.Thug2Remix)
            return bytes.Length > 0 && bytes[^1] == (byte)QbTokenType.EndOfFile;

        if (!QbSectionParser.IsSectionedQb(bytes) || bytes.Length < 28)
            return false;
        var little = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4));
        var big = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4));
        return little == bytes.Length || big == bytes.Length;
    }

    private static bool TryLocateBuild(
        string fullLevelPath,
        out Game game,
        out string datapRoot,
        out string assetRoot)
    {
        game = default;
        datapRoot = string.Empty;
        assetRoot = string.Empty;
        var ownerDirectory = Path.GetDirectoryName(fullLevelPath);
        var parent = ownerDirectory == null ? null : Path.GetDirectoryName(ownerDirectory);
        if (ownerDirectory == null || parent == null)
            return false;

        if (Path.GetFileName(parent).Equals("levels", StringComparison.OrdinalIgnoreCase))
        {
            var datap = Path.GetDirectoryName(parent);
            if (datap == null || !Path.GetFileName(datap).Equals("datap", StringComparison.OrdinalIgnoreCase))
                return false;
            game = Game.Thug2Remix;
            datapRoot = datap;
            assetRoot = parent;
            return true;
        }

        if (!Path.GetFileName(parent).Equals("worldzones", StringComparison.OrdinalIgnoreCase))
            return false;
        var worlds = Path.GetDirectoryName(parent);
        var p8Datap = worlds == null ? null : Path.GetDirectoryName(worlds);
        if (worlds == null || p8Datap == null
                           || !Path.GetFileName(worlds).Equals("worlds", StringComparison.OrdinalIgnoreCase)
                           || !Path.GetFileName(p8Datap).Equals("datap", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        game = Game.Project8;
        datapRoot = p8Datap;
        assetRoot = parent;
        return true;
    }

    private static string? FindUniqueRelativeFile(
        string root,
        IReadOnlyList<string> directories,
        string fileName)
    {
        var current = root;
        foreach (var directory in directories)
        {
            var child = FindUniqueChildDirectory(current, directory);
            if (child == null)
                return null;
            current = child;
        }

        return FindUniqueChildFile(current, fileName);
    }

    private static string? FindUniqueScene(string root, string assetName, bool network)
    {
        var directory = FindUniqueChildDirectory(root, assetName);
        return directory == null
            ? null
            : FindUniqueChildFile(directory,
                assetName + (network ? "_net" : string.Empty) + PspLevelFile.Suffix);
    }

    private static string? FindUniqueChildDirectory(string directory, string childName)
    {
        if (!Directory.Exists(directory))
            return null;
        try
        {
            var matches = Directory.EnumerateDirectories(directory)
                .Where(path => Path.GetFileName(path).Equals(childName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindUniqueChildFile(string directory, string fileName)
    {
        if (!Directory.Exists(directory))
            return null;
        try
        {
            var matches = Directory.EnumerateFiles(directory)
                .Where(path => Path.GetFileName(path).Equals(fileName, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).Equals(Path.GetFullPath(right), PathComparison);

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;
}
