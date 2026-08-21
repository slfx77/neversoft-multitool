using System.Buffers.Binary;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Decodes the <b>sequenced music</b> in a Shin'en GAX Sound Engine GBA ROM —
///     the song / order-list / pattern structure the game plays, as opposed to the
///     raw PCM wave set (<see cref="GbaGaxAudio" />). Verified against Tony Hawk's
///     Pro Skater 2 (GBA, GAX v1.99d): 11 songs, whose decoded note events form
///     coherent diatonic music.
///
///     <para><b>Locating songs.</b> A GAX song header is 20 bytes and its
///     <c>sample_address</c> (+0x10) points at the wave set's leading null
///     <c>{0,0}</c> record — the same bank <see cref="GbaGaxAudio" /> locates, minus
///     its 8-byte null entry. So every header is found by scanning for a 4-aligned
///     u32 equal to that base and validating the surrounding fields; no fixed song
///     table offset is assumed.</para>
///
///     <para><b>Order lists precede the header.</b> Each channel owns
///     <c>orderLength</c> 4-byte entries <c>{u16 patternOffset, s8 transpose, u8}</c>;
///     the whole <c>channelCount × orderLength × 4</c> block sits immediately before
///     the header (layout <c>[note pool][order lists][header]</c>). The per-channel
///     stride is therefore <c>orderLength × 4</c> — established structurally: at that
///     stride every pattern offset in all 11 songs lands inside the note pool, while
///     a fixed stride (which only coincides with song 0's 14-pattern order) sends
///     several songs' offsets out of bounds.</para>
///
///     <para><b>Pattern grammar</b> (from the ROM's note interpreter at 0x080362FC):
///     a pattern begins with a flag byte (nonzero ⇒ the channel is silent for that
///     pattern), then one command per row — <c>0xFF n</c> rests n rows; <c>0x80</c>
///     is an empty row; <c>0x80|k</c> (k=1..0x79) is a 2-byte <c>{note=k, param1}</c>;
///     <c>0x80|k</c> (k=0x7A..0x7E) is a 3-byte effect-only command; a byte below
///     0x80 is a 4-byte <c>{note, param1, effect, effParam}</c>. note 0 = continue,
///     1 = note-off, ≥2 = play; <c>param1</c> is the instrument index (0 = keep).</para>
///
///     This class is the faithful sequence decoder; timbre, tempo and the exact
///     instrument→sample binding are rendering concerns handled (approximately) by
///     <see cref="GaxRenderer" />.
/// </summary>
public static class GbaGaxMusic
{
    private const uint RomBase = 0x08000000;
    private const uint RomEnd = 0x0A000000;

    public readonly record struct GaxSongHeader(
        uint Address,
        int ChannelCount,
        int RowsPerPattern,
        int OrderLength,
        int LoopPoint,
        uint NotesAddress,
        uint InstrumentAddress);

    /// <summary>One decoded pattern row for a channel. Note: 0 = continue, 1 = off, ≥2 = play.</summary>
    public readonly record struct GaxNoteEvent(
        int Row, int Note, int Instrument, int Effect, int EffectParam, int Transpose);

    /// <summary>
    ///     The wave-set base address that song headers reference: the 8-byte null
    ///     record that precedes the <see cref="GbaGaxAudio" /> sample table.
    /// </summary>
    public static bool TryGetWaveBaseAddress(ReadOnlySpan<byte> rom, out uint waveBase)
    {
        waveBase = 0;
        if (!GbaGaxAudio.TryFindWaveSet(rom, out var tableOffset, out _))
            return false;
        if (tableOffset < 8)
            return false;
        // The header points at the leading {0,0} null entry, not the first record.
        for (var i = tableOffset - 8; i < tableOffset; i++)
            if (rom[i] != 0)
                return false;
        waveBase = RomBase + (uint)tableOffset - 8;
        return true;
    }

