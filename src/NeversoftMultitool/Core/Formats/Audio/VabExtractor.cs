using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Extracts audio samples from PS1 VAB (VAG Body) sound bank files.
///     Each VAB contains multiple VAG waveforms encoded as SPU-ADPCM.
///     Produces one WAV file per waveform in a subdirectory.
/// </summary>
public static class VabExtractor
{
    private const int NeutralMidiNote = 60;
    private const int RawSpuBaseRate = 44100;
    private const int HeaderSize = 0x20;
    private const int ProgramTableOffset = 0x20;
    private const int ProgramEntrySize = 16;
    private const int ProgramSlots = 128;
    private const int ToneEntrySize = 32;
    private const int TonesPerProgram = 16;
    private const int VagSizeTableEntries = 256;
    public const int DefaultSampleRate = 11025;

    /// <summary>
    ///     Enumerates samples in a VAB file without decoding audio data.
    ///     Returns the index and data size of each valid (non-zero) VAG waveform.
    /// </summary>
    public static List<VabSampleInfo> EnumerateSamples(string inputPath)
    {
        using var stream = File.OpenRead(inputPath);
        return EnumerateSamplesFromStream(stream);
    }

    /// <summary>In-memory variant.</summary>
    public static List<VabSampleInfo> EnumerateSamples(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        return EnumerateSamplesFromStream(stream);
    }

    private static List<VabSampleInfo> EnumerateSamplesFromStream(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var layout = ReadLayout(reader);
        if (layout == null) return [];

        var results = new List<VabSampleInfo>();
        for (var v = 1; v <= layout.Header.VagCount && v < VagSizeTableEntries; v++)
        {
            if (layout.VagSizes[v] > 0)
                results.Add(new VabSampleInfo(v, layout.VagSizes[v], GetSuggestedSampleRate(layout, v)));
        }

        return results;
    }

    /// <summary>
    ///     Extracts a single VAG sample by index to a WAV file.
    ///     Returns the output path on success, or null on failure.
    /// </summary>
    public static string? ExtractSingleToWav(string inputPath, int sampleIndex, string outputDir,
        int sampleRate = 0)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            return ExtractSingleToWavFromStream(stream, Path.GetFileNameWithoutExtension(inputPath),
                sampleIndex, outputDir, sampleRate);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>In-memory variant.</summary>
    public static string? ExtractSingleToWav(byte[] data, string stem, int sampleIndex, string outputDir,
        int sampleRate = 0)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return ExtractSingleToWavFromStream(stream, stem, sampleIndex, outputDir, sampleRate);
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractSingleToWavFromStream(
        Stream stream, string stem, int sampleIndex, string outputDir, int sampleRate)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var layout = ReadLayout(reader);
        if (layout == null || sampleIndex < 1 || sampleIndex > layout.Header.VagCount)
            return null;

        var size = layout.VagSizes[sampleIndex];
        if (size <= 0) return null;

        var currentOffset = layout.VagDataOffset;
        for (var v = 0; v < sampleIndex; v++)
            currentOffset += layout.VagSizes[v];

        stream.Position = currentOffset;
        var vagData = reader.ReadBytes(size);
        var pcm = SpuAdpcm.Decode(vagData);
        if (pcm.Length == 0) return null;

