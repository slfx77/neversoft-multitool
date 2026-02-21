using System.Collections.Frozen;

namespace NeversoftMultitool.Core;

/// <summary>
/// Neversoft QBKey hash algorithm (reflected CRC-32) and name resolution.
/// Algorithm: polynomial 0xEDB88320, init 0xFFFFFFFF, no final XOR.
/// PS1/DC/Xbox-era games (Apocalypse through THPS2X) use case-sensitive hashing.
/// THUG+ era games lowercase input before hashing.
/// </summary>
public static partial class QbKey
{
    private static readonly uint[] Table = InitTable();

    /// <summary>
    /// Hash a name using case-sensitive reflected CRC-32.
    /// This is the correct algorithm for PS1/DC/Xbox-era Neversoft games
    /// (Apocalypse, Spider-Man, THPS1, THPS2, THPS2X).
    /// </summary>
    public static uint Hash(string name)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var ch in name)
        {
            crc = (crc >> 8) ^ Table[(crc ^ (byte)ch) & 0xFF];
        }
        return crc;
    }

    /// <summary>
    /// Hash with lowercase normalization (THUG+ era convention).
    /// </summary>
    public static uint HashLower(string name) => Hash(name.ToLowerInvariant());

    public static string? TryResolve(uint hash) =>
        KnownNames.GetValueOrDefault(hash);

    public static IReadOnlyDictionary<uint, string> GetAllKnownMappings() => KnownNames;

    private static uint[] InitTable()
    {
        const uint poly = 0xEDB88320u;
        var table = new uint[256];
        for (var i = 0u; i < 256; i++)
        {
            var crc = i;
            for (var j = 0; j < 8; j++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ poly : crc >> 1;
            }
            table[i] = crc;
        }
        return table;
    }
}
