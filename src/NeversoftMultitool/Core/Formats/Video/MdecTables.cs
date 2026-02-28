namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Static lookup tables and constants for MDEC video decoding:
///     quantization matrix, reverse zigzag, YCbCr coefficients, IDCT constants, and VLC table.
/// </summary>
internal static class MdecTables
{
    // ── YCbCr -> RGB fixed-point coefficients (16-bit fractional) ─────────

    public const long Cr_R = 91893; // ~1.402
    public const long Cb_G = 22525; // ~0.3437
    public const long Cr_G = 46812; // ~0.7143
    public const long Cb_B = 116224; // ~1.772

    // ── IDCT constants (simple_idct from FFmpeg, Loeffler algorithm) ──────

    public const int W1 = 22725, W2 = 21407, W3 = 19266, W4 = 16383;
    public const int W5 = 12873, W6 = 8867, W7 = 4520;
    public const int RowShift = 11, ColShift = 20;

    // ── VLC Lookup Table ──────────────────────────────────────────────────

    public const int VlcBits = 17;

    public const int VlcTableSize = 1 << VlcBits;
    // ── Quantization & Zigzag ─────────────────────────────────────────────

    // PSX default quantization matrix (MPEG-1 standard, from MdecInputStream.java)
    public static readonly int[] QuantizationMatrix =
    [
        2, 16, 19, 22, 26, 27, 29, 34,
        16, 16, 22, 24, 27, 29, 34, 37,
        19, 22, 26, 27, 29, 34, 34, 38,
        22, 22, 26, 27, 29, 34, 37, 40,
        22, 26, 27, 29, 32, 35, 40, 48,
        26, 27, 29, 32, 35, 40, 48, 58,
        26, 27, 29, 34, 38, 46, 56, 69,
        27, 29, 35, 38, 46, 56, 69, 83
    ];

    // Reverse zig-zag: maps linear position -> 8x8 matrix index
    public static readonly int[] ReverseZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    public static readonly VlcEntry[] VlcTable = BuildVlcTable();

