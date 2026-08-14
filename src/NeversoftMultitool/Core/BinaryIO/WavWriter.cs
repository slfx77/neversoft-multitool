namespace NeversoftMultitool.Core.BinaryIO;

public static class WavWriter
{
    /// <summary>
    ///     Writes 16-bit PCM audio data to a WAV file.
    /// </summary>
    public static void WritePcm16(string outputPath, int sampleRate, int channels, short[] samples)
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

        var riffSize = 36L + dataSize;
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
    }
}
