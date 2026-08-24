using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Nds;

/// <summary>Nintendo DS wave types, shared by SWAV, SWAR members and STRM.</summary>
public enum NitroWaveType
{
    Pcm8 = 0,
    Pcm16 = 1,
    Adpcm = 2
}

/// <summary>
///     Nintendo Nitro SDK <c>SWAV</c> wave — the one standard-format asset the
///     Vicarious Visions DS carts kept: 1,405 of them ship inside the GOB
///     container, all wave type 2 (IMA-ADPCM) at 11025-22050 Hz.
///
///     Layout: a 16-byte Nitro file header (<c>SWAV</c>, BOM <c>0xFEFF</c>,
///     version, file size, header size, block count) then a single <c>DATA</c>
///     block: <c>{u8 waveType, u8 loop, u16 sampleRate, u16 time, u16 loopStart,
///     u32 loopLength}</c> followed by the samples. <c>loopStart</c> and
///     <c>loopLength</c> are counted in 32-BIT WORDS, not samples or bytes, so the
///     payload is <c>(loopStart + loopLength) * 4</c> bytes — which is how the
///     parser cross-checks the DATA size it was handed.
/// </summary>
public sealed class SwavFile
{
    private const int FileHeaderSize = 16;
    private const int WaveInfoSize = 12;

    private SwavFile(NitroWaveType waveType, bool loops, int sampleRate, int loopStartWords,
        int loopLengthWords, byte[] payload)
    {
        WaveType = waveType;
        Loops = loops;
        SampleRate = sampleRate;
        LoopStartWords = loopStartWords;
        LoopLengthWords = loopLengthWords;
        Payload = payload;
    }

    public NitroWaveType WaveType { get; }
    public bool Loops { get; }
    public int SampleRate { get; }
    public int LoopStartWords { get; }
    public int LoopLengthWords { get; }

    /// <summary>Raw encoded samples (ADPCM nibbles, or PCM8/PCM16).</summary>
    public byte[] Payload { get; }

    public static bool IsSwav(ReadOnlySpan<byte> data)
    {
        return data.Length >= FileHeaderSize + 8 && data[..4].SequenceEqual("SWAV"u8);
    }

    public static SwavFile Parse(ReadOnlySpan<byte> data)
    {
        if (!IsSwav(data))
            throw new InvalidDataException("Not a SWAV wave (missing the SWAV magic).");

        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        if (headerSize < FileHeaderSize || headerSize + 8 > data.Length)
            throw new InvalidDataException($"SWAV header size {headerSize} is outside the file.");

        var block = data[headerSize..];
        if (!block[..4].SequenceEqual("DATA"u8))
            throw new InvalidDataException("SWAV is missing its DATA block.");

        var blockSize = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        if (blockSize < 8 + WaveInfoSize || headerSize + blockSize > (uint)data.Length)
            throw new InvalidDataException($"SWAV DATA block size {blockSize} is outside the file.");

        var info = block[8..];
        var waveType = (NitroWaveType)info[0];
        if (waveType is not (NitroWaveType.Pcm8 or NitroWaveType.Pcm16 or NitroWaveType.Adpcm))
            throw new InvalidDataException($"SWAV wave type {info[0]} is not one of PCM8/PCM16/ADPCM.");

        var loops = info[1] != 0;
        var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(info[2..]);
        var loopStart = BinaryPrimitives.ReadUInt16LittleEndian(info[6..]);
        var loopLength = BinaryPrimitives.ReadUInt32LittleEndian(info[8..]);
        if (sampleRate == 0)
            throw new InvalidDataException("SWAV declares a zero sample rate.");

        var payload = block[(8 + WaveInfoSize)..(int)blockSize];

        // loopStart+loopLength are 32-bit word counts; they must describe exactly
        // the payload the DATA block carries.
        var declared = ((long)loopStart + loopLength) * 4;
        if (declared != payload.Length)
        {
            throw new InvalidDataException(
                $"SWAV declares {declared} payload bytes (loopStart {loopStart} + loopLength {loopLength} words) " +
                $"but its DATA block carries {payload.Length}.");
        }

        return new SwavFile(waveType, loops, sampleRate, loopStart, (int)loopLength, payload.ToArray());
    }

    /// <summary>Decodes the wave to interleaved mono PCM16.</summary>
    public short[] Decode()
    {
        return DecodeWave(WaveType, Payload);
    }

    /// <summary>Shared PCM8/PCM16/ADPCM decode used by SWAV and STRM blocks alike.</summary>
    internal static short[] DecodeWave(NitroWaveType waveType, ReadOnlySpan<byte> payload)
    {
        switch (waveType)
        {
            case NitroWaveType.Pcm8:
            {
                var samples = new short[payload.Length];
                for (var i = 0; i < payload.Length; i++)
                    samples[i] = (short)((sbyte)payload[i] << 8);
                return samples;
            }

            case NitroWaveType.Pcm16:
            {
                var samples = new short[payload.Length / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = BinaryPrimitives.ReadInt16LittleEndian(payload[(i * 2)..]);
                return samples;
            }

            default:
            {
                var samples = new short[NitroAdpcm.SampleCount(payload.Length)];
                var written = NitroAdpcm.Decode(payload, samples);
                return written == samples.Length ? samples : samples[..written];
            }
        }
    }
}