        var wavPath = Path.Combine(outputDir, $"{stem}_{sampleIndex:D3}.wav");
        var resolvedSampleRate = sampleRate > 0 ? sampleRate : GetSuggestedSampleRate(layout, sampleIndex);
        WavWriter.WritePcm16(wavPath, resolvedSampleRate, 1, pcm);
        return wavPath;
    }

    public static AudioConvertResult ExtractToWav(string inputPath, string outputDir,
        int sampleRate = DefaultSampleRate)
    {
        try
        {
            using var stream = File.OpenRead(inputPath);
            return ExtractToWavFromStream(stream, Path.GetFileNameWithoutExtension(inputPath), outputDir, sampleRate);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>In-memory variant.</summary>
    public static AudioConvertResult ExtractToWav(byte[] data, string stem, string outputDir,
        int sampleRate = DefaultSampleRate)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            return ExtractToWavFromStream(stream, stem, outputDir, sampleRate);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    private static AudioConvertResult ExtractToWavFromStream(
        Stream stream, string stem, string outputDir, int sampleRate)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);

        var layout = ReadLayout(reader);
        if (layout == null)
            return new AudioConvertResult { ErrorMessage = "Invalid VAB header (missing pBAV magic)" };

        var outDir = Path.Combine(outputDir, stem);
        var filesWritten = 0;

        var currentOffset = layout.VagDataOffset;
        for (var v = 0; v < VagSizeTableEntries; v++)
        {
            var size = layout.VagSizes[v];
            if (size <= 0)
                continue;

            if (v > 0 && v <= layout.Header.VagCount)
            {
                stream.Position = currentOffset;
                var vagData = reader.ReadBytes(size);
                var pcm = SpuAdpcm.Decode(vagData);

                if (pcm.Length > 0)
                {
                    var wavPath = Path.Combine(outDir, $"{v:D3}.wav");
                    var resolvedSampleRate = sampleRate > 0 ? sampleRate : GetSuggestedSampleRate(layout, v);
                    WavWriter.WritePcm16(wavPath, resolvedSampleRate, 1, pcm);
                    filesWritten++;
                }
            }

            currentOffset += size;
        }

        return new AudioConvertResult { Success = true, SamplesWritten = filesWritten };
    }

    private static VabLayout? ReadLayout(BinaryReader reader)
    {
        var header = ReadHeader(reader);
        if (header == null)
            return null;

        var programToneCounts = ReadProgramToneCounts(reader, header.ProgramCount);
        var toneMap = ReadToneMap(reader, header.ProgramCount, header.VagCount, programToneCounts);
        var vagSizes = ReadVagSizes(reader);

        return new VabLayout(header, vagSizes, toneMap, reader.BaseStream.Position);
    }

    private static int[] ReadProgramToneCounts(BinaryReader reader, int programCount)
    {
        var toneCounts = new int[programCount];
        reader.BaseStream.Position = ProgramTableOffset;

        for (var programIndex = 0; programIndex < programCount; programIndex++)
        {
            toneCounts[programIndex] = reader.ReadByte();
            reader.BaseStream.Position += ProgramEntrySize - 1;
        }

        return toneCounts;
    }

    private static Dictionary<int, VabToneInfo> ReadToneMap(
        BinaryReader reader,
        int programCount,
        int vagCount,
        int[] programToneCounts)
    {
        var toneInfos = new Dictionary<int, List<VabToneInfo>>();
        reader.BaseStream.Position = ProgramTableOffset + ProgramSlots * ProgramEntrySize;

        for (var programIndex = 0; programIndex < programCount; programIndex++)
        {
            var toneCount = Math.Min(programToneCounts[programIndex], TonesPerProgram);
            for (var toneIndex = 0; toneIndex < TonesPerProgram; toneIndex++)
            {
                var toneBytes = reader.ReadBytes(ToneEntrySize);
                if (toneBytes.Length < ToneEntrySize)
                    return [];

                if (toneIndex >= toneCount)
                    continue;

                var vagIndex = BinaryPrimitives.ReadInt16LittleEndian(toneBytes.AsSpan(22, 2));
                if (vagIndex <= 0 || vagIndex > vagCount)
                    continue;

                var center = toneBytes[4];
                var shift = toneBytes[5];
                if (!toneInfos.TryGetValue(vagIndex, out var infoList))
                {
                    infoList = [];
                    toneInfos[vagIndex] = infoList;
                }

                infoList.Add(new VabToneInfo(center, shift, infoList.Count));
            }
        }

        return toneInfos.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value
                .GroupBy(static info => (info.Center, info.Shift))
                .OrderByDescending(static group => group.Count())
                .ThenBy(static group => group.Min(static info => info.Order))
                .Select(static group => group.First() with { Order = 0 })
                .First());
    }

    private static int[] ReadVagSizes(BinaryReader reader)
    {
        var vagSizes = new int[VagSizeTableEntries];
        for (var i = 0; i < VagSizeTableEntries; i++)
            vagSizes[i] = reader.ReadUInt16() * 8;

        return vagSizes;
    }

    private static int GetSuggestedSampleRate(VabLayout layout, int sampleIndex)
    {
        return layout.ToneMap.TryGetValue(sampleIndex, out var toneInfo)
            ? EstimateSampleRate(toneInfo.Center, toneInfo.Shift)
            : DefaultSampleRate;
    }

    private static int EstimateSampleRate(byte center, byte shift)
    {
        var fineTuneCents = shift * 100.0 / 128.0;
        var semitoneOffset = NeutralMidiNote - center - (fineTuneCents / 100.0);
        var sampleRate = RawSpuBaseRate * Math.Pow(2.0, semitoneOffset / 12.0);
        return Math.Clamp((int)Math.Round(sampleRate), 2000, 96000);
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

    public sealed record VabSampleInfo(int Index, int DataSize, int SampleRate);

    private readonly record struct VabToneInfo(byte Center, byte Shift, int Order);

    private sealed class VabLayout
    {
        public VabLayout(VabHeader header, int[] vagSizes, IReadOnlyDictionary<int, VabToneInfo> toneMap, long vagDataOffset)
        {
            Header = header;
            VagSizes = vagSizes;
            ToneMap = toneMap;
            VagDataOffset = vagDataOffset;
        }

        public VabHeader Header { get; }
        public int[] VagSizes { get; }
        public IReadOnlyDictionary<int, VabToneInfo> ToneMap { get; }
        public long VagDataOffset { get; }
    }

    private sealed class VabHeader
    {
        public uint Version { get; init; }
        public int ProgramCount { get; init; }
        public int ToneCount { get; init; }
        public int VagCount { get; init; }
    }
}
