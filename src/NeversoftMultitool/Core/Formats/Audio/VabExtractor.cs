namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
/// Extracts audio samples from PS1 VAB (VAG Body) sound bank files.
/// Each VAB contains multiple VAG waveforms encoded as SPU-ADPCM.
/// Produces one WAV file per waveform in a subdirectory.
/// </summary>
public static class VabExtractor
{
    private const int HeaderSize = 0x20;
    private const int ProgramTableOffset = 0x20;
    private const int ProgramEntrySize = 16;
    private const int ProgramSlots = 128;
    private const int ToneEntrySize = 32;
    private const int TonesPerProgram = 16;
    private const int VagSizeTableEntries = 256;
    private const int AdpcmBlockSize = 16;
    private const int SamplesPerBlock = 28;
    private const int DefaultSampleRate = 11025;

    // SPU-ADPCM filter coefficients (f0, f1) scaled by 1/64
    // SPU supports 5 filters (0-4), unlike XA which only uses 0-3
    private static readonly int[] F0 = [0, 60, 115, 98, 122];
    private static readonly int[] F1 = [0, 0, -52, -55, -60];

    /// <summary>
    /// Enumerates samples in a VAB file without decoding audio data.
    /// Returns the index and data size of each valid (non-zero) VAG waveform.
    /// </summary>
    public static List<VabSampleInfo> EnumerateSamples(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        using var reader = new BinaryReader(stream);

        var header = ReadHeader(reader);
        if (header == null) return [];

        stream.Position = ProgramTableOffset + ProgramSlots * ProgramEntrySize;
        var toneTableSize = header.ProgramCount * TonesPerProgram * ToneEntrySize;
        stream.Position += toneTableSize;

        var vagSizes = new int[VagSizeTableEntries];
        for (var i = 0; i < VagSizeTableEntries; i++)
            vagSizes[i] = reader.ReadUInt16() * 8;

        var results = new List<VabSampleInfo>();
        for (var v = 1; v <= header.VagCount && v < VagSizeTableEntries; v++)
        {
            if (vagSizes[v] > 0)
                results.Add(new VabSampleInfo(v, vagSizes[v]));
        }

        return results;
    }

    public sealed record VabSampleInfo(int Index, int DataSize);

    /// <summary>
    /// Extracts a single VAG sample by index to a WAV file.
    /// Returns the output path on success, or null on failure.
    /// </summary>
    public static string? ExtractSingleToWav(string inputPath, int sampleIndex, string outputDir, int sampleRate = DefaultSampleRate)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            using var reader = new BinaryReader(stream);

            var header = ReadHeader(reader);
            if (header == null || sampleIndex < 1 || sampleIndex > header.VagCount)
                return null;

            // Skip program table and tone table
            stream.Position = ProgramTableOffset + ProgramSlots * ProgramEntrySize;
            var toneTableSize = header.ProgramCount * TonesPerProgram * ToneEntrySize;
            stream.Position += toneTableSize;

            // Read VAG size table
            var vagSizes = new int[VagSizeTableEntries];
            for (var i = 0; i < VagSizeTableEntries; i++)
                vagSizes[i] = reader.ReadUInt16() * 8;

            var size = vagSizes[sampleIndex];
            if (size <= 0) return null;

            // Calculate offset to the target sample (sum sizes of all preceding entries)
            var vagDataOffset = stream.Position;
            long currentOffset = vagDataOffset;
            for (var v = 0; v < sampleIndex; v++)
                currentOffset += vagSizes[v];

