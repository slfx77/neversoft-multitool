using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The THPS2 GBA cart's <b>tricks.bin</b> — the same Neversoft trick-table
///     bytecode the PS1 discs ship (see <c>docs/formats/psx-tricks-bin.md</c>),
///     re-encoded by Vicarious Visions and embedded in the ROM. It is the game's
///     own trick table: a trick is a record run carrying a name (<c>0x0B</c> +
///     C string) and, in opcode <c>0x01</c>, the <b>animation clip index</b> the
///     engine plays for it — so it NAMES the otherwise-anonymous clips of
///     <see cref="GbaSkaterModel" />.
///
///     <para><b>Nothing here is a hardcoded address.</b> Three structures are
///     located by shape, each provably unique in the ROM:</para>
///     <list type="number">
///         <item>The <b>opcode-width table</b> comes from the ROM's own
///         <c>Trick_Skip</c> dispatcher — found as <c>ldr r0,[r0]; mov pc,r0</c>
///         preceded within 16 bytes by <c>cmp rX,#0x5A</c> (the case bound) —
///         by decoding each jump-table case body: <c>adds r2,#k</c> is a flat
///         width, <c>k==1</c> plus <c>mov r1,r2</c> is the C-string class,
///         <c>k==1</c> plus <c>ldrb r0,[r2]</c> is the counted class. Reading
///         the table rather than assuming the PS1 one matters: <b>13 widths
///         differ</b> (<c>0x01</c> is 2 bytes here, 3 on PS1), and a single wrong
///         width desynchronises the stream within a few records.</item>
///         <item>The <b>table base</b> is the literal-pool word that both
///         satisfies the header identity (8×s16, <c>[7]==0</c>, the other seven
///         distinct and in range, with sections 0 and 1 addressing 15 non-empty
///         8-byte record lists each) AND is actually loaded by code. The header
///         identity alone matches three places; requiring a <c>ldr [pc]</c> site
///         — how the game itself reaches the table — makes it unique.</item>
///         <item>The <b>extent</b> is derived, not guessed: the furthest
///         terminator reachable by walking every script the bounded record lists
///         (sections 0, 1, 4, 5, 6) name.</item>
///     </list>
///
///     <para>THPS2 GBA: 174 tricks, 146 distinct names, clip references bounded
///     by the 221-entry clip table. Later VV carts carry no such dispatcher, so
///     the locator declines them.</para>
/// </summary>
public static class GbaTricksFile
{
    private const uint RomBase = 0x08000000;
    private const int OpcodeBound = 0x5A;
    private const int RecordSize = 8;
    private const int MaxNameLength = 44;

    /// <summary>One trick: its name and the clip indices its script plays.</summary>
    public readonly record struct Trick(int Offset, string Name, IReadOnlyList<int> ClipIndices);

    /// <summary>A width class for one opcode — a flat byte count, a trailing C
    ///     string, or a counted list.</summary>
    private enum WidthKind
    {
        Flat,
        CString,
        Counted
    }

    /// <summary>
    ///     Reads the ROM's trick table, or null when this cart carries none
    ///     (every GBA Tony Hawk cart but THPS2).
    /// </summary>
    public static IReadOnlyList<Trick>? TryRead(ReadOnlySpan<byte> rom)
    {
        var widths = TryReadOpcodeWidths(rom);
        if (widths == null)
            return null;
        var tableBase = FindTableBase(rom);
        if (tableBase < 0)
            return null;
        var end = FindTableEnd(rom, tableBase, widths);
        if (end <= tableBase)
            return null;

        var tricks = new List<Trick>();
        var offset = tableBase;
        while (offset < end)
        {
            if (rom[offset] != 0x0B || !TryReadName(rom, offset, end, out var name, out var afterName))
            {
                offset++;
                continue;
            }

            tricks.Add(new Trick(offset, name, ReadClipIndices(rom, offset, end, widths)));
            offset = afterName;
        }

        return tricks.Count > 0 ? tricks : null;
    }

