using System.Security.Cryptography;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>Identifies one video input for deterministic flat output naming.</summary>
public readonly record struct VideoOutputStemInput(string FileName, string RelativePath);

/// <summary>
///     Plans collision-free MP4 stems for a conversion batch. A unique source
///     keeps its familiar stem; every member of a case-insensitive collision
///     group receives a stable suffix derived from its source-relative identity.
/// </summary>
public static class VideoOutputStemPlanner
{
    private const int ShortHashCharacters = 8;
    private const int MaxBaseStemCharacters = 224;

    public static IReadOnlyList<string> Plan(IReadOnlyList<VideoOutputStemInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            return [];

        var items = inputs.Select((input, index) =>
        {
            var preferredStem = FfmpegVideoFormats.GetOutputStem(input.FileName);
            var (hashPath, sortPath) = NormalizeRelativePath(input.RelativePath, input.FileName);
            return new PlannedItem(
                index,
                SafeStem(preferredStem),
                hashPath,
                sortPath,
                input.FileName);
        }).ToArray();
        var output = new string[items.Length];
        var needsSuffix = new bool[items.Length];

        foreach (var group in items.GroupBy(static item => item.BaseStem, StringComparer.OrdinalIgnoreCase)
                     .Where(static group => group.Count() > 1))
        {
            foreach (var item in group)
                needsSuffix[item.Index] = true;
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Singletons own their historical name. Reserve those names first so
        // a generated hash suffix cannot shadow an unrelated singleton.
        foreach (var item in items.Where(item => !needsSuffix[item.Index]))
        {
            output[item.Index] = item.BaseStem;
            used.Add(item.BaseStem);
        }

        foreach (var item in items
                     .Where(item => needsSuffix[item.Index])
                     .OrderBy(static item => item.BaseStem, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.BaseStem, StringComparer.Ordinal)
                     .ThenBy(static item => item.HashRelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.SortRelativePath, StringComparer.Ordinal)
                     .ThenBy(static item => item.FileName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(static item => item.FileName, StringComparer.Ordinal)
                     .ThenBy(static item => item.Index))
        {
            var candidate = $"{item.BaseStem}_{ShortHash(item.HashRelativePath)}";
            output[item.Index] = MakeUnique(candidate, used);
        }

        return output;
    }

    internal static bool IsSafeOutputStem(string stem)
    {
        return !string.IsNullOrWhiteSpace(stem)
               && stem is not "." and not ".."
               && string.Equals(stem, Path.GetFileName(stem), StringComparison.Ordinal)
               && !stem.EndsWith(' ')
               && !stem.EndsWith('.')
               && stem.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
               && !IsReservedDeviceName(stem);
    }

    private static string MakeUnique(string candidate, HashSet<string> used)
    {
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var proposed = suffix == 1 ? candidate : $"{candidate}_{suffix}";
            if (used.Add(proposed))
                return proposed;
        }

        throw new InvalidOperationException($"Could not allocate a unique output stem for {candidate}");
    }

    private static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..ShortHashCharacters].ToLowerInvariant();
    }

    private static (string HashPath, string SortPath) NormalizeRelativePath(
        string relativePath,
        string fileName)
    {
        var path = string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath;
        var segments = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;

            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment.Normalize(NormalizationForm.FormC));
        }

        var normalized = string.Join('/', segments);
        return (normalized.ToLowerInvariant(), normalized);
    }

    private static string SafeStem(string preferredStem)
    {
        var normalized = preferredStem.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(leaf
            .Select(character => character < ' ' || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd(' ', '.')
            .Normalize(NormalizationForm.FormC);

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            cleaned = "video";

        if (IsReservedDeviceName(cleaned))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxBaseStemCharacters)
        {
            var hash = ShortHash(cleaned.ToLowerInvariant());
            var prefixLength = MaxBaseStemCharacters - ShortHashCharacters - 1;
            if (char.IsHighSurrogate(cleaned[prefixLength - 1])
                && char.IsLowSurrogate(cleaned[prefixLength]))
                prefixLength--;
            cleaned = $"{cleaned[..prefixLength]}_{hash}";
        }

        return cleaned;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        var device = stem.Split('.', 2)[0];
        if (device.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || device.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || device.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || device.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONIN$", StringComparison.OrdinalIgnoreCase)
            || device.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        return device.Length == 4
               && (device.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                   || device.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
               && device[3] is >= '1' and <= '9' or '¹' or '²' or '³';
    }

    private sealed record PlannedItem(
        int Index,
        string BaseStem,
        string HashRelativePath,
        string SortRelativePath,
        string FileName);
}
