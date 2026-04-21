namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     VLC (variable-length code) decoders for Factor 5 M4Decoder bitstream.
///     Tables extracted from THAW GC main.dol data sections. Each packed uint32
///     entry: bits_to_consume = entry >> 17, decoded_value = entry &amp; 0x1FFFF.
///     Ported from FUN_8029CDA0 (selector), FUN_8029CEE0/CE58 (raw-code A/B),
///     FUN_8029CFA4+CC50 (motion vector delta).
/// </summary>
internal static class Vid1VlcDecoder
{
    // 6-bit peek → selector value (64 entries, DOL 0x8031A504)
    private static readonly uint[] SelectorTable =
    [
        0xFFFFFFFF, 0xFFFFFFFF, 0x000C0006, 0x000C0009, 0x000A0008, 0x000A0008, 0x000A0004, 0x000A0004,
        0x000A0002, 0x000A0002, 0x000A0001, 0x000A0001, 0x00080000, 0x00080000, 0x00080000, 0x00080000,
        0x0008000C, 0x0008000C, 0x0008000C, 0x0008000C, 0x0008000A, 0x0008000A, 0x0008000A, 0x0008000A,
        0x0008000E, 0x0008000E, 0x0008000E, 0x0008000E, 0x00080005, 0x00080005, 0x00080005, 0x00080005,
        0x0008000D, 0x0008000D, 0x0008000D, 0x0008000D, 0x00080003, 0x00080003, 0x00080003, 0x00080003,
        0x0008000B, 0x0008000B, 0x0008000B, 0x0008000B, 0x00080007, 0x00080007, 0x00080007, 0x00080007,
        0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F,
        0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F, 0x0004000F,
    ];

    // 9-bit peek >> 3 → raw-code A (64 entries, DOL 0x8031A000)
    private static readonly uint[] RawCodeTableA =
    [
        0xFFFFFFFF, 0x000C0014, 0x000C0024, 0x000C0034, 0x00080004, 0x00080004, 0x00080004, 0x00080004,
        0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013, 0x00060013,
        0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023, 0x00060023,
        0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033, 0x00060033,
        0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
        0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
        0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
        0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003, 0x00020003,
    ];

    // 9-bit peek → raw-code B (257 entries, DOL 0x8031A100)
    private static readonly uint[] RawCodeTableB =
    [
        0xFFFFFFFF, 0x001200FF, 0x00120034, 0x00120024, 0x00120014, 0x00120031, 0x00100023, 0x00100023,
        0x00100013, 0x00100013, 0x00100032, 0x00100032, 0x000E0033, 0x000E0033, 0x000E0033, 0x000E0033,
        0x000E0022, 0x000E0022, 0x000E0022, 0x000E0022, 0x000E0012, 0x000E0012, 0x000E0012, 0x000E0012,
        0x000E0021, 0x000E0021, 0x000E0021, 0x000E0021, 0x000E0011, 0x000E0011, 0x000E0011, 0x000E0011,
        0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004,
        0x000C0030, 0x000C0030, 0x000C0030, 0x000C0030, 0x000C0030, 0x000C0030, 0x000C0030, 0x000C0030,
        0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003,
        0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003, 0x000A0003,
        0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020,
        0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020,
        0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020,
        0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020, 0x00080020,
        0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010,
        0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010,
        0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010,
        0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010, 0x00080010,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002, 0x00060002,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001, 0x00060001,
        0x00020000,
    ];

    // MV VLC tables (DOL 0x803264B8..0x8032685F): three ranges of a 12-bit peek
    // decoded_value for MV uses 17-bit signed (two's complement in lower 17 bits)
    private static readonly uint[] MvTableLarge =
    [
        0x00080003, 0x0009FFFD, 0x00060002, 0x00060002, 0x0007FFFE, 0x0007FFFE, 0x00040001, 0x00040001,
        0x00040001, 0x00040001, 0x0005FFFF, 0x0005FFFF, 0x0005FFFF, 0x0005FFFF,
    ];

