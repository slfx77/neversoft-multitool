using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>
///     Nintendo Nitro SDK <c>STRM</c> streamed audio. American Sk8land ships its
///     whole 62-minute soundtrack this way — 30 tracks, 40 MB, inside the
///     <c>sound_stream.sdat</c> archive in the cart's Nitro filesystem, with real
///     names (<c>STRM_DRUMS_OF_FIRE</c>, <c>STRM_CALIFORNIA</c>, …) in the SDAT's
///     SYMB block.
///
///     <c>HEAD</c> layout after the 16-byte Nitro file header:
///     <code>
///     0x18 u8  waveType (0 PCM8 / 1 PCM16 / 2 ADPCM)
///     0x19 u8  loop
///     0x1A u8  channels
///     0x1B u8  reserved
///     0x1C u16 sampleRate
///     0x1E u16 time            (ARM7 timer reload)
///     0x20 u32 loopOffset
///     0x24 u32 sampleCount
///     0x28 u32 dataOffset      (absolute; the DATA block's payload)
///     0x2C u32 blockCount
///     0x30 u32 blockLength     (bytes, PER CHANNEL)
///     0x34 u32 samplesPerBlock
///     0x38 u32 lastBlockLength
///     0x3C u32 lastBlockSamples
///     </code>
///
///     The field reading is not assumed — it is pinned by an arithmetic identity
///     that holds for all 30 corpus tracks:
///     <c>(blockCount - 1) * samplesPerBlock + lastBlockSamples == sampleCount</c>.
///     Blocks are stored channel-interleaved (every channel's block N in turn),
///     and each ADPCM block re-seeds its own predictor, so a block is decodable on
///     its own.
/// </summary>
public sealed class StrmFile
{
    private const int FileHeaderSize = 16;

    private StrmFile(NitroWaveType waveType, bool loops, int channels, int sampleRate, int sampleCount,
        int dataOffset, int blockCount, int blockLength, int samplesPerBlock, int lastBlockLength,
        int lastBlockSamples, byte[] data)
    {
        WaveType = waveType;
        Loops = loops;
        Channels = channels;
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        DataOffset = dataOffset;
        BlockCount = blockCount;
        BlockLength = blockLength;
        SamplesPerBlock = samplesPerBlock;
        LastBlockLength = lastBlockLength;
        LastBlockSamples = lastBlockSamples;
        _data = data;
    }

    private readonly byte[] _data;

    public NitroWaveType WaveType { get; }
    public bool Loops { get; }
    public int Channels { get; }
    public int SampleRate { get; }
    public int SampleCount { get; }
    public int DataOffset { get; }
    public int BlockCount { get; }
    public int BlockLength { get; }
    public int SamplesPerBlock { get; }
    public int LastBlockLength { get; }
    public int LastBlockSamples { get; }

    public double DurationSeconds => SampleRate > 0 ? (double)SampleCount / SampleRate : 0;

    public static bool IsStrm(ReadOnlySpan<byte> data)
    {
        return data.Length >= 0x40 && data[..4].SequenceEqual("STRM"u8);
    }

    public static StrmFile Parse(byte[] data)
    {
        if (!IsStrm(data))
            throw new InvalidDataException("Not a STRM stream (missing the STRM magic).");

        var span = data.AsSpan();
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(span[12..]);
        if (headerSize < FileHeaderSize || headerSize + 8 > data.Length)
            throw new InvalidDataException($"STRM header size {headerSize} is outside the file.");
        if (!span.Slice(headerSize, 4).SequenceEqual("HEAD"u8))
            throw new InvalidDataException("STRM is missing its HEAD block.");

        var waveType = (NitroWaveType)span[0x18];
        if (waveType is not (NitroWaveType.Pcm8 or NitroWaveType.Pcm16 or NitroWaveType.Adpcm))
            throw new InvalidDataException($"STRM wave type {span[0x18]} is not one of PCM8/PCM16/ADPCM.");

        var loops = span[0x19] != 0;
        int channels = span[0x1A];
        var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(span[0x1C..]);
        var sampleCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x24..]);
        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(span[0x28..]);
        var blockCount = BinaryPrimitives.ReadUInt32LittleEndian(span[0x2C..]);
        var blockLength = BinaryPrimitives.ReadUInt32LittleEndian(span[0x30..]);
        var samplesPerBlock = BinaryPrimitives.ReadUInt32LittleEndian(span[0x34..]);
        var lastBlockLength = BinaryPrimitives.ReadUInt32LittleEndian(span[0x38..]);
        var lastBlockSamples = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);

        if (channels is < 1 or > 2)
            throw new InvalidDataException($"STRM declares {channels} channels.");
        if (sampleRate == 0)
            throw new InvalidDataException("STRM declares a zero sample rate.");
        if (blockCount == 0 || blockLength == 0)
            throw new InvalidDataException("STRM declares no audio blocks.");
        if (dataOffset > (uint)data.Length)
            throw new InvalidDataException($"STRM data offset {dataOffset} is outside the file.");

        // The identity that pins the whole field reading.
        var declared = (long)(blockCount - 1) * samplesPerBlock + lastBlockSamples;
        if (declared != sampleCount)
        {
            throw new InvalidDataException(
                $"STRM block table describes {declared} samples but the header declares {sampleCount}.");
        }

        var needed = (long)dataOffset + (long)(blockCount - 1) * blockLength * channels
                     + (long)lastBlockLength * channels;
        if (needed > data.Length + 1) // the corpus pads the final block by at most a byte
            throw new InvalidDataException(
                $"STRM audio data needs {needed} bytes but the file is {data.Length}.");

        return new StrmFile(waveType, loops, channels, sampleRate, checked((int)sampleCount),
            checked((int)dataOffset), checked((int)blockCount), checked((int)blockLength),
            checked((int)samplesPerBlock), checked((int)lastBlockLength),
            checked((int)lastBlockSamples), data);
    }

    /// <summary>Decodes the stream to interleaved PCM16 frames.</summary>
    public short[] Decode()
    {
        var result = new short[(long)SampleCount * Channels <= int.MaxValue
            ? SampleCount * Channels
            : throw new InvalidDataException("STRM is too long to decode into a single buffer.")];

        var scratch = new short[Math.Max(SamplesPerBlock, LastBlockSamples)];
        var position = DataOffset;
        var frame = 0;

        for (var block = 0; block < BlockCount; block++)
        {
            var last = block == BlockCount - 1;
            var length = last ? LastBlockLength : BlockLength;
            var samples = last ? LastBlockSamples : SamplesPerBlock;

            for (var channel = 0; channel < Channels; channel++)
            {
                var available = Math.Max(0, Math.Min(length, _data.Length - position));
                var span = _data.AsSpan(position, available);
                var decoded = DecodeBlock(span, scratch.AsSpan(0, samples));

                for (var i = 0; i < samples; i++)
                {
                    var at = (frame + i) * Channels + channel;
                    if (at < result.Length)
                        result[at] = i < decoded ? scratch[i] : (short)0;
                }

                position += length;
            }

            frame += samples;
        }

        return result;
    }

    private int DecodeBlock(ReadOnlySpan<byte> block, Span<short> destination)
    {
        if (WaveType == NitroWaveType.Adpcm)
            return NitroAdpcm.Decode(block, destination);

        var decoded = SwavFile.DecodeWave(WaveType, block);
        var n = Math.Min(decoded.Length, destination.Length);
        decoded.AsSpan(0, n).CopyTo(destination);
        return n;
    }
}
