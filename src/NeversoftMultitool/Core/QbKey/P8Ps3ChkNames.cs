using System.Collections.Frozen;
using System.Globalization;
using System.Reflection;

namespace NeversoftMultitool.Core.QbKey;

/// <summary>
///     Resolves Project 8 PS3's hash-named data files. The PS3 port keeps the
///     real DATA directory tree but names every file
///     <c>&lt;QbKey(lowercased filename)&gt;.CHK</c> — e.g.
///     <c>standardkeyq.bin.ps3</c> → <c>2B745D86.CHK</c>, with movies hashing
///     their Xbox logical <c>.xen</c> name and MEMCARD art its bare name. The
///     mapping is the re-hash-proven harvest in
///     <c>QbKeyNames.P8Ps3Disc.txt</c> (4,826 of 4,835 shipped stems; see the
///     csproj comment for sources and the 9-file residue). This dedicated
///     loader exists so the corpus rename is deterministic — the global
///     first-wins QbKey dictionary could prefer another resource's string for
///     a colliding key.
/// </summary>
public static class P8Ps3ChkNames
{
    private static readonly Lazy<FrozenDictionary<uint, string>> Names = new(Load);

    /// <summary>Number of proven pairs in the shipped resource.</summary>
    public static int Count => Names.Value.Count;

    /// <summary>Resolves a CHK stem hash to its proven lowercased filename.</summary>
    public static bool TryResolve(uint hash, out string name)
    {
        return Names.Value.TryGetValue(hash, out name!);
    }

    /// <summary>
    ///     Resolves a hash-named file like <c>2B745D86.CHK</c> to its proven
    ///     filename. Returns false for non-CHK names, non-hex stems, and
    ///     unharvested hashes (the caller keeps the original name).
    /// </summary>
    public static bool TryResolveChkFileName(string fileName, out string resolved)
    {
        resolved = "";
        if (!fileName.EndsWith(".CHK", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (stem.Length != 8 ||
            !uint.TryParse(stem, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
        {
            return false;
        }

        return TryResolve(hash, out resolved);
    }

    private static FrozenDictionary<uint, string> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("QbKeyNames.P8Ps3Disc.txt")
            ?? throw new InvalidOperationException("QbKeyNames.P8Ps3Disc.txt resource missing.");
        using var reader = new StreamReader(stream);

        var map = new Dictionary<uint, string>();
        while (reader.ReadLine() is { } line)
        {
            var eq = line.LastIndexOf("=0x", StringComparison.Ordinal);
            if (eq <= 0) continue;
            var name = line[..eq];
            if (uint.TryParse(line[(eq + 3)..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
                map[hash] = name;
        }

        return map.ToFrozenDictionary();
    }
}
