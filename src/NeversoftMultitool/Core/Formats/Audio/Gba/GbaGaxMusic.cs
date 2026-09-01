using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     Decodes the <b>sequenced music</b> in a Shin'en GAX Sound Engine GBA ROM —
///     the song / order-list / pattern structure the game plays, as opposed to the
///     raw PCM wave set (<see cref="GbaGaxAudio" />). The three layouts used by the
///     Tony Hawk GBA line are supported: the compact GAX 1.x header, GAX 2.x's
///     handler graph, and GAX 3.x's 32-pointer channel table.
///
///     <para><b>Locating songs.</b> Headers are found structurally, never by a
///     game-specific address. GAX 1.x references the shared null-led sample table;
///     GAX 2.x is validated through its adjacent info handler and top-level handler
///     graph; GAX 3.x requires all active channel pointers and all 32 inactive slots
///     to agree with the declared channel count.</para>
///
///     <para><b>Order lists.</b> Each channel owns
///     <c>orderLength</c> 4-byte entries <c>{u16 patternOffset, s8 transpose, u8}</c>;
///     GAX 1.x stores the lists immediately before the header, GAX 2.x reaches them
///     through per-channel sound handlers, and GAX 3.x stores their addresses in the
///     header. The pattern entry and sequence grammar itself stays compatible.</para>
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
///     instrument→sample binding are rendering concerns handled by
///     <see cref="GaxRenderer" />.
/// </summary>
public static class GbaGaxMusic
{
    private const uint RomBase = 0x08000000;

    public enum GaxSongLayout
    {
        Version1,
        Version2,
        Version3
    }

    public readonly record struct GaxSongHeader(
        uint Address,
        int ChannelCount,
        int RowsPerPattern,
        int OrderLength,
        int LoopPoint,
        uint NotesAddress,
        uint InstrumentAddress,
        uint SampleAddress,
        int MasterVolume,
        int MixingRate,
        GaxSongLayout Layout,
        IReadOnlyList<uint> ChannelOrderAddresses);

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

    /// <summary>Every structurally valid GAX song header in the ROM, in address order.</summary>
    public static List<GaxSongHeader> FindSongHeaders(ReadOnlySpan<byte> rom)
    {
        return GbaGaxAudio.GetEngineMajorVersion(rom) switch
        {
            1 => FindVersion1SongHeaders(rom),
            2 => FindVersion2SongHeaders(rom),
            >= 3 => FindVersion3SongHeaders(rom),
            _ => []
        };
    }

    private static List<GaxSongHeader> FindVersion1SongHeaders(ReadOnlySpan<byte> rom)
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

            if (channelCount is < 1 or > 32 || rowsPerPattern < 1 || orderLength < 1 || loopPoint >= orderLength)
                continue;
            if (!IsRomPointer(notesAddress, rom.Length) || !IsRomPointer(instrumentAddress, rom.Length))
                continue;

            var orderAddresses = new uint[channelCount];
            var stride = orderLength * 4;
            var orderBlock = headerOffset - channelCount * stride;
            if (orderBlock < 0)
                continue;
            for (var channel = 0; channel < channelCount; channel++)
                orderAddresses[channel] = RomBase + (uint)(orderBlock + channel * stride);
            if (!ValidateOrderLists(rom, notesAddress, orderLength, orderAddresses))
                continue;

