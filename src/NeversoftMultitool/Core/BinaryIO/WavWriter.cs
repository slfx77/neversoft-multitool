namespace NeversoftMultitool.Core.BinaryIO;

public static class WavWriter
{
    /// <summary>
    ///     Writes 16-bit PCM audio data to a WAV file.
    /// </summary>
    public static void WritePcm16(
        string outputPath,
        int sampleRate,
        int channels,
        short[] samples,
        Pcm16WavLoop? loop = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Length % channels != 0)
        {
            throw new ArgumentException(
                $"Sample count {samples.Length} does not contain whole {channels}-channel PCM frames.",
                nameof(samples));
        }

        var sampleFrameCount = samples.LongLength / channels;
        if (loop is { } sampleLoop)
        {
            if (sampleLoop.StartSampleFrame > sampleLoop.EndSampleFrameInclusive)
            {
                throw new ArgumentException(
                    "WAV sample loop start exceeds its inclusive end.", nameof(loop));
            }

            if (sampleLoop.EndSampleFrameInclusive >= sampleFrameCount)
            {
                throw new ArgumentException(
                    $"WAV sample loop end {sampleLoop.EndSampleFrameInclusive} is outside " +
                    $"the {sampleFrameCount}-frame PCM payload.", nameof(loop));
            }
        }

        const int bitsPerSample = 16;
        var blockAlign = (long)channels * sizeof(short);
        if (blockAlign > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(channels),
                $"PCM16 channel count {channels} produces block alignment {blockAlign}, " +
                $"which exceeds the WAV 16-bit field.");
        }

        var byteRate = (long)sampleRate * blockAlign;
        if (byteRate > uint.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sampleRate),
                $"Sample rate {sampleRate} with block alignment {blockAlign} produces byte rate {byteRate}, " +
                $"which exceeds the WAV 32-bit field.");
        }

        var dataSize = samples.LongLength * sizeof(short);
        if (dataSize > Array.MaxLength)
        {
            throw new ArgumentException(
                $"PCM16 payload requires {dataSize} bytes, exceeding the runtime byte-array limit.",
                nameof(samples));
        }

        const int singleLoopSamplerChunkSize = 60;
        const int singleLoopSamplerChunkTotalSize = 8 + singleLoopSamplerChunkSize;
        var riffSize = 36L + dataSize + (loop == null ? 0 : singleLoopSamplerChunkTotalSize);
        if (dataSize > uint.MaxValue || riffSize > uint.MaxValue)
        {
            throw new ArgumentException(
                $"PCM16 payload size {dataSize} cannot be represented in a WAV RIFF header.",
                nameof(samples));
        }

        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write((uint)riffSize); // file size - 8
        writer.Write("WAVE"u8);

        // fmt sub-chunk
        writer.Write("fmt "u8);
        writer.Write(16u); // sub-chunk size
        writer.Write((ushort)1); // PCM format
        writer.Write((ushort)channels);
        writer.Write((uint)sampleRate);
        writer.Write((uint)byteRate);
        writer.Write((ushort)blockAlign);
        writer.Write((ushort)bitsPerSample);

        // data sub-chunk
        writer.Write("data"u8);
        writer.Write((uint)dataSize);

        var byteData = new byte[(int)dataSize];
        Buffer.BlockCopy(samples, 0, byteData, 0, byteData.Length);
        writer.Write(byteData);

        if (loop is not { } wavLoop)
            return;

        // RIFF smpl uses an inclusive loop end. A play count of zero means
        // infinite playback, which is also the only N64 ALADPCM loop form the
        // audited corpus currently exports through this writer.
        writer.Write("smpl"u8);
        writer.Write((uint)singleLoopSamplerChunkSize);
        writer.Write(0u); // manufacturer
        writer.Write(0u); // product
        writer.Write(checked((uint)Math.Floor(1_000_000_000d / sampleRate + 0.5d)));
        writer.Write(60u); // MIDI unity note
        writer.Write(0u); // MIDI pitch fraction
        writer.Write(0u); // SMPTE format
        writer.Write(0u); // SMPTE offset
        writer.Write(1u); // sample-loop count
        writer.Write(0u); // sampler-data byte count
        writer.Write(0u); // cue-point ID
        writer.Write(0u); // forward loop
        writer.Write(wavLoop.StartSampleFrame);
        writer.Write(wavLoop.EndSampleFrameInclusive);
        writer.Write(0u); // fractional loop offset
        writer.Write(wavLoop.PlayCount);
    }
}

/// <summary>
///     One RIFF <c>smpl</c> forward-loop descriptor. The end is inclusive and
///     a play count of zero denotes an infinite loop.
/// </summary>
public readonly record struct Pcm16WavLoop(
    uint StartSampleFrame,
    uint EndSampleFrameInclusive,
    uint PlayCount);
