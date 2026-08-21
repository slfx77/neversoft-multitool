using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     <c>tricks.bin</c> — the bytecode trick table the PS1-era skating games
///     ship alongside the skater animation bank.
///     <para>
///         Each trick is a run of opcode records introduced by a name record
///         (<c>0x0B</c>, opcode + C string) and closed by <c>0x07</c>. Record
///         <c>0x01</c> carries an animation slot index, which
///         <c>ExtraAnims_AddTrick</c> appends onto the skater's animation list
///         before <c>Spool_StripModel</c> filters the bank down to the slots
///         that list names. That makes this file the only place the otherwise
///         anonymous PSX animation slots are given human names.
///     </para>
///     <para>
///         <b>Two dialects.</b> The 2000-3-29 THPS2 prototype stores opcode and
///         operands as halfwords, so records are 2-byte aligned; every shipped
///         retail build re-encoded the opcode to a single byte while leaving
///         operands 16-bit and UNALIGNED — retail's operand reader is an
///         explicit <c>lbu</c>/<c>lbu</c>/shift pair rather than an <c>lh</c>
///         precisely because a record may start at an odd offset. The dialect is
///         detected from the file, not from the build.
///     </para>
/// </summary>
public sealed class TricksFile
{
    /// <summary>Opcode that introduces a trick and carries its name.</summary>
    private const int NameOpcode = 0x0B;

    /// <summary>Opcode whose operand is an animation slot index.</summary>
    private const int AnimationOpcode = 0x01;

    /// <summary>Opcode that closes a trick's record run.</summary>
    private const int TerminatorOpcode = 0x07;

    /// <summary>Longest trick name accepted when locating a name record.</summary>
    private const int MaxNameLength = 40;

    /// <summary>Shortest, so stray punctuation in operand data cannot anchor.</summary>
    private const int MinNameLength = 2;

    /// <summary>Runaway guard; the longest real trick is far shorter.</summary>
    private const int MaxRecordsPerTrick = 4096;

    /// <summary>
    ///     Widest slot index treated as plausible when choosing a byte order.
    ///     Real banks hold 147-235 slots; the bound is deliberately loose because
    ///     it only has to separate real indices from byte-swapped noise, and the
    ///     wrong order produces values spanning the whole s16 range.
    /// </summary>
    private const int PlausibleSlotBound = 512;

    public required IReadOnlyList<TrickEntry> Tricks { get; init; }

    public required TricksDialect Dialect { get; init; }

    /// <summary>
    ///     Whether operands are stored big-endian. True for the N64 ports, which
    ///     keep the byte opcode and the whole record grammar but store their
    ///     16-bit operands in the console's native order.
    /// </summary>
    public required bool BigEndianOperands { get; init; }

    /// <summary>
    ///     Parses <c>tricks.bin</c>, or returns null when the bytes hold no
    ///     recognisable trick stream.
    ///     <para>
    ///         Opcode width and operand byte order are both read off the file
    ///         rather than assumed from the build, and both are decided by
    ///         scoring real parses. Byte order is not a cosmetic choice: the
    ///         N64 THPS2 table and its PS1 sibling hold the same 202 tricks, and
    ///         each parses cleanly in exactly one order and into whole-range
    ///         garbage in the other.
    ///     </para>
    /// </summary>
    public static TricksFile? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;

        TricksFile? best = null;
        var bestScore = (Plausible: -1, Terminated: -1);
        foreach (var dialect in new[] { TricksDialect.ByteOpcode, TricksDialect.HalfwordOpcode })
        {
            // The halfword reading finds no anchors in a byte-opcoded file and
            // vice versa, so a wrong dialect scores zero rather than competing.
            var anchors = FindNameAnchors(data, dialect);
            if (anchors.Count == 0) continue;

            foreach (var bigEndian in new[] { false, true })
            {
                var tricks = new List<TrickEntry>(anchors.Count);
                foreach (var (offset, name) in anchors)
                    tricks.Add(ReadTrick(data, offset, name, dialect, bigEndian));

                if (!IsCredible(tricks)) continue;

                var score = Score(tricks);
                if (score.CompareTo(bestScore) <= 0) continue;

                bestScore = score;
                best = new TricksFile
                {
                    Tricks = tricks,
                    Dialect = dialect,
                    BigEndianOperands = bigEndian,
                };
            }
        }

