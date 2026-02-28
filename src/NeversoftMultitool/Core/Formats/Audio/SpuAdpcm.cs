namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Shared SPU-ADPCM decoder used by both VAB (PS1 sound banks) and standalone VAG audio files.
///     SPU-ADPCM uses 16-byte blocks encoding 28 PCM samples each.
///     Block format: byte 0 = shift|filter, byte 1 = flags, bytes 2-15 = 28 nibble samples.
///     SPU supports 5 filter coefficients (0-4), unlike CD-XA which only uses 0-3.
/// </summary>
public static class SpuAdpcm
{
    public const int BlockSize = 16;
    public const int SamplesPerBlock = 28;

    /// <summary>
    ///     Flags in byte[1] of each ADPCM block.
    /// </summary>
    public const byte FlagEnd = 0x01; // End of stream

    public const byte FlagLoopStart = 0x04; // Loop start marker
    public const byte FlagLoop = 0x02; // Loop (jump to loop start on end)

    // SPU-ADPCM filter coefficients (f0, f1) scaled by 1/64
    private static readonly int[] F0 = [0, 60, 115, 98, 122];
    private static readonly int[] F1 = [0, 0, -52, -55, -60];

    /// <summary>
    ///     Decodes a sequence of SPU-ADPCM blocks into 16-bit PCM samples.
    ///     Stops at end-of-stream flag or end of data.
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> data)
    {
        var blockCount = data.Length / BlockSize;
        var samples = new List<short>(blockCount * SamplesPerBlock);
        int prev1 = 0, prev2 = 0;

        for (var b = 0; b < blockCount; b++)
        {
            var block = data.Slice(b * BlockSize, BlockSize);
            var flags = block[1];

            DecodeBlock(block, ref prev1, ref prev2, samples);

            if ((flags & FlagEnd) != 0)
                break;
        }

        return samples.ToArray();
    }

    /// <summary>
    ///     Decodes a single 16-byte SPU-ADPCM block, appending 28 PCM samples to the output list.
    ///     Maintains prev1/prev2 state across consecutive blocks.
    /// </summary>
    public static void DecodeBlock(ReadOnlySpan<byte> block, ref int prev1, ref int prev2, List<short> output)
    {
        var shiftFilter = block[0];
        var shift = Math.Min(shiftFilter & 0x0F, 12);
        var filter = (shiftFilter >> 4) & 0x0F;
        if (filter > 4) filter = 0;

        var f0 = F0[filter];
        var f1 = F1[filter];

        for (var i = 2; i < BlockSize; i++)
        {
            var byteVal = block[i];

            // Low nibble first (earlier sample)
            var lo = byteVal & 0x0F;
            if (lo >= 8) lo -= 16;
            var sample = (lo << (12 - shift)) + (f0 * prev1 + f1 * prev2 + 32) / 64;
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            prev2 = prev1;
            prev1 = sample;
            output.Add((short)sample);

            // High nibble (later sample)
            var hi = (byteVal >> 4) & 0x0F;
            if (hi >= 8) hi -= 16;
            sample = (hi << (12 - shift)) + (f0 * prev1 + f1 * prev2 + 32) / 64;
            sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
            prev2 = prev1;
            prev1 = sample;
            output.Add((short)sample);
        }
    }
}
