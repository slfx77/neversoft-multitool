using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>
///     Vicarious Visions' own DS streaming-audio format — the Downhill Jam and
///     Proving Ground soundtracks, 86 minutes of it across 35 files. (American
///     Sk8land ships none: it uses a stock Nintendo SDAT instead.)
///
///     A 512-byte header, then 4-bit IMA-family ADPCM in fixed 16 KB blocks:
///     <code>
///     0x00 u32 magic      = 'sawh' on disk
///     0x04 u32 blockSize  = 16384
///     0x08 u32 sampleRate = 22019
///     0x0C u32 channels   = 1
///     0x10 u32 0
///     0x14 u32 storedBytes = fileSize - 512 = ceil(dataBytes / 512) * 512
///     0x18 u32 dataBytes   = the payload bytes that carry audio
///     0x1C..0x1FF zero
///     </code>
///     Every constant above holds for all 35 shipped files, and the game's own
///     header WRITER — these carts contain a runtime <c>.hwas</c> recorder — builds
///     the fields in this order and writes 0x200 bytes. <c>dataBytes</c> is always
///     short of a whole block (the last one runs 1,175 to 15,975 bytes) and is odd
///     in 11 files, so the trailing padding must be dropped rather than decoded.
///
///     <b>Each block restarts the codec.</b> There is no per-block header to say so
///     — which is why an earlier reading took the payload for one continuous stream —
///     but the predictor and step index reset to zero every 16,384 bytes, and the
///     file says so three ways: the first nibble of every block is 0 in 3,500 of
///     3,500 blocks (at half or a quarter of that stride, 56% and 35%); decoding
///     continuously saturates 0.13-2.95% of samples against 0.000-0.021% with the
///     reset; and per-block DC wanders to -12,539 continuously but stays within
///     about ±100 with it.
///
///     The codec itself was read out of the carts' own ADPCM <b>encoder</b>
///     (Downhill Jam <c>AdpcmEncodeSample</c> @ <c>0x020AE6EC</c>, Proving Ground
///     <c>0x0206D7CC</c>, identical), whose index and step tables sit beside it. The
///     encoder packs the low nibble first and returns one byte per two samples;
///     <see cref="Decode" /> is its exact inverse, which round-trips against it at a
///     mean 99.79% sample match — the ceiling for a lossy codec. No software DECODER
///     exists in any of the three carts: the step table occurs exactly once and every
///     reference to it belongs to the encoder, so playback is presumably the SPU's
///     hardware ADPCM. That leaves one detail unproven, and it is worth stating: the
///     low predictor clamp is taken as -32768 because that is what the encoder uses,
///     while -32767 (the Nitro SDK's) differs on 8.3% of samples by at most 1 LSB.
/// </summary>
public sealed class HwasStream
{
    /// <summary>'sawh' as it sits on disk, read as a little-endian u32.</summary>
    private const uint Magic = 0x68776173;

    private const int HeaderBytes = 512;

    private static readonly short[] StepTable =
    [
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143, 157, 173, 190, 209, 230,
        253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963,
        1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327,
        3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794,
        32767
    ];

    private static readonly int[] IndexTable = [-1, -1, -1, -1, 2, 4, 6, 8];

    private readonly byte[] _data;

    private HwasStream(byte[] data, int blockSize, int sampleRate, int channels, int dataBytes)
    {
        _data = data;
        BlockSize = blockSize;
        SampleRate = sampleRate;
        Channels = channels;
        DataBytes = dataBytes;
    }

    /// <summary>Bytes of payload per codec reset. 16,384 in every shipped file.</summary>
    public int BlockSize { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    /// <summary>Payload bytes that carry audio; the rest of the file is padding.</summary>
    public int DataBytes { get; }

    public int SampleCount => DataBytes * 2;

    public double DurationSeconds =>
        SampleRate > 0 && Channels > 0 ? (double)SampleCount / Channels / SampleRate : 0;

    public static bool IsHwas(ReadOnlySpan<byte> data)
    {
        return data.Length >= HeaderBytes
               && BinaryPrimitives.ReadUInt32LittleEndian(data) == Magic;
    }

    /// <summary>
    ///     Parses the header. Throws <see cref="InvalidDataException" /> when a field
    ///     contradicts the file's own size — the size identities are exact across the
    ///     corpus, so a file that fails one is not this format.
    /// </summary>
    public static HwasStream Parse(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (!IsHwas(data))
            throw new InvalidDataException("Not a Vicarious Visions .hwas stream.");

        var blockSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        var channels = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        var storedBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(20)) & 0xFFFFFFFF;
        var dataBytes = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(24)) & 0xFFFFFFFF;

        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new InvalidDataException($"hwas block size {blockSize} is not a power of two.");
        if (sampleRate is <= 0 or > 96000)
            throw new InvalidDataException($"hwas sample rate {sampleRate} is out of range.");
        if (channels is not (1 or 2))
            throw new InvalidDataException($"hwas channel count {channels} is not 1 or 2.");
        if (HeaderBytes + storedBytes != data.Length)
            throw new InvalidDataException("hwas stored size does not match the file length.");
        if (dataBytes < 0 || dataBytes > storedBytes)
            throw new InvalidDataException("hwas payload size exceeds the stored size.");

        return new HwasStream(data, blockSize, sampleRate, channels, (int)dataBytes);
    }

    /// <summary>
    ///     Decodes the whole stream to interleaved PCM16, resetting the codec at every
    ///     block boundary.
    /// </summary>
    public short[] Decode()
    {
        var samples = new short[SampleCount];
        var predictor = 0;
        var index = 0;
        var at = 0;

        for (var i = 0; i < DataBytes; i++)
        {
            if (i % BlockSize == 0)
            {
                predictor = 0;
                index = 0;
            }

            var packed = _data[HeaderBytes + i];
            // The encoder writes the low nibble first, so the decoder reads it first.
            samples[at++] = Step(packed & 0x0F, ref predictor, ref index);
            samples[at++] = Step(packed >> 4, ref predictor, ref index);
        }

        return samples;
    }

    private static short Step(int code, ref int predictor, ref int index)
    {
        var step = (int)StepTable[index];

        // Four SEPARATE truncating shifts, exactly as the encoder accumulates them.
        // The common one-liner ((code & 7) * 2 + 1) * step / 8 is NOT equivalent.
        var diff = step >> 3;
        if ((code & 4) != 0)
            diff += step;
        if ((code & 2) != 0)
            diff += step >> 1;
        if ((code & 1) != 0)
            diff += step >> 2;

        predictor += (code & 8) != 0 ? -diff : diff;
        predictor = Math.Clamp(predictor, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[code & 7], 0, StepTable.Length - 1);
        return (short)predictor;
    }
}
