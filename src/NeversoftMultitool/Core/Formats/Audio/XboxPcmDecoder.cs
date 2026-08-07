using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Sample rate, channel count and duration of a <c>.pcm</c> sound.</summary>
public sealed record XboxPcmProbeResult(int SampleRate, int Channels, double DurationSeconds);

/// <summary>
///     THUG2 Xbox <c>.pcm</c> sound effects: a RIFF/WAVE container holding Xbox
///     ADPCM (<see cref="XboxImaAdpcm" />). Both the Xbox and the Windows build
///     ship the same 1,376 files byte-identically — the PC port re-authored only
///     its loose sound tree, not the PRE archives.
///     <para>
///         The engine loads these through <c>Gel/SoundFX/Xbox/p_sfx.cpp</c>, which
///         builds the path as <c>sounds\pcm\&lt;name&gt;.pcm</c>, hardcodes
///         <c>wSamplesPerBlock = 64</c> and truncates the buffer to a whole
///         multiple of nBlockAlign before submitting it.
///     </para>
/// </summary>
public static class XboxPcmDecoder
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
            if (!RiffWaveReader.TryRead(data, out var info))
                return new AudioConvertResult { ErrorMessage = "Not a RIFF/WAVE container" };

            if (info.FormatTag != XboxImaAdpcm.FormatTag)
            {
                return new AudioConvertResult
                {
                    ErrorMessage = $"Unexpected wFormatTag 0x{info.FormatTag:X4} (expected 0x0069 Xbox ADPCM)"
                };
            }

            if (info.Channels is not (1 or 2))
                return new AudioConvertResult { ErrorMessage = $"Unsupported channel count {info.Channels}" };

            var expectedAlign = XboxImaAdpcm.BlockAlignPerChannel * info.Channels;
            if (info.BlockAlign != expectedAlign)
            {
                return new AudioConvertResult
                {
                    ErrorMessage = $"Unexpected nBlockAlign {info.BlockAlign} (expected {expectedAlign})"
                };
            }

            if (info.SampleRate <= 0)
                return new AudioConvertResult { ErrorMessage = $"Invalid sample rate {info.SampleRate}" };

            // Whole blocks only, exactly as the engine does before submitting.
            var usable = info.DataLength - info.DataLength % info.BlockAlign;
            if (usable <= 0)
                return new AudioConvertResult { ErrorMessage = "No complete ADPCM blocks" };

            var pcm = XboxImaAdpcm.Decode(data.AsSpan(info.DataOffset, usable), info.Channels);
            if (pcm.Length == 0)
                return new AudioConvertResult { ErrorMessage = "No audio samples decoded" };

            WavWriter.WritePcm16(Path.Combine(outputDir, $"{stem}.wav"), info.SampleRate, info.Channels, pcm);
            return new AudioConvertResult { Success = true, SamplesWritten = 1 };
        }
        catch (Exception ex)
        {
            return new AudioConvertResult { ErrorMessage = ex.Message };
        }
    }

    public static XboxPcmProbeResult? Probe(string filePath)
    {
        return RiffWaveReader.TryReadHeader(filePath, out var info) ? Describe(info) : null;
    }

    public static XboxPcmProbeResult? Probe(byte[] data)
    {
        return RiffWaveReader.TryRead(data, out var info) ? Describe(info) : null;
    }

    private static XboxPcmProbeResult? Describe(in RiffWaveInfo info)
    {
        if (info.FormatTag != XboxImaAdpcm.FormatTag || info.SampleRate <= 0 || info.Channels is not (1 or 2))
            return null;

        var blockAlign = XboxImaAdpcm.BlockAlignPerChannel * info.Channels;
        var blocks = info.DataLength / blockAlign;
        var frames = blocks * XboxImaAdpcm.SamplesPerBlock;
        return new XboxPcmProbeResult(info.SampleRate, info.Channels, (double)frames / info.SampleRate);
    }
}