    private static readonly uint[] MvTableMedium =
    [
        0x0014000C, 0x0015FFF4, 0x0014000B, 0x0015FFF5, 0x0012000A, 0x0012000A, 0x0013FFF6, 0x0013FFF6,
        0x00120009, 0x00120009, 0x0013FFF7, 0x0013FFF7, 0x00120008, 0x00120008, 0x0013FFF8, 0x0013FFF8,
        0x000E0007, 0x000E0007, 0x000E0007, 0x000E0007, 0x000E0007, 0x000E0007, 0x000E0007, 0x000E0007,
        0x000FFFF9, 0x000FFFF9, 0x000FFFF9, 0x000FFFF9, 0x000FFFF9, 0x000FFFF9, 0x000FFFF9, 0x000FFFF9,
        0x000E0006, 0x000E0006, 0x000E0006, 0x000E0006, 0x000E0006, 0x000E0006, 0x000E0006, 0x000E0006,
        0x000FFFFA, 0x000FFFFA, 0x000FFFFA, 0x000FFFFA, 0x000FFFFA, 0x000FFFFA, 0x000FFFFA, 0x000FFFFA,
        0x000E0005, 0x000E0005, 0x000E0005, 0x000E0005, 0x000E0005, 0x000E0005, 0x000E0005, 0x000E0005,
        0x000FFFFB, 0x000FFFFB, 0x000FFFFB, 0x000FFFFB, 0x000FFFFB, 0x000FFFFB, 0x000FFFFB, 0x000FFFFB,
        0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004,
        0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004, 0x000C0004,
        0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC,
        0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC, 0x000DFFFC,
    ];

    private static readonly uint[] MvTableSmall =
    [
        0x00180020, 0x0019FFE0, 0x0018001F, 0x0019FFE1, 0x0016001E, 0x0016001E, 0x0017FFE2, 0x0017FFE2,
        0x0016001D, 0x0016001D, 0x0017FFE3, 0x0017FFE3, 0x0016001C, 0x0016001C, 0x0017FFE4, 0x0017FFE4,
        0x0016001B, 0x0016001B, 0x0017FFE5, 0x0017FFE5, 0x0016001A, 0x0016001A, 0x0017FFE6, 0x0017FFE6,
        0x00160019, 0x00160019, 0x0017FFE7, 0x0017FFE7, 0x00140018, 0x00140018, 0x00140018, 0x00140018,
        0x0015FFE8, 0x0015FFE8, 0x0015FFE8, 0x0015FFE8, 0x00140017, 0x00140017, 0x00140017, 0x00140017,
        0x0015FFE9, 0x0015FFE9, 0x0015FFE9, 0x0015FFE9, 0x00140016, 0x00140016, 0x00140016, 0x00140016,
        0x0015FFEA, 0x0015FFEA, 0x0015FFEA, 0x0015FFEA, 0x00140015, 0x00140015, 0x00140015, 0x00140015,
        0x0015FFEB, 0x0015FFEB, 0x0015FFEB, 0x0015FFEB, 0x00140014, 0x00140014, 0x00140014, 0x00140014,
        0x0015FFEC, 0x0015FFEC, 0x0015FFEC, 0x0015FFEC, 0x00140013, 0x00140013, 0x00140013, 0x00140013,
        0x0015FFED, 0x0015FFED, 0x0015FFED, 0x0015FFED, 0x00140012, 0x00140012, 0x00140012, 0x00140012,
        0x0015FFEE, 0x0015FFEE, 0x0015FFEE, 0x0015FFEE, 0x00140011, 0x00140011, 0x00140011, 0x00140011,
        0x0015FFEF, 0x0015FFEF, 0x0015FFEF, 0x0015FFEF, 0x00140010, 0x00140010, 0x00140010, 0x00140010,
        0x0015FFF0, 0x0015FFF0, 0x0015FFF0, 0x0015FFF0, 0x0014000F, 0x0014000F, 0x0014000F, 0x0014000F,
        0x0015FFF1, 0x0015FFF1, 0x0015FFF1, 0x0015FFF1, 0x0014000E, 0x0014000E, 0x0014000E, 0x0014000E,
        0x0015FFF2, 0x0015FFF2, 0x0015FFF2, 0x0015FFF2, 0x0014000D, 0x0014000D, 0x0014000D, 0x0014000D,
        0x0015FFF3, 0x0015FFF3, 0x0015FFF3, 0x0015FFF3,
    ];

    private static (int bitsToConsume, int decodedValue) Unpack(uint entry)
    {
        return ((int)(entry >> 17), (int)(entry & 0x1FFFF));
    }

    private static int SignExtend17(int value)
    {
        return (value & 0x10000) != 0 ? value | unchecked((int)0xFFFE0000) : value;
    }

    /// <summary>
    ///     Decode a selector value (FUN_8029CDA0). Peeks 6 bits, looks up in
    ///     SelectorTable, returns value optionally inverted (0xF - value).
    /// </summary>
    public static int DecodeSelector(Vid1BitReader reader, bool invert)
    {
        var peek = reader.PeekBits(6);
        var entry = SelectorTable[peek];
        if (entry == 0xFFFFFFFF)
            throw new InvalidDataException($"selector VLC sentinel at peek=0x{peek:X2}");
        var (bits, value) = Unpack(entry);
        reader.SkipBits(bits);
        return invert ? value : 0xF - value;
    }

