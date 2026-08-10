using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Sample rate, channel count, and duration of a THUG2 PC <c>.snd</c> sound.</summary>
public sealed record Thug2PcSndProbeResult(int SampleRate, int Channels, double DurationSeconds);

/// <summary>
///     Converts THUG2 PC <c>.snd</c> sound effects to 16-bit mono PCM WAV.
/// </summary>
/// <remarks>
///     These files are RIFF/WAVE containers whose <c>fmt </c> chunk falsely claims
///     PCM. <c>nAvgBytesPerSec</c> instead stores the decoded byte count: four times
///     the compressed payload length, or two bytes less for an odd sample count.
/// </remarks>
public static class Thug2PcSndDecoder
{
    public static AudioConvertResult ConvertToWav(string inputPath, string outputDir)
    {
        try
        {
            return ConvertToWav(
                File.ReadAllBytes(inputPath),
                Path.GetFileNameWithoutExtension(inputPath),
                outputDir);
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static AudioConvertResult ConvertToWav(byte[] data, string stem, string outputDir)
    {
        try
        {
            if (!TryDescribe(data, out var info, out var sampleCount, out var error))
                return new AudioConvertResult { ErrorMessage = error };

            var pcm = Thug2PcSndCodec.Decode(
                data.AsSpan(info.DataOffset, info.DataLength),
                sampleCount);
            if (pcm.Length == 0)
                return new AudioConvertResult { ErrorMessage = "No audio samples decoded" };

            WavWriter.WritePcm16(Path.Combine(outputDir, $"{stem}.wav"), info.SampleRate, 1, pcm);
            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static Thug2PcSndProbeResult? Probe(string filePath)
    {
        try
        {
            return Probe(File.ReadAllBytes(filePath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static Thug2PcSndProbeResult? Probe(byte[] data)
    {
        if (!TryDescribe(data, out var info, out var sampleCount, out _))
            return null;

        return new Thug2PcSndProbeResult(
            info.SampleRate,
            1,
            sampleCount / (double)info.SampleRate);
    }

    private static bool TryDescribe(
        ReadOnlySpan<byte> data,
        out RiffWaveInfo info,
        out int sampleCount,
        out string error)
    {
        sampleCount = 0;
        if (!RiffWaveReader.TryRead(data, out info))
        {
            error = "Not a RIFF/WAVE container with readable fmt and data chunks";
            return false;
        }

        // The authoring pipeline left the RIFF length describing the decoded
        // stream even though data contains the much smaller packed stream. This
        // is also the discriminator that keeps an ordinary 0.25-second PCM WAV
        // (whose byte rate can algebraically equal 4 * data length) from being
        // mistaken for THUG2 SND and decoded into static.
        var declaredFileLength = (long)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4)) + 8;
        if (declaredFileLength <= data.Length)
        {
            error = "Not a THUG2 PC SND payload: RIFF length does not describe a larger decoded stream";
            return false;
        }

        if (info.FormatTag != 1)
        {
            error = $"Unexpected wFormatTag 0x{info.FormatTag:X4} (expected the THUG2 PC PCM marker 0x0001)";
            return false;
        }

        if (info.Channels != 1)
        {
            error = $"Unsupported channel count {info.Channels} (THUG2 PC SND is mono)";
            return false;
        }

        if (info.BlockAlign != 2 || info.BitsPerSample != 16)
        {
            error =
                $"Unexpected PCM marker geometry: block align {info.BlockAlign}, {info.BitsPerSample} bits per sample";
            return false;
        }

        if (info.SampleRate <= 0)
        {
            error = $"Invalid sample rate {info.SampleRate}";
            return false;
        }

        if (info.DataLength <= 0)
        {
            error = "No compressed audio payload";
            return false;
        }

        var fullDecodedBytes = (long)info.DataLength * 4;
        if (info.AvgBytesPerSec != fullDecodedBytes && info.AvgBytesPerSec != fullDecodedBytes - 2)
        {
            error =
                $"Not a THUG2 PC SND payload: decoded byte count {info.AvgBytesPerSec} does not match " +
                $"compressed length {info.DataLength}";
            return false;
        }

        if ((info.AvgBytesPerSec & 1) != 0)
        {
            error = $"Invalid decoded byte count {info.AvgBytesPerSec}";
            return false;
        }

        sampleCount = info.AvgBytesPerSec >> 1;
        error = "";
        return true;
    }
}
