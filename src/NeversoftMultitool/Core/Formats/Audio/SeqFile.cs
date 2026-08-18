using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     PSY-Q SEQ music sequence (<c>pQES</c> magic) — the PlayStation SDK's
///     single-track MIDI variant, played by <c>SsSeqPlay</c> through a VAB
///     sound bank. Apocalypse is the only PS1-era Neversoft title using it
///     (11 files, each with a same-stem <c>.vab</c> sibling); the later games
///     moved to streamed XA/STR music.
/// </summary>
/// <remarks>
///     Layout: big-endian header — magic (4), version u32 (=1), resolution
///     u16 (ticks per quarter note), initial tempo u24 (µs per quarter),
///     time signature 2 bytes (numerator, denominator as a power of two) —
///     then a standard MIDI event stream with variable-length delta times and
///     running status, terminated by the End-of-Track meta (FF 2F 00).
/// </remarks>
public sealed class SeqFile
{
    public required int Resolution { get; init; }
    public required int InitialTempoMicroseconds { get; init; }
    public required byte TimeSignatureNumerator { get; init; }
    public required byte TimeSignatureDenominatorPower { get; init; }
    public required IReadOnlyList<SeqEvent> Events { get; init; }

    /// <summary>Total length in ticks (the last event's absolute time).</summary>
    public long TotalTicks => Events.Count == 0 ? 0 : Events[^1].Tick;

    public static bool IsSeq(ReadOnlySpan<byte> data)
    {
        return data.Length >= 15
               && data[0] == (byte)'p' && data[1] == (byte)'Q'
               && data[2] == (byte)'E' && data[3] == (byte)'S'
               && BinaryPrimitives.ReadUInt32BigEndian(data[4..]) == 1;
    }

    public static SeqFile? Parse(ReadOnlySpan<byte> data)
    {
        if (!IsSeq(data))
            return null;

        var resolution = BinaryPrimitives.ReadUInt16BigEndian(data[8..]);
        var tempo = (data[10] << 16) | (data[11] << 8) | data[12];
        var numerator = data[13];
        var denominatorPower = data[14];
        if (resolution == 0 || tempo == 0)
            return null;

        var events = new List<SeqEvent>();
        var offset = 15;
        long tick = 0;
        byte runningStatus = 0;

        while (offset < data.Length)
        {
            if (!TryReadVariableLength(data, ref offset, out var delta))
                return null;
            tick += delta;

            if (offset >= data.Length)
                return null;

            var status = data[offset];
            if (status >= 0x80)
            {
                offset++;
            }
            else
            {
                // Running status: reuse the previous channel-voice status.
                if (runningStatus < 0x80)
                    return null;
                status = runningStatus;
            }

            if (status == 0xFF)
            {
                if (offset >= data.Length)
                    return null;
                var metaType = data[offset++];
                if (metaType == 0x2F)
                {
                    events.Add(new SeqEvent(tick, SeqEventType.EndOfTrack, 0, 0, 0));
                    break;
                }

                if (!TryReadVariableLength(data, ref offset, out var length)
                    || offset + length > data.Length)
                {
                    return null;
                }

                if (metaType == 0x51 && length == 3)
                {
                    var microseconds =
                        (data[offset] << 16) | (data[offset + 1] << 8) | data[offset + 2];
                    events.Add(new SeqEvent(
                        tick, SeqEventType.Tempo, 0, 0, microseconds));
                }

                offset += (int)length;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                if (!TryReadVariableLength(data, ref offset, out var length)
                    || offset + length > data.Length)
                {
                    return null;
                }

                offset += (int)length;
                continue;
            }

            runningStatus = status;
            var channel = (byte)(status & 0x0F);
            var kind = status & 0xF0;
            var dataBytes = kind is 0xC0 or 0xD0 ? 1 : 2;
            if (offset + dataBytes > data.Length)
                return null;

            var d0 = data[offset];
            var d1 = dataBytes == 2 ? data[offset + 1] : (byte)0;
            offset += dataBytes;

            switch (kind)
            {
                case 0x90 when d1 > 0:
                    events.Add(new SeqEvent(tick, SeqEventType.NoteOn, channel, d0, d1));
                    break;
                case 0x90:
                case 0x80:
                    events.Add(new SeqEvent(tick, SeqEventType.NoteOff, channel, d0, d1));
                    break;
                case 0xC0:
                    events.Add(new SeqEvent(tick, SeqEventType.ProgramChange, channel, d0, 0));
                    break;
                case 0xB0:
                    events.Add(new SeqEvent(tick, SeqEventType.Control, channel, d0, d1));
                    break;
                case 0xE0:
                    events.Add(new SeqEvent(
                        tick, SeqEventType.PitchBend, channel, 0, (d1 << 7) | d0));
                    break;
                // Aftertouch (0xA0/0xD0) has no audible role in this renderer.
            }
        }

        return new SeqFile
        {
            Resolution = resolution,
            InitialTempoMicroseconds = tempo,
            TimeSignatureNumerator = numerator,
            TimeSignatureDenominatorPower = denominatorPower,
            Events = events
        };
    }

    private static bool TryReadVariableLength(
        ReadOnlySpan<byte> data, ref int offset, out uint value)
    {
        value = 0;
        for (var i = 0; i < 4; i++)
        {
            if (offset >= data.Length)
                return false;
            var b = data[offset++];
            value = (value << 7) | (uint)(b & 0x7F);
            if ((b & 0x80) == 0)
                return true;
        }

        return false;
    }
}

public enum SeqEventType
{
    NoteOn,
    NoteOff,
    ProgramChange,
    Control,
    PitchBend,
    Tempo,
    EndOfTrack
}

/// <summary>
///     One sequenced event at an absolute tick. Value carries the second data
///     byte (velocity/controller value), the 14-bit pitch-bend value, or the
///     tempo in microseconds per quarter note.
/// </summary>
public readonly record struct SeqEvent(
    long Tick,
    SeqEventType Type,
    byte Channel,
    byte Data,
    int Value);
