using System.IO.Hashing;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Recovers the authored name of every DS model set from the cart's own code.
///
///     A model set is keyed by <c>idA</c>, and that id is simply <c>CRC32</c> of the
///     set's authored name — the same case-sensitive CRC the container uses for
///     filenames, without the <c>.\</c> prefix or any extension. So a name is not
///     searched for or inferred: an ASCII string lying in ARM9 or an overlay is
///     tested by re-hash, and it either IS the set's name or it is not.
///
///     That makes the recovery complete and self-proving. Across the three carts it
///     names <b>196 of 196</b>, <b>124 of 124</b> and <b>160 of 160</b> model sets,
///     no id draws two different candidate strings, and impossible control names
///     score zero. The result is corroborated from an entirely separate source: each
///     <c>Level_&lt;Name&gt;_Visual</c> set pairs one-for-one with a
///     <c>&lt;Name&gt;_Collision.prp</c> file in the container (8/8, 7/7, 8/8, counting
///     the front end),
///     and those two facts share no machinery.
///
///     What comes back is the studio's own vocabulary: the levels
///     (<c>Level_Alcatraz_Visual</c>, <c>Level_Rio_Visual</c>) and their skies, the
///     front end, and the gameplay entities — <c>skate_s</c> … <c>skate_e</c>,
///     <c>videotape</c>, twelve <c>*_orb</c> trick pickups, the pedestrians and the
///     pro skaters. An entity type is a one-piece set whose idA and idB are equal.
///
///     Only reachable with the CART open; a bare extracted <c>.gob</c> carries no
///     code, so callers must treat every name as optional.
/// </summary>
public static class NdsSetNames
{
    /// <summary>The suffix a level's own model set carries.</summary>
    public const string LevelSuffix = "_Visual";

    /// <summary>A level's sky is a separate set spelled with this instead.</summary>
    public const string SkySuffix = "_Sky_Visual";

    private const int MinimumLength = 3;
    private const int MaximumLength = 63;

    /// <summary>
    ///     Names as many of <paramref name="setIds" /> as the code spells.
    /// </summary>
    /// <param name="regions">ARM9 and the ARM9 overlays, in any order.</param>
    /// <param name="setIds">The container's own model-set ids.</param>
    public static IReadOnlyDictionary<uint, string> Harvest(
        IReadOnlyList<(string Name, uint VirtualBase, byte[] Data)> regions,
        IReadOnlyCollection<uint> setIds)
    {
        ArgumentNullException.ThrowIfNull(regions);
        ArgumentNullException.ThrowIfNull(setIds);
        if (setIds.Count == 0)
            return new Dictionary<uint, string>();

        var wanted = setIds as HashSet<uint> ?? [.. setIds];
        var found = new Dictionary<uint, string>();
        var ambiguous = new HashSet<uint>();

        foreach (var (_, _, data) in regions)
        {
            foreach (var candidate in AsciiRuns(data))
            {
                var hash = Hash(candidate);
                if (!wanted.Contains(hash))
                    continue;
                if (found.TryGetValue(hash, out var existing))
                {
                    // Two different strings hashing to one set id would mean the
                    // name is a guess. It never happens in the shipped carts, and
                    // if it ever did the honest answer is no name at all.
                    if (!string.Equals(existing, candidate, StringComparison.Ordinal))
                        ambiguous.Add(hash);
                    continue;
                }

                found[hash] = candidate;
            }
        }

        foreach (var id in ambiguous)
            found.Remove(id);
        return found;
    }

    /// <summary>
    ///     True when <paramref name="name" /> is a level's own model set — the set
    ///     carrying its world geometry, as opposed to the separate one-to-four-piece
    ///     set holding its sky. The front end counts: <c>Frontend_Visual</c> has world
    ///     geometry and its own <c>Frontend_Collision.prp</c> like any other level.
    /// </summary>
    public static bool IsLevel(string? name)
    {
        return name != null
               && name.EndsWith(LevelSuffix, StringComparison.Ordinal)
               && !name.EndsWith(SkySuffix, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The name to SHOW a set under, and to export it as.
    ///
    ///     <c>_Visual</c> is the exporter's tag marking which of a level's sets holds
    ///     its drawable geometry — the level itself is <c>Level_Alcatraz</c>, and its
    ///     collision file is spelled that way too. The authored name keeps the suffix
    ///     and everything that MATCHES on it still uses the authored form:
    ///     <see cref="IsLevel" /> and the <c>.prp</c> pairing both read the real name.
    /// </summary>
    public static string DisplayName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(LevelSuffix, StringComparison.Ordinal)
            ? name[..^LevelSuffix.Length]
            : name;
    }

    /// <summary>
    ///     A set name reduced to a filesystem-safe export stem. Names are already
    ///     plain identifiers in every shipped cart; this only guards the general case.
    /// </summary>
    public static string ToStem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var stem = new StringBuilder(name.Length);
        foreach (var c in name)
            stem.Append(char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return stem.ToString();
    }

    /// <summary>
    ///     Every printable-ASCII run in the image that could be a name. Names are
    ///     plain identifiers, so the alphabet is deliberately narrow — it keeps the
    ///     candidate count down without ever excluding a real one (all 480 names
    ///     recovered across the three carts match it).
    /// </summary>
    private static IEnumerable<string> AsciiRuns(byte[] data)
    {
        var start = -1;
        for (var i = 0; i <= data.Length; i++)
        {
            var ok = i < data.Length && IsNameByte(data[i]);
            if (ok)
            {
                if (start < 0)
                    start = i;
                continue;
            }

            if (start >= 0)
            {
                var length = i - start;
                // A run longer than a name can still CONTAIN one, but only at its
                // start: the hash covers the whole string, so a substring elsewhere
                // would need its own run, which the byte scan already produces
                // wherever a NUL or punctuation separates it.
                if (length is >= MinimumLength and <= MaximumLength
                    && char.IsAsciiLetter((char)data[start]))
                {
                    yield return Encoding.ASCII.GetString(data, start, length);
                }

                start = -1;
            }
        }
    }

    /// <summary>
    ///     CRC-32 of the name exactly as authored — the id its model set carries. This deliberately does NOT
    ///     lowercase, unlike <see cref="Gob.GobNames.Hash" />: the container keys a
    ///     FILENAME by its lowercased spelling, but a model-set id is the hash of the
    ///     name's own casing. Measured — case-sensitive names 116 of the 116 entity
    ///     types, lowercased only the 100 that were already lowercase.
    /// </summary>
    public static uint Hash(string name)
    {
        return Crc32.HashToUInt32(Encoding.ASCII.GetBytes(name));
    }

    private static bool IsNameByte(byte b)
    {
        return char.IsAsciiLetterOrDigit((char)b) || b is (byte)'_' or (byte)'-' or (byte)'.' or (byte)' ';
    }
}
