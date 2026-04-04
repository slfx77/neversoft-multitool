namespace NeversoftMultitool.Core.BinaryIO;

public static class WavWriter
{
    /// <summary>
    ///     Writes 16-bit PCM audio data to a WAV file.
    /// </summary>
    public static void WritePcm16(string outputPath, int sampleRate, int channels, short[] samples)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var stream = File.Create(outputPath);
        using var writer = new BinaryWriter(stream);

        var dataSize = samples.Length * 2;
        var bitsPerSample = 16;
        var blockAlign = channels * (bitsPerSample / 8);
        var byteRate = sampleRate * blockAlign;

        // RIFF header
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize); // file size - 8
        writer.Write("WAVE"u8);

        // fmt sub-chunk
        writer.Write("fmt "u8);
        writer.Write(16); // sub-chunk size
        writer.Write((short)1); // PCM format
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(byteRate);
        writer.Write((short)blockAlign);
        writer.Write((short)bitsPerSample);

        // data sub-chunk
        writer.Write("data"u8);
        writer.Write(dataSize);

        var byteData = new byte[dataSize];
        Buffer.BlockCopy(samples, 0, byteData, 0, dataSize);
        writer.Write(byteData);
    }
}
