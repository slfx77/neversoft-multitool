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
