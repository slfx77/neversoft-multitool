using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Names carved model slots, preferring what the ROM itself says.
///     <para>
///         TWO SOURCES, and they are complementary rather than redundant.
///     </para>
///     <para>
///         PRIMARY — the TRIGGERS. A TRG script spells the files it loads as
///         literal strings (<see cref="N64TrgFileReferences" />), and a level's
///         files occupy ONE CONTIGUOUS RUN of model slots ordered
///         case-insensitively by filename. So the trigger's family set lines up
///         with the run one-for-one:
///         <code>
///     TRG 001 sorted:  skdown  skdown_2  skdown_h  skdown_l  skdown_o  skdownl2
///     slots 4..9:      skdown  skdown_2  skdown_h  (stub)    skdown_o  (stub)
///         </code>
///         This is the only source that can name a <c>_l</c> texture library:
///         those carve as 24-byte stubs with no content to key on, so content
///         identity is structurally blind to them. 268 of 594 slots resolve this
///         way, 100 of them beyond the dictionary's reach.
///     </para>
///     <para>
///         FALLBACK — <see cref="N64BundleNames" />, content identity against
///         the PS1 corpus. Most characters, props and vehicles do not belong to
///         a level-family run, so this covers what the primary alignment cannot.
///     </para>
///     <para>
///         The alignment is ANCHORED on a content-named slot rather than on
///         position: it needs one confirmed correspondence, then contiguity and
///         the shared ordering carry the rest — including the stubs, which could
///         never be anchored directly. Every content-named slot inside a
///         candidate run must agree, so a coincidental single match cannot
///         mis-name a whole run.
///     </para>
///     <para>
///         A final fail-closed pass can resolve a content ambiguity when a TRG
///         names one candidate as an outsider, every other occurrence of that
///         content key already has an authoritative family/content name, and no
///         other unresolved key competes for the literal. This proves two more
///         Spider-Man slots (<c>Jameson</c> and <c>DEM4_G</c>) without guessing
///         between the still-ambiguous Mysterio/firering pairs.
///     </para>
/// </summary>
internal static class N64BundleNameResolver
{
    /// <summary>One carved bundle: its slot directory name and its shell bytes.</summary>
    internal readonly record struct Bundle(string Slot, byte[] Shell);

    internal readonly record struct ContentIdentity(ulong Key, IReadOnlyList<string> Candidates);

    /// <summary>
    ///     Slot → name for every slot either source can name. Slots absent from
    ///     the result keep their bare number.
    /// </summary>
    internal static Dictionary<string, string> Resolve(
        IReadOnlyList<Bundle> bundles,
        IReadOnlyList<byte[]> triggers)
    {
        // Content names first: they are the anchors the run alignment needs, and
        // the fallback for everything no trigger mentions.
        var content = new string?[bundles.Count];
        var identities = new ContentIdentity?[bundles.Count];
        for (var i = 0; i < bundles.Count; i++)
        {
            if (N64BundleNames.TryReadShellIdentity(bundles[i].Shell) is not { } identity)
                continue;

            identities[i] = new ContentIdentity(identity.Key, identity.Candidates);
            content[i] = N64BundleNames.TryResolve(identity.Key);
        }

        var fileSets = triggers
            .Select(TryReadFileSet)
            .OfType<N64TrgFileReferences.FileSet>()
            .ToArray();

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var set in fileSets)
        {
            if (!set.HasFamily)
                continue;

            var assignment = TryAlign(bundles, content, set.Family);
            if (assignment == null)
                continue;

            foreach (var (slot, name) in assignment)
                resolved[slot] = name;
        }

        // Content fills in only where the triggers said nothing, so a ROM-stated
        // name always wins over an inferred one.
        for (var i = 0; i < bundles.Count; i++)
        {
            if (content[i] is { } name)
                resolved.TryAdd(bundles[i].Slot, name);
        }

        var currentNames = new string?[bundles.Count];
        for (var i = 0; i < bundles.Count; i++)
            resolved.TryGetValue(bundles[i].Slot, out currentNames[i]);

