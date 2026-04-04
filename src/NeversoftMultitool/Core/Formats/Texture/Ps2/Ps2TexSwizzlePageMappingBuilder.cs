namespace NeversoftMultitool.Core.Formats.Texture.Ps2;

internal static class Ps2TexSwizzlePageMappingBuilder
{
    private static readonly bool[,] CanConv4to32Table =
    {
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, false, false, false, false, false },
        { false, false, false, false, false, true, false, false, false, false },
        { false, false, false, false, false, false, true, false, false, false },
        { false, false, false, false, false, false, false, true, true, true },
        { false, false, false, false, false, false, false, true, true, true },
        { false, false, false, false, false, false, false, true, true, true }
    };

    // Decompiled from DAT_0049a968 / FUN_001c8ac8. THAW PS2 does not use the same
    // sparse eligibility gate for the 8-bit family as the 4-bit Conv4-to-32 path.
    private static readonly bool[,] CanConv8to32Table =
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

    internal static bool CanConv4to32(int width, int height)
    {
        var tw = NumBits(width) - 1;
        var th = NumBits(height) - 1;
        return tw >= 0 && tw < 10 && th >= 0 && th < 10 && CanConv4to32Table[th, tw];
    }

    internal static bool CanConv8to32(int width, int height)
    {
        var tw = NumBits(width) - 1;
        var th = NumBits(height) - 1;
        return tw >= 0 && tw < 10 && th >= 0 && th < 10 && CanConv8to32Table[th, tw];
    }

    internal static int[] BuildConv8to32Mapping(int width, int height)
    {
        const int psmt8PageW = 128;
        const int psmt8PageH = 64;
        const int psmct32PageW = 64;
        const int psmct32PageH = 32;

        var nPageW = (width - 1) / psmt8PageW + 1;
        var nPageH = (height - 1) / psmt8PageH + 1;

        int nInputWidthByte;
        int nOutputWidthByte;
        int nInputHeight;
        int nOutputHeight;
        if (nPageW == 1)
        {
            nInputWidthByte = width;
            nOutputWidthByte = width * 2;
        }
        else
        {
            nInputWidthByte = psmt8PageW;
            nOutputWidthByte = psmct32PageW * 4;
        }

        if (nPageH == 1)
        {
            nInputHeight = height;
            nOutputHeight = height / 2;
        }
        else
        {
            nInputHeight = psmt8PageH;
            nOutputHeight = psmct32PageH;
        }

        var identity = new int[width * height];
        for (var i = 0; i < identity.Length; i++)
            identity[i] = i;

        var output = new int[width * height];

        for (var pi = 0; pi < nPageH; pi++)
        {
            for (var pj = 0; pj < nPageW; pj++)
            {
                var inputPage = new int[psmt8PageW * psmt8PageH];
                for (var k = 0; k < nInputHeight; k++)
                {
                    var srcStart = nInputWidthByte * psmt8PageH * nPageW * pi
                                   + nInputWidthByte * pj
                                   + k * nInputWidthByte * nPageW;
                    for (var c = 0; c < nInputWidthByte; c++)
                    {
                        var srcIdx = srcStart + c;
                        if (srcIdx < identity.Length)
                            inputPage[k * psmt8PageW + c] = identity[srcIdx];
                    }
                }

                var outputPage = PageConv8to32(inputPage);

                for (var k = 0; k < nOutputHeight; k++)
                {
                    var dstStart = nOutputWidthByte * nOutputHeight * nPageW * pi
                                   + nOutputWidthByte * pj
                                   + k * nOutputWidthByte * nPageW;
                    for (var c = 0; c < nOutputWidthByte; c++)
                    {
                        var dstIdx = dstStart + c;
                        if (dstIdx < output.Length)
                            output[dstIdx] = outputPage[k * psmct32PageW * 4 + c];
                    }
                }
            }
        }

        return output;
    }

    internal static int[] BuildConv4to32Mapping(int width, int height)
    {
        const int psmt4PageW = 128;
        const int psmt4PageH = 128;
        const int psmct32PageH = 32;
        const int psmct32PageWNibbles = 512;

        var nPageW = (width - 1) / psmt4PageW + 1;
        var nPageH = (height - 1) / psmt4PageH + 1;

        int nInputWidth;
        int nOutputWidth;
        int nInputHeight;
        int nOutputHeight;
        if (nPageW == 1)
        {
            nInputWidth = width;
            nOutputHeight = width / 4;
        }
        else
        {
            nInputWidth = psmt4PageW;
            nOutputHeight = psmct32PageH;
        }

        if (nPageH == 1)
        {
            nInputHeight = height;
            nOutputWidth = height * 4;
        }
        else
        {
            nInputHeight = psmt4PageH;
            nOutputWidth = psmct32PageWNibbles;
        }

        var totalNibbles = width * height;
        var identity = new int[totalNibbles];
        for (var i = 0; i < identity.Length; i++)
            identity[i] = i;

        var output = new int[totalNibbles];

        for (var pi = 0; pi < nPageH; pi++)
        {
            for (var pj = 0; pj < nPageW; pj++)
            {
                var inputPage = new int[psmt4PageW * psmt4PageH];
                for (var k = 0; k < nInputHeight; k++)
                {
                    var srcStart = nInputWidth * psmt4PageH * nPageW * pi
                                   + nInputWidth * pj
                                   + k * nInputWidth * nPageW;
                    for (var c = 0; c < nInputWidth; c++)
                    {
                        var srcIdx = srcStart + c;
                        if (srcIdx < identity.Length)
                            inputPage[k * psmt4PageW + c] = identity[srcIdx];
                    }
                }

                var outputPage = PageConv4to32(inputPage);

                for (var k = 0; k < nOutputHeight; k++)
                {
                    var dstStart = nOutputWidth * psmct32PageH * nPageW * pi
                                   + nOutputWidth * pj
                                   + k * nOutputWidth * nPageW;
                    for (var c = 0; c < nOutputWidth; c++)
                    {
                        var dstIdx = dstStart + c;
                        if (dstIdx < output.Length)
                            output[dstIdx] = outputPage[k * psmct32PageWNibbles + c];
                    }
                }
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

    private static int[] PageConv8to32(int[] inputPage)
    {
        const int psmt8BlockW = 16;
        const int psmt8BlockH = 16;
        const int psmct32BlockW = 8;
        const int psmct32BlockH = 8;
        const int inputPageLineSize = 128;
        const int outputPageLineSize = 256;
        const int nWidth = 8;
        const int nHeight = 4;

        int[] blockTable8 =
        {
            0, 1, 4, 5, 16, 17, 20, 21, 2, 3, 6, 7, 18, 19, 22, 23, 8, 9, 12, 13, 24, 25, 28, 29, 10, 11, 14, 15, 26,
            27, 30, 31
        };
        int[] blockTable32 =
        {
            0, 1, 4, 5, 16, 17, 20, 21, 2, 3, 6, 7, 18, 19, 22, 23, 8, 9, 12, 13, 24, 25, 28, 29, 10, 11, 14, 15, 26,
            27, 30, 31
        };

        var index32H = new int[32];
        var index32V = new int[32];
        var idx = 0;
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                index32H[blockTable32[idx]] = j;
                index32V[blockTable32[idx]] = i;
                idx++;
            }
        }

        var outputPage = new int[outputPageLineSize * psmct32BlockH * 4];

        for (var bi = 0; bi < nHeight; bi++)
        {
            for (var bj = 0; bj < nWidth; bj++)
            {
                var inputBlock = new int[256];
                for (var k = 0; k < psmt8BlockH; k++)
                {
                    var srcOff = psmt8BlockH * bi * inputPageLineSize + bj * psmt8BlockW + k * inputPageLineSize;
                    for (var c = 0; c < psmt8BlockW; c++)
                    {
                        if (srcOff + c < inputPage.Length)
                            inputBlock[k * psmt8BlockW + c] = inputPage[srcOff + c];
                    }
                }

                var inBlockNb = blockTable8[bi * nWidth + bj];
                var outputBlock = BlockConv8to32(inputBlock);

                var outBaseRow = psmct32BlockH * index32V[inBlockNb];
                var outBaseCol = index32H[inBlockNb] * psmct32BlockW * 4;
                for (var k = 0; k < psmct32BlockH; k++)
                {
                    for (var c = 0; c < psmct32BlockW * 4; c++)
                    {
                        var outOff = (outBaseRow + k) * outputPageLineSize + outBaseCol + c;
                        if (outOff < outputPage.Length)
                            outputPage[outOff] = outputBlock[k * psmct32BlockW * 4 + c];
                    }
                }
            }
        }

        return outputPage;
    }

    private static int[] BlockConv8to32(int[] input)
    {
        int[] lut =
        {
            0, 36, 8, 44, 1, 37, 9, 45, 2, 38, 10, 46, 3, 39, 11, 47,
            4, 32, 12, 40, 5, 33, 13, 41, 6, 34, 14, 42, 7, 35, 15, 43,
            16, 52, 24, 60, 17, 53, 25, 61, 18, 54, 26, 62, 19, 55, 27, 63,
            20, 48, 28, 56, 21, 49, 29, 57, 22, 50, 30, 58, 23, 51, 31, 59,
            4, 32, 12, 40, 5, 33, 13, 41, 6, 34, 14, 42, 7, 35, 15, 43,
            0, 36, 8, 44, 1, 37, 9, 45, 2, 38, 10, 46, 3, 39, 11, 47,
            20, 48, 28, 56, 21, 49, 29, 57, 22, 50, 30, 58, 23, 51, 31, 59,
            16, 52, 24, 60, 17, 53, 25, 61, 18, 54, 26, 62, 19, 55, 27, 63
        };

        var output = new int[256];
        var index1 = 0;
        for (var k = 0; k < 4; k++)
        {
            var index0 = k % 2 * 64;
            var inputBase = k * 64;
            for (var i = 0; i < 16; i++)
            {
                for (var j = 0; j < 4; j++)
                    output[index1++] = input[inputBase + lut[index0++]];
            }
        }

        return output;
    }

    private static int[] PageConv4to32(int[] inputPage)
    {
        const int psmt4BlockW = 32;
        const int psmt4BlockH = 16;
        const int psmct32BlockW = 8;
        const int psmct32BlockH = 8;
        const int inputPageLineNibbles = 128;
        const int outputPageLineNibbles = 512;
        const int nWidth = 4;
        const int nHeight = 8;
        const int outputBlockRowNibbles = psmct32BlockW * 4 * 2;

        int[] blockTable4 =
        {
            0, 2, 8, 10, 1, 3, 9, 11, 4, 6, 12, 14, 5, 7, 13, 15, 16, 18, 24, 26, 17, 19, 25, 27, 20, 22, 28, 30, 21,
            23, 29, 31
        };
        int[] blockTable32 =
        {
            0, 1, 4, 5, 16, 17, 20, 21, 2, 3, 6, 7, 18, 19, 22, 23, 8, 9, 12, 13, 24, 25, 28, 29, 10, 11, 14, 15, 26,
            27, 30, 31
        };

        var index32H = new int[32];
        var index32V = new int[32];
        var idx = 0;
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 8; j++)
            {
                index32H[blockTable32[idx]] = j;
                index32V[blockTable32[idx]] = i;
                idx++;
            }
        }

        var outputPage = new int[outputPageLineNibbles * psmct32BlockH * 4];

        for (var bi = 0; bi < nHeight; bi++)
        {
            for (var bj = 0; bj < nWidth; bj++)
            {
                var inputBlock = new int[512];
                for (var k = 0; k < psmt4BlockH; k++)
                {
                    var srcOff = (psmt4BlockH * bi + k) * inputPageLineNibbles + bj * psmt4BlockW;
                    for (var c = 0; c < psmt4BlockW; c++)
                    {
                        if (srcOff + c < inputPage.Length)
                            inputBlock[k * psmt4BlockW + c] = inputPage[srcOff + c];
                    }
                }

                var inBlockNb = blockTable4[bi * nWidth + bj];
                var outputBlock = BlockConv4to32(inputBlock);

                var outBaseRow = psmct32BlockH * index32V[inBlockNb];
                var outBaseCol = index32H[inBlockNb] * outputBlockRowNibbles;
                for (var k = 0; k < psmct32BlockH; k++)
                {
                    for (var c = 0; c < outputBlockRowNibbles; c++)
                    {
                        var outOff = (outBaseRow + k) * outputPageLineNibbles + outBaseCol + c;
                        if (outOff < outputPage.Length)
                            outputPage[outOff] = outputBlock[k * outputBlockRowNibbles + c];
                    }
                }
            }
        }

        return outputPage;
    }

    private static int[] BlockConv4to32(int[] input)
    {
        int[] lut =
        {
            0, 68, 8, 76, 16, 84, 24, 92,
            1, 69, 9, 77, 17, 85, 25, 93,
            2, 70, 10, 78, 18, 86, 26, 94,
            3, 71, 11, 79, 19, 87, 27, 95,
            4, 64, 12, 72, 20, 80, 28, 88,
            5, 65, 13, 73, 21, 81, 29, 89,
            6, 66, 14, 74, 22, 82, 30, 90,
            7, 67, 15, 75, 23, 83, 31, 91,
            32, 100, 40, 108, 48, 116, 56, 124,
            33, 101, 41, 109, 49, 117, 57, 125,
            34, 102, 42, 110, 50, 118, 58, 126,
            35, 103, 43, 111, 51, 119, 59, 127,
            36, 96, 44, 104, 52, 112, 60, 120,
            37, 97, 45, 105, 53, 113, 61, 121,
            38, 98, 46, 106, 54, 114, 62, 122,
            39, 99, 47, 107, 55, 115, 63, 123,
            4, 64, 12, 72, 20, 80, 28, 88,
            5, 65, 13, 73, 21, 81, 29, 89,
            6, 66, 14, 74, 22, 82, 30, 90,
            7, 67, 15, 75, 23, 83, 31, 91,
            0, 68, 8, 76, 16, 84, 24, 92,
            1, 69, 9, 77, 17, 85, 25, 93,
            2, 70, 10, 78, 18, 86, 26, 94,
            3, 71, 11, 79, 19, 87, 27, 95,
            36, 96, 44, 104, 52, 112, 60, 120,
            37, 97, 45, 105, 53, 113, 61, 121,
            38, 98, 46, 106, 54, 114, 62, 122,
            39, 99, 47, 107, 55, 115, 63, 123,
            32, 100, 40, 108, 48, 116, 56, 124,
            33, 101, 41, 109, 49, 117, 57, 125,
            34, 102, 42, 110, 50, 118, 58, 126,
            35, 103, 43, 111, 51, 119, 59, 127
        };

        var output = new int[512];
        var outIdx = 0;
        for (var k = 0; k < 4; k++)
        {
            var index0 = k % 2 * 128;
            var inputBase = k * 128;
            for (var i = 0; i < 16; i++)
            {
                for (var j = 0; j < 4; j++)
                {
                    output[outIdx++] = input[inputBase + lut[index0++]];
                    output[outIdx++] = input[inputBase + lut[index0++]];
                }
            }
        }

        return output;
    }
}