    /// <summary>
    ///     Clip index → trick name, for clips a single trick name owns. A clip
    ///     several tricks play (shared approach/recovery animations, and the real
    ///     skating identities — a backside boardslide IS a frontside lipslide, so
    ///     both names claim one clip) keeps its synthetic label rather than taking
    ///     an arbitrary owner's name. Null when the ROM carries no trick table.
    /// </summary>
    public static IReadOnlyDictionary<int, string>? TryBuildClipNames(
        ReadOnlySpan<byte> rom, int clipCount)
    {
        var tricks = TryRead(rom);
        if (tricks == null)
            return null;

        // A trick's OWN animation is the first clip its script plays; later
        // references are transitions into shared recoveries.
        var claimants = new Dictionary<int, HashSet<string>>();
        foreach (var trick in tricks)
        {
            if (trick.ClipIndices.Count == 0)
                continue;
            var clip = trick.ClipIndices[0];
            if (clip < 0 || clip >= clipCount)
                continue;
            if (!claimants.TryGetValue(clip, out var names))
                claimants[clip] = names = new HashSet<string>(StringComparer.Ordinal);
            names.Add(trick.Name);
        }

        var resolved = claimants
            .Where(pair => pair.Value.Count == 1)
            .ToDictionary(pair => pair.Key, pair => pair.Value.First());

        // The cart carries a second, UPPERCASE trick list whose entries play
        // DIFFERENT clips (KICKFLIP is clip 149, Kickflip is 20), so both are
        // legitimately named. Casing is the only thing telling them apart, and
        // consumers compare names case-insensitively — the GUI pane would drop
        // one of each pair and the exporter would suffix it "_2" as though it
        // were a duplicate. Naming the clip in both keeps them distinguishable
        // without claiming anything beyond which clip each one is.
        foreach (var collision in resolved
                     .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            foreach (var (clip, name) in collision.ToList())
                resolved[clip] = $"{name} ({clip})";
        }

        return resolved.Count > 0 ? resolved : null;
    }

    // The dispatcher's jump table states every opcode's width. Case bodies are
    // `adds r2,#k` then a branch; the two k==1 cases are distinguished by what
    // follows (mov r1,r2 = walk a C string; ldrb r0,[r2] = a counted list).
    private static (WidthKind Kind, int Width)[]? TryReadOpcodeWidths(ReadOnlySpan<byte> rom)
    {
        var dispatch = FindDispatch(rom);
        if (dispatch < 0)
            return null;

        var jumpTable = FindJumpTable(rom, dispatch);
        if (jumpTable < 0 || jumpTable + (OpcodeBound + 1) * 4 > rom.Length)
            return null;

        var widths = new (WidthKind, int)[OpcodeBound + 1];
        for (var opcode = 0; opcode <= OpcodeBound; opcode++)
        {
            var target = (int)(ReadU32(rom, jumpTable + opcode * 4) & ~1u) - (int)RomBase;
            if (target < 0 || target + 4 > rom.Length)
                return null;
            var head = ReadU16(rom, target);
            if ((head & 0xFF00) != 0x3200) // adds r2, #k
                return null;
            var k = head & 0xFF;
            var next = ReadU16(rom, target + 2);
            widths[opcode] = (k, next) switch
            {
                (1, 0x1C11) => (WidthKind.CString, 0),
                (1, 0x7810) => (WidthKind.Counted, 0),
                _ => (WidthKind.Flat, k)
            };
        }

        return widths;
    }

