namespace NeversoftMultitool.Core.Formats.Psx;

/// <summary>
///     Un-swizzle algorithms for PS2 GS VRAM tiled pixel layouts (PSMT8, PSMT4).
///     THUG2+ build tools pre-apply Conv8to32/Conv4to32/Conv4to16 swizzle to paletted texture data,
///     storing it in PS2 GS VRAM page layout in the file. These methods reverse that transformation.
///     Ported from THUG source: Gfx/NGPS/NX/texturemem.cpp.
/// </summary>
internal static class Ps2TexSwizzle
{
    // Mapping caches keyed by (width, height). Mappings are deterministic and immutable after construction.
    private static readonly Dictionary<(int, int), int[]> Conv8to32Cache = [];
    private static readonly Dictionary<(int, int), int[]> Conv4to32Cache = [];
    private static readonly Dictionary<(int, int), int[]> Conv4to16Cache = [];
    private static readonly Lock CacheLock = new();
    // CanConv4to32 table (texturemem.cpp sCanConvert4to32, #if 1 branch).
    // Indexed by [log2(height)][log2(width)]. True = Conv4to32 (PSMCT32 layout).
    // Dimensions: 32x32, 64x64, >=128x128.
    private static readonly bool[,] CanConv4to32Table =
    {
        //       1      2      4      8     16     32     64    128    256    512
        { false, false, false, false, false, false, false, false, false, false }, // 1
        { false, false, false, false, false, false, false, false, false, false }, // 2
        { false, false, false, false, false, false, false, false, false, false }, // 4
        { false, false, false, false, false, false, false, false, false, false }, // 8
        { false, false, false, false, false, false, false, false, false, false }, // 16
        { false, false, false, false, false, true, false, false, false, false }, // 32
        { false, false, false, false, false, false, true, false, false, false }, // 64
        { false, false, false, false, false, false, false, true, true, true }, // 128
        { false, false, false, false, false, false, false, true, true, true }, // 256
        { false, false, false, false, false, false, false, true, true, true } // 512
    };

    // CanConv4to16 table (texturemem.cpp sCanConvert4to16).
    // Indexed by [log2(height)][log2(width)]. True = Conv4to16 (PSMCT16 layout).
    // Conv4to16 uses GS VRAM simulation (writeTexPSMT4 + readTexPSMCT16).
    private static readonly bool[,] CanConv4to16Table =
    {
        //       1      2      4      8     16     32     64    128    256    512
        { false, false, false, false, false, false, false, false, false, false }, // 1
        { false, false, false, false, false, false, false, false, false, false }, // 2
        { false, false, false, false, false, false, false, false, false, false }, // 4
        { false, false, false, false, true, true, true, true, true, false }, // 8
        { false, false, false, false, true, true, true, true, true, false }, // 16
        { false, false, false, false, true, true, true, true, true, false }, // 32
        { false, false, false, false, true, true, true, true, true, false }, // 64
        { false, false, false, false, true, true, true, true, true, true }, // 128
        { false, false, false, false, false, false, false, true, true, true }, // 256
        { false, false, false, false, false, false, false, true, true, true } // 512
    };

    // ---- GS VRAM Simulator tables (from texturemem.cpp) for Conv4to16 ----

    // PSMT4 block layout: page = 128x128 pixels, blocks = 32x16 pixels, 4 columns x 8 rows.
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

    // PSMT4 column word table: [column & 1][cx + cy * 32], 2 x 128 entries.
    private static readonly int[,] ColumnWord4 =
    {
        // even column (column & 1 == 0)
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
        // odd column (column & 1 == 1)
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

    // PSMT4 column byte table: [cx + cy * 32], 128 entries.
    private static readonly int[] ColumnByte4 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 4, 4, 4, 4, 4, 4, 4, 4, 6, 6, 6, 6, 6, 6, 6, 6,
        0, 0, 0, 0, 0, 0, 0, 0, 2, 2, 2, 2, 2, 2, 2, 2, 4, 4, 4, 4, 4, 4, 4, 4, 6, 6, 6, 6, 6, 6, 6, 6,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 5, 5, 5, 5, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7,
        1, 1, 1, 1, 1, 1, 1, 1, 3, 3, 3, 3, 3, 3, 3, 3, 5, 5, 5, 5, 5, 5, 5, 5, 7, 7, 7, 7, 7, 7, 7, 7
    };

