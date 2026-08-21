namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Turns a <see cref="TricksFile" /> into real names for the otherwise
///     anonymous slots of a PSX skater animation bank.
///     <para>
///         <b>Only uniquely-owned slots are named.</b> Trick scripts share their
///         approach and recovery animations heavily — in the 2000-3-29 THPS2
///         prototype slot 14 leads "Kissed the Rail" but is referenced by 28
///         different tricks — so naming a slot after the first trick that
///         mentions it would attach an arbitrary, usually wrong, label. A slot
///         reached from more than one trick keeps its synthetic
///         <c>anim_N</c> name.
///     </para>
/// </summary>
public static class TrickAnimationNames
{
    /// <summary>
    ///     Maps animation slot index to trick name for every slot exactly one
    ///     trick references.
    /// </summary>
    public static IReadOnlyDictionary<int, string> Build(TricksFile tricks)
    {
        var owners = new Dictionary<int, HashSet<string>>();
        foreach (var trick in tricks.Tricks)
        {
            if (string.IsNullOrWhiteSpace(trick.Name)) continue;
            foreach (var slot in trick.AnimationIds)
            {
                if (slot < 0) continue;
                if (!owners.TryGetValue(slot, out var set))
                    owners[slot] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(trick.Name);
            }
        }

        var named = new Dictionary<int, string>();
        foreach (var (slot, set) in owners)
        {
            if (set.Count != 1) continue;
            named[slot] = Clean(set.First());
        }

        return named;
    }

    /// <summary>
    ///     Builds the map only when the slot indices actually fit the bank, so a
    ///     tricks.bin paired with the wrong bank names nothing rather than
    ///     mislabelling it. Every shipped pairing satisfies this exactly: the
    ///     highest slot a trick references is the bank's last index (prototype
    ///     146/147, THPS2 217/218, THPS3 225/226, THPS4 234/235).
    /// </summary>
    public static IReadOnlyDictionary<int, string> BuildForBank(
        TricksFile tricks, int bankSlotCount)
    {
        var names = Build(tricks);
        if (names.Count == 0) return names;
        return names.Keys.Any(slot => slot >= bankSlotCount)
            ? new Dictionary<int, string>()
            : names;
    }

    /// <summary>
    ///     As <see cref="BuildForBank" />, but requires the bank to be EXACTLY
    ///     the one the table describes: its last index must be the highest slot
    ///     any trick references.
    ///     <para>
    ///         Needed where the table is paired with a bank by search rather
    ///         than by being its sibling on disc. "Every slot fits" is far too
    ///         weak then — a carved N64 cart holds shells with as many as 300
    ///         clips, and every one of those would swallow a 218-slot table's
    ///         names. The equality is not an extra assumption: it holds in all
    ///         four shipped pairings (147/218/226/235) and is pinned.
    ///     </para>
    /// </summary>
    public static IReadOnlyDictionary<int, string> BuildForExactBank(
        TricksFile tricks, int bankSlotCount)
    {
        var highest = tricks.Tricks
            .SelectMany(static trick => trick.AnimationIds)
            .DefaultIfEmpty(-1)
            .Max();

        return highest == bankSlotCount - 1
            ? BuildForBank(tricks, bankSlotCount)
            : new Dictionary<int, string>();
    }

    /// <summary>
    ///     Strips the braces retail wraps "special" trick names in, so a slot
    ///     reads <c>Christ Air</c> rather than <c>{Christ Air}</c>.
    /// </summary>
    private static string Clean(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length > 2 && trimmed[0] == '{' && trimmed[^1] == '}')
            trimmed = trimmed[1..^1].Trim();
        return trimmed;
    }
}
