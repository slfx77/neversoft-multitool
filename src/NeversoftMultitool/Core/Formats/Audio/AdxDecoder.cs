using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Decodes CRI ADX ADPCM audio files to PCM WAV.
///     ADX is a lossy audio format used in many Dreamcast and PS2 games.
/// </summary>
public static class AdxDecoder
{
    /// <summary>
    ///     Converts an ADX file to a WAV file in the specified output directory.
    /// </summary>
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            using var reader = new BinaryReader(stream);

            var header = ReadHeader(reader);
            if (header == null)
                return new AudioConvertResult { ErrorMessage = "Invalid ADX header" };

            if (header.Encoding != 3)
                return new AudioConvertResult { ErrorMessage = $"Unsupported ADX encoding type: {header.Encoding}" };

            var (coef1, coef2) = CalculateCoefficients(header.HighpassFrequency, header.SampleRate);

            // Seek to audio data
            stream.Position = header.DataOffset;

            var samples = DecodeFrames(reader, header, coef1, coef2);

            var outputPath = Path.Combine(outputDir,
                Path.GetFileNameWithoutExtension(inputPath) + ".wav");
            WavWriter.WritePcm16(outputPath, header.SampleRate, header.ChannelCount, samples);

            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    private static AdxHeader? ReadHeader(BinaryReader reader)
    {
        var magic = ReadBigEndianUInt16(reader);
        if (magic != 0x8000)
            return null;

        var copyrightOffset = ReadBigEndianUInt16(reader);
        var encoding = reader.ReadByte();
        var blockSize = reader.ReadByte();
        var bitsPerSample = reader.ReadByte();
        var channelCount = reader.ReadByte();
        var sampleRate = (int)ReadBigEndianUInt32(reader);
        var totalSamples = (int)ReadBigEndianUInt32(reader);
        var highpassFrequency = ReadBigEndianUInt16(reader);

        // Data starts after the copyright offset + 4 (for magic + offset field)
        var dataOffset = copyrightOffset + 4;

        return new AdxHeader
        {
            Encoding = encoding,
            BlockSize = blockSize,
            BitsPerSample = bitsPerSample,
            ChannelCount = channelCount,
            SampleRate = sampleRate,
            TotalSamples = totalSamples,
            HighpassFrequency = highpassFrequency,
            DataOffset = dataOffset
        };
    }

    private static (int coef1, int coef2) CalculateCoefficients(int highpassFrequency, int sampleRate)
    {
        var z = Math.Cos(2.0 * Math.PI * highpassFrequency / sampleRate);
        var a = Math.Sqrt(2.0) - z;
        var b = Math.Sqrt(2.0) - 1.0;
        var c = (a - Math.Sqrt((a + b) * (a - b))) / b;

        var coef1 = (int)(c * 8192.0);
        var coef2 = (int)(c * c * -4096.0);
        return (coef1, coef2);
    }

    private static short[] DecodeFrames(BinaryReader reader, AdxHeader header, int coef1, int coef2)
    {
        var samplesPerFrame = (header.BlockSize - 2) * 2; // 2 bytes header, rest are nibble pairs
        var totalFrames = (header.TotalSamples + samplesPerFrame - 1) / samplesPerFrame;
        var totalInterleavedFrames = totalFrames * header.ChannelCount;

        // Per-channel history
        var hist1 = new int[header.ChannelCount];
        var hist2 = new int[header.ChannelCount];

        // Decode interleaved frames into per-channel buffers
        var channelBuffers = new List<short>[header.ChannelCount];
        for (var ch = 0; ch < header.ChannelCount; ch++)
            channelBuffers[ch] = new List<short>(header.TotalSamples);

        var framesDecoded = 0;
        while (framesDecoded < totalInterleavedFrames &&
               reader.BaseStream.Position + header.BlockSize <= reader.BaseStream.Length)
        {
            var ch = framesDecoded % header.ChannelCount;

            var scale = ReadBigEndianInt16(reader) + 1;
            var dataBytes = reader.ReadBytes(header.BlockSize - 2);

            foreach (var b in dataBytes)
            {
                // High nibble first, then low nibble
                var highNibble = (b >> 4) & 0x0F;
                var lowNibble = b & 0x0F;

                DecodeNibble(highNibble, scale, coef1, coef2, ref hist1[ch], ref hist2[ch], channelBuffers[ch]);
                DecodeNibble(lowNibble, scale, coef1, coef2, ref hist1[ch], ref hist2[ch], channelBuffers[ch]);
            }

            framesDecoded++;
        }

        // Interleave channels into output
        return InterleaveChannels(channelBuffers, header.TotalSamples, header.ChannelCount);
    }

    private static void DecodeNibble(int nibble, int scale, int coef1, int coef2,
        ref int hist1, ref int hist2, List<short> output)
    {
        // Sign-extend 4-bit nibble
        if (nibble >= 8) nibble -= 16;

        var prediction = (coef1 * hist1 + coef2 * hist2) >> 12;
        var sample = prediction + nibble * scale;
        sample = Math.Clamp(sample, short.MinValue, short.MaxValue);

        hist2 = hist1;
        hist1 = sample;
        output.Add((short)sample);
    }

    private static short[] InterleaveChannels(List<short>[] channelBuffers, int totalSamples, int channelCount)
    {
        var samplesPerChannel = Math.Min(totalSamples,
            channelBuffers.Min(b => b.Count));

        var output = new short[samplesPerChannel * channelCount];
        for (var i = 0; i < samplesPerChannel; i++)
        {
            for (var ch = 0; ch < channelCount; ch++)
            {
                output[i * channelCount + ch] = channelBuffers[ch][i];
            }
        }

        return output;
    }

    private static ushort ReadBigEndianUInt16(BinaryReader reader)
    {
        var b = reader.ReadBytes(2);
        return (ushort)((b[0] << 8) | b[1]);
    }

    private static short ReadBigEndianInt16(BinaryReader reader)
    {
        var b = reader.ReadBytes(2);
        return (short)((b[0] << 8) | b[1]);
    }

    private static uint ReadBigEndianUInt32(BinaryReader reader)
    {
        var b = reader.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private sealed class AdxHeader
    {
        public byte Encoding { get; init; }
        public byte BlockSize { get; init; }
        public byte BitsPerSample { get; init; }
        public byte ChannelCount { get; init; }
        public int SampleRate { get; init; }
        public int TotalSamples { get; init; }
        public int HighpassFrequency { get; init; }
        public int DataOffset { get; init; }
    }
}