    /// <summary>Every 20-byte v1.99 song header in the ROM, in address order.</summary>
    public static List<GaxSongHeader> FindSongHeaders(ReadOnlySpan<byte> rom)
    {
        var headers = new List<GaxSongHeader>();
        if (!TryGetWaveBaseAddress(rom, out var waveBase))
            return headers;

        Span<byte> key = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(key, waveBase);

        // The header's sample_address sits at +0x10, so a match at file offset j
        // means the header begins at j - 0x10.
        for (var j = 0x10; j + 4 <= rom.Length; j += 4)
        {
            if (!rom.Slice(j, 4).SequenceEqual(key))
                continue;

            var addr = RomBase + (uint)(j - 0x10);
            var headerOffset = j - 0x10;
            var channelCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(headerOffset, 2));
            var rowsPerPattern = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(headerOffset + 2, 2));
            var orderLength = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(headerOffset + 4, 2));
            var loopPoint = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(headerOffset + 6, 2));
            var notesAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(headerOffset + 8, 4));
            var instrumentAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(headerOffset + 0x0C, 4));

            if (channelCount is < 1 or > 32 || orderLength < 1)
                continue;
            if (!IsRomPointer(notesAddress) || !IsRomPointer(instrumentAddress))
                continue;

            headers.Add(new GaxSongHeader(
                addr, channelCount, rowsPerPattern, orderLength, loopPoint, notesAddress, instrumentAddress));
        }

        headers.Sort((a, b) => a.Address.CompareTo(b.Address));
        return headers;
    }

    /// <summary>
    ///     Flattens one channel's order list into per-global-row note events and
    ///     reports the channel's total row count (order length × rows per pattern).
    /// </summary>
    public static List<GaxNoteEvent> DecodeChannel(
        ReadOnlySpan<byte> rom, GaxSongHeader header, int channel, out int totalRows)
    {
        var events = new List<GaxNoteEvent>();
        var stride = header.OrderLength * 4; // per-channel order-list stride
        var blockStart = (int)(header.Address - RomBase) - header.ChannelCount * stride;
        var orderBase = blockStart + channel * stride;
        var notesOffset = (int)(header.NotesAddress - RomBase);
        totalRows = header.OrderLength * header.RowsPerPattern;

        for (var k = 0; k < header.OrderLength; k++)
        {
            var entry = orderBase + k * 4;
            if (entry < 0 || entry + 4 > rom.Length)
                continue;
            var patternOffset = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(entry, 2));
            var transpose = (sbyte)rom[entry + 2];
            DecodePattern(rom, notesOffset, patternOffset, header.RowsPerPattern, k, transpose, events);
        }

        return events;
    }

    private static void DecodePattern(
        ReadOnlySpan<byte> rom, int notesOffset, int patternOffset, int rowsPerPattern,
        int orderIndex, int transpose, List<GaxNoteEvent> events)
    {
        var globalBase = orderIndex * rowsPerPattern;
        var p = notesOffset + patternOffset;
        if (p < 0 || p >= rom.Length)
            return;

        var flag = rom[p++];
        if (flag != 0) // silent pattern
            return;

        var row = 0;
        while (row < rowsPerPattern && p < rom.Length)
        {
            var cmd = rom[p];
            int note, param1 = 0, effect = 0, effectParam = 0;

            if (cmd == 0xFF)
            {
                if (p + 1 >= rom.Length)
                    break;
                row += rom[p + 1];
                p += 2;
                continue;
            }

            if ((cmd & 0x80) != 0)
            {
                var low = cmd & 0x7F;
                if (low == 0) // empty row
                {
                    p += 1;
                    row += 1;
                    continue;
                }

                if (low <= 0x79) // 2-byte: note + instrument
                {
                    if (p + 2 > rom.Length)
                        break;
                    note = low;
                    param1 = rom[p + 1];
                    p += 2;
                }
                else // 3-byte: effect-only command
                {
                    if (p + 3 > rom.Length)
                        break;
                    note = 0;
                    effect = rom[p + 1];
                    effectParam = rom[p + 2];
                    p += 3;
                }
            }
            else // 4-byte: note + instrument + effect
            {
                if (p + 4 > rom.Length)
                    break;
                note = cmd;
                param1 = rom[p + 1];
                effect = rom[p + 2];
                effectParam = rom[p + 3];
                p += 4;
            }

            events.Add(new GaxNoteEvent(globalBase + row, note, param1, effect, effectParam, transpose));
            row += 1;
        }
    }

    private static bool IsRomPointer(uint address) => address is >= RomBase and < RomEnd;

    /// <summary>The GAX engine banner, e.g. "GAX Sound Engine v1.99d (Mar 30 2001)".</summary>
    public static string? GetBanner(ReadOnlySpan<byte> rom) => GbaGaxAudio.GetVersionBanner(rom);
}
