using System.Security.Cryptography;
using System.Text;

namespace NeversoftMultitool.Core.Formats;

internal enum ScriptOutputKind
{
    Qb,
    Trg
}

internal readonly record struct ScriptOutputPathInput(string SourcePath, ScriptOutputKind Kind);

/// <summary>
///     Assigns one flat output filename to each script in a batch without allowing
///     platform-suffixed inputs to overwrite one another.
/// </summary>
internal static class ScriptOutputPathPlanner
{
    private const int MaxOutputNameCharacters = 255;
    private const int ShortHashCharacters = 8;

    public static IReadOnlyList<string> Plan(IReadOnlyList<ScriptOutputPathInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        if (inputs.Count == 0)
            return [];

        // Sort before choosing which alias keeps the historical name. Filesystem
        // enumeration order must not affect output ownership, but the returned
        // filenames still line up with the caller's original entry order.
        var candidates = inputs
            .Select((input, index) => CreateCandidate(input, index))
            .OrderBy(static candidate => candidate.SourcePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static candidate => candidate.SourcePath, StringComparer.Ordinal)
            .ThenBy(static candidate => candidate.Kind)
            .ThenBy(static candidate => candidate.OriginalIndex)
            .ToArray();

        // Reserve every natural output before allocating ordinals. Otherwise the
        // second `level.qb.*` could take `level.qb_2.q` from `level.qb_2.qb`.
        var reservedPreferredNames = candidates
            .Select(static candidate => candidate.PreferredName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var assignedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedNames = new string[inputs.Count];

        foreach (var candidate in candidates)
        {
            var outputName = candidate.PreferredName;
            if (!assignedNames.Add(outputName))
            {
                outputName = AllocateOrdinalName(
                    candidate,
                    reservedPreferredNames,
                    assignedNames);
                assignedNames.Add(outputName);
            }

            plannedNames[candidate.OriginalIndex] = outputName;
        }

        return plannedNames;
    }

    private static PlanningCandidate CreateCandidate(
        ScriptOutputPathInput input,
        int originalIndex)
    {
        ArgumentNullException.ThrowIfNull(input.SourcePath);

        var stem = GetSafeStem(input.SourcePath);
        var extension = input.Kind switch
        {
            ScriptOutputKind.Qb => ".q",
            ScriptOutputKind.Trg => ".json",
            _ => throw new ArgumentOutOfRangeException(
                nameof(input), input.Kind, "Unknown script output kind.")
        };

        return new PlanningCandidate(
            originalIndex,
            input.SourcePath,
            input.Kind,
            stem,
            extension,
            BuildOutputName(stem, "", extension));
    }

    private static string AllocateOrdinalName(
        PlanningCandidate candidate,
        HashSet<string> reservedPreferredNames,
        HashSet<string> assignedNames)
    {
        for (var ordinal = 2L; ordinal < long.MaxValue; ordinal++)
        {
            var proposed = BuildOutputName(
                candidate.Stem,
                $"_{ordinal}",
                candidate.Extension);
            if (!reservedPreferredNames.Contains(proposed)
                && !assignedNames.Contains(proposed))
            {
                return proposed;
            }
        }

        throw new InvalidOperationException(
            $"Unable to allocate a unique output filename for '{candidate.SourcePath}'.");
    }

    private static string GetSafeStem(string sourcePath)
    {
        // Treat both directory separators explicitly so the result is one leaf on
        // every host OS, including when a Windows path is planned on another host.
        var normalized = sourcePath.Replace('\\', '/');
        var archiveSeparator = normalized.LastIndexOf("::", StringComparison.Ordinal);
        if (archiveSeparator >= 0)
            normalized = normalized[(archiveSeparator + 2)..];

        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var extensionSeparator = leaf.LastIndexOf('.');
        var rawStem = extensionSeparator >= 0 ? leaf[..extensionSeparator] : leaf;

        var builder = new StringBuilder(rawStem.Length);
        var index = 0;
        while (index < rawStem.Length)
        {
            var character = rawStem[index];
            if (char.IsHighSurrogate(character))
            {
                if (index + 1 < rawStem.Length && char.IsLowSurrogate(rawStem[index + 1]))
                {
                    builder.Append(character);
                    builder.Append(rawStem[index + 1]);
                    index += 2;
                }
                else
                {
                    builder.Append('_');
                    index++;
                }

                continue;
            }

            builder.Append(char.IsLowSurrogate(character) || IsInvalidWindowsFileNameCharacter(character)
                ? '_'
                : character);
            index++;
        }

        var stem = builder.ToString().TrimEnd(' ', '.');
        return IsReservedWindowsDeviceName(stem) ? "_" + stem : stem;
    }

    private static string BuildOutputName(
        string stem,
        string ordinalSuffix,
        string extension)
    {
        var desiredStem = stem + ordinalSuffix;
        var desiredName = desiredStem + extension;
        if (desiredName.Length <= MaxOutputNameCharacters)
            return desiredName;

        // Hash the complete desired leaf. A generated `name_2.q` and a natural
        // `name_2.q` therefore truncate identically, so preferred-name reservation
        // remains effective even at the component-length limit.
        var hash = ShortHash(desiredName);
        var tail = $"_{hash}{extension}";
        var prefix = TruncateWithoutSplittingSurrogate(
            desiredStem,
            MaxOutputNameCharacters - tail.Length);
        return prefix + tail;
    }

    private static string ShortHash(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(digest)[..ShortHashCharacters].ToLowerInvariant();
    }

    private static string TruncateWithoutSplittingSurrogate(string value, int maxCharacters)
    {
        if (value.Length <= maxCharacters)
            return value;

        var length = maxCharacters;
        if (length > 0
            && char.IsHighSurrogate(value[length - 1])
            && char.IsLowSurrogate(value[length]))
        {
            length--;
        }

        return value[..length];
    }

    private static bool IsInvalidWindowsFileNameCharacter(char character)
    {
        return char.IsControl(character)
               || character is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*';
    }

    private static bool IsReservedWindowsDeviceName(string stem)
    {
        var extensionSeparator = stem.IndexOf('.');
        var device = (extensionSeparator >= 0 ? stem[..extensionSeparator] : stem)
            .TrimEnd(' ', '.');

        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return device.Length == 4
               && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && device[3] is >= '1' and <= '9';
    }

    private readonly record struct PlanningCandidate(
        int OriginalIndex,
        string SourcePath,
        ScriptOutputKind Kind,
        string Stem,
        string Extension,
        string PreferredName);
}
