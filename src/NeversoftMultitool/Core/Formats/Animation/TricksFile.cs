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

    public required IReadOnlyList<TrickEntry> Tricks { get; init; }

    public required TricksDialect Dialect { get; init; }

    /// <summary>
    ///     Parses <c>tricks.bin</c>, or returns null when the bytes hold no
    ///     recognisable trick stream in either dialect.
    /// </summary>
    public static TricksFile? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 16) return null;

        // Pick the dialect by which alignment the name records actually occur
        // at. The two are cleanly separated in every shipped file: a retail
        // build yields zero halfword-aligned names and a prototype zero
        // byte-aligned ones, so this never has to arbitrate a close call.
        var byteAnchors = FindNameAnchors(data, TricksDialect.ByteOpcode);
        var halfAnchors = FindNameAnchors(data, TricksDialect.HalfwordOpcode);
        if (byteAnchors.Count == 0 && halfAnchors.Count == 0) return null;

        var dialect = byteAnchors.Count >= halfAnchors.Count
            ? TricksDialect.ByteOpcode
            : TricksDialect.HalfwordOpcode;
        var anchors = dialect == TricksDialect.ByteOpcode ? byteAnchors : halfAnchors;

        var tricks = new List<TrickEntry>(anchors.Count);
        foreach (var (offset, name) in anchors)
            tricks.Add(ReadTrick(data, offset, name, dialect));

        return new TricksFile { Tricks = tricks, Dialect = dialect };
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
        ReadOnlySpan<byte> data, int start, string name, TricksDialect dialect)
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
                ids.Add(BinaryPrimitives.ReadInt16LittleEndian(data[operandAt..]));
            }

            var next = Skip(data, offset, dialect);
            if (next <= offset || next > data.Length) break;
            offset = next;
        }

        return new TrickEntry(name, ids, terminated);
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
    private static int Skip(ReadOnlySpan<byte> data, int offset, TricksDialect dialect)
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
                var count = BinaryPrimitives.ReadInt16LittleEndian(data[countAt..]);
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
