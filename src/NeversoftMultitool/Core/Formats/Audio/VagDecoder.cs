using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
/// Converts standalone VAG audio files and headerless SPU-ADPCM streams to WAV.
/// Handles two variants:
///   1. Standard VAG with "VAGp" header (sample rate + data size from header)
///   2. Headerless raw SPU-ADPCM blocks (defaults to specified sample rate)
/// Used for PS2 .vag files and extensionless voice/music streams from STREAMS WADs.
/// </summary>
public static class VagDecoder
{
    private const uint VagpMagic = 0x56414770; // "VAGp" big-endian
    private const int VagHeaderSize = 48;
    private const int DefaultSampleRate = 22050;

    /// <summary>
    /// Converts a VAG or raw SPU-ADPCM file to a WAV file.
    /// Returns an AudioConvertResult indicating success/failure.
    /// </summary>
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir, int overrideSampleRate = 0)
    {
        try
        {
            var data = File.ReadAllBytes(inputPath);
            if (data.Length < SpuAdpcm.BlockSize)
                return new AudioConvertResult { ErrorMessage = "File too small to contain audio data" };

            int sampleRate;
            ReadOnlySpan<byte> adpcmData;

            if (TryParseVagHeader(data, out var header))
            {
                sampleRate = overrideSampleRate > 0 ? overrideSampleRate : header.SampleRate;
                var dataLength = Math.Min(header.DataSize, data.Length - VagHeaderSize);
                adpcmData = data.AsSpan(VagHeaderSize, dataLength);
            }
            else
            {
                // Headerless raw SPU-ADPCM
                sampleRate = overrideSampleRate > 0 ? overrideSampleRate : DefaultSampleRate;
                adpcmData = data.AsSpan();
            }

            var pcm = SpuAdpcm.Decode(adpcmData);
            if (pcm.Length == 0)
                return new AudioConvertResult { ErrorMessage = "No audio samples decoded" };

            var stem = Path.GetFileNameWithoutExtension(inputPath);
            var wavPath = Path.Combine(outputDir, $"{stem}.wav");
            WavWriter.WritePcm16(wavPath, sampleRate, 1, pcm);

            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Probes a file to determine if it's a valid VAG/SPU-ADPCM audio file.
    /// Returns the sample rate and estimated duration, or null if not audio.
    /// </summary>
    public static VagProbeResult? Probe(string filePath)
    {
        try
        {
            using var stream = File.OpenRead(filePath);
            if (stream.Length < SpuAdpcm.BlockSize) return null;

            var headerBytes = new byte[Math.Min(VagHeaderSize, stream.Length)];
            stream.ReadExactly(headerBytes);

            if (TryParseVagHeader(headerBytes, out var header))
            {
                var totalBlocks = header.DataSize / SpuAdpcm.BlockSize;
                var totalSamples = totalBlocks * SpuAdpcm.SamplesPerBlock;
                return new VagProbeResult(
                    header.SampleRate,
                    totalSamples / (double)header.SampleRate,
                    HasHeader: true,
                    header.Name);
            }

            // Headerless: estimate from file size
            var blocks = (int)(stream.Length / SpuAdpcm.BlockSize);
            var samples = blocks * SpuAdpcm.SamplesPerBlock;
            var duration = samples / (double)DefaultSampleRate;
            return new VagProbeResult(DefaultSampleRate, duration, HasHeader: false, Name: null);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryParseVagHeader(ReadOnlySpan<byte> data, out VagHeader header)
    {
        header = default;
        if (data.Length < VagHeaderSize) return false;

        // "VAGp" is stored big-endian in the file
        var magic = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (magic != VagpMagic) return false;

        var version = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
        var dataSize = BinaryPrimitives.ReadInt32BigEndian(data[12..]);
        var sampleRate = BinaryPrimitives.ReadInt32BigEndian(data[16..]);

        // Sanity check
        if (sampleRate <= 0 || sampleRate > 96000 || dataSize <= 0)
            return false;

        // Name is at offset 32, 16 bytes, null-terminated ASCII
        var nameSpan = data.Slice(32, 16);
        var nameEnd = nameSpan.IndexOf((byte)0);
        var name = nameEnd > 0
            ? System.Text.Encoding.ASCII.GetString(nameSpan[..nameEnd])
            : System.Text.Encoding.ASCII.GetString(nameSpan).TrimEnd('\0');

        header = new VagHeader(version, dataSize, sampleRate, name.Length > 0 ? name : null);
        return true;
    }

    private readonly record struct VagHeader(int Version, int DataSize, int SampleRate, string? Name);

    public sealed record VagProbeResult(int SampleRate, double DurationSeconds, bool HasHeader, string? Name);
}
