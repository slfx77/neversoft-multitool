using System.Collections.Frozen;

namespace NeversoftMultitool.Core.Formats.Archives;

/// <summary>
///     Dictionary mapping Crc32Neversoft hashes to filenames for hashed HED files.
///     Generated from THPS2 cross-reference: prototype (plaintext) vs final (hashed) HED.
///     Algorithm: Crc32Neversoft (BinaryReaderExtensions.Crc32Neversoft).
/// </summary>
internal static class HedDictionary
{
    private static readonly FrozenDictionary<uint, string> Names = CreateNames();

    internal static string? TryResolve(uint hash)
    {
        return Names.GetValueOrDefault(hash);
    }

    private static FrozenDictionary<uint, string> CreateNames()
    {
        var names = new Dictionary<uint, string>();
        HedDictionaryPart1.AddEntries(names);
        HedDictionaryPart2.AddEntries(names);
        HedDictionaryPart3.AddEntries(names);
        HedDictionaryPart4.AddEntries(names);
        return names.ToFrozenDictionary();
    }
}