    /// <summary>
    ///     Decode a raw-code via table A (FUN_8029CEE0). Peek-9 loop skipping
    ///     value=1 sentinels, then table lookup.
    /// </summary>
    public static int DecodeRawCodeA(Vid1BitReader reader)
    {
        int peek;
        while ((peek = reader.PeekBits(9)) == 1)
            reader.SkipBits(9);

        var index = (peek >> 1) & 0x7FFFFFFC;
        var entry = RawCodeTableA[index / 4];
        if (entry == 0xFFFFFFFF)
            throw new InvalidDataException($"raw-code A VLC sentinel at peek=0x{peek:X3}");
        var (bits, value) = Unpack(entry);
        reader.SkipBits(bits);
        return value;
    }

    /// <summary>
    ///     Decode a raw-code via table B (FUN_8029CE58). Double-peek variant
    ///     with 257-entry table, skipping value=1 sentinels.
    /// </summary>
    public static int DecodeRawCodeB(Vid1BitReader reader)
    {
        int tableIndex;
        while (true)
        {
            var peek1 = reader.PeekBits(9);
            tableIndex = peek1 < 0x101
                ? reader.PeekBits(9)
                : 0x100;
            if (tableIndex != 1) break;
            reader.SkipBits(9);
        }

        var entry = RawCodeTableB[tableIndex];
        if (entry == 0xFFFFFFFF)
            throw new InvalidDataException($"raw-code B VLC sentinel at tableIndex=0x{tableIndex:X3}");
        var (bits, value) = Unpack(entry);
        reader.SkipBits(bits);
        return value;
    }

    /// <summary>
    ///     Decode a motion vector magnitude (FUN_8029CC50 inner). Read 1 flag
    ///     bit; if set, MV=0. Otherwise peek 12 bits and dispatch to three
    ///     range-based tables.
    /// </summary>
    public static int DecodeMvMagnitude(Vid1BitReader reader)
    {
        if (reader.ReadBits(1) != 0)
            return 0;

        var peek = reader.PeekBits(12);
        uint entry;
        if (peek < 0x80)
        {
            var index = peek - 4;
            if ((uint)index >= (uint)MvTableSmall.Length)
                throw new InvalidDataException($"MV magnitude VLC sentinel at peek=0x{peek:X3}");
            entry = MvTableSmall[index];
        }
        else if (peek < 0x200)
        {
            var index = (peek >> 2) - 0x20;
            if ((uint)index >= (uint)MvTableMedium.Length)
                throw new InvalidDataException($"MV magnitude VLC sentinel at peek=0x{peek:X3}");
            entry = MvTableMedium[index];
        }
        else
        {
            var index = (peek >> 8) - 2;
            if ((uint)index >= (uint)MvTableLarge.Length)
                throw new InvalidDataException($"MV magnitude VLC sentinel at peek=0x{peek:X3}");
            entry = MvTableLarge[index];
        }

        if (entry == 0xFFFFFFFF)
            throw new InvalidDataException($"MV magnitude VLC sentinel at peek=0x{peek:X3}");
        var (bits, value) = Unpack(entry);
        reader.SkipBits(bits);
        return SignExtend17(value);
    }

    /// <summary>
    ///     Decode a full motion vector delta with f-code residual bits
    ///     (FUN_8029CFA4). Returns the final signed MV delta.
    /// </summary>
    public static int DecodeMvDelta(Vid1BitReader reader, int fCode)
    {
        var halfRange = 1 << (fCode - 1);
        var magnitude = DecodeMvMagnitude(reader);

        if (halfRange == 1 || magnitude == 0)
            return magnitude;

        var isNegative = magnitude < 0;
        var absMag = isNegative ? -magnitude : magnitude;
        var residualBits = reader.ReadBits(fCode - 1);
        var result = (absMag - 1) * halfRange + residualBits + 1;

        return isNegative ? -result : result;
    }

    /// <summary>
    ///     Sign-extend a value read from the bitstream (FUN_8029CE08).
    ///     If MSB of the <paramref name="bitCount"/>-bit value is 0, negate.
    /// </summary>
    public static int SignExtendValue(Vid1BitReader reader, int bitCount)
    {
        var value = reader.ReadBits(bitCount);
        if ((value >> (bitCount - 1)) == 0)
            value = -(value ^ ((1 << bitCount) - 1));
        return value;
    }
}
