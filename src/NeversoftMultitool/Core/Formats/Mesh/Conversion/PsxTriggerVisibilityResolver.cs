using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;
using QbKeyHasher = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves the initial named-geometry visibility authored by a PSX
///     level's companion TRG. Spider-Man's SetVisibilityByName command names
///     a mesh prefix followed by an inclusive two-digit suffix range.
/// </summary>
internal static class PsxTriggerVisibilityResolver
{
    private const int MaxSuffixRange = 1024;

    internal static IReadOnlySet<int> FindInitiallyHiddenMeshes(
        AssetSource source,
        string fileName,
        PsxMeshFile file)
    {
        if (file.IsSuperModel || !TryGetLevelStem(fileName, out var levelStem))
            return new HashSet<int>();

        byte[]? trgBytes = null;
        foreach (var companionName in GetCompanionNames(levelStem))
        {
            trgBytes = source.TryReadCompanion(companionName);
            if (trgBytes != null)
                break;
        }

        if (trgBytes == null)
            return new HashSet<int>();

        try
        {
            using var stream = new MemoryStream(trgBytes, writable: false);
            using var reader = new BinaryReader(stream);
            var trg = TrgFile.Parse(reader, levelStem + "_t.trg");
            return FindInitiallyHiddenMeshes(trg, file);
        }
        catch (Exception ex)
        {
            // A malformed or unrelated companion must never prevent the mesh
            // itself from opening; fall back to displaying its authored data.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to parse optional PSX trigger companion: {ex.Message}");
            return new HashSet<int>();
        }
    }

    internal static IReadOnlySet<int> FindInitiallyHiddenMeshes(
        TrgFile trg,
        PsxMeshFile file)
    {
        if (!trg.IsSpiderMan)
            return new HashSet<int>();

        // Only apply an unambiguous single restart state. Many levels have
        // several restart command lists for different gameplay checkpoints;
        // arbitrarily choosing one would hide otherwise useful level geometry.
        // Command points describe later transitions and must not be applied to
        // a static preview (for example, L8A4's What-If swap).
        var restarts = trg.Nodes
            .Where(static node => node.TypeId == TrgNodeMetadata.TypeRestart)
            .Take(2)
            .ToArray();
        if (restarts.Length != 1)
            return new HashSet<int>();

        var commands = restarts[0].Commands;
        if (commands == null)
            return new HashSet<int>();

        var hiddenHashes = new HashSet<uint>();
        foreach (var command in commands)
        {
            if (command.Opcode != 0xBF
                || command.Args is not { Count: >= 4 } args
                || args[0] is not string prefix
                || !TryGetUInt16(args[1], out var firstSuffix)
                || !TryGetUInt16(args[2], out var lastSuffix)
                || !TryGetUInt16(args[3], out var visible)
                || lastSuffix < firstSuffix
                || lastSuffix - firstSuffix > MaxSuffixRange
                || visible > 1)
            {
                continue;
            }

            for (var suffix = firstSuffix; suffix <= lastSuffix; suffix++)
            {
                var name = prefix + suffix.ToString("D2", CultureInfo.InvariantCulture);
                var hash = QbKeyHasher.Hash(name);
                if (visible == 0)
                    hiddenHashes.Add(hash);
                else
                    hiddenHashes.Remove(hash);
            }
        }

        var hiddenMeshes = new HashSet<int>();
        for (var meshIndex = 0; meshIndex < file.MeshNameHashes.Length; meshIndex++)
        {
            if (hiddenHashes.Contains(file.MeshNameHashes[meshIndex]))
                hiddenMeshes.Add(meshIndex);
        }

        return hiddenMeshes;
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
}
