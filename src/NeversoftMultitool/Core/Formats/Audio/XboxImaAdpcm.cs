namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Xbox ADPCM (<c>WAVE_FORMAT_XBOX_ADPCM</c>, wFormatTag 0x0069) — the IMA/DVI
///     variant THUG2 Xbox ships every sound effect in.
///     <para>
///         Block layout, per channel, 36 bytes: <c>int16</c> LE initial predictor,
///         <c>uint8</c> step index, one reserved byte (0 in every corpus block),
///         then 32 payload bytes of 4-bit nibbles, LOW nibble first. Stereo
///         interleaves whole 36-byte sub-blocks, so nBlockAlign is 36 x channels.
///         State re-seeds from the header at every block; it does not carry across.
///     </para>
///     <para>
///         A block emits exactly 64 samples: <b>the header predictor as sample 0,
///         then 63 nibble-decoded samples</b>. The 64th nibble is encoder padding
///         and is discarded. That is not an assumption — decoding
///         CarBrakeSqueal.pcm both ways and diffing against ffmpeg's dedicated
///         <c>adpcm_ima_xbox</c> decoder gives a bit-exact match for this layout
///         (30,592 of 30,592 samples) and a mismatch for "all 64 nibbles, predictor
///         not emitted". The format also self-describes it: wSamplesPerBlock is 64
///         in all 2,752 corpus files and nAvgBytesPerSec equals
///         <c>rate * 36 / 64</c> truncated at every sample rate they use.
///     </para>
/// </summary>
public static class XboxImaAdpcm
{
    /// <summary>wFormatTag for WAVE_FORMAT_XBOX_ADPCM.</summary>
    public const int FormatTag = 0x0069;

    /// <summary>Bytes per block, per channel.</summary>
    public const int BlockAlignPerChannel = 36;

    /// <summary>Samples a single block decodes to, per channel.</summary>
    public const int SamplesPerBlock = 64;

    private const int PayloadBytes = 32;
    private const int MaxStepIndex = 88;

    private static readonly int[] IndexTable =
        [-1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8];

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
    ///     Decodes a whole ADPCM stream to interleaved 16-bit PCM. A trailing
    ///     partial block is ignored, matching the engine
    ///     (<c>Gel/SoundFX/Xbox/p_sfx.cpp</c> truncates to a whole multiple of
    ///     nBlockAlign before submitting the buffer).
    /// </summary>
    public static short[] Decode(ReadOnlySpan<byte> data, int channels)
    {
        if (channels is not (1 or 2))
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Xbox ADPCM supports 1 or 2 channels");

        var blockAlign = BlockAlignPerChannel * channels;
        var blocks = data.Length / blockAlign;
        if (blocks == 0)
            return [];

        var samples = new short[blocks * SamplesPerBlock * channels];

        for (var block = 0; block < blocks; block++)
        {
            var blockBase = block * blockAlign;
            var outBase = block * SamplesPerBlock * channels;

            for (var channel = 0; channel < channels; channel++)
            {
                DecodeBlock(
                    data.Slice(blockBase + channel * BlockAlignPerChannel, BlockAlignPerChannel),
                    samples,
                    outBase + channel,
                    channels);
            }
        }

        return samples;
    }

    private static void DecodeBlock(ReadOnlySpan<byte> block, short[] samples, int outIndex, int stride)
    {
        int predictor = BitConverter.ToInt16(block[..2]);
        var index = Math.Clamp((int)block[2], 0, MaxStepIndex);

        // Sample 0 is the block header's predictor, emitted verbatim.
        samples[outIndex] = (short)predictor;
        var written = 1;

        for (var i = 0; i < PayloadBytes && written < SamplesPerBlock; i++)
        {
            var b = block[4 + i];
            for (var half = 0; half < 2 && written < SamplesPerBlock; half++)
            {
                // Low nibble first.
                var nibble = half == 0 ? b & 0x0F : b >> 4;

                var step = StepTable[index];
                // Multiply form. Equivalent within <=32 LSB to the canonical
                // shift-accumulate form, but this is what ffmpeg/vgmstream emit for
                // tag 0x0069, so our output matches theirs bit for bit.
                var diff = ((2 * (nibble & 7) + 1) * step) >> 3;
                predictor = (nibble & 8) != 0 ? predictor - diff : predictor + diff;
                predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
                index = Math.Clamp(index + IndexTable[nibble], 0, MaxStepIndex);

                samples[outIndex + written * stride] = (short)predictor;
                written++;
            }
        }
    }
}
