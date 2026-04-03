namespace NeversoftMultitool.Core.Formats.Psx;

internal static class Ps2TexSwizzleVramMappingBuilder
{
    private static readonly bool[,] CanConv4to16Table =
    {
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, true, true, true, true, true, false },
        { false, false, false, false, true, true, true, true, true, false },
        { false, false, false, false, true, true, true, true, true, false },
        { false, false, false, false, true, true, true, true, true, false },
        { false, false, false, false, true, true, true, true, true, true },
        { false, false, false, false, false, false, false, true, true, true },
        { false, false, false, false, false, false, false, true, true, true }
    };

    private static readonly int[] Block4 =
    {
        0, 2, 8, 10,
        1, 3, 9, 11,
        4, 6, 12, 14,
        5, 7, 13, 15,
        16, 18, 24, 26,
        17, 19, 25, 27,
        20, 22, 28, 30,
        21, 23, 29, 31
    };

    private static readonly int[,] ColumnWord4 =
    {
        {
            0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
            0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
            2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15,
            2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15,
            8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5,
            8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5,
            10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7,
            10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7
        },
        {
            8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5,
            8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5,
            10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7,
            10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7,
            0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
            0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
            2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15,
            2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15
        }
    };

    private static readonly int[] ColumnByte4 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 4, 4, 4, 4, 4, 4, 4, 4, 6, 6, 6, 6, 6, 6, 6, 6,
        0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 4, 4, 4, 4, 4, 4, 4, 4, 6, 6, 6, 6, 6, 6, 6, 6,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 5, 5, 5, 5, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 5, 5, 5, 5, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7
    };

    private static readonly int[] Block16 =
    {
        0, 2, 8, 10,
        1, 3, 9, 11,
        4, 6, 12, 14,
        5, 7, 13, 15,
        16, 18, 24, 26,
        17, 19, 25, 27,
        20, 22, 28, 30,
        21, 23, 29, 31
    };

    private static readonly int[] ColumnWord16 =
    {
        0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
        2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15
    };

    private static readonly int[] ColumnHalf16 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1
    };

    internal static bool CanConv4to16(int width, int height)
    {
        var tw = NumBits(width) - 1;
        var th = NumBits(height) - 1;
        return tw >= 0 && tw < 10 && th >= 0 && th < 10 && CanConv4to16Table[th, tw];
    }

    internal static byte[] TransformPsmt4ViaPsmct16Staging(ReadOnlySpan<byte> swizzled, int width, int height)
    {
        var writePageWidth = Math.Max((width + 0x7F) >> 7, 1) << 1;
        var readPageWidth = Math.Max((width + 0xFF) >> 8, 1) << 1;
        var pageRows = Math.Max((height + 0x7F) >> 7, 1);
        var stage = new byte[readPageWidth * pageRows * 0x2000];

        WritePsmt4ToPsmct16Stage(stage, writePageWidth, width, height, swizzled);
        return ReadPsmct16StageAsPsmt4(stage, readPageWidth, width, height);
    }

    internal static int[] BuildConv4to16Mapping(int width, int height)
    {
        var totalNibbles = width * height;

        var min4W = Math.Max(width, 128);
        var min4H = Math.Max(height, 128);
        var min16W = Math.Max(width / 2, 64);
        var min16H = Math.Max(height / 2, 64);

        var pages4 = min4W / 128 * (min4H / 128);
        var pages16 = min16W / 64 * (min16H / 64);
        var blocksNeeded = Math.Max(pages4 * 32, pages16 * 32);
        var wordsNeeded = blocksNeeded * 64;

        var vramNibbles = new int[wordsNeeded * 8];
        Array.Fill(vramNibbles, -1);

        var psmt4Tbw = Math.Max(width / 128, 1) * 2;
        var psmct16Tbw = Math.Max(width / 2 / 128, 1) * 2;

        var linearPosition = 0;
        for (var y = 0; y < height; y++)
        {
            var pageY = y / 128;
            var py = y - pageY * 128;
            var blockY = py / 16;
            var by = py - blockY * 16;
            var column = by / 4;
            var cy = by - column * 4;

            for (var x = 0; x < width; x++)
            {
                var pageX = x / 128;
                var dbwShifted = psmt4Tbw >> 1;
                var page = pageX + pageY * dbwShifted;

                var px = x - pageX * 128;
                var blockX = px / 32;
                var block = Block4[blockX + blockY * 4];

                var bx = px - blockX * 32;
                var cw = ColumnWord4[column & 1, bx + cy * 32];
                var cb = ColumnByte4[bx + cy * 32];

                var gsIndex = page * 2048 + block * 64 + column * 16 + cw;
                var vramNibbleIndex = gsIndex * 8 + (cb >> 1) * 2 + (cb & 1);

                if (vramNibbleIndex < vramNibbles.Length)
                    vramNibbles[vramNibbleIndex] = linearPosition;

                linearPosition++;
            }
        }

        var outputWidth = width / 2;
        var outputHeight = height / 2;
        var mapping = new int[totalNibbles];
        Array.Fill(mapping, -1);
        var outputNibblePosition = 0;

        for (var y = 0; y < outputHeight; y++)
        {
            var pageY = y / 64;
            var py = y - pageY * 64;
            var blockY = py / 8;
            var by = py - blockY * 8;
            var column = by / 2;
            var cy = by - column * 2;

            for (var x = 0; x < outputWidth; x++)
            {
                var pageX = x / 64;
                var page = pageX + pageY * psmct16Tbw;

                var px = x - pageX * 64;
                var blockX = px / 16;
                var block = Block16[blockX + blockY * 4];

                var bx = px - blockX * 16;
                var cw = ColumnWord16[bx + cy * 16];
                var ch = ColumnHalf16[bx + cy * 16];

                var gsIndex = page * 2048 + block * 64 + column * 16 + cw;
                var baseNibble = gsIndex * 8 + ch * 4;

                for (var nib = 0; nib < 4 && outputNibblePosition < mapping.Length; nib++)
                {
                    var nibbleAddress = baseNibble + nib;
                    if (nibbleAddress < vramNibbles.Length)
                        mapping[outputNibblePosition] = vramNibbles[nibbleAddress];
                    outputNibblePosition++;
                }
            }
        }

        return mapping;
    }

    private static void WritePsmt4ToPsmct16Stage(
        byte[] stage,
        int pageWidthTwice,
        int width,
        int height,
        ReadOnlySpan<byte> source)
    {
        var sourceNibbleIndex = 0;
        for (var y = 0; y < height; y++)
        {
            var pageY = y >> 7;
            var localY = y & 0x7F;
            var blockY = localY >> 4;
            var rowInBlock = localY & 0x0F;
            var column = rowInBlock >> 2;
            var cy = rowInBlock & 0x03;

            for (var x = 0; x < width; x++)
            {
                var pageX = x >> 7;
                var page = pageX + pageY * (pageWidthTwice >> 1);
                var localX = x & 0x7F;
                var blockX = localX >> 5;
                var block = Block4[blockX + blockY * 4];
                var bx = localX & 0x1F;
                var tableIndex = bx + cy * 32;
                var cw = ColumnWord4[column & 1, tableIndex];
                var cb = ColumnByte4[tableIndex];
                var stageByteIndex = (page * 0x800 + block * 0x40 + column * 0x10 + cw) * 4 + (cb >> 1);
                if ((uint)stageByteIndex >= (uint)stage.Length)
                    continue;

                var sourceByteIndex = sourceNibbleIndex >> 1;
                if ((uint)sourceByteIndex >= (uint)source.Length)
                    break;

                var sourceNibble = ((source[sourceByteIndex] >> ((sourceNibbleIndex & 1) * 4)) & 0x0F);
                if ((cb & 1) == 0)
                    stage[stageByteIndex] = (byte)((stage[stageByteIndex] & 0xF0) | sourceNibble);
                else
                    stage[stageByteIndex] = (byte)((stage[stageByteIndex] & 0x0F) | (sourceNibble << 4));

                sourceNibbleIndex++;
            }
        }
    }

    private static byte[] ReadPsmct16StageAsPsmt4(byte[] stage, int pageWidthTwice, int width, int height)
    {
        var readWidth = width >> 1;
        var readHeight = height >> 1;
        var output = new byte[width * height / 2];
        var outputIndex = 0;

        for (var y = 0; y < readHeight; y++)
        {
            var pageY = y >> 6;
            var localY = y & 0x3F;
            var blockY = localY >> 3;
            var rowInBlock = localY & 0x07;
            var column = rowInBlock >> 1;
            var cy = rowInBlock & 0x01;

            for (var x = 0; x < readWidth && outputIndex + 1 < output.Length; x++)
            {
                var pageX = x >> 6;
                var page = pageX + pageY * pageWidthTwice;
                var localX = x & 0x3F;
                var blockX = localX >> 4;
                var block = Block16[blockX + blockY * 4];
                var bx = localX & 0x0F;
                var tableIndex = bx + cy * 16;
                var cw = ColumnWord16[tableIndex];
                var ch = ColumnHalf16[tableIndex];
                var stageByteIndex = (page * 0x800 + block * 0x40 + column * 0x10 + cw) * 4 + ch * 2;
                if ((uint)(stageByteIndex + 1) >= (uint)stage.Length)
                    continue;

                output[outputIndex++] = stage[stageByteIndex];
                output[outputIndex++] = stage[stageByteIndex + 1];
            }
        }

        return output;
    }

    private static int NumBits(int size)
    {
        var bits = 0;
        while (size > 0)
        {
            size >>= 1;
            bits++;
        }

        return bits;
    }
}
