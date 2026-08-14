using System.Security.Cryptography;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Identifies one audio input for deterministic batch-output naming.</summary>
public readonly record struct AudioOutputStemInput(string FileName, string RelativePath);

/// <summary>
///     Plans flat, traversal-safe output stems for an audio batch. Unique source
///     stems remain unchanged; every member of a duplicate group receives a stable
///     suffix derived from its normalized relative path.
/// </summary>
public static class AudioOutputStemPlanner
{
    private const int ShortHashCharacters = 8;
    private const int MaxBaseStemCharacters = 192;

    public static IReadOnlyList<string> Plan(IReadOnlyList<AudioOutputStemInput> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            return [];

        var items = inputs.Select((input, index) =>
        {
            var (hashPath, sortPath) = NormalizeRelativePath(input.RelativePath, input.FileName);
            return new PlannedItem(
                index,
                SafeStem(input.FileName),
                hashPath,
                sortPath,
                input.FileName,
                Path.GetExtension(input.FileName).Equals(".vid", StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var output = new string[items.Length];
        var groups = items
            .GroupBy(static item => item.BaseStem, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var needsSuffix = new bool[items.Length];
        foreach (var group in groups.Where(static group => group.Count() > 1))
        {
            foreach (var item in group)
                needsSuffix[item.Index] = true;
        }

        // VID1 writes extra dubbed tracks beside its primary WAV as
        // "{stem}_track{N}.wav". Model that namespace even without parsing the
        // file, otherwise foo.vid and foo_track1.snd can silently overwrite one
        // another despite having different primary stems.
        foreach (var vid in items.Where(static item => item.IsVid))
        {
            foreach (var other in items)
            {
                if (other.Index == vid.Index || !IsVidTrackStem(other.BaseStem, vid.BaseStem))
                    continue;

                needsSuffix[vid.Index] = true;
                needsSuffix[other.Index] = true;
            }
        }

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedVidStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Singletons own their historical unsuffixed name. Reserving them first
        // also prevents a generated duplicate suffix from shadowing one.
        foreach (var item in items.Where(item => !needsSuffix[item.Index]))
        {
            output[item.Index] = item.BaseStem;
            used.Add(item.BaseStem);
            if (item.IsVid)
                usedVidStems.Add(item.BaseStem);
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
            var hash = ShortHash(item.HashRelativePath);
            var candidate = $"{item.BaseStem}_{hash}";
            output[item.Index] = MakeUnique(candidate, item.IsVid, used, usedVidStems);
        }

        return output;
    }

    private static string MakeUnique(
        string candidate,
        bool isVid,
        HashSet<string> used,
        HashSet<string> usedVidStems)
    {
        for (var suffix = 1; suffix < int.MaxValue; suffix++)
        {
            var proposed = suffix == 1 ? candidate : $"{candidate}_{suffix}";
            if (used.Contains(proposed)
                || isVid && used.Any(stem => IsVidTrackStem(stem, proposed))
                || usedVidStems.Any(vidStem => IsVidTrackStem(proposed, vidStem)))
                continue;

            used.Add(proposed);
            if (isVid)
                usedVidStems.Add(proposed);
            return proposed;
        }

        throw new InvalidOperationException($"Could not allocate a unique output stem for {candidate}");
    }

    private static string ShortHash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(digest)[..ShortHashCharacters].ToLowerInvariant();
    }

    private static (string HashPath, string SortPath) NormalizeRelativePath(string relativePath, string fileName)
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

    private static string SafeStem(string fileName)
    {
        var normalized = fileName.Replace('\\', '/');
        var leaf = normalized[(normalized.LastIndexOf('/') + 1)..];
        var stem = Path.GetFileNameWithoutExtension(leaf);
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(stem
            .Select(character => character < ' ' || invalid.Contains(character) ? '_' : character)
            .ToArray())
            .TrimEnd(' ', '.')
            .Normalize(NormalizationForm.FormC);

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned is "." or "..")
            cleaned = "audio";

        if (IsReservedDeviceName(cleaned))
            cleaned = "_" + cleaned;

        if (cleaned.Length > MaxBaseStemCharacters)
        {
            var hash = ShortHash(cleaned.Normalize(NormalizationForm.FormC).ToLowerInvariant());
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

    private static bool IsVidTrackStem(string candidate, string vidStem)
    {
        var prefix = vidStem + "_track";
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var index = candidate.AsSpan(prefix.Length);
        return index.Length > 0
               && index[0] is >= '1' and <= '9'
               && index[1..].IndexOfAnyExceptInRange('0', '9') < 0;
    }

    private sealed record PlannedItem(
        int Index,
        string BaseStem,
        string HashRelativePath,
        string SortRelativePath,
        string FileName,
        bool IsVid);
}