    private static int FindDispatch(ReadOnlySpan<byte> rom)
    {
        var found = -1;
        for (var offset = 0; offset + 4 <= rom.Length; offset += 2)
        {
            if (ReadU16(rom, offset) != 0x6800 || ReadU16(rom, offset + 2) != 0x4687)
                continue;

            // The case bound identifies THIS dispatcher among the ROM's others.
            var windowStart = Math.Max(0, offset - 16);
            var bounded = false;
            for (var probe = windowStart; probe + 2 <= offset; probe += 2)
            {
                var instruction = ReadU16(rom, probe);
                if ((instruction & 0xF800) == 0x2800 && (instruction & 0xFF) == OpcodeBound)
                {
                    bounded = true;
                    break;
                }
            }

            if (!bounded)
                continue;
            if (found >= 0)
                return -1; // ambiguous: decline rather than guess
            found = offset;
        }

        return found;
    }

    // The jump table's address is the last pc-relative load before the dispatch.
    private static int FindJumpTable(ReadOnlySpan<byte> rom, int dispatch)
    {
        var pool = -1;
        for (var offset = Math.Max(0, dispatch - 8); offset + 2 <= dispatch; offset += 2)
        {
            var instruction = ReadU16(rom, offset);
            if ((instruction & 0xF800) == 0x4800) // ldr rX, [pc, #imm]
                pool = (int)(((RomBase + (uint)offset + 4) & ~3u) + (uint)(instruction & 0xFF) * 4 - RomBase);
        }

        if (pool < 0 || pool + 4 > rom.Length)
            return -1;
        return (int)(ReadU32(rom, pool) - RomBase);
    }

    private static int FindTableBase(ReadOnlySpan<byte> rom)
    {
        var found = -1;
        for (var offset = 0; offset + 4 <= rom.Length; offset += 4)
        {
            var value = ReadU32(rom, offset);
            if (value < RomBase || value >= RomBase + (uint)rom.Length)
                continue;
            var candidate = (int)(value - RomBase);
            if (!HasHeaderIdentity(rom, candidate) || !HasLoadSite(rom, offset))
                continue;
            if (found >= 0 && found != candidate)
                return -1; // ambiguous: decline rather than guess
            found = candidate;
        }

        return found;
    }

    private static bool HasHeaderIdentity(ReadOnlySpan<byte> rom, int at)
    {
        if (at < 0 || at + 16 > rom.Length)
            return false;
        var sections = new short[8];
        for (var i = 0; i < 8; i++)
            sections[i] = ReadS16(rom, at + i * 2);
        if (sections[7] != 0)
            return false;
        if (sections.Take(7).Distinct().Count() != 7)
            return false;
        if (sections.Take(7).Any(v => v <= 0 || v >= 0x7FF0))
            return false;

        // Sections 0 and 1 are per-skater: 15 entries each, every one addressing
        // a non-empty record list.
        foreach (var section in (int[])[0, 1])
        {
            for (var skater = 0; skater < 15; skater++)
            {
                var entryAt = at + sections[section] + skater * 2;
                if (entryAt + 2 > rom.Length)
                    return false;
                var value = ReadS16(rom, entryAt);
                if (value <= 0 || value >= 0x7FF0 || CountRecords(rom, at + value) == 0)
                    return false;
            }
        }

        return true;
    }

    private static int CountRecords(ReadOnlySpan<byte> rom, int at)
    {
        var count = 0;
        while (at >= 0 && at + RecordSize <= rom.Length && rom[at] != 0 && count < 2000)
        {
            count++;
            at += RecordSize;
        }

        return count;
    }

    private static bool HasLoadSite(ReadOnlySpan<byte> rom, int poolOffset)
    {
        var target = RomBase + (uint)poolOffset;
        var from = Math.Max(0, poolOffset - 4 - 1020) & ~1;
        for (var offset = from; offset + 2 <= poolOffset; offset += 2)
        {
            var instruction = ReadU16(rom, offset);
            if ((instruction & 0xF800) != 0x4800)
                continue;
            if (((RomBase + (uint)offset + 4) & ~3u) + (uint)(instruction & 0xFF) * 4 == target)
                return true;
        }

        return false;
    }

