namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts audio samples from PS1 VAB (VAG Body) sound bank files.
///     Each VAB contains multiple VAG waveforms encoded as SPU-ADPCM.
///     Produces one WAV file per waveform in a subdirectory.
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
    private const int DefaultSampleRate = 11025;

    /// <summary>
    ///     Enumerates samples in a VAB file without decoding audio data.
    ///     Returns the index and data size of each valid (non-zero) VAG waveform.
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

    /// <summary>
    ///     Extracts a single VAG sample by index to a WAV file.
    ///     Returns the output path on success, or null on failure.
    /// </summary>
    public static string? ExtractSingleToWav(string inputPath, int sampleIndex, string outputDir,
        int sampleRate = DefaultSampleRate)
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
            var currentOffset = vagDataOffset;
            for (var v = 0; v < sampleIndex; v++)
                currentOffset += vagSizes[v];

            stream.Position = currentOffset;
            var vagData = reader.ReadBytes(size);
            var pcm = SpuAdpcm.Decode(vagData);
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

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir,
        int sampleRate = DefaultSampleRate)
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
                    var pcm = SpuAdpcm.Decode(vagData);

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

    public sealed record VabSampleInfo(int Index, int DataSize);

    private sealed class VabHeader
    {
        public uint Version { get; init; }
        public int ProgramCount { get; init; }
        public int ToneCount { get; init; }
        public int VagCount { get; init; }
    }
}