    // PSMCT16 block layout: page = 64x64 pixels, blocks = 16x8 pixels, 4 columns x 8 rows.
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

    // PSMCT16 column word table: [cx + cy * 16], 32 entries.
    private static readonly int[] ColumnWord16 =
    {
        0, 1, 4, 5, 8, 9, 12, 13, 0, 1, 4, 5, 8, 9, 12, 13,
        2, 3, 6, 7, 10, 11, 14, 15, 2, 3, 6, 7, 10, 11, 14, 15
    };

    // PSMCT16 column half table: [cx + cy * 16], 32 entries (0 = low halfword, 1 = high halfword).
    private static readonly int[] ColumnHalf16 =
    {
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 1, 1, 1, 1
    };

    /// <summary>
    ///     Un-swizzles PSMT8 pixel data from Conv8to32 (PSMCT32 tiled) layout back to linear.
    ///     THUG2+ build tools pre-apply texturemem.cpp Conv8to32() to PSMT8 data, storing it in
    ///     PSMCT32 page layout in the file. This reverses that transformation.
    ///     Verified against the exact THUG source algorithm (tools/build_unswizzle_table.py).
    /// </summary>
    internal static byte[] UnswizzlePsmt8(ReadOnlySpan<byte> swizzled, int width, int height)
    {
        // Build the forward Conv8to32 mapping on identity data, then invert it.
        // mapping[out_pos] = linear_pos means file byte at out_pos came from linear position.
        var mapping = GetOrBuildMapping(Conv8to32Cache, width, height, BuildConv8to32Mapping);
        var output = new byte[width * height];

        for (var filePos = 0; filePos < mapping.Length && filePos < swizzled.Length; filePos++)
        {
            var linearPos = mapping[filePos];
            if (linearPos >= 0 && linearPos < output.Length)
                output[linearPos] = swizzled[filePos];
        }

        return output;
    }

    /// <summary>
    ///     Un-swizzles PSMT4 pixel data back to linear scan-line order.
    ///     The THUG engine uses two swizzle paths for PSMT4 (texture.cpp lines 742-799):
    ///     1. Conv4to32 (PSMCT32 layout) -- for 32x32, 64x64, >=128x128
    ///     2. Conv4to16 (PSMCT16 layout) -- for other dimensions (GS VRAM simulation)
    ///     The CanConv4to32 table is checked first; if false, CanConv4to16 is used.
    /// </summary>
    internal static byte[] UnswizzlePsmt4(ReadOnlySpan<byte> swizzled, int width, int height)
    {
        int[] mapping;
        if (CanConv4to32(width, height))
            mapping = GetOrBuildMapping(Conv4to32Cache, width, height, BuildConv4to32Mapping);
        else if (CanConv4to16(width, height))
            mapping = GetOrBuildMapping(Conv4to16Cache, width, height, BuildConv4to16Mapping);
        else
            return swizzled.ToArray(); // Neither table supports this size -- data is linear

        return ApplyNibbleMapping(swizzled, mapping, width * height);
    }

    /// <summary>
    ///     Checks the CanConv4to32 table from texturemem.cpp.
    /// </summary>
    private static bool CanConv4to32(int width, int height)
    {
        var tw = NumBits(width) - 1;
        var th = NumBits(height) - 1;
        return tw >= 0 && tw < 10 && th >= 0 && th < 10 && CanConv4to32Table[th, tw];
    }