            headers.Add(new GaxSongHeader(
                addr,
                channelCount,
                rowsPerPattern,
                orderLength,
                loopPoint,
                notesAddress,
                instrumentAddress,
                waveBase,
                0x100,
                0,
                GaxSongLayout.Version1,
                orderAddresses));
        }

        headers.Sort((a, b) => a.Address.CompareTo(b.Address));
        return headers;
    }

    private static List<GaxSongHeader> FindVersion2SongHeaders(ReadOnlySpan<byte> rom)
    {
        const int infoSize = 0x1C;
        const int handlerSize = 0x1C;
        var headers = new List<GaxSongHeader>();

        for (var offset = 0; offset + infoSize + handlerSize <= rom.Length; offset += 4)
        {
            if (!TryReadCommonHeader(
                    rom,
                    offset,
                    out var channelCount,
                    out var rowsPerPattern,
                    out var orderLength,
                    out var loopPoint,
                    out var masterVolume,
                    out var notesAddress,
                    out var instrumentAddress,
                    out var sampleAddress))
                continue;

            var mixingRate = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 0x18, 2));
            if (!IsMixingRate(mixingRate) || rom[offset + 0x1A] > 16)
                continue;

            var infoAddress = RomBase + (uint)offset;
            var infoHandlerAddress = infoAddress + infoSize;
            if (ReadUInt32(rom, offset + infoSize + 0x18) != infoAddress)
                continue;

            var rootOffset = FindVersion2Root(rom, infoHandlerAddress, channelCount);
            if (rootOffset < 0)
                continue;

            var orderAddresses = new uint[channelCount];
            var handlersValid = true;
            for (var channel = 0; channel < channelCount; channel++)
            {
                var handlerAddress = ReadUInt32(rom, rootOffset + 0x10 + channel * 4);
                if (!TryAddressToOffset(handlerAddress, rom.Length, out var handlerOffset)
                    || handlerOffset + handlerSize > rom.Length)
                {
                    handlersValid = false;
                    break;
                }

                var orderAddress = ReadUInt32(rom, handlerOffset + 0x18);
                if (!IsRomPointer(orderAddress, rom.Length))
                {
                    handlersValid = false;
                    break;
                }

                orderAddresses[channel] = orderAddress;
            }

            if (!handlersValid || !ValidateOrderLists(rom, notesAddress, orderLength, orderAddresses))
                continue;

            headers.Add(new GaxSongHeader(
                infoAddress,
                channelCount,
                rowsPerPattern,
                orderLength,
                loopPoint,
                notesAddress,
                instrumentAddress,
                sampleAddress,
                masterVolume,
                mixingRate,
                GaxSongLayout.Version2,
                orderAddresses));
        }

        headers.Sort((a, b) => a.Address.CompareTo(b.Address));
        return headers;
    }

    private static List<GaxSongHeader> FindVersion3SongHeaders(ReadOnlySpan<byte> rom)
    {
        const int channelPointerCount = 32;
        const int headerSize = 0x20 + channelPointerCount * 4;
        var headers = new List<GaxSongHeader>();

        for (var offset = 0; offset + headerSize <= rom.Length; offset += 4)
        {
            if (!TryReadCommonHeader(
                    rom,
                    offset,
                    out var channelCount,
                    out var rowsPerPattern,
                    out var orderLength,
                    out var loopPoint,
                    out var masterVolume,
                    out var notesAddress,
                    out var instrumentAddress,
                    out var sampleAddress))
                continue;

            var mixingRate = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 0x18, 2));
            var fxMixingRate = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 0x1A, 2));
            if (!IsMixingRate(mixingRate)
                || fxMixingRate != 0 && !IsMixingRate(fxMixingRate)
                || rom[offset + 0x1C] > 16
                || rom[offset + 0x1D] != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 0x1E, 2)) != 0)
                continue;

            var orderAddresses = new uint[channelCount];
            var pointersValid = true;
            for (var channel = 0; channel < channelPointerCount; channel++)
            {
                var address = ReadUInt32(rom, offset + 0x20 + channel * 4);
                if (channel < channelCount)
                {
                    if (!IsRomPointer(address, rom.Length))
                    {
                        pointersValid = false;
                        break;
                    }

                    orderAddresses[channel] = address;
                }
                else if (address != 0)
                {
                    pointersValid = false;
                    break;
                }
            }

            if (!pointersValid || !ValidateOrderLists(rom, notesAddress, orderLength, orderAddresses))
                continue;

            headers.Add(new GaxSongHeader(
                RomBase + (uint)offset,
                channelCount,
                rowsPerPattern,
                orderLength,
                loopPoint,
                notesAddress,
                instrumentAddress,
                sampleAddress,
                masterVolume,
                mixingRate,
                GaxSongLayout.Version3,
                orderAddresses));
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
        totalRows = header.OrderLength * header.RowsPerPattern;
        if ((uint)channel >= (uint)header.ChannelCount)
            return events;

        var orderBase = header.Layout == GaxSongLayout.Version1
            ? (int)(header.Address - RomBase) - header.ChannelCount * header.OrderLength * 4
              + channel * header.OrderLength * 4
            : (int)(header.ChannelOrderAddresses[channel] - RomBase);
        var notesOffset = (int)(header.NotesAddress - RomBase);

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

    private static bool TryReadCommonHeader(
        ReadOnlySpan<byte> rom,
        int offset,
        out int channelCount,
        out int rowsPerPattern,
        out int orderLength,
        out int loopPoint,
        out int masterVolume,
        out uint notesAddress,
        out uint instrumentAddress,
        out uint sampleAddress)
    {
        channelCount = rowsPerPattern = orderLength = loopPoint = masterVolume = 0;
        notesAddress = instrumentAddress = sampleAddress = 0;
        if (offset < 0 || offset + 0x18 > rom.Length)
            return false;

        channelCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset, 2));
        rowsPerPattern = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 2, 2));
        orderLength = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 4, 2));
        loopPoint = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 6, 2));
        masterVolume = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 8, 2));
        var reserved = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 0x0A, 2));
        notesAddress = ReadUInt32(rom, offset + 0x0C);
        instrumentAddress = ReadUInt32(rom, offset + 0x10);
        sampleAddress = ReadUInt32(rom, offset + 0x14);

        return channelCount is >= 1 and <= 32
               && rowsPerPattern is >= 1 and < 0x200
               && orderLength is >= 1 and < 0x100
               && loopPoint < orderLength
               && masterVolume is >= 1 and <= 0x400
               && reserved == 0
               && IsRomPointer(notesAddress, rom.Length)
               && IsRomPointer(instrumentAddress, rom.Length)
               && IsRomPointer(sampleAddress, rom.Length);
    }

    private static int FindVersion2Root(ReadOnlySpan<byte> rom, uint infoHandlerAddress, int channelCount)
    {
        var rootOffset = -1;
        for (var referenceOffset = 8; referenceOffset + 4 <= rom.Length; referenceOffset += 4)
        {
            if (ReadUInt32(rom, referenceOffset) != infoHandlerAddress)
                continue;

            var candidate = referenceOffset - 8;
            if (ReadUInt32(rom, candidate) != (uint)(channelCount + 3)
                || ReadUInt32(rom, candidate + 8) != infoHandlerAddress
                || candidate + 0x10 + channelCount * 4 > rom.Length)
                continue;

            if (rootOffset >= 0)
                return -1; // ambiguous graph: do not guess
            rootOffset = candidate;
        }

        return rootOffset;
    }

    private static bool ValidateOrderLists(
        ReadOnlySpan<byte> rom,
        uint notesAddress,
        int orderLength,
        IReadOnlyList<uint> orderAddresses)
    {
        if (!TryAddressToOffset(notesAddress, rom.Length, out var notesOffset))
            return false;

        foreach (var orderAddress in orderAddresses)
        {
            if (!TryAddressToOffset(orderAddress, rom.Length, out var orderOffset)
                || orderOffset + orderLength * 4 > rom.Length)
                return false;

            for (var order = 0; order < orderLength; order++)
            {
                var entry = orderOffset + order * 4;
                var patternOffset = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(entry, 2));
                if (rom[entry + 3] != 0 || notesOffset + patternOffset >= rom.Length)
                    return false;
            }
        }

        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static bool TryAddressToOffset(uint address, int romLength, out int offset)
    {
        offset = 0;
        if (!IsRomPointer(address, romLength))
            return false;
        offset = (int)(address - RomBase);
        return true;
    }

    private static bool IsRomPointer(uint address, int romLength) =>
        address >= RomBase && (ulong)address < (ulong)RomBase + (uint)romLength;

    private static bool IsMixingRate(int rate) => rate is
        5735 or 9079 or 10513 or 11469 or 13380 or 15769 or 18158
        or 21025 or 26760 or 31537 or 36316 or 40138 or 42049;

    /// <summary>The GAX engine banner, e.g. "GAX Sound Engine v1.99d (Mar 30 2001)".</summary>
    public static string? GetBanner(ReadOnlySpan<byte> rom) => GbaGaxAudio.GetVersionBanner(rom);
}
