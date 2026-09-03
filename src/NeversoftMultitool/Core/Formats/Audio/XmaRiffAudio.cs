using System.Buffers.Binary;
using System.Diagnostics;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     Canonical RIFF wrapper and decoder bridge for Neversoft's raw Xbox 360
///     XMA packet streams. Both measured container families use 2 KiB packets
///     and the same ffmpeg-compatible 0x0166 wrapper.
/// </summary>
internal static class XmaRiffAudio
{
    public const int PacketSize = 2048;
    public const int BlockSize = 0x8000;
    public const int WaveHeaderSize = 80;

    private const ushort WaveFormatXma = 0x0166;
    private const byte EncoderVersion = 4;

    internal static void WriteWaveHeader(
        Span<byte> header,
        int channels,
        int sampleRate,
        int dataSize,
        int decodedSampleCount)
    {
        if (header.Length < WaveHeaderSize)
            throw new ArgumentException("XMA header buffer is too small", nameof(header));
        if (channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate is < 4000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (dataSize <= 0 || decodedSampleCount <= 0)
            throw new ArgumentOutOfRangeException(
                dataSize <= 0 ? nameof(dataSize) : nameof(decodedSampleCount));

        var blockCount = checked((dataSize + BlockSize - 1) / BlockSize);
        if (blockCount > ushort.MaxValue)
            throw new InvalidDataException("XMA stream has too many 32 KiB blocks");

        header[..WaveHeaderSize].Clear();
        "RIFF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], checked((uint)(dataSize + 72)));
        "WAVEfmt "u8.CopyTo(header[8..16]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..20], 52);
        BinaryPrimitives.WriteUInt16LittleEndian(header[20..22], WaveFormatXma);
        BinaryPrimitives.WriteUInt16LittleEndian(header[22..24], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..28], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(
            header[28..32], checked((uint)(sampleRate * channels * sizeof(short))));
        BinaryPrimitives.WriteUInt16LittleEndian(header[32..34], sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(header[34..36], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header[36..38], 34);
        BinaryPrimitives.WriteUInt16LittleEndian(header[38..40], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header[44..48], (uint)decodedSampleCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header[48..52], BlockSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header[56..60], (uint)decodedSampleCount);
        header[69] = EncoderVersion;
        BinaryPrimitives.WriteUInt16LittleEndian(header[70..72], (ushort)blockCount);
        "data"u8.CopyTo(header[72..76]);
        BinaryPrimitives.WriteUInt32LittleEndian(header[76..80], (uint)dataSize);
    }

    internal static bool RunFfmpeg(string inputPath, string outputPath, out string error)
    {
        var ffmpeg = SfdConverter.FindFfmpeg();
        if (ffmpeg == null)
        {
            error = "ffmpeg not found on PATH";
            return false;
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
                 {
                     "-y", "-loglevel", "error", "-i", inputPath,
                     "-map", "0:a:0", "-vn", "-c:a", "pcm_s16le", outputPath
                 })
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(300_000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // It exited between the timeout and Kill.
            }

            error = "ffmpeg timed out while decoding XMA audio";
            return false;
        }

        var stderr = stderrTask.GetAwaiter().GetResult();
        if (process.ExitCode == 0 && File.Exists(outputPath))
        {
            error = "";
            return true;
        }

        error = string.IsNullOrWhiteSpace(stderr)
            ? $"ffmpeg exited with code {process.ExitCode}"
            : stderr.Trim();
        return false;
    }

    internal static bool IsPcm16WaveFile(string path, int sampleRate, int channels)
    {
        return RiffWaveReader.TryReadHeader(path, out var wave)
               && wave.FormatTag == 1
               && wave.Channels == channels
               && wave.SampleRate == sampleRate
               && wave.BitsPerSample == 16
               && wave.BlockAlign == wave.Channels * sizeof(short)
               && wave.DataLength > 0;
    }
}
