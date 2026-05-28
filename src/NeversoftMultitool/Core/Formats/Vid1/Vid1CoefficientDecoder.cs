namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     DCT coefficient VLC decoder for Factor 5 M4Decoder (FUN_802A08B4).
///     Decodes run-level-last tokens from the bitstream using two VLC bundles
///     (A and B), each with three-range peek-12 dispatch + escape modes.
///     Ported from tools/diagnostics/dump_vid1_coeffs.py which was validated
///     against the THAW GC DOL tables.
/// </summary>
internal static class Vid1CoefficientDecoder
{
    private const int EscapeCode = 0x1BFF;

    private static readonly bool UseSecondaryTailForLowPeek =
        string.Equals(
            Environment.GetEnvironmentVariable("VID1_LOW_PEEK_MODE"),
            "secondary-tail",
            StringComparison.OrdinalIgnoreCase);

    private static readonly byte[] ZigzagScan =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63
    ];

    private static readonly byte[] HorizontalScan =
    [
        0, 1, 2, 3, 8, 9, 16, 17, 10, 11, 4, 5, 6, 7, 15, 14,
        13, 12, 19, 18, 24, 25, 32, 33, 26, 27, 20, 21, 22, 23, 28, 29,
        30, 31, 34, 35, 40, 41, 48, 49, 42, 43, 36, 37, 38, 39, 44, 45,
        46, 47, 50, 51, 56, 57, 58, 59, 52, 53, 54, 55, 60, 61, 62, 63
    ];

    private static readonly byte[] VerticalScan =
    [
        0, 8, 16, 24, 1, 9, 2, 10, 17, 25, 32, 40, 48, 56, 57, 49,
        41, 33, 26, 18, 3, 11, 4, 12, 19, 27, 34, 42, 50, 58, 35, 43,
        51, 59, 20, 28, 5, 13, 6, 14, 21, 29, 36, 44, 52, 60, 37, 45,
        53, 61, 22, 30, 7, 15, 23, 31, 38, 46, 54, 62, 39, 47, 55, 63
    ];

    // Bundle A tables (DOL 0x80326860, 112 entries / 0x80326A20, 96 / 0x80326BA0, 120)
    // Token format: last = bit16, run = bits15-8, level = bits7-0
    private static readonly int[] BundleAPrimary =
    [
        0x00E1081, 0x00E1071, 0x00E1061, 0x00E1051, 0x00E00C1, 0x00E00B1, 0x00E00A1, 0x00E0004,
        0x00C1041, 0x00C1041, 0x00C1031, 0x00C1031, 0x00C1021, 0x00C1021, 0x00C1011, 0x00C1011,
        0x00C0091, 0x00C0091, 0x00C0081, 0x00C0081, 0x00C0071, 0x00C0071, 0x00C0061, 0x00C0061,
        0x00C0012, 0x00C0012, 0x00C0003, 0x00C0003, 0x00A0051, 0x00A0051, 0x00A0051, 0x00A0051,
        0x00A0041, 0x00A0041, 0x00A0041, 0x00A0041, 0x00A0031, 0x00A0031, 0x00A0031, 0x00A0031,
        0x0081001, 0x0081001, 0x0081001, 0x0081001, 0x0081001, 0x0081001, 0x0081001, 0x0081001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011,
        0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011, 0x0060011,
        0x0080021, 0x0080021, 0x0080021, 0x0080021, 0x0080021, 0x0080021, 0x0080021, 0x0080021,
        0x0080002, 0x0080002, 0x0080002, 0x0080002, 0x0080002, 0x0080002, 0x0080002, 0x0080002
    ];

    private static readonly int[] BundleASecondary =
    [
        0x0140009, 0x0140008, 0x0121181, 0x0121181, 0x0121171, 0x0121171, 0x0121161, 0x0121161,
        0x0121151, 0x0121151, 0x0121141, 0x0121141, 0x0121131, 0x0121131, 0x0121121, 0x0121121,
        0x0121111, 0x0121111, 0x0121002, 0x0121002, 0x0120161, 0x0120161, 0x0120151, 0x0120151,
        0x0120141, 0x0120141, 0x0120131, 0x0120131, 0x0120121, 0x0120121, 0x0120111, 0x0120111,
        0x0120101, 0x0120101, 0x01200F1, 0x01200F1, 0x0120042, 0x0120042, 0x0120032, 0x0120032,
        0x0120007, 0x0120007, 0x0120006, 0x0120006, 0x0101101, 0x0101101, 0x0101101, 0x0101101,
        0x01010F1, 0x01010F1, 0x01010F1, 0x01010F1, 0x01010E1, 0x01010E1, 0x01010E1, 0x01010E1,
        0x01010D1, 0x01010D1, 0x01010D1, 0x01010D1, 0x01010C1, 0x01010C1, 0x01010C1, 0x01010C1,
        0x01010B1, 0x01010B1, 0x01010B1, 0x01010B1, 0x01010A1, 0x01010A1, 0x01010A1, 0x01010A1,
        0x0101091, 0x0101091, 0x0101091, 0x0101091, 0x01000E1, 0x01000E1, 0x01000E1, 0x01000E1,
        0x01000D1, 0x01000D1, 0x01000D1, 0x01000D1, 0x0100022, 0x0100022, 0x0100022, 0x0100022,
        0x0100013, 0x0100013, 0x0100013, 0x0100013, 0x0100005, 0x0100005, 0x0100005, 0x0100005
    ];

    private static readonly int[] BundleATertiary =
    [
        0x0161012, 0x0161012, 0x0161003, 0x0161003, 0x016000B, 0x016000B, 0x016000A, 0x016000A,
        0x01411C1, 0x01411C1, 0x01411C1, 0x01411C1, 0x01411B1, 0x01411B1, 0x01411B1, 0x01411B1,
        0x01411A1, 0x01411A1, 0x01411A1, 0x01411A1, 0x0141191, 0x0141191, 0x0141191, 0x0141191,
        0x0140092, 0x0140092, 0x0140092, 0x0140092, 0x0140082, 0x0140082, 0x0140082, 0x0140082,
        0x0140072, 0x0140072, 0x0140072, 0x0140072, 0x0140062, 0x0140062, 0x0140062, 0x0140062,
        0x0140052, 0x0140052, 0x0140052, 0x0140052, 0x0140033, 0x0140033, 0x0140033, 0x0140033,
        0x0140023, 0x0140023, 0x0140023, 0x0140023, 0x0140014, 0x0140014, 0x0140014, 0x0140014,
        0x016000C, 0x016000C, 0x0160015, 0x0160015, 0x0160171, 0x0160171, 0x0160181, 0x0160181,
        0x01611D1, 0x01611D1, 0x01611E1, 0x01611E1, 0x01611F1, 0x01611F1, 0x0161201, 0x0161201,
        0x0180016, 0x0180024, 0x0180043, 0x0180053, 0x0180063, 0x01800A2, 0x0180191, 0x01801A1,
        0x0181211, 0x0181221, 0x0181231, 0x0181241, 0x0181251, 0x0181261, 0x0181271, 0x0181281,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF
    ];

    // Bundle B tables (DOL 0x80326D80, 112 entries / 0x80326F40, 96 / 0x803270C0, 120)
    // Token format: same as bundle A — last = bit16, run = bits15-8, level = bits7-0
    private static readonly int[] BundleBPrimary =
    [
        0x00F0401, 0x00F0301, 0x00E0601, 0x00F0501, 0x00E0701, 0x00E0202, 0x00E0103, 0x00E0009,
        0x00D0002, 0x00D0002, 0x00C0501, 0x00C0501, 0x00D0201, 0x00D0201, 0x00D0101, 0x00D0101,
        0x00C0401, 0x00C0401, 0x00C0301, 0x00C0301, 0x00C0008, 0x00C0008, 0x00C0007, 0x00C0007,
        0x00C0102, 0x00C0102, 0x00C0006, 0x00C0006, 0x00A0201, 0x00A0201, 0x00A0201, 0x00A0201,
        0x00A0005, 0x00A0005, 0x00A0005, 0x00A0005, 0x00A0004, 0x00A0004, 0x00A0004, 0x00A0004,
        0x0090001, 0x0090001, 0x0090001, 0x0090001, 0x0090001, 0x0090001, 0x0090001, 0x0090001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001, 0x0040001,
        0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002,
        0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002, 0x0060002,
        0x0080101, 0x0080101, 0x0080101, 0x0080101, 0x0080101, 0x0080101, 0x0080101, 0x0080101,
        0x0080003, 0x0080003, 0x0080003, 0x0080003, 0x0080003, 0x0080003, 0x0080003, 0x0080003
    ];

    private static readonly int[] BundleBSecondary =
    [
        0x0140012, 0x0140011, 0x0130E01, 0x0130E01, 0x0130D01, 0x0130D01, 0x0130C01, 0x0130C01,
        0x0130B01, 0x0130B01, 0x0130A01, 0x0130A01, 0x0130102, 0x0130102, 0x0130004, 0x0130004,
        0x0120C01, 0x0120C01, 0x0120B01, 0x0120B01, 0x0120702, 0x0120702, 0x0120602, 0x0120602,
        0x0120502, 0x0120502, 0x0120303, 0x0120303, 0x0120203, 0x0120203, 0x0120106, 0x0120106,
        0x0120105, 0x0120105, 0x0120010, 0x0120010, 0x0120402, 0x0120402, 0x012000F, 0x012000F,
        0x012000E, 0x012000E, 0x012000D, 0x012000D, 0x0110801, 0x0110801, 0x0110801, 0x0110801,
        0x0110701, 0x0110701, 0x0110701, 0x0110701, 0x0110601, 0x0110601, 0x0110601, 0x0110601,
        0x0110003, 0x0110003, 0x0110003, 0x0110003, 0x0100A01, 0x0100A01, 0x0100A01, 0x0100A01,
        0x0100901, 0x0100901, 0x0100901, 0x0100901, 0x0100801, 0x0100801, 0x0100801, 0x0100801,
        0x0110901, 0x0110901, 0x0110901, 0x0110901, 0x0100302, 0x0100302, 0x0100302, 0x0100302,
        0x0100104, 0x0100104, 0x0100104, 0x0100104, 0x010000C, 0x010000C, 0x010000C, 0x010000C,
        0x010000B, 0x010000B, 0x010000B, 0x010000B, 0x010000A, 0x010000A, 0x010000A, 0x010000A
    ];

    private static readonly int[] BundleBTertiary =
    [
        0x0170007, 0x0170007, 0x0170006, 0x0170006, 0x0160016, 0x0160016, 0x0160015, 0x0160015,
        0x0150202, 0x0150202, 0x0150202, 0x0150202, 0x0150103, 0x0150103, 0x0150103, 0x0150103,
        0x0150005, 0x0150005, 0x0150005, 0x0150005, 0x0140D01, 0x0140D01, 0x0140D01, 0x0140D01,
        0x0140503, 0x0140503, 0x0140503, 0x0140503, 0x0140802, 0x0140802, 0x0140802, 0x0140802,
        0x0140403, 0x0140403, 0x0140403, 0x0140403, 0x0140304, 0x0140304, 0x0140304, 0x0140304,
        0x0140204, 0x0140204, 0x0140204, 0x0140204, 0x0140107, 0x0140107, 0x0140107, 0x0140107,
        0x0140014, 0x0140014, 0x0140014, 0x0140014, 0x0140013, 0x0140013, 0x0140013, 0x0140013,
        0x0160017, 0x0160017, 0x0160018, 0x0160018, 0x0160108, 0x0160108, 0x0160902, 0x0160902,
        0x0170302, 0x0170302, 0x0170402, 0x0170402, 0x0170F01, 0x0170F01, 0x0171001, 0x0171001,
        0x0180019, 0x018001A, 0x018001B, 0x0180109, 0x0180603, 0x018010A, 0x0180205, 0x0180703,
        0x0180E01, 0x0190008, 0x0190502, 0x0190602, 0x0191101, 0x0191201, 0x0191301, 0x0191401,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF,
        0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF, 0x00E1BFF
    ];

    // Escape correction tables (DOL 0x80325FB8, 256 bytes / 0x803260B8, 1024 bytes)
    private static readonly byte[] Correction64 =
    [
        27, 10, 5, 4, 3, 3, 3, 3, 2, 2, 1, 1, 1, 1, 1, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        8, 3, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        12, 6, 4, 3, 3, 3, 3, 2, 2, 2, 2, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        3, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1,
        1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    private static readonly byte[] Correction256 =
    [
        0, 14, 9, 7, 3, 2, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 20, 6, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 26, 10, 6, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 40, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0
    ];

    public static byte[] GetScanTable(string scanName)
    {
        return scanName switch
        {
            "zigzag" => ZigzagScan,
            "horizontal" => HorizontalScan,
            "vertical" => VerticalScan,
            _ => ZigzagScan
        };
    }

    public static void DecodeBlock(
        Vid1BitReader reader,
        bool useBundleB,
        byte[] scanTable,
        Span<short> output,
        int startIndex = 0)
    {
        var coefficientIndex = startIndex;

        while (true)
        {
            int last, run, level;

            if (useBundleB)
                DecodeBundleBToken(reader, out last, out run, out level);
            else
                DecodeBundleAToken(reader, out last, out run, out level);

            coefficientIndex += run;
            if (coefficientIndex < 64)
                output[scanTable[coefficientIndex]] = (short)level;
            coefficientIndex++;

            if (last != 0)
                return;
        }
    }

    public static void DecodeInterBlock(
        Vid1BitReader reader,
        byte[] scanTable,
        Span<short> output)
    {
        var coefficientIndex = 0;
        var useBundleB = string.Equals(
            Environment.GetEnvironmentVariable("VID1_INTER_RESIDUAL_BUNDLE"),
            "B",
            StringComparison.OrdinalIgnoreCase);

        while (true)
        {
            int last, run, level;
            if (useBundleB)
                DecodeInterBundleBToken(reader, out last, out run, out level);
            else
                DecodeInterBundleAToken(reader, out last, out run, out level);

            coefficientIndex += run;
            if (coefficientIndex < 64)
                output[scanTable[coefficientIndex]] = (short)level;
            coefficientIndex++;

            if (last != 0)
                return;
        }
    }

    private static int DecodePrimaryEntry(Vid1BitReader reader, int[] primary, int[] secondary, int[] tertiary)
    {
        var peek = reader.PeekBits(12);
        if (peek >= 0x200)
        {
            var idx = (peek >> 5) - 0x10;
            if (idx < 0 || idx >= primary.Length)
                throw new InvalidDataException($"VLC prefix 0x{peek:X} out of primary table bounds");
            return primary[idx];
        }

        if (peek >= 0x80)
        {
            var idx = (peek >> 2) - 0x20;
            if (idx < 0 || idx >= secondary.Length)
                throw new InvalidDataException($"VLC prefix 0x{peek:X} out of secondary table bounds");
            return secondary[idx];
        }

        if (peek < 8)
        {
            // FUN_802A08B4 has no lower-bound guard before indexing the
            // tertiary table. In memory, the eight entries before tertiary are
            // the tail of the secondary table. Keep the old sentinel behavior
            // as the default until the MAE/trace validates this path.
            return UseSecondaryTailForLowPeek ? secondary[secondary.Length - 8 + peek] : 0x00E1BFF;
        }

        var tertiaryIndex = peek - 8;
        if (tertiaryIndex >= tertiary.Length)
            throw new InvalidDataException($"VLC prefix 0x{peek:X} out of tertiary table bounds");
        return tertiary[tertiaryIndex];
    }

    private static void DecodeBundleAToken(Vid1BitReader reader, out int last, out int run, out int level)
    {
        var entry = DecodePrimaryEntry(reader, BundleAPrimary, BundleASecondary, BundleATertiary);
        var token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        if (token != EscapeCode)
        {
            last = (token >> 16) & 1;
            run = (token >> 8) & 0xFF;
            level = token & 0xFF;
            if (reader.ReadBits(1) != 0) level = -level;
            return;
        }

        var escapeMode = reader.PeekBits(2);
        if (escapeMode == 3)
        {
            reader.SkipBits(2);
            last = reader.ReadBits(1);
            run = reader.ReadBits(6);
            reader.SkipBits(1);
            level = reader.ReadBits(12);
            reader.SkipBits(1);
            if ((level & 0x800) != 0) level |= unchecked((int)0xFFFFF000);
            return;
        }

        reader.SkipBits(escapeMode == 2 ? 2 : 1);
        entry = DecodePrimaryEntry(reader, BundleAPrimary, BundleASecondary, BundleATertiary);
        token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        last = (token >> 16) & 1;
        run = (token >> 8) & 0xFF;
        level = token & 0xFF;

        if (escapeMode < 2)
        {
            var corrIdx = run + (last << 6);
            level += corrIdx < Correction64.Length ? Correction64[corrIdx] : 0;
        }
        else
        {
            var levelIdx = level + (last << 8);
            run += 1 + (levelIdx < Correction256.Length ? Correction256[levelIdx] : 0);
        }

        if (reader.ReadBits(1) != 0) level = -level;
    }

    private static void DecodeBundleBToken(Vid1BitReader reader, out int last, out int run, out int level)
    {
        var entry = DecodePrimaryEntry(reader, BundleBPrimary, BundleBSecondary, BundleBTertiary);
        var token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        if (token != EscapeCode)
        {
            last = (token >> 16) & 1;
            run = (token >> 8) & 0xFF;
            level = token & 0xFF;
            if (reader.ReadBits(1) != 0) level = -level;
            return;
        }

        var escapeMode = reader.PeekBits(2);
        if (escapeMode == 3)
        {
            reader.SkipBits(2);
            last = reader.ReadBits(1);
            run = reader.ReadBits(6);
            reader.SkipBits(1);
            level = reader.ReadBits(12);
            reader.SkipBits(1);
            if ((level & 0x800) != 0) level |= unchecked((int)0xFFFFF000);
            return;
        }

        reader.SkipBits(escapeMode == 2 ? 2 : 1);
        entry = DecodePrimaryEntry(reader, BundleBPrimary, BundleBSecondary, BundleBTertiary);
        token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        last = (token >> 16) & 1;
        run = (token >> 8) & 0xFF;
        level = token & 0xFF;

        if (escapeMode < 2)
        {
            var corrIdx = run + (last << 6);
            level += corrIdx < Correction64.Length ? Correction64[corrIdx] : 0;
        }
        else
        {
            var levelIdx = level + (last << 8);
            run += 1 + (levelIdx < Correction256.Length ? Correction256[levelIdx] : 0);
        }

        if (reader.ReadBits(1) != 0) level = -level;
    }

    private static void DecodeInterBundleBToken(Vid1BitReader reader, out int last, out int run, out int level)
    {
        var entry = DecodePrimaryEntry(reader, BundleBPrimary, BundleBSecondary, BundleBTertiary);
        var token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        if (token != EscapeCode)
        {
            last = (token >> 12) & 1;
            run = (token >> 4) & 0xFF;
            level = token & 0xF;
            if (reader.ReadBits(1) != 0) level = -level;
            return;
        }

        var escapeMode = reader.PeekBits(2);
        if (escapeMode == 3)
        {
            reader.SkipBits(2);
            last = reader.ReadBits(1);
            run = reader.ReadBits(6);
            reader.SkipBits(1);
            level = reader.ReadBits(12);
            reader.SkipBits(1);
            if ((level & 0x800) != 0) level |= unchecked((int)0xFFFFF000);
            return;
        }

        reader.SkipBits(escapeMode == 2 ? 2 : 1);
        entry = DecodePrimaryEntry(reader, BundleBPrimary, BundleBSecondary, BundleBTertiary);
        token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        last = (token >> 12) & 1;
        run = (token >> 4) & 0xFF;
        level = token & 0xF;

        if (escapeMode < 2)
        {
            var corrIdx = run + ((last + 2) << 6);
            level += corrIdx < Correction64.Length ? Correction64[corrIdx] : 0;
        }
        else
        {
            var levelIdx = level + ((last + 2) << 8);
            run += 1 + (levelIdx < Correction256.Length ? Correction256[levelIdx] : 0);
        }

        if (reader.ReadBits(1) != 0) level = -level;
    }

    private static void DecodeInterBundleAToken(Vid1BitReader reader, out int last, out int run, out int level)
    {
        var entry = DecodePrimaryEntry(reader, BundleAPrimary, BundleASecondary, BundleATertiary);
        var token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        if (token != EscapeCode)
        {
            last = (token >> 12) & 1;
            run = (token >> 4) & 0xFF;
            level = token & 0xF;
            if (reader.ReadBits(1) != 0) level = -level;
            return;
        }

        var escapeMode = reader.PeekBits(2);
        if (escapeMode == 3)
        {
            reader.SkipBits(2);
            last = reader.ReadBits(1);
            run = reader.ReadBits(6);
            reader.SkipBits(1);
            level = reader.ReadBits(12);
            reader.SkipBits(1);
            if ((level & 0x800) != 0) level |= unchecked((int)0xFFFFF000);
            return;
        }

        reader.SkipBits(escapeMode == 2 ? 2 : 1);
        entry = DecodePrimaryEntry(reader, BundleAPrimary, BundleASecondary, BundleATertiary);
        token = entry & 0x1FFFF;
        reader.SkipBits(entry >> 17);

        last = (token >> 12) & 1;
        run = (token >> 4) & 0xFF;
        level = token & 0xF;

        if (escapeMode < 2)
        {
            var corrIdx = run + ((last + 2) << 6);
            level += corrIdx < Correction64.Length ? Correction64[corrIdx] : 0;
        }
        else
        {
            var levelIdx = level + ((last + 2) << 8);
            run += 1 + (levelIdx < Correction256.Length ? Correction256[levelIdx] : 0);
        }

        if (reader.ReadBits(1) != 0) level = -level;
    }
}