            stream.Position = currentOffset;
            var vagData = reader.ReadBytes(size);
            var pcm = DecodeSpuAdpcm(vagData);
            if (pcm.Length == 0) return null;

            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var wavPath = Path.Combine(outputDir, $"{stem}_{sampleIndex:D3}.wav");
            WavWriter.WritePcm16(wavPath, sampleRate, 1, pcm);
            return wavPath;
        }
        catch
        {
            return null;
        }
    }

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir, int sampleRate = DefaultSampleRate)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            using var reader = new BinaryReader(stream);

            var header = ReadHeader(reader);
            if (header == null)
                return new AudioConvertResult { ErrorMessage = "Invalid VAB header (missing pBAV magic)" };

            // Skip program table (128 entries × 16 bytes, always 128 slots)
            stream.Position = ProgramTableOffset + ProgramSlots * ProgramEntrySize;

            // Skip tone table (numPrograms × 16 tones × 32 bytes)
            var toneTableSize = header.ProgramCount * TonesPerProgram * ToneEntrySize;
            stream.Position += toneTableSize;

            // Read VAG size table (256 entries × 2 bytes, sizes are in units of 8 bytes)
            var vagSizes = new int[VagSizeTableEntries];
            for (var i = 0; i < VagSizeTableEntries; i++)
                vagSizes[i] = reader.ReadUInt16() * 8;

            var vagDataOffset = stream.Position;
            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var outDir = Path.Combine(outputDir, stem);
            var filesWritten = 0;

            // VAG index 0 is unused; valid indices start from 1
            var currentOffset = vagDataOffset;
            for (var v = 0; v < VagSizeTableEntries; v++)
            {
                var size = vagSizes[v];
                if (size <= 0)
                    continue;

                if (v > 0 && v <= header.VagCount)
                {
                    stream.Position = currentOffset;
                    var vagData = reader.ReadBytes(size);
                    var pcm = DecodeSpuAdpcm(vagData);

                    if (pcm.Length > 0)
                    {
                        var wavPath = Path.Combine(outDir, $"{v:D3}.wav");
                        WavWriter.WritePcm16(wavPath, sampleRate, 1, pcm);
                        filesWritten++;
                    }
                }

                currentOffset += size;
            }

            return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    private static VabHeader? ReadHeader(BinaryReader reader)
    {
        if (reader.BaseStream.Length < HeaderSize)
            return null;

        var magic = reader.ReadUInt32();
        if (magic != 0x56414270) // "pBAV" in little-endian
            return null;

        var version = reader.ReadUInt32();
        reader.ReadUInt32(); // vabId
        reader.ReadUInt32(); // totalSize
        reader.ReadUInt16(); // reserved (0xEEEE)
        var programCount = reader.ReadUInt16();
        var toneCount = reader.ReadUInt16();
        var vagCount = reader.ReadUInt16();

        return new VabHeader
        {
            Version = version,
            ProgramCount = programCount,
            ToneCount = toneCount,
            VagCount = vagCount
        };
    }

    /// <summary>
    /// Decodes raw SPU-ADPCM data (no VAG file header) to 16-bit PCM.
    /// Each 16-byte block: byte 0 = shift|filter, byte 1 = flags, bytes 2-15 = 28 nibble samples.
    /// </summary>
    private static short[] DecodeSpuAdpcm(byte[] data)
    {
        var blockCount = data.Length / AdpcmBlockSize;
        var samples = new List<short>(blockCount * SamplesPerBlock);
        var prev1 = 0;
        var prev2 = 0;

        for (var b = 0; b < blockCount; b++)
        {
            var blockOffset = b * AdpcmBlockSize;
            var shiftFilter = data[blockOffset];
            var flags = data[blockOffset + 1];

            var shift = Math.Min(shiftFilter & 0x0F, 12);
            var filter = (shiftFilter >> 4) & 0x0F;
            if (filter > 4) filter = 0; // clamp invalid filters

            var f0 = F0[filter];
            var f1 = F1[filter];

            // Decode 14 data bytes = 28 nibble samples (low nibble first)
            for (var i = 2; i < AdpcmBlockSize; i++)
            {
                var byteVal = data[blockOffset + i];

                // Low nibble first (earlier sample)
                var lo = byteVal & 0x0F;
                if (lo >= 8) lo -= 16;
                var sample = (lo << (12 - shift)) + (f0 * prev1 + f1 * prev2 + 32) / 64;
                sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                prev2 = prev1;
                prev1 = sample;
                samples.Add((short)sample);

                // High nibble (later sample)
                var hi = (byteVal >> 4) & 0x0F;
                if (hi >= 8) hi -= 16;
                sample = (hi << (12 - shift)) + (f0 * prev1 + f1 * prev2 + 32) / 64;
                sample = Math.Clamp(sample, short.MinValue, short.MaxValue);
                prev2 = prev1;
                prev1 = sample;
                samples.Add((short)sample);
            }

            // Check for end flag (bit 0)
            if ((flags & 0x01) != 0)
                break;
        }

        return samples.ToArray();
    }

    private sealed class VabHeader
    {
        public uint Version { get; init; }
        public int ProgramCount { get; init; }
        public int ToneCount { get; init; }
        public int VagCount { get; init; }
    }
}
