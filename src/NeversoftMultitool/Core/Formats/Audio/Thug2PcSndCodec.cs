namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Decodes the headerless 4-bit payload inside THUG2 PC <c>.snd</c> files.
/// </summary>
/// <remarks>
///     This is the software decoder at THUG2.exe VA <c>0x005F5A20</c>. It resembles
///     IMA ADPCM, but differs in two material ways: the step index is updated before
///     the step-table lookup, and the two terms of the delta are truncated separately.
///     Decoder state starts at zero for every payload.
/// </remarks>
public static class Thug2PcSndCodec
{
    private const int MaxStepIndex = 88;

    private static readonly int[] IndexDelta = [-1, -1, -1, -1, 2, 4, 6, 8];

    private static readonly int[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767
    ];

    /// <summary>
    ///     Decodes <paramref name="sampleCount" /> samples, consuming low then high
    ///     nibble from each byte. An odd count deliberately leaves the last high
    ///     nibble unused, matching the original decoder.
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> payload, int sampleCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        if (sampleCount > (long)payload.Length * 2)
        {
            throw new ArgumentException(
                $"The {payload.Length}-byte payload contains at most {payload.Length * 2L} samples, " +
                $"but {sampleCount} were requested.",
                nameof(sampleCount));
        }

        if (sampleCount == 0)
            return [];

        var samples = new short[sampleCount];
        var predictor = 0;
        var stepIndex = 0;

        for (var i = 0; i < sampleCount; i++)
        {
            var packed = payload[i >> 1];
            var nibble = (i & 1) == 0 ? packed & 0x0F : packed >> 4;
            var magnitude = nibble & 7;

            // THUG2 updates the index before choosing the step. Textbook IMA does
            // this after applying the current nibble, which produces audible drift.
            stepIndex = Math.Clamp(stepIndex + IndexDelta[magnitude], 0, MaxStepIndex);
            var step = StepTable[stepIndex];

            // Keep the shifts separate. Combining this into
            // ((2 * magnitude + 1) * step) >> 3 changes rounding for some steps.
            var difference = ((step * magnitude) >> 2) + (step >> 3);
            if ((nibble & 8) != 0)
                difference = -difference;

            predictor = Math.Clamp(predictor + difference, short.MinValue, short.MaxValue);
            samples[i] = (short)predictor;
        }

        return samples;
    }
}