    /// <summary>
    ///     Checks the CanConv4to16 table from texturemem.cpp.
    /// </summary>
    private static bool CanConv4to16(int width, int height)
    {
        var tw = NumBits(width) - 1;
        var th = NumBits(height) - 1;
        return tw >= 0 && tw < 10 && th >= 0 && th < 10 && CanConv4to16Table[th, tw];
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

    /// <summary>
    ///     Builds the Conv8to32 position mapping by running the forward algorithm on identity data.
    ///     Returns mapping[output_pos] = input_linear_pos.
    ///     Exact port of texturemem.cpp Conv8to32 -> PageConv8to32 -> BlockConv8to32.
    /// </summary>
    private static int[] BuildConv8to32Mapping(int width, int height)
    {
        const int psmt8PageW = 128, psmt8PageH = 64;
        const int psmct32PageW = 64, psmct32PageH = 32;

        var nPageW = (width - 1) / psmt8PageW + 1;
        var nPageH = (height - 1) / psmt8PageH + 1;

        int nInputWidthByte, nOutputWidthByte, nInputHeight, nOutputHeight;
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

        // Identity: input[pos] = pos
        var identity = new int[width * height];
        for (var i = 0; i < identity.Length; i++) identity[i] = i;

        var output = new int[width * height];

        for (var pi = 0; pi < nPageH; pi++)
        {
            for (var pj = 0; pj < nPageW; pj++)
            {
                // Copy input page
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

                // Convert page (PageConv8to32)
                var outputPage = PageConv8to32(inputPage);

                // Copy output page
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

    /// <summary>
    ///     Port of texturemem.cpp PageConv8to32: converts one page of position-tracked data.
    /// </summary>
    private static int[] PageConv8to32(int[] inputPage)
    {
        const int psmt8BlockW = 16, psmt8BlockH = 16;
        const int psmct32BlockW = 8, psmct32BlockH = 8;
        const int inputPageLineSize = 128;
        const int outputPageLineSize = 256;
        const int nWidth = 8; // 128 / 16
        const int nHeight = 4; // 64 / 16

        // Block arrangement tables (same for both PSMT8 and PSMCT32)
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

        // Build inverse block32 lookup
        var index32H = new int[32];
        var index32V = new int[32];
        var idx = 0;
        for (var i = 0; i < 4; i++)
        for (var j = 0; j < 8; j++)
        {
            index32H[blockTable32[idx]] = j;
            index32V[blockTable32[idx]] = i;
            idx++;
        }

        var outputPage = new int[outputPageLineSize * psmct32BlockH * 4];

        for (var bi = 0; bi < nHeight; bi++)
        {
            for (var bj = 0; bj < nWidth; bj++)
            {
                // Extract input block (16x16) from page
                var inputBlock = new int[256];
                for (var k = 0; k < psmt8BlockH; k++)
                {
                    var srcOff = psmt8BlockH * bi * inputPageLineSize + bj * psmt8BlockW + k * inputPageLineSize;
                    for (var c = 0; c < psmt8BlockW; c++)
                        if (srcOff + c < inputPage.Length)
                            inputBlock[k * psmt8BlockW + c] = inputPage[srcOff + c];
                }

                var inBlockNb = blockTable8[bi * nWidth + bj];

                // Convert block
                var outputBlock = BlockConv8to32(inputBlock);

                // Write output block (8x8 "pixels" of 4 bytes = 32 bytes/row, 8 rows)
                var outBaseRow = psmct32BlockH * index32V[inBlockNb];
                var outBaseCol = index32H[inBlockNb] * psmct32BlockW * 4;
                for (var k = 0; k < psmct32BlockH; k++)
                for (var c = 0; c < psmct32BlockW * 4; c++)
                {
                    var outOff = (outBaseRow + k) * outputPageLineSize + outBaseCol + c;
                    if (outOff < outputPage.Length)
                        outputPage[outOff] = outputBlock[k * psmct32BlockW * 4 + c];
                }
            }
        }

        return outputPage;
    }

    /// <summary>
    ///     Port of texturemem.cpp BlockConv8to32: rearranges 256 values within a 16x16 block.
    /// </summary>
    private static int[] BlockConv8to32(int[] input)
    {
        int[] lut =
        {
            0, 36, 8, 44, 1, 37, 9, 45, 2, 38, 10, 46, 3, 39, 11, 47,
            4, 32, 12, 40, 5, 33, 13, 41, 6, 34, 14, 42, 7, 35, 15, 43,
            16, 52, 24, 60, 17, 53, 25, 61, 18, 54, 26, 62, 19, 55, 27, 63,
            20, 48, 28, 56, 21, 49, 29, 57, 22, 50, 30, 58, 23, 51, 31, 59,
            // odd column
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
            var pInBase = k * 64;
            for (var i = 0; i < 16; i++)
            for (var j = 0; j < 4; j++)
                output[index1++] = input[pInBase + lut[index0++]];
        }

        return output;
    }

    /// <summary>
    ///     Applies a nibble-position mapping to un-swizzle PSMT4 data.
    ///     mapping[fileNibble] = linearNibble.
    /// </summary>
    private static byte[] ApplyNibbleMapping(ReadOnlySpan<byte> swizzled, int[] mapping, int totalNibbles)
    {
        var output = new byte[totalNibbles / 2];

        for (var outNibble = 0; outNibble < mapping.Length; outNibble++)
        {
            var linearNibble = mapping[outNibble];
            if (linearNibble < 0 || linearNibble >= totalNibbles) continue;

            // Read nibble from swizzled data at file position outNibble
            var swizByte = outNibble / 2;
            var swizShift = (outNibble & 1) * 4;
            if (swizByte >= swizzled.Length) continue;
            var value = (swizzled[swizByte] >> swizShift) & 0x0F;

            // Write nibble to output at linear position linearNibble
            var outByte = linearNibble / 2;
            var outShift = (linearNibble & 1) * 4;
            output[outByte] = (byte)((output[outByte] & ~(0x0F << outShift)) | (value << outShift));
        }

        return output;
    }

    /// <summary>
    ///     Builds the Conv4to32 nibble-position mapping by running the forward algorithm on identity data.
    ///     Returns mapping[output_nibble] = input_linear_nibble.
    /// </summary>
    private static int[] BuildConv4to32Mapping(int width, int height)
    {
        const int psmt4PageW = 128, psmt4PageH = 128;
        const int psmct32PageH = 32;
        const int psmct32PageWNibbles = 512; // 64 pixels x 4 bytes x 2 nibbles

        var nPageW = (width - 1) / psmt4PageW + 1;
        var nPageH = (height - 1) / psmt4PageH + 1;

        // Dimensions in nibbles (= pixels for 4-bit). Derived from texturemem.cpp byte-level x 2.
        int nInputWidth, nOutputWidth, nInputHeight, nOutputHeight;
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
        for (var i = 0; i < identity.Length; i++) identity[i] = i;

        var output = new int[totalNibbles];

        for (var pi = 0; pi < nPageH; pi++)
        {
            for (var pj = 0; pj < nPageW; pj++)
            {
                // Copy input page (nibble level)
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

                // Copy output page (nibble level)
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

    /// <summary>
    ///     Port of texturemem.cpp PageConv4to32 at nibble level.
    ///     PSMT4 page: 128x128 pixels, 4x8 blocks of 32x16.
    /// </summary>
    private static int[] PageConv4to32(int[] inputPage)
    {
        const int psmt4BlockW = 32, psmt4BlockH = 16;
        const int psmct32BlockW = 8, psmct32BlockH = 8;
        const int inputPageLineNibbles = 128;
        const int outputPageLineNibbles = 512; // 64 x 4 x 2
        const int nWidth = 4; // 128 / 32
        const int nHeight = 8; // 128 / 16
        const int outputBlockRowNibbles = psmct32BlockW * 4 * 2; // 64

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
        for (var j = 0; j < 8; j++)
        {
            index32H[blockTable32[idx]] = j;
            index32V[blockTable32[idx]] = i;
            idx++;
        }

        var outputPage = new int[outputPageLineNibbles * psmct32BlockH * 4]; // 512 x 32

        for (var bi = 0; bi < nHeight; bi++)
        {
            for (var bj = 0; bj < nWidth; bj++)
            {
                // Extract input block (32x16 pixels = 512 nibbles)
                var inputBlock = new int[512];
                for (var k = 0; k < psmt4BlockH; k++)
                {
                    var srcOff = (psmt4BlockH * bi + k) * inputPageLineNibbles + bj * psmt4BlockW;
                    for (var c = 0; c < psmt4BlockW; c++)
                        if (srcOff + c < inputPage.Length)
                            inputBlock[k * psmt4BlockW + c] = inputPage[srcOff + c];
                }

                var inBlockNb = blockTable4[bi * nWidth + bj];
                var outputBlock = BlockConv4to32(inputBlock);

                // Place output block (8 rows x 64 nibbles)
                var outBaseRow = psmct32BlockH * index32V[inBlockNb];
                var outBaseCol = index32H[inBlockNb] * outputBlockRowNibbles;
                for (var k = 0; k < psmct32BlockH; k++)
                for (var c = 0; c < outputBlockRowNibbles; c++)
                {
                    var outOff = (outBaseRow + k) * outputPageLineNibbles + outBaseCol + c;
                    if (outOff < outputPage.Length)
                        outputPage[outOff] = outputBlock[k * outputBlockRowNibbles + c];
                }
            }
        }

        return outputPage;
    }

    /// <summary>
    ///     Port of texturemem.cpp BlockConv4to32 at nibble level.
    ///     Rearranges 512 nibble positions within a 32x16 block.
    /// </summary>
    private static int[] BlockConv4to32(int[] input)
    {
        int[] lut =
        {
            // even column (128 entries)
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
            // odd column (128 entries)
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
            var pInBase = k * 128; // 4 chunks of 128 nibbles each
            for (var i = 0; i < 16; i++)
            for (var j = 0; j < 4; j++)
            {
                output[outIdx++] = input[pInBase + lut[index0++]]; // low nibble
                output[outIdx++] = input[pInBase + lut[index0++]]; // high nibble
            }
        }

        return output;
    }

    /// <summary>
    ///     Builds the Conv4to16 nibble-position mapping using GS VRAM simulation.
    ///     Port of texturemem.cpp Conv4to16: writeTexPSMT4 -> simulated VRAM -> readTexPSMCT16.
    ///     Returns mapping[output_nibble] = input_linear_nibble.
    /// </summary>
    private static int[] BuildConv4to16Mapping(int width, int height)
    {
        var totalNibbles = width * height;

        // Calculate VRAM size: round up to page boundaries
        var min4W = Math.Max(width, 128);
        var min4H = Math.Max(height, 128);
        var min16W = Math.Max(width / 2, 64);
        var min16H = Math.Max(height / 2, 64);

        var pages4 = min4W / 128 * (min4H / 128);
        var pages16 = min16W / 64 * (min16H / 64);
        var blocksNeeded = Math.Max(pages4 * 32, pages16 * 32);
        var wordsNeeded = blocksNeeded * 64;

        // VRAM nibble position tracker: vram[nibble_slot] = linear_nibble_position
        var vramNibbles = new int[wordsNeeded * 8]; // 8 nibbles per 32-bit word
        Array.Fill(vramNibbles, -1);

        // TBW parameters (from Conv4to16 source)
        var psmt4Tbw = Math.Max(width / 128, 1) * 2;
        var psmct16Tbw = Math.Max(width / 2 / 128, 1) * 2;

        // --- writeTexPSMT4: write linear nibble positions to VRAM ---
        var linPos = 0;
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
                var dbwShifted = psmt4Tbw >> 1; // writeTexPSMT4 does dbw >>= 1
                var page = pageX + pageY * dbwShifted;

                var px = x - pageX * 128;
                var blockX = px / 32;
                var block = Block4[blockX + blockY * 4];

                var bx = px - blockX * 32;
                var cw = ColumnWord4[column & 1, bx + cy * 32];
                var cb = ColumnByte4[bx + cy * 32];

                var gsIndex = page * 2048 + block * 64 + column * 16 + cw;
                // Nibble address within VRAM: word has 4 bytes = 8 nibbles
                // cb>>1 = byte index (0-3), cb&1 = nibble half (0=low, 1=high)
                var vramNibIdx = gsIndex * 8 + (cb >> 1) * 2 + (cb & 1);

                if (vramNibIdx < vramNibbles.Length)
                    vramNibbles[vramNibIdx] = linPos;

                linPos++;
            }
        }

        // --- readTexPSMCT16: read from VRAM in PSMCT16 order ---
        var outW = width / 2;
        var outH = height / 2;
        var mapping = new int[totalNibbles];
        Array.Fill(mapping, -1);
        var outNibPos = 0;

        for (var y = 0; y < outH; y++)
        {
            var pageY = y / 64;
            var py = y - pageY * 64;
            var blockY = py / 8;
            var by = py - blockY * 8;
            var column = by / 2;
            var cy = by - column * 2;

            for (var x = 0; x < outW; x++)
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
                // PSMCT16 pixel = 16 bits = half of a 32-bit word
                // ch selects which half: 0 = low 16 bits (4 nibbles 0-3), 1 = high 16 bits (4 nibbles 4-7)
                var baseNib = gsIndex * 8 + ch * 4;

                for (var nib = 0; nib < 4 && outNibPos < mapping.Length; nib++)
                {
                    var nibAddr = baseNib + nib;
                    if (nibAddr < vramNibbles.Length)
                        mapping[outNibPos] = vramNibbles[nibAddr];
                    outNibPos++;
                }
            }
        }

        return mapping;
    }

    private static int[] GetOrBuildMapping(
        Dictionary<(int, int), int[]> cache, int width, int height,
        Func<int, int, int[]> buildMapping)
    {
        var key = (width, height);
        lock (CacheLock)
        {
            if (cache.TryGetValue(key, out var cached))
                return cached;
        }

        var mapping = buildMapping(width, height);

        lock (CacheLock)
        {
            cache.TryAdd(key, mapping);
            return cache[key];
        }
    }
}