        var outsiderNames = fileSets
            .SelectMany(static set => set.Outsiders)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (index, name) in SelectUniqueOutsiderNames(identities, currentNames, outsiderNames))
            resolved.Add(bundles[index].Slot, name);

        return resolved;
    }

    /// <summary>
    ///     Selects only outsider literals which have a one-to-one remaining
    ///     content assignment. A shared rig or repeated unresolved shell stays
    ///     unnamed; so does a literal already consumed by another named slot.
    /// </summary>
    internal static Dictionary<int, string> SelectUniqueOutsiderNames(
        IReadOnlyList<ContentIdentity?> identities,
        IReadOnlyList<string?> currentNames,
        IReadOnlySet<string> outsiderNames)
    {
        if (identities.Count != currentNames.Count)
            throw new ArgumentException("Identity and current-name arrays must have equal length");

        var usedNames = currentNames
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unresolvedByKey = Enumerable.Range(0, identities.Count)
            .Where(i => identities[i] != null && currentNames[i] == null)
            .GroupBy(i => identities[i]!.Value.Key)
            .Where(static group => group.Count() == 1);

        var proposals = new List<(int Index, string Name)>();
        foreach (var group in unresolvedByKey)
        {
            var index = group.Single();
            var matches = identities[index]!.Value.Candidates
                .Where(name => outsiderNames.Contains(name) && !usedNames.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (matches.Length == 1)
                proposals.Add((index, matches[0]));
        }

        // A literal which still fits two different content keys does not say
        // which bundle the trigger meant. Retain neither assignment.
        return proposals
            .GroupBy(static proposal => proposal.Name, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .ToDictionary(static proposal => proposal.Index, static proposal => proposal.Name);
    }

    private static N64TrgFileReferences.FileSet? TryReadFileSet(byte[] trigger)
    {
        try
        {
            using var stream = new MemoryStream(trigger, false);
            using var reader = new BinaryReader(stream);
            return N64TrgFileReferences.Collect(TrgFile.Parse(reader));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException
                                       or ArgumentException or IndexOutOfRangeException)
        {
            // A trigger that will not parse simply contributes no names.
            return null;
        }
    }

    /// <summary>
    ///     Places a sorted family onto a contiguous slot run, anchored by a
    ///     content match. Returns null when no placement satisfies every
    ///     content-named slot it would cover.
    /// </summary>
    private static List<(string Slot, string Name)>? TryAlign(
        IReadOnlyList<Bundle> bundles,
        string?[] content,
        IReadOnlyList<string> family)
    {
        for (var anchorSlot = 0; anchorSlot < bundles.Count; anchorSlot++)
        {
            if (content[anchorSlot] is not { } anchorName)
                continue;

            for (var position = 0; position < family.Count; position++)
            {
                if (!string.Equals(family[position], anchorName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var start = anchorSlot - position;
                if (start < 0 || start + family.Count > bundles.Count)
                    continue;

                if (TryPlace(bundles, content, family, start) is { } placement)
                    return placement;
            }
        }

        return null;
    }

    private static List<(string Slot, string Name)>? TryPlace(
        IReadOnlyList<Bundle> bundles,
        string?[] content,
        IReadOnlyList<string> family,
        int start)
    {
        var placement = new List<(string Slot, string Name)>(family.Count);
        for (var offset = 0; offset < family.Count; offset++)
        {
            var index = start + offset;
            // A slot the dictionary names differently refutes this placement.
            if (content[index] is { } known
                && !string.Equals(known, family[offset], StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            placement.Add((bundles[index].Slot, family[offset]));
        }

        return placement;
    }

    /// <summary>
    ///     Whether a shell is an authored-empty stub. Exposed so the carver can
    ///     report stub slots without re-parsing.
    /// </summary>
    internal static bool IsStub(byte[] shell)
    {
        return PsxN64ShellFile.Parse(shell) == null;
    }
}
