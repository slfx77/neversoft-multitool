using System.Collections.Frozen;
using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Gob;

/// <summary>
///     Maps a GOB file's <see cref="GobFile.NameCrc" /> back to its name.
///
///     The key is a standard CRC-32 (the zlib polynomial) of the name LOWERCASED,
///     spelled with the loader's leading <c>.\</c> — the ARM9 runs its <c>strlwr</c>
///     (Sk8land <c>0x020B9934</c>) before hashing, and every name the game composes
///     goes through a <c>.\%s</c>-shaped format string. That is a different algorithm
///     from the Neversoft CRC behind <c>QbKey</c>, so these pairs live
///     in their own resource and must never be merged into <c>QbKeyNames*.txt</c>:
///     they would not re-hash there and would poison those dictionaries' coverage.
///
///     The embedded map holds only PROVEN pairs — a candidate string harvested from
///     the carts' ARM9, overlays, and decompressed GOB content is accepted only when
///     it re-hashes to a file's own key. Coverage is deliberately partial (1,724 of
///     23,572 distinct keys): most files are named by the loader's
///     <c>.\%08x.&lt;kind&gt;.bin</c> templates, whose 8 hex digits span exactly the
///     32-bit CRC space, so no preimage search can distinguish a real name from a
///     coincidence. See <c>docs/formats/ds-gob-gfc.md</c> for that measurement.
/// </summary>
public static class GobNames
{
    private static readonly FrozenDictionary<uint, string> Names = Load();

    /// <summary>Number of proven name/key pairs in the embedded map.</summary>
    public static int Count => Names.Count;

    /// <summary>
    ///     The name as it is hashed (leading <c>.\</c> included), or null if unknown.
    /// </summary>
    public static string? TryResolve(uint nameCrc)
    {
        return Names.TryGetValue(nameCrc, out var name) ? name : null;
    }

    /// <summary>The container's key for a name: CRC-32 of its lowercased spelling.</summary>
    public static uint Hash(string name)
    {
        return Crc32.HashToUInt32(Encoding.UTF8.GetBytes(name.ToLowerInvariant()));
    }

    /// <summary>
    ///     Relative path to extract a name to: the <c>.\</c> prefix dropped and
    ///     backslashes turned into forward slashes.
    /// </summary>
    public static string ToRelativePath(string name)
    {
        var path = name;
        if (path.StartsWith(".\\", StringComparison.Ordinal) || path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..];
        return path.Replace('\\', '/').TrimStart('/');
    }

    private static FrozenDictionary<uint, string> Load()
    {
        var dict = new Dictionary<uint, string>();
        using var stream = typeof(GobNames).Assembly.GetManifestResourceStream("GobNames.txt");
        if (stream == null)
            return dict.ToFrozenDictionary();

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // Format: name=0xHASH. Split on the LAST '=' — a harvested name may
            // legitimately contain one.
            var separator = line.LastIndexOf('=');
            if (separator < 1 || separator + 3 >= line.Length)
                continue;
            if (uint.TryParse(line.AsSpan(separator + 3), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out var hash))
            {
                dict.TryAdd(hash, line[..separator]);
            }
        }

        return dict.ToFrozenDictionary();
    }
}
