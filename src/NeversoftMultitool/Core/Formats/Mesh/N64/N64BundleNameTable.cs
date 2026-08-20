using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     The model-slot name array the ports keep in <c>boot.bin</c>: a
///     contiguous stride-8 run of <c>{char* "&lt;name&gt;.psx", u32 slotIndex}</c>.
///     <para>
///         This is the only source that STATES a slot's name rather than
///         inferring it. The trigger alignment infers from a level's contiguous
///         slot run, and content identity infers from mesh-name hashes — which
///         structurally cannot separate characters built on a shared rig, so
///         distinct slots collapse onto one name (THPS1 33/34 both read
///         <c>glif</c>; Spider-Man 106/107/221/222 all read <c>lizman</c>). The
///         array separates them because the engine has to.
///     </para>
///     <para>
///         It is SELF-LOCATING. Scanning for runs of pairs whose pointer
///         resolves to a printable <c>*.psx</c> string finds EXACTLY ONE run in
///         each of the four shipped ROMs — not the longest of several, the only
///         one — so no per-game offset is stored. Anything other than exactly
///         one run means the shape is not what this reader thinks it is, and it
///         declines rather than picking.
///     </para>
///     <para>
///         A slot named more than once is DECLINED, not resolved. Four THPS2
///         slots carry two names each (108 <c>skny_o</c>/<c>skny_o2</c>, and
///         138/139/140 as <c>bkfact</c>/<c>bkfact_2</c> pairs): those are the
///         one- and two-player bank pairs, so the slot really does hold
///         different content depending on which boot script ran and there is no
///         single correct name. The other sources name those slots instead.
///     </para>
/// </summary>
internal static class N64BundleNameTable
{
    private const int EntryStride = 8;

    /// <summary>
    ///     A run has to be long enough that it cannot be a coincidence of
    ///     neighbouring words. The real arrays are 80-298 entries; the measured
    ///     runner-up in every ROM is zero.
    /// </summary>
    private const int MinimumRunLength = 16;

    /// <summary>
    ///     Slot indices are small table positions. The largest authored slot in
    ///     the corpus is 263 (Spider-Man's <c>sym_dark</c>), and THPS3's sparse
    ///     numbering reaches 196, so the bound only has to exclude values that
    ///     are obviously not indices.
    /// </summary>
    private const uint MaximumSlotIndex = 4096;

    private const string NameSuffix = ".psx";

    internal readonly record struct Entry(string Name, int Slot);

    /// <summary>
    ///     Reads the array, returning slot → name for slots the array names
    ///     UNIQUELY. Empty when the boot image carries no single unambiguous
    ///     run. Names keep their authored case and lose the <c>.psx</c>
    ///     extension, matching how the trigger route already spells them
    ///     (<c>SkDown_O</c>, not <c>skdown_o</c>).
    /// </summary>
    internal static IReadOnlyDictionary<int, string> Resolve(N64BootImage? boot)
    {
        var entries = ReadEntries(boot);
        if (entries.Count == 0)
            return new Dictionary<int, string>();

        var bySlot = new Dictionary<int, string?>();
        foreach (var entry in entries)
        {
            var stem = entry.Name[..^NameSuffix.Length];
            if (!bySlot.TryGetValue(entry.Slot, out var existing))
            {
                bySlot[entry.Slot] = stem;
                continue;
            }

            // Null marks a slot already known to be ambiguous, so a third
            // spelling cannot resurrect it.
            if (existing != null && !string.Equals(existing, stem, StringComparison.OrdinalIgnoreCase))
                bySlot[entry.Slot] = null;
        }

        var resolved = new Dictionary<int, string>(bySlot.Count);
        foreach (var (slot, name) in bySlot)
        {
            if (name != null)
                resolved[slot] = name;
        }

        return resolved;
    }

    /// <summary>
    ///     The array's entries in stored order, or empty when the image does
    ///     not contain exactly one qualifying run.
    /// </summary>
    internal static IReadOnlyList<Entry> ReadEntries(N64BootImage? boot)
    {
        if (boot == null)
            return [];

        IReadOnlyList<Entry>? found = null;
        var offset = 0;
        while (offset + EntryStride <= boot.Length)
        {
            var run = ReadRun(boot, offset);
            if (run.Count < MinimumRunLength)
            {
                offset += 4;
                continue;
            }

            // A second qualifying run would mean this reader cannot tell which
            // table the engine indexes. Decline both.
            if (found != null)
                return [];

            found = run;
            offset += run.Count * EntryStride;
        }

        return found ?? [];
    }

    /// <summary>Reads the maximal run of valid pairs starting at an offset.</summary>
    private static List<Entry> ReadRun(N64BootImage boot, int start)
    {
        var entries = new List<Entry>();
        for (var offset = start; offset + EntryStride <= boot.Length; offset += EntryStride)
        {
            var pointer = boot.ReadUInt32(offset);
            var slot = boot.ReadUInt32(offset + 4);
            if (slot > MaximumSlotIndex)
                break;

            var name = boot.TryReadCString(pointer);
            if (name == null || !name.EndsWith(NameSuffix, StringComparison.OrdinalIgnoreCase))
                break;

            entries.Add(new Entry(name, (int)slot));
        }

        return entries;
    }
}