    private static VlcEntry[] BuildVlcTable()
    {
        var table = new VlcEntry[VlcTableSize];

        // MPEG-1 Table B-14 entries: (code_string, run, level)
        // The 's' suffix means a sign bit follows
        ReadOnlySpan<(uint code, int bits, int run, int level)> entries =
        [
            (0b11, 2, 0, 1),
            (0b011, 3, 1, 1),
            (0b0100, 4, 0, 2),
            (0b0101, 4, 2, 1),
            (0b00101, 5, 0, 3),
            (0b00110, 5, 4, 1),
            (0b00111, 5, 3, 1),
            (0b000100, 6, 7, 1),
            (0b000101, 6, 6, 1),
            (0b000110, 6, 1, 2),
            (0b000111, 6, 5, 1),
            (0b0000100, 7, 2, 2),
            (0b0000101, 7, 9, 1),
            (0b0000110, 7, 0, 4),
            (0b0000111, 7, 8, 1),
            (0b00100000, 8, 13, 1),
            (0b00100001, 8, 0, 6),
            (0b00100010, 8, 12, 1),
            (0b00100011, 8, 11, 1),
            (0b00100100, 8, 3, 2),
            (0b00100101, 8, 1, 3),
            (0b00100110, 8, 0, 5),
            (0b00100111, 8, 10, 1),
            (0b0000001000, 10, 16, 1),
            (0b0000001001, 10, 5, 2),
            (0b0000001010, 10, 0, 7),
            (0b0000001011, 10, 2, 3),
            (0b0000001100, 10, 1, 4),
            (0b0000001101, 10, 15, 1),
            (0b0000001110, 10, 14, 1),
            (0b0000001111, 10, 4, 2),
            (0b000000010000, 12, 0, 11),
            (0b000000010001, 12, 8, 2),
            (0b000000010010, 12, 4, 3),
            (0b000000010011, 12, 0, 10),
            (0b000000010100, 12, 2, 4),
            (0b000000010101, 12, 7, 2),
            (0b000000010110, 12, 21, 1),
            (0b000000010111, 12, 20, 1),
            (0b000000011000, 12, 0, 9),
            (0b000000011001, 12, 19, 1),
            (0b000000011010, 12, 18, 1),
            (0b000000011011, 12, 1, 5),
            (0b000000011100, 12, 3, 3),
            (0b000000011101, 12, 0, 8),
            (0b000000011110, 12, 6, 2),
            (0b000000011111, 12, 17, 1),
            (0b0000000010000, 13, 10, 2),
            (0b0000000010001, 13, 9, 2),
            (0b0000000010010, 13, 5, 3),
            (0b0000000010011, 13, 3, 4),
            (0b0000000010100, 13, 2, 5),
            (0b0000000010101, 13, 1, 7),
            (0b0000000010110, 13, 1, 6),
            (0b0000000010111, 13, 0, 15),
            (0b0000000011000, 13, 0, 14),
            (0b0000000011001, 13, 0, 13),
            (0b0000000011010, 13, 0, 12),
            (0b0000000011011, 13, 26, 1),
            (0b0000000011100, 13, 25, 1),
            (0b0000000011101, 13, 24, 1),
            (0b0000000011110, 13, 23, 1),
            (0b0000000011111, 13, 22, 1),
            (0b00000000010000, 14, 0, 31),
            (0b00000000010001, 14, 0, 30),
            (0b00000000010010, 14, 0, 29),
            (0b00000000010011, 14, 0, 28),
            (0b00000000010100, 14, 0, 27),
            (0b00000000010101, 14, 0, 26),
            (0b00000000010110, 14, 0, 25),
            (0b00000000010111, 14, 0, 24),
            (0b00000000011000, 14, 0, 23),
            (0b00000000011001, 14, 0, 22),
            (0b00000000011010, 14, 0, 21),
            (0b00000000011011, 14, 0, 20),
            (0b00000000011100, 14, 0, 19),
            (0b00000000011101, 14, 0, 18),
            (0b00000000011110, 14, 0, 17),
            (0b00000000011111, 14, 0, 16),
            (0b000000000010000, 15, 0, 40),
            (0b000000000010001, 15, 0, 39),
            (0b000000000010010, 15, 0, 38),
            (0b000000000010011, 15, 0, 37),
            (0b000000000010100, 15, 0, 36),
            (0b000000000010101, 15, 0, 35),
            (0b000000000010110, 15, 0, 34),
            (0b000000000010111, 15, 0, 33),
            (0b000000000011000, 15, 0, 32),
            (0b000000000011001, 15, 1, 14),
            (0b000000000011010, 15, 1, 13),
            (0b000000000011011, 15, 1, 12),
            (0b000000000011100, 15, 1, 11),
            (0b000000000011101, 15, 1, 10),
            (0b000000000011110, 15, 1, 9),
            (0b000000000011111, 15, 1, 8),
            (0b0000000000010000, 16, 1, 18),
            (0b0000000000010001, 16, 1, 17),
            (0b0000000000010010, 16, 1, 16),
            (0b0000000000010011, 16, 1, 15),
            (0b0000000000010100, 16, 6, 3),
            (0b0000000000010101, 16, 16, 2),
            (0b0000000000010110, 16, 15, 2),
            (0b0000000000010111, 16, 14, 2),
            (0b0000000000011000, 16, 13, 2),
            (0b0000000000011001, 16, 12, 2),
            (0b0000000000011010, 16, 11, 2),
            (0b0000000000011011, 16, 31, 1),
            (0b0000000000011100, 16, 30, 1),
            (0b0000000000011101, 16, 29, 1),
            (0b0000000000011110, 16, 28, 1),
            (0b0000000000011111, 16, 27, 1)
        ];

        // Fill run/level entries (each has a +1 sign bit)
        foreach (var (code, bits, run, level) in entries)
        {
            var totalBits = bits + 1; // +1 for sign bit
            var shift = VlcBits - totalBits;
            if (shift < 0) continue;

            var baseIndex = (int)(code << (shift + 1));
            var count = 1 << shift;

            // Sign = 0 -> positive level
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry((byte)totalBits, (byte)run, (short)level, false, false);

            // Sign = 1 -> negative level
            baseIndex |= count; // set the sign bit position
            for (var i = 0; i < count; i++)
                table[baseIndex | i] = new VlcEntry((byte)totalBits, (byte)run, (short)-level, false, false);
        }

        InitEndOfBlockEntries(table);
        InitEscapeEntries(table);

        return table;
    }

    /// <summary>
    ///     Fills VLC table entries for End of Block code: "10" (2 bits).
    /// </summary>
    private static void InitEndOfBlockEntries(VlcEntry[] table)
    {
        var shift = VlcBits - 2;
        var baseIndex = 0b10 << shift;
        var count = 1 << shift;
        for (var i = 0; i < count; i++)
            table[baseIndex | i] = new VlcEntry(2, 0, 0, false, true);
    }

    /// <summary>
    ///     Fills VLC table entries for Escape code: "000001" (6 bits).
    ///     Followed by raw 6-bit run + 10-bit level.
    /// </summary>
    private static void InitEscapeEntries(VlcEntry[] table)
    {
        var shift = VlcBits - 6;
        var baseIndex = 0b000001 << shift;
        var count = 1 << shift;
        for (var i = 0; i < count; i++)
            table[baseIndex | i] = new VlcEntry(6, 0, 0, true, false);
    }

    internal readonly struct VlcEntry(byte bitLength, byte run, short level, bool isEscape, bool isEndOfBlock)
    {
        public readonly byte BitLength = bitLength;
        public readonly byte Run = run;
        public readonly short Level = level;
        public readonly bool IsEscape = isEscape;
        public readonly bool IsEndOfBlock = isEndOfBlock;
    }
}
