namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
/// Extracts audio samples from Dreamcast KAT soundbank files.
/// Supports 4-bit Yamaha AICA ADPCM, 8-bit PCM, and 16-bit PCM encodings.
/// Produces one WAV file per sample in a subdirectory.
/// </summary>
public static class KatExtractor
{
    private const int EntrySize = 44;

    // Yamaha AICA ADPCM tables
    private static readonly int[] DiffLookup =
        [1, 3, 5, 7, 9, 11, 13, 15, -1, -3, -5, -7, -9, -11, -13, -15];

    private static readonly int[] IndexScale =
        [0x0E6, 0x0E6, 0x0E6, 0x0E6, 0x133, 0x199, 0x200, 0x266];

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 4)
                return new AudioConvertResult { ErrorMessage = "File too small for KAT header" };

            var entryCount = reader.ReadUInt32();
            var expectedHeaderSize = 4 + entryCount * EntrySize;

            if (stream.Length < expectedHeaderSize)
                return new AudioConvertResult { ErrorMessage = $"File too small for {entryCount} entries" };

            // Read entry table
            var entries = new KatEntry[entryCount];
            for (var i = 0; i < entryCount; i++)
            {
                entries[i] = new KatEntry
                {
                    Channels = reader.ReadUInt32(),
                    Offset = reader.ReadUInt32(),
                    Size = reader.ReadUInt32(),
                    SampleRate = reader.ReadUInt32(),
                    Loop = reader.ReadUInt32(),
                    Bits = reader.ReadUInt32(),
                    Unknown = reader.ReadUInt32(),
                    Name = reader.ReadBytes(16)
                };
            }

            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var outDir = Path.Combine(outputDir, stem);
            var filesWritten = 0;

            for (var i = 0; i < entries.Length; i++)
            {
                var entry = entries[i];
                if (entry.Size == 0 || entry.SampleRate == 0)
                    continue;

                stream.Position = entry.Offset;
                var rawData = reader.ReadBytes((int)entry.Size);

                // Skip silent placeholders (all zeros)
                if (IsAllZeros(rawData))
                    continue;

                short[] pcm;
                switch (entry.Bits)
                {
                    case 4:
                        pcm = DecodeAicaAdpcm(rawData);
                        break;
                    case 8:
                        pcm = DecodePcm8(rawData);
                        break;
                    case 16:
                        pcm = DecodePcm16(rawData);
                        break;
                    default:
                        continue; // skip unknown encoding
                }

                if (pcm.Length > 0)
                {
                    var channels = (int)Math.Max(entry.Channels, 1);
                    var wavPath = Path.Combine(outDir, $"{i:D3}.wav");
                    WavWriter.WritePcm16(wavPath, (int)entry.SampleRate, channels, pcm);
                    filesWritten++;
                }
            }

            return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Decodes Yamaha AICA 4-bit ADPCM to 16-bit PCM.
    /// Low nibble first, then high nibble (AICA ordering).
    /// </summary>
    private static short[] DecodeAicaAdpcm(byte[] data)
    {
        var samples = new short[data.Length * 2];
        var stepSize = 127;
        var history = 0;
        var outIdx = 0;

        foreach (var b in data)
        {
            // Low nibble first (AICA ordering)
            DecodeAdpcmNibble(b & 0x0F, ref stepSize, ref history);
            samples[outIdx++] = (short)history;

            // High nibble
            DecodeAdpcmNibble((b >> 4) & 0x0F, ref stepSize, ref history);
            samples[outIdx++] = (short)history;
        }

        return samples;
    }

    private static void DecodeAdpcmNibble(int nibble, ref int stepSize, ref int history)
    {
        var diff = stepSize * DiffLookup[nibble & 0x0F];
        // Add rounding: (x + (x >>> 29)) >> 3 is equivalent to (x + 4) >> 3 for positive,
        // but handles sign correctly via unsigned right shift
        var x = history + ((diff + ((diff >> 29) & 0x07)) >> 3);
        history = Math.Clamp(x, short.MinValue, short.MaxValue);

        // Update step size using magnitude (lower 3 bits)
        stepSize = (stepSize * IndexScale[nibble & 0x07]) >> 8;
        stepSize = Math.Clamp(stepSize, 0x7F, 0x6000);

        // High-pass filter to remove DC offset
        history = history * 254 / 256;
    }

    /// <summary>
    /// Converts signed 8-bit PCM to 16-bit PCM by left-shifting.
    /// </summary>
    private static short[] DecodePcm8(byte[] data)
    {
        var samples = new short[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            // Interpret as signed byte, scale to 16-bit range
            samples[i] = (short)((sbyte)data[i] << 8);
        }

        return samples;
    }

    /// <summary>
    /// Reads 16-bit little-endian signed PCM directly.
    /// </summary>
    private static short[] DecodePcm16(byte[] data)
    {
        var sampleCount = data.Length / 2;
        var samples = new short[sampleCount];
        Buffer.BlockCopy(data, 0, samples, 0, sampleCount * 2);
        return samples;
    }

    private static bool IsAllZeros(byte[] data)
    {
        foreach (var b in data)
        {
            if (b != 0) return false;
        }

        return true;
    }

    private sealed class KatEntry
    {
        public uint Channels { get; init; }
        public uint Offset { get; init; }
        public uint Size { get; init; }
        public uint SampleRate { get; init; }
        public uint Loop { get; init; }
        public uint Bits { get; init; }
        public uint Unknown { get; init; }
        public byte[] Name { get; init; } = [];
    }
}