    /// <summary>
    ///     The blob's end: the furthest terminator reachable from the scripts the
    ///     bounded record lists name. Derived rather than assumed — legitimate
    ///     gaps inside the blob reach two kilobytes, so no gap heuristic works.
    /// </summary>
    private static int FindTableEnd(
        ReadOnlySpan<byte> rom, int tableBase, (WidthKind Kind, int Width)[] widths)
    {
        var sections = new short[8];
        for (var i = 0; i < 8; i++)
            sections[i] = ReadS16(rom, tableBase + i * 2);

        var roots = new HashSet<int>();
        foreach (var section in (int[])[0, 1])
            for (var skater = 0; skater < 15; skater++)
            {
                var listAt = tableBase + ReadS16(rom, tableBase + sections[section] + skater * 2);
                AddScriptRoots(rom, tableBase, listAt, roots);
            }

        foreach (var section in (int[])[4, 5, 6])
            AddScriptRoots(rom, tableBase, tableBase + sections[section], roots);

        var end = 0;
        foreach (var root in roots)
        {
            var offset = root;
            for (var step = 0; step < 8192 && offset >= 0 && offset < rom.Length; step++)
            {
                if (rom[offset] == 0x07)
                {
                    end = Math.Max(end, offset + 1);
                    break;
                }

                var next = Skip(rom, offset, widths);
                if (next <= offset)
                    break;
                offset = next;
            }
        }

        return end;
    }

    private static void AddScriptRoots(
        ReadOnlySpan<byte> rom, int tableBase, int listAt, HashSet<int> roots)
    {
        var count = 0;
        while (listAt >= 0 && listAt + RecordSize <= rom.Length && rom[listAt] != 0 && count < 2000)
        {
            roots.Add(tableBase + ReadS16(rom, listAt + 4));
            listAt += RecordSize;
            count++;
        }
    }

    private static bool TryReadName(
        ReadOnlySpan<byte> rom, int at, int end, out string name, out int afterName)
    {
        name = "";
        afterName = at + 1;
        var start = at + 1;
        var stop = start;
        while (stop < end && rom[stop] >= 0x20 && rom[stop] <= 0x7E && stop - start <= MaxNameLength)
            stop++;
        if (stop - start < 2 || stop >= end || rom[stop] != 0)
            return false;

        name = Encoding.ASCII.GetString(rom[start..stop]);
        afterName = stop + 1;
        return true;
    }

    private static List<int> ReadClipIndices(
        ReadOnlySpan<byte> rom, int at, int end, (WidthKind Kind, int Width)[] widths)
    {
        var clips = new List<int>();
        var offset = at;
        while (offset < end)
        {
            var opcode = rom[offset];
            if (opcode == 0x07)
                break;
            if (opcode == 0x0B && offset != at)
                break;
            if (opcode == 0x01 && offset + 1 < end)
                clips.Add(rom[offset + 1]);

            var next = Skip(rom, offset, widths);
            if (next <= offset)
                break;
            offset = next;
        }

        return clips;
    }

    private static int Skip(
        ReadOnlySpan<byte> rom, int at, (WidthKind Kind, int Width)[] widths)
    {
        var opcode = rom[at];
        // The dispatcher's default for an out-of-range opcode is a single byte.
        if (opcode > OpcodeBound)
            return at + 1;

        var (kind, width) = widths[opcode];
        switch (kind)
        {
            case WidthKind.CString:
                var zero = rom[(at + 1)..].IndexOf((byte)0);
                return zero < 0 ? -1 : at + 1 + zero + 1;
            case WidthKind.Counted:
                return at + 1 < rom.Length ? at + 2 + 2 * rom[at + 1] : -1;
            default:
                return at + width;
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> rom, int at) =>
        BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(at, 2));

    private static short ReadS16(ReadOnlySpan<byte> rom, int at) =>
        BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(at, 2));

    private static uint ReadU32(ReadOnlySpan<byte> rom, int at) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(at, 4));
}