        return best;
    }

    /// <summary>
    ///     How well a candidate reading explains the file: how many slot indices
    ///     land in a plausible range, then how many tricks reached a terminator.
    ///     <para>
    ///         Slot plausibility leads because its margin is enormous — the
    ///         wrong byte order spreads indices across the entire s16 range —
    ///         whereas termination separates the orders by only a trick or two,
    ///         which is too thin to arbitrate on alone.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     Whether a candidate reading is a trick table at all, rather than
    ///     arbitrary bytes that happen to contain <c>0x0B</c> followed by ASCII.
    ///     <para>
    ///         The gate is that EVERY trick reaches its terminator. That is not
    ///         arbitrary strictness — it is measured. All eight shipped tables
    ///         terminate 100% of their tricks in the correct reading, a real
    ///         table read in the WRONG byte order drops to 201 of 202, and the
    ///         one false positive in an N64 carve (a 430 KB render bank that
    ///         yields 193 plausible-looking "tricks") terminates only 153 of
    ///         them. Requiring completeness rejects junk and sharpens byte-order
    ///         selection at the same time, and errs toward naming nothing rather
    ///         than naming animations wrongly.
    ///     </para>
    /// </summary>
    private static bool IsCredible(List<TrickEntry> tricks)
    {
        if (tricks.Count < MinimumTricks) return false;

        var names = new HashSet<string>(StringComparer.Ordinal);
        var carryingSlots = 0;
        foreach (var trick in tricks)
        {
            if (!trick.Terminated) return false;
            names.Add(trick.Name);
            foreach (var slot in trick.AnimationIds)
            {
                if (slot is < 0 or >= PlausibleSlotBound) continue;
                carryingSlots++;
                break;
            }
        }

        // A trick table references animations and names its tricks distinctly.
        // Both ratios sit at 0.82-0.90 in all eight shipped tables and collapse
        // in anything else, so half is a wide margin rather than a tuned value.
        return carryingSlots * 2 >= tricks.Count && names.Count * 2 >= tricks.Count;
    }

    /// <summary>
    ///     Fewest tricks a credible table may hold. The smallest shipped one
    ///     carries 121, so this keeps a wide margin while excluding incidental
    ///     runs in unrelated data — an N64 render bank yields eight
    ///     "tricks" all named <c>aaaaaaaa</c>, which clears every other gate.
    /// </summary>
    private const int MinimumTricks = 32;

    private static (int Plausible, int Terminated) Score(List<TrickEntry> tricks)
    {
        var plausible = 0;
        var terminated = 0;
        foreach (var trick in tricks)
        {
            if (trick.Terminated) terminated++;
            foreach (var slot in trick.AnimationIds)
            {
                if (slot is >= 0 and < PlausibleSlotBound) plausible++;
            }
        }

        return (plausible, terminated);
    }

    /// <summary>
    ///     Walks one trick from its name record to its terminator, collecting
    ///     every animation slot the run references.
    ///     <para>
    ///         Whether the walk reached a terminator is reported rather than
    ///         assumed: it is the check that the opcode table is right. A table
    ///         with even one wrong width desynchronises within a few records and
    ///         runs on past the end of the trick, so "every trick terminated" is
    ///         a real verification rather than a formality.
    ///     </para>
    /// </summary>
    private static TrickEntry ReadTrick(
        ReadOnlySpan<byte> data, int start, string name, TricksDialect dialect, bool bigEndian)
    {
        var ids = new List<int>();
        var offset = start;
        var terminated = false;
        for (var step = 0; step < MaxRecordsPerTrick && offset < data.Length; step++)
        {
            var opcode = ReadOpcode(data, offset, dialect);
            if (opcode == TerminatorOpcode && offset != start)
            {
                terminated = true;
                break;
            }

            if (opcode == AnimationOpcode)
            {
                var operandAt = offset + OpcodeWidth(dialect);
                if (operandAt + 2 > data.Length) break;
                ids.Add(ReadOperand(data, operandAt, bigEndian));
            }

            var next = Skip(data, offset, dialect, bigEndian);
            if (next <= offset || next > data.Length) break;
            offset = next;
        }

        return new TrickEntry(name, ids, terminated);
    }

    private static int ReadOperand(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data[offset..])
            : BinaryPrimitives.ReadInt16LittleEndian(data[offset..]);
    }

    /// <summary>
    ///     Every position holding a name record. Anchoring on content rather
    ///     than walking from a header keeps the reader independent of the
    ///     section table, which differs between builds (the prototype ships
    ///     three section pointers, retail seven).
    /// </summary>
    private static List<(int Offset, string Name)> FindNameAnchors(
        ReadOnlySpan<byte> data, TricksDialect dialect)
    {
        var width = OpcodeWidth(dialect);
        var anchors = new List<(int, string)>();
        for (var offset = 0; offset + width + MinNameLength < data.Length; offset += width)
        {
            if (ReadOpcode(data, offset, dialect) != NameOpcode) continue;

            var start = offset + width;
            var end = start;
            while (end < data.Length && data[end] is >= 0x20 and <= 0x7E
                   && end - start <= MaxNameLength)
                end++;

            if (end - start < MinNameLength || end >= data.Length || data[end] != 0)
                continue;

            anchors.Add((offset, System.Text.Encoding.ASCII.GetString(data[start..end])));
        }

        return anchors;
    }

    private static int OpcodeWidth(TricksDialect dialect)
    {
        return dialect == TricksDialect.ByteOpcode ? 1 : 2;
    }

    private static int ReadOpcode(ReadOnlySpan<byte> data, int offset, TricksDialect dialect)
    {
        if (dialect == TricksDialect.ByteOpcode) return data[offset];
        return offset + 2 <= data.Length
            ? BinaryPrimitives.ReadUInt16LittleEndian(data[offset..])
            : -1;
    }

    /// <summary>
    ///     Advances past one record — the engine's <c>Trick_Skip</c>.
    ///     <para>
    ///         The retail table is read straight out of the jump table that
    ///         function dispatches through, and is byte-for-byte identical in
    ///         THPS2 (<c>0x800AFA74</c>), THPS3 PS1 (<c>0x800B19C8</c>) and
    ///         THPS4 PS1 (<c>0x800B4288</c>) — same 0x5A bound, same nine case
    ///         classes, same per-opcode assignment. The prototype table comes
    ///         from the matched decomp's <c>Trick_Skip</c> (PHYSICS.cpp:4268)
    ///         and is the retail one plus one everywhere, the halfword opcode
    ///         accounting for the extra byte.
    ///     </para>
    /// </summary>
    private static int Skip(
        ReadOnlySpan<byte> data, int offset, TricksDialect dialect, bool bigEndian)
    {
        var opcode = ReadOpcode(data, offset, dialect);
        if (opcode < 0) return -1;
        var width = OpcodeWidth(dialect);

        var kind = dialect == TricksDialect.ByteOpcode
            ? RetailKind(opcode)
            : PrototypeKind(opcode);

        switch (kind)
        {
            case OpcodeKind.String:
                return SkipString(data, offset + width, width);
            case OpcodeKind.PayloadThenString:
                // Eight bytes of payload, then a C string.
                return SkipString(data, offset + width + 8, width);
            case OpcodeKind.CountedOperands:
            {
                var countAt = offset + width;
                if (countAt + 2 > data.Length) return -1;
                var count = ReadOperand(data, countAt, bigEndian);
                if (count < 0) return -1;
                return countAt + 2 + count * 2;
            }
            default:
                return offset + (int)kind;
        }
    }

    /// <summary>
    ///     Skips a NUL-terminated string, then re-aligns to the opcode width.
    ///     <para>
    ///         The alignment is load-bearing for the halfword dialect: the
    ///         prototype's <c>Trick_Skip</c> ends its name case with <c>&amp; ~1</c>,
    ///         because the next opcode is a halfword and a name of even length
    ///         would otherwise leave the stream on an odd offset. Retail's
    ///         byte opcodes need no padding, which is exactly why its records may
    ///         start at any offset.
    ///     </para>
    /// </summary>
    private static int SkipString(ReadOnlySpan<byte> data, int start, int alignment)
    {
        if (start >= data.Length) return -1;
        var terminator = data[start..].IndexOf((byte)0);
        if (terminator < 0) return -1;
        var end = start + terminator + 1;
        return alignment > 1 ? (end + alignment - 1) / alignment * alignment : end;
    }

    /// <summary>
    ///     Retail record widths, indexed by opcode. Opcodes at or above the
    ///     table's length take the dispatcher's default of one byte.
    /// </summary>
    private const string RetailKinds =
        "13111111133s133733311315331313131313333333371333111131357v311313333337155313413" +
        "9s323321132";

    private static OpcodeKind RetailKind(int opcode)
    {
        if (opcode < 0 || opcode >= RetailKinds.Length) return OpcodeKind.Size1;
        return RetailKinds[opcode] switch
        {
            '2' => OpcodeKind.Size2,
            '3' => OpcodeKind.Size3,
            '4' => OpcodeKind.Size4,
            '5' => OpcodeKind.Size5,
            '7' => OpcodeKind.Size7,
            '9' => OpcodeKind.PayloadThenString,
            's' => OpcodeKind.String,
            'v' => OpcodeKind.CountedOperands,
            _ => OpcodeKind.Size1,
        };
    }

    /// <summary>
    ///     Prototype record widths, from the matched decomp's own
    ///     <c>Trick_Skip</c>. Held separately rather than derived from
    ///     <see cref="RetailKinds" />: the halfword opcode does make every
    ///     shared width exactly one larger, but <c>0x17</c> genuinely changed
    ///     class between the builds (4 bytes here, 5 in retail), so deriving one
    ///     table from the other would mis-size it.
    /// </summary>
    private const string PrototypeKinds =
        "24222222244s244844422424442424242424444444482444222242468v42242444444826622222222" +
        "2222222222";

    private static OpcodeKind PrototypeKind(int opcode)
    {
        if (opcode < 0 || opcode >= PrototypeKinds.Length) return OpcodeKind.Size2;
        return PrototypeKinds[opcode] switch
        {
            '4' => OpcodeKind.Size4,
            '6' => OpcodeKind.Size6,
            '8' => OpcodeKind.Size8,
            's' => OpcodeKind.String,
            'v' => OpcodeKind.CountedOperands,
            _ => OpcodeKind.Size2,
        };
    }

    /// <summary>
    ///     A record's width, or the shape that determines it. Fixed widths use
    ///     their own byte count as the enum value so the common path is a cast.
    /// </summary>
    private enum OpcodeKind
    {
        Size1 = 1,
        Size2 = 2,
        Size3 = 3,
        Size4 = 4,
        Size5 = 5,
        Size6 = 6,
        Size7 = 7,
        Size8 = 8,
        String = -1,
        PayloadThenString = -2,
        CountedOperands = -3,
    }
}

/// <summary>One trick: its name and the animation slots its script plays.</summary>
/// <param name="Terminated">
///     Whether the record walk reached this trick's <c>0x07</c> terminator. False
///     means the opcode table desynchronised, which is how a mis-sized record
///     announces itself.
/// </param>
public readonly record struct TrickEntry(
    string Name, IReadOnlyList<int> AnimationIds, bool Terminated);

/// <summary>Which encoding a <c>tricks.bin</c> uses for opcodes.</summary>
public enum TricksDialect
{
    /// <summary>Retail: 1-byte opcode, unaligned 16-bit operands.</summary>
    ByteOpcode,

    /// <summary>2000-3-29 THPS2 prototype: 16-bit opcode, 2-byte aligned.</summary>
    HalfwordOpcode,
}
