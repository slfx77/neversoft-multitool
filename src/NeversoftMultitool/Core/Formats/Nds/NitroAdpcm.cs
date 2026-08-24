namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>
///     Nintendo DS IMA-ADPCM, the wave type 2 used by <see cref="SwavFile" /> and
///     <see cref="StrmFile" />.
///
///     Transcribed from GBATEK's description of the ARM7 sound hardware, whose
///     integer truncation is load-bearing: the step is divided by 8, 4, 2 and 1
///     SEPARATELY and each quotient truncates on its own, so the common
///     "((n &amp; 7) * 2 + 1) * step / 8" one-liner is NOT equivalent and drifts.
///     Saturation is to ±0x7FFF, not the full s16 range.
///
///     Each block is self-contained: a 4-byte header carrying the initial
///     predictor and step index, then 4-bit nibbles low-nibble-first. The header
///     sample is state, not output, so a block of N bytes yields (N-4)*2 samples —
///     which is exactly the identity every STRM in the corpus satisfies
///     (blockLength 512 → samplesPerBlock 1016).
/// </summary>
internal static class NitroAdpcm
{
    public const int HeaderSize = 4;

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411, 1552,
        1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484,
        7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385,
        24623, 27086, 29794, 32767
    ];

    /// <summary>Samples a block of <paramref name="blockBytes" /> bytes decodes to.</summary>
    public static int SampleCount(int blockBytes)
    {
        return blockBytes <= HeaderSize ? 0 : (blockBytes - HeaderSize) * 2;
    }

    /// <summary>
    ///     Decodes one self-contained block into <paramref name="destination" />,
    ///     returning the number of samples written (never more than the destination
    ///     length, so a caller that knows the real sample count can clip the odd
    ///     trailing nibble a final block carries).
    /// </summary>
    public static int Decode(ReadOnlySpan<byte> block, Span<short> destination)
    {
        if (block.Length <= HeaderSize || destination.IsEmpty)
            return 0;

        var header = (uint)(block[0] | (block[1] << 8) | (block[2] << 16) | (block[3] << 24));
        var predictor = (short)(header & 0xFFFF);
        var index = (int)((header >> 16) & 0x7F);
        if (index > 88)
            index = 88;

        var written = 0;
        for (var i = HeaderSize; i < block.Length && written < destination.Length; i++)
        {
            var packed = block[i];
            written += Step(packed & 0x0F, ref predictor, ref index, destination, written);
            if (written >= destination.Length)
                break;
            written += Step(packed >> 4, ref predictor, ref index, destination, written);
        }

        return written;
    }

    private static int Step(int nibble, ref short predictor, ref int index, Span<short> destination, int at)
    {
        var step = StepTable[index];

        // Four separate truncating divisions — see the class remarks.
        var diff = step / 8;
        if ((nibble & 1) != 0)
            diff += step / 4;
        if ((nibble & 2) != 0)
            diff += step / 2;
        if ((nibble & 4) != 0)
            diff += step;

        var sample = (nibble & 8) != 0 ? predictor - diff : predictor + diff;
        predictor = (short)Math.Clamp(sample, -0x7FFF, 0x7FFF);

        index = Math.Clamp(index + IndexTable[nibble & 7], 0, 88);
        destination[at] = predictor;
        return 1;
    }
}
